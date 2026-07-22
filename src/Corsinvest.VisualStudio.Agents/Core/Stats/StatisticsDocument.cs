/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Opens (or re-activates) the frameless Statistics document-tab. Singleton by moniker:
/// a second invocation focuses the already-open tab instead of opening a duplicate. The moniker is
/// a synthetic URI (no file on disk) — the editor factory is resolved by GUID, not by extension.</summary>
internal static class StatisticsDocument
{
    /// <summary>Synthetic moniker (not a real file path). Shared with the pane's GetCurFile so the
    /// RDT identifies the single open instance.</summary>
    public const string Moniker = "cv4vs://statistics";

    public static void Open()
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                // Singleton: if the moniker is already open, just activate its frame.
                if (VsShellUtilities.IsDocumentOpen(pkg, Moniker, VSConstants.LOGVIEWID_Primary,
                        out _, out _, out var existing) && existing != null)
                {
                    ErrorHandler.ThrowOnFailure(existing.Show());
                    return;
                }

                if (await pkg.GetServiceAsync(typeof(SVsUIShellOpenDocument)) is not IVsUIShellOpenDocument openDoc)
                {
                    OutputWindowLogger.Warn("[stats] no IVsUIShellOpenDocument — cannot open the tab");
                    return;
                }

                var factoryGuid = PackageGuids.StatisticsEditorFactory;
                var logicalView = VSConstants.LOGVIEWID_Primary;
                var oleSp = ServiceProvider.GlobalProvider.GetService(typeof(IOleServiceProvider)) as IOleServiceProvider;

                // grfOpenSpecific = 0: no special open flags, just open with our factory.
                ErrorHandler.ThrowOnFailure(openDoc.OpenSpecificEditor(
                    0,
                    Moniker,
                    ref factoryGuid,
                    null,
                    ref logicalView,
                    "Statistics",
                    null,
                    VSConstants.VSITEMID_NIL,
                    IntPtr.Zero,
                    oleSp,
                    out var frame));
                if (frame != null) { ErrorHandler.ThrowOnFailure(frame.Show()); }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("StatisticsDocument.Open", ex);
            }
        });
    }
}
