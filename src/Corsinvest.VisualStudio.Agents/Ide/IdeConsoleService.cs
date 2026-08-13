/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using EnvDTE;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// The real console of a debugged console application — what the program wrote to stdout and what
/// it is waiting to read from stdin. Not the Debug output pane: that only carries what goes through
/// Debug.WriteLine, so a Console.WriteLine prompt, and the fact that the program is blocked on
/// Console.ReadLine, are invisible there.
///
/// Everything here goes through AttachConsole, which is a PER-PROCESS switch: while devenv is
/// attached to the debuggee's console it is detached from its own. That is why every call holds a
/// lock for the whole attach → act → detach sequence, why the detach sits in a finally, and why
/// nothing inside that sequence awaits — a continuation resuming elsewhere would leave Visual
/// Studio without a console.
/// </summary>
internal sealed class IdeConsoleService
{
    public static IdeConsoleService Instance { get; } = new();

    /// <summary>Serialises the attach/detach window. Two concurrent tool calls would otherwise
    /// interleave their AttachConsole/FreeConsole pairs and strand the process.</summary>
    private static readonly object ConsoleLock = new();

    private const int StdOutputHandle = -11;
    private const int StdInputHandle = -10;

    /// <summary>Rows per ReadConsoleOutput call. The API writes into a single buffer the caller
    /// sizes, and a tall scrollback in one go overflows it — reading in bands keeps each request
    /// small whatever the buffer height.</summary>
    private const int RowsPerRead = 256;

    public sealed class ConsoleResult
    {
        public bool Ok { get; set; }
        public int ProcessId { get; set; }
        public string Content { get; set; }
        public int TotalLines { get; set; }
        public bool Truncated { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Read the console's visible buffer, keeping the last <paramref name="tailLines"/>
    /// lines (0 for all). Trailing blank rows — the unused part of the buffer — are dropped.</summary>
    public async Task<ConsoleResult> ReadAsync(int processId, int tailLines)
    {
        var (pid, reason) = await ResolveProcessIdAsync(processId);
        if (reason != null) { return new ConsoleResult { Ok = false, Reason = reason }; }

        // Off the UI thread: the P/Invoke sequence blocks, and it must not run where a pumped
        // message could re-enter it.
        return await Task.Run(() => WithConsole(pid, StdOutputHandle, handle =>
        {
            if (!GetConsoleScreenBufferInfo(handle, out var info))
            {
                return Fail(pid, $"Could not read the console buffer of process {pid}.");
            }

            var width = info.dwSize.X;
            var rows = info.dwCursorPosition.Y + 1; // everything above the cursor has been written
            if (width <= 0 || rows <= 0)
            {
                return new ConsoleResult { Ok = true, ProcessId = pid, Content = "", TotalLines = 0 };
            }

            var lines = new List<string>(rows);
            for (var top = 0; top < rows; top += RowsPerRead)
            {
                var band = Math.Min(RowsPerRead, rows - top);
                var buffer = new CHAR_INFO[width * band];
                var region = new SMALL_RECT
                {
                    Left = 0,
                    Top = (short)top,
                    Right = (short)(width - 1),
                    Bottom = (short)(top + band - 1),
                };
                if (!ReadConsoleOutput(handle, buffer, new COORD { X = (short)width, Y = (short)band },
                                       new COORD { X = 0, Y = 0 }, ref region))
                {
                    return Fail(pid, $"Could not read the console of process {pid} at row {top}.");
                }

                for (var r = 0; r < band; r++)
                {
                    var sb = new StringBuilder(width);
                    for (var c = 0; c < width; c++) { sb.Append(buffer[(r * width) + c].UnicodeChar); }
                    lines.Add(sb.ToString().TrimEnd());
                }
            }

            // The buffer is a fixed grid, so everything the program never wrote reads as blanks.
            while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) { lines.RemoveAt(lines.Count - 1); }

            var total = lines.Count;
            var truncated = tailLines > 0 && total > tailLines;
            if (truncated) { lines.RemoveRange(0, total - tailLines); }

            return new ConsoleResult
            {
                Ok = true,
                ProcessId = pid,
                Content = string.Join("\n", lines),
                TotalLines = total,
                Truncated = truncated,
            };
        }));
    }

