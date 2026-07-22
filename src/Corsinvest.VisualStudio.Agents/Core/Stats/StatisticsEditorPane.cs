/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Document pane for the Statistics tab. Frameless + read-only: no backing file, so every
/// IVsPersistDocData / IPersistFileFormat member is a no-op (never dirty, never saves, no reload).
/// The content is a WPF control — a placeholder here, replaced by StatisticsControl in the next
/// step. The factory returns this same object as both doc view and doc data.</summary>
internal sealed class StatisticsEditorPane : WindowPane, IVsPersistDocData, IPersistFileFormat
{
    // The placeholder file path VS opened. Held only so GetCurFile can return it (the RDT moniker);
    // its content is never read — the UI pulls from StatsService.
    private string _fileName = "";

    public StatisticsEditorPane() : base(null)
    {
        Content = new System.Windows.Controls.TextBlock
        {
            Text = "Statistics (placeholder)",
            Margin = new System.Windows.Thickness(12),
        };
    }

    // IVsPersistDocData — no file backing: never dirty, nothing to load or save.
    int IVsPersistDocData.GetGuidEditorType(out Guid pClassID)
    {
        pClassID = PackageGuids.StatisticsEditorFactory;
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.IsDocDataDirty(out int pfDirty)
    {
        pfDirty = 0;
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.IsDocDataReloadable(out int pfReloadable)
    {
        pfReloadable = 0;
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.LoadDocData(string pszMkDocument)
    {
        _fileName = pszMkDocument ?? "";
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.SetUntitledDocPath(string pszDocDataPath) => VSConstants.S_OK;

    int IVsPersistDocData.SaveDocData(VSSAVEFLAGS dwSave, out string pbstrMkDocumentNew, out int pfSaveCanceled)
    {
        pbstrMkDocumentNew = null;
        pfSaveCanceled = 0;
        return VSConstants.S_OK;
    }

    int IVsPersistDocData.Close() => VSConstants.S_OK;

    int IVsPersistDocData.OnRegisterDocData(uint docCookie, IVsHierarchy pHierNew, uint itemidNew)
        => VSConstants.S_OK;

    int IVsPersistDocData.RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew)
        => VSConstants.S_OK;

    int IVsPersistDocData.ReloadDocData(uint grfFlags) => VSConstants.S_OK;

    // IPersistFileFormat — same story: no file, no format, never dirty.
    int IPersist.GetClassID(out Guid pClassID)
    {
        pClassID = PackageGuids.StatisticsEditorFactory;
        return VSConstants.S_OK;
    }

    int IPersistFileFormat.GetClassID(out Guid pClassID)
    {
        pClassID = PackageGuids.StatisticsEditorFactory;
        return VSConstants.S_OK;
    }

    int IPersistFileFormat.GetCurFile(out string ppszFilename, out uint pnFormatIndex)
    {
        ppszFilename = _fileName;
        pnFormatIndex = 0;
        return VSConstants.S_OK;
    }

    int IPersistFileFormat.GetFormatList(out string ppszFormatList)
    {
        ppszFormatList = "";
        return VSConstants.S_OK;
    }

    int IPersistFileFormat.InitNew(uint nFormatIndex) => VSConstants.S_OK;

    int IPersistFileFormat.IsDirty(out int pfIsDirty)
    {
        pfIsDirty = 0;
        return VSConstants.S_OK;
    }

    int IPersistFileFormat.Load(string pszFilename, uint grfMode, int fReadOnly)
    {
        if (pszFilename != null) { _fileName = pszFilename; }
        // Content is never read from disk — the UI pulls from StatsService.
        return VSConstants.S_OK;
    }

    int IPersistFileFormat.Save(string pszFilename, int fRemember, uint nFormatIndex) => VSConstants.S_OK;

    int IPersistFileFormat.SaveCompleted(string pszFilename) => VSConstants.S_OK;
}
