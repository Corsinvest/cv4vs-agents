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

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>Opens (or re-activates) the Context usage document-tab. Backed by a fixed placeholder
/// file under our data folder (extension .cv4vscontext, mapped to ContextEditorFactory): VS opens the
/// file → calls the factory → mounts ContextUsageControl. The file content is irrelevant (the pane
/// fetches context usage from the CLI) — it exists only so VS has a real document to open (a
/// synthetic no-file moniker crashes the shell). Singleton: the fixed path means re-opening focuses
/// the existing tab.</summary>
internal static class ContextDocument
{
    /// <summary>Extension mapped to the editor factory (ProvideEditorExtension on the package).</summary>
    public const string Extension = ".cv4vscontext";

    /// <summary>Fixed placeholder file so the tab is a singleton (same moniker every time).</summary>
    private static string FilePath => Path.Combine(AppPaths.DataFolder, "context" + Extension);

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
                // never read (the pane fetches context usage live).
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, "");
                }

                var factoryGuid = PackageGuids.ContextEditorFactory;
                VsShellUtilities.OpenDocumentWithSpecificEditor(
                    pkg, path, factoryGuid, VSConstants.LOGVIEWID_Primary,
                    out _, out _, out var frame);
                if (frame != null)
                {
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_OwnerCaption, "cv4vs Agents - Context usage");
                    frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, "");
                    frame.Show();
                }
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("ContextDocument.Open", ex);
            }
        });
    }
}