    /// <summary>Send text (optionally followed by Enter) or a named key to the console's input
    /// queue, as if the user had typed it.</summary>
    public async Task<ConsoleResult> SendAsync(int processId, string text, string key, bool newline)
    {
        var (pid, reason) = await ResolveProcessIdAsync(processId);
        if (reason != null) { return new ConsoleResult { Ok = false, Reason = reason }; }
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(key))
        {
            return new ConsoleResult { Ok = false, ProcessId = pid, Reason = "Pass text, or a key name." };
        }

        // Ctrl+C and Ctrl+Break are signals, not characters: they go through the console control
        // path, which is what actually interrupts a program blocked on a read.
        if (!string.IsNullOrEmpty(key)
            && (key.Equals("ctrl+c", StringComparison.OrdinalIgnoreCase)
                || key.Equals("ctrl+break", StringComparison.OrdinalIgnoreCase)))
        {
            var ctrlEvent = key.Equals("ctrl+c", StringComparison.OrdinalIgnoreCase) ? 0u : 1u;
            return await Task.Run(() => WithConsole(pid, StdInputHandle, _ =>
                GenerateConsoleCtrlEvent(ctrlEvent, 0)
                    ? new ConsoleResult { Ok = true, ProcessId = pid, Content = $"Sent {key}." }
                    : Fail(pid, $"Could not send {key} to process {pid}.")));
        }

        var records = new List<INPUT_RECORD>();
        if (!string.IsNullOrEmpty(key))
        {
            if (!KeyCodes.TryGetValue(key, out var k))
            {
                return new ConsoleResult
                {
                    Ok = false,
                    ProcessId = pid,
                    Reason = $"Unknown key '{key}'. Known: {string.Join(", ", KeyCodes.Keys)}, ctrl+c, ctrl+break.",
                };
            }
            records.Add(KeyEvent(k.Vk, k.Ch, true));
            records.Add(KeyEvent(k.Vk, k.Ch, false));
        }
        else
        {
            foreach (var ch in text)
            {
                records.Add(KeyEvent(0, ch, true));
                records.Add(KeyEvent(0, ch, false));
            }
            if (newline)
            {
                records.Add(KeyEvent(VkReturn, '\r', true));
                records.Add(KeyEvent(VkReturn, '\r', false));
            }
        }

        var input = records.ToArray();
        return await Task.Run(() => WithConsole(pid, StdInputHandle, handle =>
            WriteConsoleInput(handle, input, (uint)input.Length, out _)
                ? new ConsoleResult { Ok = true, ProcessId = pid, Content = $"Sent {input.Length / 2} key event(s)." }
                : Fail(pid, $"Could not write to the console input of process {pid}.")));
    }

    /// <summary>The pid to act on: the one asked for, or the first debugged process. Reports why
    /// there isn't one rather than returning zero.</summary>
    private static async Task<(int Pid, string Reason)> ResolveProcessIdAsync(int requested)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (requested > 0) { return (requested, null); }

