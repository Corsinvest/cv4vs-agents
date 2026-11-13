/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>A working directory in the two spellings it can have: the one the IDE and the pane use (<see cref="Logical"/>),
/// and the same folder with every link in it resolved (<see cref="Real"/>).
/// <para>They differ when the solution is opened through a junction or a substituted drive
/// (<c>C:\Users\me\Dev</c> to <c>D:\</c>). What the extension asks of other programs comes back in either: git prints the real
/// top of a working tree whatever path it was started in, and Claude Code 2.1.314 reports the real path of its working directory
/// where earlier versions reported the one they were started in. The IDE compares the paths of its documents as text, so a file
/// reported as <c>D:\x</c> is not the document that is open as <c>C:\Users\me\Dev\x</c>: it opens a second time, as a
/// miscellaneous file, outside every project. <see cref="ToLogical"/> turns the real spelling of a path under the working
/// directory into the logical one.</para>
/// <para>Only the working directory's own subtree is mapped, where the two are known to be one folder. A path outside it that goes
/// through the same link is left as it is: taking the link to map everything one to one would, for some paths, name a file that
/// is not there.</para></summary>
internal sealed class PaneRoot
{
    /// <summary>No link to speak of: every path is in the only spelling there is.</summary>
    public static PaneRoot None { get; } = new(null, null);

    public PaneRoot(string logical, string real)
    {
        Logical = Plain(logical);
        Real = Plain(real);
    }

    /// <summary>The working directory as the IDE has it: no trailing separator, null for none.</summary>
    public string Logical { get; }

    /// <summary>The same folder with links resolved: no trailing separator, null for none.</summary>
    public string Real { get; }

    /// <summary>The two spellings differ, so a path may need to be turned from one into the other.</summary>
    public bool IsLinked => Logical is not null && Real is not null && !string.Equals(Logical, Real, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ask the file system for the real path of <paramref name="workingDirectory"/>. <see cref="None"/> when it has none to
    /// give (the folder is not there, or cannot be opened). Reads the disk: not for the UI thread.</summary>
    public static PaneRoot Of(string workingDirectory)
    {
        var logical = Plain(workingDirectory);
        if (logical is null) { return None; }
        var real = FinalPath(logical);
        return real is null ? None : new PaneRoot(logical, real);
    }

    /// <summary><paramref name="path"/> in the working directory's own spelling when it lies under the real one, as it does when git
    /// or Claude Code reported it. Anything else comes back as it was, and so does everything when the two spellings are the same.
    /// <paramref name="path"/> is absolute and normalised, as <see cref="ChangePaths.Normalize(string, string)"/> leaves it.</summary>
    public string ToLogical(string path)
    {
        if (!IsLinked || string.IsNullOrEmpty(path)) { return path; }
        if (path.Length < Real.Length || !path.StartsWith(Real, StringComparison.OrdinalIgnoreCase)) { return path; }
        if (path.Length == Real.Length) { return Logical; }
        // At a folder boundary only: D:\GitHub\repo-two is not under D:\GitHub\repo.
        return path[Real.Length] is '\\' or '/' ? Logical + path.Substring(Real.Length) : path;
    }

    private static string Plain(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        try { return Path.GetFullPath(path.Trim()).TrimEnd('\\', '/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    // The folder as the file system itself names it: every junction, symbolic link and substituted drive in the path resolved.
    private static string FinalPath(string folder)
    {
        try
        {
            using var handle = Native.CreateFile(folder, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
            if (handle.IsInvalid) { return null; }
            var text = new StringBuilder(512);
            var needed = Native.GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
            if (needed > text.Capacity)
            {
                text.EnsureCapacity((int)needed + 1);
                needed = Native.GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
            }
            return needed == 0 || needed > text.Capacity ? null : Plain(WithoutDevicePrefix(text.ToString()));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    // \\?\D:\x is how the API spells D:\x, and \\?\UNC\server\share\x is \\server\share\x.
    private static string WithoutDevicePrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) { return @"\\" + path.Substring(8); }
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
    }

    private const uint ShareAll = 0x7;            // read, write and delete: the folder is not held by being asked about
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;   // required to open a directory

    private static class Native
    {
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                                       uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);
    }
}
