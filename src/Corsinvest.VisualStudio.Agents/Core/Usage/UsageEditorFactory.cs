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

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Editor factory for the frameless Usage document. Creates a UsageEditorPane (both the doc
/// view and the doc data). Opened only via the View menu command (UsageDocument.Open), never by
/// opening a file — the placeholder file exists just so the shell has a real moniker.</summary>
[Guid(PackageGuids.UsageEditorFactoryString)]
internal sealed class UsageEditorFactory : IVsEditorFactory, IDisposable
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

        if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0)
        {
            return VSConstants.E_INVALIDARG;
        }
        // Fresh doc data every time (the pane fetches live usage, not the file).
        if (punkDocDataExisting != IntPtr.Zero) { return VSConstants.VS_E_INCOMPATIBLEDOCDATA; }

        var pane = new UsageEditorPane();
        ppunkDocView = Marshal.GetIUnknownForObject(pane);
        ppunkDocData = Marshal.GetIUnknownForObject(pane);
        pbstrEditorCaption = "Usage";
        return VSConstants.S_OK;
    }

    public int Close() => VSConstants.S_OK;

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
