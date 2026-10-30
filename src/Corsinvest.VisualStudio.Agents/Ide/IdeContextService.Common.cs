/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// IdeContextService shared helpers used by both the tracking side and the operations
/// side: document-window-frame enumeration and per-frame moniker/dirty/path checks.
/// </summary>
internal sealed partial class IdeContextService
{
    //  Document-window-frame enumeration. DTE.Documents/Windows.Document miss
    //  tabs whose buffer isn't materialized yet (preview / never-clicked tabs);
    //  the shell's document-window frames are the authoritative list of what's
    //  open. Used by getOpenEditors / checkDocumentDirty / saveDocument.

    private static List<IVsWindowFrame> DocumentFrames()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var frames = new List<IVsWindowFrame>();
        if (Package.GetGlobalService(typeof(SVsUIShell)) is not IVsUIShell shell
            || shell.GetDocumentWindowEnum(out var en) != VSConstants.S_OK || en == null)
        {
            return frames;
        }
        var arr = new IVsWindowFrame[1];
        while (en.Next(1, arr, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            if (arr[0] != null) { frames.Add(arr[0]); }
        }
        return frames;
    }

    private static string FrameMoniker(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var o) == VSConstants.S_OK
            ? o as string
            : null;
    }

    private static bool FrameDirty(IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocData, out var o) == VSConstants.S_OK
            && o is IVsPersistDocData pdd
            && pdd.IsDocDataDirty(out var d) == VSConstants.S_OK
            && d != 0;
    }

    /// <summary>The path of an open document, matched the way the shell sees it. Returns the
    /// moniker rather than a bool so the caller can then look the document up by a path DTE agrees
    /// with, whatever casing or separator style it came in as.
    /// <para>Goes through the frames for the reason the enumeration exists: DTE.Documents misses
    /// tabs whose buffer is not materialised yet. A preview tab reported "not open" from there while
    /// checkDocumentDirty, which does use the frames, said it was open AND dirty, for exactly the
    /// file whose buffer differs from disk, which is the one case worth asking about.</para></summary>
    private static string OpenDocumentMoniker(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var frame in DocumentFrames())
        {
            var moniker = FrameMoniker(frame);
            if (PathEquals(moniker, path)) { return moniker; }
        }
        return null;
    }

    /// <summary>The window frames of an open document, in the order the shell lists them: none when it is not open, more than
    /// one for a file in two windows (Window > New Window) or a designer with its code view. Matched the way the shell sees the
    /// path. UI thread.</summary>
    internal static List<IVsWindowFrame> FindDocumentFrames(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var found = new List<IVsWindowFrame>();
        foreach (var frame in DocumentFrames())
        {
            if (PathEquals(FrameMoniker(frame), path)) { found.Add(frame); }
        }
        return found;
    }

    /// <summary>Every open document's frames, by <see cref="FrameKey"/> of its path: for a caller that looks up many files and
    /// should not walk the frames for each. UI thread.</summary>
    internal static Dictionary<string, List<IVsWindowFrame>> OpenDocumentFrames()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var frames = new Dictionary<string, List<IVsWindowFrame>>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in DocumentFrames())
        {
            var moniker = FrameMoniker(frame);
            if (string.IsNullOrEmpty(moniker)) { continue; }
            var key = FrameKey(moniker);
            if (!frames.TryGetValue(key, out var list)) { frames[key] = list = new List<IVsWindowFrame>(); }
            list.Add(frame);
        }
        return frames;
    }

    /// <summary>A path spelt the way <see cref="PathEquals"/> compares them: one separator, none at the end, and in the spelling of
    /// the solution folder when it was given in the real spelling of a folder that is a link (see <see cref="SolutionPaths"/>).</summary>
    internal static string FrameKey(string path) => SolutionPaths.ToLogical(path.Replace('/', '\\').TrimEnd('\\'));

    /// <summary>The path of the document in a frame, as the shell spells it. UI thread.</summary>
    internal static string DocumentMoniker(IVsWindowFrame frame) => FrameMoniker(frame);

    private static bool PathEquals(string a, string b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(FrameKey(a), FrameKey(b), StringComparison.OrdinalIgnoreCase);
}
