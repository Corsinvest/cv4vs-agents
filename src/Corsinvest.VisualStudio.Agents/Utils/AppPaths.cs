/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;

namespace Corsinvest.VisualStudio.Agents;

internal static class AppPaths
{
    public static readonly string DataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Corsinvest",
        AppConstants.AppId);

    public static readonly string WebView2Folder = Path.Combine(DataFolder, "WebView2");

    /// <summary>Environment profiles store. A plain JSON file (not the VS settings store)
    /// so the menu/launch can read profiles without materializing the Options page first.</summary>
    public static readonly string ProfilesFile = Path.Combine(DataFolder, "profiles.json");

    /// <summary>
    /// Cache folder for file-type icons rasterised from VS KnownMonikers. The
    /// WebView serves these via the `cv4vs-icons.local` virtual host, generated
    /// lazily the first time an extension is requested.
    /// </summary>
    public static readonly string IconCacheFolder = Path.Combine(DataFolder, "icons");

    public static string WebViewHtml()
    {
        // The TS+Lit WebView is built from WebViewSrc/ into WebView2/ at
        // build time (see csproj BuildWebViewSrc target). Sits next to our own DLL.
        var baseDir = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location);
        return Path.Combine(baseDir, "WebView2", "index.html");
    }

    /// <summary>Root of OUR per-project data: <DataFolder>/data/projects/<project-hash>/. This is the
    /// PER-SOLUTION scope (independent of profile): workspace.json lives here; per-profile files live
    /// in the <config-id>/ subfolder below.</summary>
    public static string ProjectFolder(string workingDirectory)
        => Path.Combine(DataFolder, "data", "projects", ClaudePaths.ProjectFolderName(workingDirectory));

    /// <summary>The per-solution workspace file (open panes). Depends only on the solution folder,
    /// not on any profile — the panes' profiles are stored inside the JSON.</summary>
    public static string WorkspaceFile(string workingDirectory)
        => Path.Combine(ProjectFolder(workingDirectory), "workspace.json");

    /// <summary>Per-(project, profile) folder: <DataFolder>/data/projects/<hash>/<config-id>/. Stats
    /// of one profile within one solution live here.</summary>
    public static string ProjectProfileFolder(ClaudePaths paths, string workingDirectory)
        => Path.Combine(ProjectFolder(workingDirectory), paths.ConfigId);

    /// <summary>A file inside the per-(project, profile) folder (by workdir). Caller creates the dir at Save.</summary>
    public static string ProjectProfileFile(ClaudePaths paths, string workingDirectory, string fileName)
        => Path.Combine(ProjectProfileFolder(paths, workingDirectory), fileName);

    /// <summary>Same, given the project-hash directly (StatsService enumerates CLI projectDirs whose
    /// basename IS the hash — avoids recomputing it).</summary>
    public static string ProjectProfileFileByHash(ClaudePaths paths, string projectHash, string fileName)
        => Path.Combine(DataFolder, "data", "projects", projectHash, paths.ConfigId, fileName);
}
