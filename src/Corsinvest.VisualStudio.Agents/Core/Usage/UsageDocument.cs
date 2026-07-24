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

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Opens (or re-activates) the Usage document-tab. Backed by a fixed placeholder file under
/// our data folder (extension .cv4vsusage, mapped to UsageEditorFactory): VS opens the file → calls
/// the factory → mounts UsageControl. The file content is irrelevant (the pane fetches usage from
/// the CLI) — it exists only so VS has a real document to open (a synthetic no-file moniker crashes
/// the shell). Singleton: the fixed path means re-opening focuses the existing tab.</summary>
internal static class UsageDocument
{
    /// <summary>Extension mapped to the editor factory (ProvideEditorExtension on the package).</summary>
    public const string Extension = ".cv4vsusage";

    /// <summary>Fixed placeholder file so the tab is a singleton (same moniker every time).</summary>
    private static string FilePath => Path.Combine(AppPaths.DataFolder, "usage" + Extension);

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
                // never read (the pane fetches usage live).
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "");
                }

                var factoryGuid = PackageGuids.UsageEditorFactory;
                VsShellUtilities.OpenDocumentWithSpecificEditor(
                    pkg, path, factoryGuid, VSConstants.LOGVIEWID_Primary,
                    out _, out _, out var frame);
                if (frame != null)
                {
                    // Short caption (the tab is narrow and truncates); the full name goes in the tooltip.
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_OwnerCaption, "Usage");
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, "");
                    // Without this the tab tooltip falls back to the placeholder file's path.
                    frame.SetProperty((int)__VSFPROPID5.VSFPROPID_OverrideToolTip, "cv4vs Agents - Usage");
                    frame.Show();
                }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("UsageDocument.Open", ex);
            }
        });
    }
}
