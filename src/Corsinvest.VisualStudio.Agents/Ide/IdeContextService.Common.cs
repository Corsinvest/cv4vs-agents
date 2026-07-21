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

    private static bool PathEquals(string a, string b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(a.Replace('/', '\\').TrimEnd('\\'),
                            b.Replace('/', '\\').TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase);
}