        var dte = Package.GetGlobalService(typeof(DTE)) as DTE;
        var dbg = dte?.Debugger;
        if (dbg == null || dbg.CurrentMode == dbgDebugMode.dbgDesignMode)
        {
            return (0, "No debug session is running — debug_start begins one.");
        }
        var procs = dbg.DebuggedProcesses;
        if (procs == null || procs.Count == 0) { return (0, "The debugger reports no processes."); }
        return (procs.Item(1).ProcessID, null);
    }

    /// <summary>Attach to the process's console, run <paramref name="action"/>, detach. Holds the
    /// lock throughout: Visual Studio is without its own console until the finally runs.</summary>
    private static ConsoleResult WithConsole(int pid, int stdHandle, Func<IntPtr, ConsoleResult> action)
    {
        lock (ConsoleLock)
        {
            // Detach from whatever we hold first: AttachConsole fails outright if the process is
            // already attached to one, and devenv usually is.
            FreeConsole();
            if (!AttachConsole((uint)pid))
            {
                var err = Marshal.GetLastWin32Error();
                OutputWindowLogger.Global.Warn($"[ide] AttachConsole({pid}) failed with Win32 error {err}");
                // AttachConsole answers the same way whether the process is gone or merely has no
                // console, so the two are told apart here — "not a console app" about a pid that
                // never existed sends the caller looking at the wrong thing.
                return Fail(pid, ProcessExists(pid)
                    ? $"Process {pid} has no console to attach to (Win32 error {err}). Only a console " +
                      "application has one — a GUI or web project does not."
                    : $"No process with PID {pid} is running (Win32 error {err}). Omit processId to use " +
                      "the first debugged process, or debug_list_processes for the live ones.");
            }
            try
            {
                var handle = GetStdHandle(stdHandle);
                return handle == IntPtr.Zero || handle == new IntPtr(-1)
                    ? Fail(pid, $"Could not open the console handle of process {pid}.")
                    : action(handle);
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("IdeConsoleService.WithConsole", ex);
                return Fail(pid, "Failed to use the console.");
            }
            finally
            {
                // Non-negotiable: without this Visual Studio stays attached to the debuggee's
                // console for the rest of the session.
                FreeConsole();
            }
        }
    }

    private static ConsoleResult Fail(int pid, string reason)
        => new() { Ok = false, ProcessId = pid, Reason = reason };

    /// <summary>Whether a process with that id is running at all — GetProcessById throws when it
    /// is not, which is the only cheap way to ask.</summary>
    private static bool ProcessExists(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const ushort VkReturn = 0x0D;

    /// <summary>Named keys a caller can send, each with the character the console should read for
    /// it. Deliberately small: the point is answering a prompt, not driving a full-screen console
    /// UI.
    ///
    /// The character is what matters. A key record carrying only a virtual-key code and a NUL
    /// UnicodeChar is delivered and counted — WriteConsoleInput reports success — but a program
    /// blocked in Console.ReadLine never sees it, because ReadLine reads characters. The arrow keys
    /// have no character, which is why they are the ones that only work against a program reading
    /// keys rather than lines.</summary>
    private static readonly Dictionary<string, (ushort Vk, char Ch)> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = (VkReturn, '\r'),
        ["escape"] = (0x1B, (char)0x1B),
        ["tab"] = (0x09, '\t'),
        ["backspace"] = (0x08, '\b'),
        ["space"] = (0x20, ' '),
        // No character of their own: a program reading lines will not see these at all.
        ["up"] = (0x26, char.MinValue),
        ["down"] = (0x28, char.MinValue),
        ["left"] = (0x25, char.MinValue),
        ["right"] = (0x27, char.MinValue),
    };

    private static INPUT_RECORD KeyEvent(ushort vk, char ch, bool down) => new()
    {
        EventType = 1, // KEY_EVENT
        KeyEvent = new KEY_EVENT_RECORD
        {
            bKeyDown = down ? 1 : 0,
            wRepeatCount = 1,
            wVirtualKeyCode = vk,
            wVirtualScanCode = 0,
            UnicodeChar = ch,
            dwControlKeyState = 0,
        },
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetConsoleScreenBufferInfo")]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr hConsoleOutput, out CONSOLE_SCREEN_BUFFER_INFO lpInfo);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ReadConsoleOutputW")]
    private static extern bool ReadConsoleOutput(IntPtr hConsoleOutput, [Out] CHAR_INFO[] lpBuffer,
                                                 COORD dwBufferSize, COORD dwBufferCoord, ref SMALL_RECT lpReadRegion);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleInputW")]
    private static extern bool WriteConsoleInput(IntPtr hConsoleInput, INPUT_RECORD[] lpBuffer,
                                                 uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT { public short Left; public short Top; public short Right; public short Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CHAR_INFO { public char UnicodeChar; public ushort Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }
}
