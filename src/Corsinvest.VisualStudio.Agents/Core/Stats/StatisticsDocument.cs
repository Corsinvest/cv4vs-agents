/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Opens (or re-activates) the Statistics document-tab. Backed by a fixed placeholder file
/// under our data folder (extension .cv4vsstats, mapped to StatisticsEditorFactory): VS opens the
/// file → calls the factory → mounts StatisticsControl. The file content is irrelevant (the pane
/// reads StatsService, not the file) — it exists only so VS has a real document to open, which is
/// how a custom editor is meant to be opened (a synthetic no-file moniker crashes the shell).
/// Singleton: the fixed path means re-opening focuses the existing tab.</summary>
internal static class StatisticsDocument
{
    /// <summary>Extension mapped to the editor factory (ProvideEditorExtension on the package).</summary>
    public const string Extension = ".cv4vsstats";

    /// <summary>Fixed placeholder file so the tab is a singleton (same moniker every time).</summary>
    private static string FilePath => Path.Combine(AppPaths.DataFolder, "statistics" + Extension);

    public static void Open()
    {
        var pkg = AgentsPackage.Instance;
        if (pkg == null) { return; }
        _ = pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var path = FilePath;
                // The placeholder must exist on disk for OpenDocument to succeed; its content is
                // never read (the pane pulls from StatsService).
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "");
                }

                // Open with our editor factory explicitly (resolved by GUID, not by whatever the
                // user's default for the extension is). Focuses the existing tab if already open.
                var factoryGuid = PackageGuids.StatisticsEditorFactory;
                VsShellUtilities.OpenDocumentWithSpecificEditor(
                    pkg, path, factoryGuid, VSConstants.LOGVIEWID_Primary,
                    out _, out _, out var frame);
                if (frame != null)
                {
                    // The tab caption defaults to the placeholder file name; override it.
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_OwnerCaption, "cv4vs Agents - Statistics");
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, "");
                    frame.Show();
                }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("StatisticsDocument.Open", ex);
            }
        });
    }
}
