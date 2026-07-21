/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 *
 * ConPTY (Windows pseudo-console) wrapper. Implemented from the public Win32 API and
 * Microsoft's documented pseudo-console setup sequence:
 *   https://learn.microsoft.com/windows/console/creating-a-pseudoconsole-session
 * The kernel32 signatures, structs and attribute constants below are dictated by Windows.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Cli;

/// <summary>
/// Managed front-end for the Windows ConPTY APIs (Windows 10 1809 / build 17763 and later).
/// A <see cref="Session"/> bundles the pseudo-console, its I/O pipes and the attached child
/// process; the static I/O helpers read/write/resize an open session.
/// </summary>
internal static class ConPty
{
    /// <summary>
    /// One running pseudo-console and its child process. Owns every native handle it holds;
    /// <see cref="Dispose"/> tears them down and reaps the child. Not thread-safe.
    /// </summary>
    internal sealed class Session : IDisposable
    {
        internal IntPtr PseudoConsoleHandle;
        internal IntPtr InputWriteHandle;
        internal IntPtr OutputReadHandle;
        internal IntPtr ProcessHandle;
        internal IntPtr ThreadHandle;
        public int ProcessId { get; internal set; }

        private bool _closed;

        public void Dispose()
        {
            if (_closed) { return; }
            _closed = true;

            // Close the pseudo-console first: that signals EOF to the child and to our output
            // reader. Then the pipe ends, then reap the process (bounded wait so a hung child
            // can't block pane teardown), then the thread handle.
            Free(ref PseudoConsoleHandle, Native.ClosePseudoConsole);
            Free(ref InputWriteHandle, h => Native.CloseHandle(h));
            Free(ref OutputReadHandle, h => Native.CloseHandle(h));

            if (ProcessHandle != IntPtr.Zero)
            {
                Native.WaitForSingleObject(ProcessHandle, 1000);
                Free(ref ProcessHandle, h => Native.CloseHandle(h));
            }
            Free(ref ThreadHandle, h => Native.CloseHandle(h));
        }

