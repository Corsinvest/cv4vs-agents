/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>The open solution's folder in the spelling the IDE has it, for the paths that come from elsewhere in the spelling of the real
/// folder (see <see cref="PaneRoot"/>): what Claude Code reports of the files it edits, what git prints, what a tool's argument says.
/// The IDE compares the paths of its documents as text, so a file named by the real path of a linked solution folder is not the
/// document that is open under the link; it would open a second time as a miscellaneous file, and a save or a lookup by that path would
/// miss the open one.
/// <para>The real path is asked of the disk in the background, when the solution folder is set; a path that is asked about before that
/// has an answer is left as it is, and the next one is mapped. Without a link in the folder nothing is ever changed.</para></summary>
internal static class SolutionPaths
{
    private sealed class Known
    {
        public Known(string folder, PaneRoot root)
        {
            Folder = folder;
            Root = root;
        }

        public string Folder { get; }
        public PaneRoot Root { get; }
    }

    private static Known _known;
    private static int _busy;

    /// <summary>Work out the real path of <paramref name="folder"/>, off the calling thread. One at a time: a call made while another
    /// is running is dropped, and the next path asked about starts it again.</summary>
    public static void Warm(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) { return; }
        var known = Volatile.Read(ref _known);
        if (known is not null && string.Equals(known.Folder, folder, StringComparison.OrdinalIgnoreCase)) { return; }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { return; }
        _ = Task.Run(() =>
        {
            try { Volatile.Write(ref _known, new Known(folder, PaneRoot.Of(folder))); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("SolutionPaths.Warm", ex); }
            finally { Volatile.Write(ref _busy, 0); }
        });
    }

    /// <summary><paramref name="path"/> in the spelling of the solution folder when it lies under that folder's real path; as it was
    /// otherwise. Backslashes and no trailing separator, the way <see cref="IdeContextService.FrameKey"/> spells a path.</summary>
    public static string ToLogical(string path)
    {
        if (string.IsNullOrEmpty(path)) { return path; }
        var folder = AgentsPackage.Instance?.CurrentSolutionFolder;
        if (string.IsNullOrEmpty(folder)) { return path; }

        var known = Volatile.Read(ref _known);
        if (known is null || !string.Equals(known.Folder, folder, StringComparison.OrdinalIgnoreCase))
        {
            Warm(folder);
            return path;
        }
        return known.Root.ToLogical(path);
    }
}
