/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Editor factory for the frameless Statistics document. Creates a StatisticsEditorPane
/// (which is both the doc view and the doc data). No file extension is registered — the tab is only
/// opened via the View menu command (StatisticsDocument.Open), never by opening a file.</summary>
[Guid(PackageGuids.StatisticsEditorFactoryString)]
internal sealed class StatisticsEditorFactory : IVsEditorFactory, IDisposable
{
    private ServiceProvider _serviceProvider;

    public int SetSite(IOleServiceProvider psp)
    {
        _serviceProvider = new ServiceProvider(psp);
        return VSConstants.S_OK;
    }

    public int MapLogicalView(ref Guid logicalView, out string physicalView)
    {
        physicalView = null;
        return logicalView == VSConstants.LOGVIEWID_Primary ? VSConstants.S_OK : VSConstants.E_NOTIMPL;
    }

    public int CreateEditorInstance(
        uint grfCreateDoc,
        string pszMkDocument,
        string pszPhysicalView,
        IVsHierarchy pvHier,
        uint itemid,
        IntPtr punkDocDataExisting,
        out IntPtr ppunkDocView,
        out IntPtr ppunkDocData,
        out string pbstrEditorCaption,
        out Guid pguidCmdUI,
        out int pgrfCDW)
    {
        ppunkDocView = IntPtr.Zero;
        ppunkDocData = IntPtr.Zero;
        pbstrEditorCaption = "";
        pguidCmdUI = Guid.Empty;
        pgrfCDW = 0;

        // A frameless singleton never re-uses existing doc data.
        if (punkDocDataExisting != IntPtr.Zero) { return VSConstants.VS_E_INCOMPATIBLEDOCDATA; }

        var pane = new StatisticsEditorPane();
        ppunkDocView = Marshal.GetIUnknownForObject(pane);
        ppunkDocData = Marshal.GetIUnknownForObject(pane);
        pbstrEditorCaption = "Statistics";
        return VSConstants.S_OK;
    }

    public int Close() => VSConstants.S_OK;

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