        private static void Free(ref IntPtr handle, Action<IntPtr> close)
        {
            if (handle == IntPtr.Zero) { return; }
            close(handle);
            handle = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Spawns <paramref name="command"/> attached to a fresh pseudo-console sized
    /// <paramref name="cols"/>×<paramref name="rows"/>, in <paramref name="workingDirectory"/>.
    /// When <paramref name="env"/> has entries the child runs with a per-process environment
    /// (the parent's env overlaid with <paramref name="env"/>) rather than inheriting the
    /// parent's — this is what lets two panes on different profiles (e.g. Chat/Claude and
    /// CLI/z.ai) coexist without a shared-env race. The returned session owns all handles.
    /// </summary>
    public static Session Create(string command, string workingDirectory, short cols, short rows,
        IReadOnlyDictionary<string, string> env = null)
    {
        var (inRead, inWrite, outRead, outWrite) = CreatePipes();

        var pcHandle = Native.CreatePseudoConsole(new COORD { X = cols, Y = rows }, inRead, outWrite, 0, out var hpc);
        if (pcHandle != 0)
        {
            CloseAll(inRead, inWrite, outRead, outWrite);
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{pcHandle:X8}");
        }

        // The pseudo-console has duplicated the child ends; we no longer need them.
        Native.CloseHandle(inRead);
        Native.CloseHandle(outWrite);

        var attributes = AllocPseudoConsoleAttributeList(hpc, out var attrBuffer);
        try
        {
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() },
                AttributeList = attributes,
            };

            var envBlock = IntPtr.Zero;
            var flags = EXTENDED_STARTUPINFO_PRESENT;
            try
            {
                if (env != null && env.Count > 0)
                {
                    envBlock = BuildEnvironmentBlock(env);
                    flags |= CREATE_UNICODE_ENVIRONMENT;
                }

                if (!Native.CreateProcessW(null, command, IntPtr.Zero, IntPtr.Zero, false, flags,
                        envBlock, workingDirectory, ref startup, out var pi))
                {
                    throw new InvalidOperationException($"CreateProcessW failed: {Marshal.GetLastWin32Error()}");
                }

                return new Session
                {
                    PseudoConsoleHandle = hpc,
                    InputWriteHandle = inWrite,
                    OutputReadHandle = outRead,
                    ProcessHandle = pi.hProcess,
                    ThreadHandle = pi.hThread,
                    ProcessId = pi.dwProcessId,
                };
            }
            catch
            {
                // Roll back everything the caller would otherwise leak.
                Native.ClosePseudoConsole(hpc);
                Native.CloseHandle(inWrite);
                Native.CloseHandle(outRead);
                throw;
            }
            finally
            {
                if (envBlock != IntPtr.Zero) { Marshal.FreeHGlobal(envBlock); }
            }
        }
        finally
        {
            Native.DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attrBuffer);
        }
    }

    /// <summary>Reads a chunk from the session's output pipe. Returns the byte count, or 0 at EOF.</summary>
    public static int Read(IntPtr outputReadHandle, byte[] buffer)
        => Native.ReadFile(outputReadHandle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) ? (int)read : 0;

    /// <summary>Writes <paramref name="count"/> bytes of <paramref name="data"/> to the input pipe.</summary>
    public static void Write(IntPtr inputWriteHandle, byte[] data, int count)
        => Native.WriteFile(inputWriteHandle, data, (uint)count, out _, IntPtr.Zero);

    /// <summary>Resizes the pseudo-console viewport to <paramref name="cols"/>×<paramref name="rows"/>.</summary>
    public static void Resize(IntPtr pseudoConsoleHandle, short cols, short rows)
        => Native.ResizePseudoConsole(pseudoConsoleHandle, new COORD { X = cols, Y = rows });

    // Two inheritable, unbuffered pipes: one for our writes → child stdin, one for child stdout → our reads.
    private static (IntPtr inRead, IntPtr inWrite, IntPtr outRead, IntPtr outWrite) CreatePipes()
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1,
        };

        if (!Native.CreatePipe(out var inRead, out var inWrite, ref sa, 0))
        {
            throw new InvalidOperationException($"CreatePipe (input) failed: {Marshal.GetLastWin32Error()}");
        }
        if (!Native.CreatePipe(out var outRead, out var outWrite, ref sa, 0))
        {
            Native.CloseHandle(inRead);
            Native.CloseHandle(inWrite);
            throw new InvalidOperationException($"CreatePipe (output) failed: {Marshal.GetLastWin32Error()}");
        }
        return (inRead, inWrite, outRead, outWrite);
    }

    // A single-entry proc-thread attribute list carrying the pseudo-console handle, so the child
    // process inherits it. Two-call idiom: size first, then fill the allocated buffer.
    private static IntPtr AllocPseudoConsoleAttributeList(IntPtr hpc, out IntPtr buffer)
    {
        var size = IntPtr.Zero;
        Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
        buffer = Marshal.AllocHGlobal(size);

        if (!Native.InitializeProcThreadAttributeList(buffer, 1, 0, ref size))
        {
            Marshal.FreeHGlobal(buffer);
            throw new InvalidOperationException($"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");
        }
        if (!Native.UpdateProcThreadAttribute(buffer, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                hpc, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
        {
            Native.DeleteProcThreadAttributeList(buffer);
            Marshal.FreeHGlobal(buffer);
            throw new InvalidOperationException($"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
        }
        return buffer;
    }

    /// <summary>Builds the UTF-16, double-null-terminated environment block CreateProcessW expects.
    /// Windows REPLACES the whole environment when lpEnvironment is set, so we start from the parent's
    /// full env and overlay the caller's keys — otherwise the child loses PATH/SystemRoot/… and won't
    /// launch. Keys are sorted so the block is byte-for-byte reproducible across launches. The caller
    /// owns the returned pointer (Marshal.FreeHGlobal).</summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string> overlay)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            vars[(string)e.Key] = (string)e.Value;
        }
        foreach (var kv in overlay) { vars[kv.Key] = kv.Value; }

        var block = new StringBuilder();
        foreach (var kv in vars) { block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0'); }
        block.Append('\0');

        return Marshal.StringToHGlobalUni(block.ToString());
    }

    private static void CloseAll(params IntPtr[] handles)
    {
        foreach (var h in handles.Where(h => h != IntPtr.Zero)) { Native.CloseHandle(h); }
    }

    // ProcThreadAttribute for a pseudo-console, and the two CreateProcess flags we need: an extended
    // STARTUPINFOEX and a Unicode env block. All fixed by Windows.
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // kernel32 entry points. Signatures are fixed by the Win32 headers.
    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool CreateProcessW(string lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
    }
}
