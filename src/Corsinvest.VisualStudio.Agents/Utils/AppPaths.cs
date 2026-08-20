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

    /// <summary>The editor context menu's prompts, next to the profiles and for the same reason:
    /// the menu is queried constantly by VS, long before any Options page exists.</summary>
    public static readonly string EditorPromptsFile = Path.Combine(DataFolder, "editor-prompts.json");

    /// <summary>What the `@` file picker hides on top of the workspace's own rules. A file rather
    /// than a settings-store value because the content IS a .gitignore — comments, sections, one
    /// rule per line — which is a thing to read and copy between machines, not a preference.
    /// <para>Named `.gitignore` rather than `.json` so VS opens it with the editor that already
    /// colours the format, and so nothing has to be escaped into a JSON string.</para></summary>
    public static readonly string IgnoreRulesFile = Path.Combine(DataFolder, "picker-ignore.gitignore");

    /// <summary>The virtual hosts the WebView is served from: one for the bundle, one for the
    /// lazily-rasterised file icons.
    /// <para>The `.invalid` suffix is the load-bearing part. `.local` belongs to mDNS, so Windows
    /// answers a name under it with a multicast query and waits ~2s for a reply that never comes —
    /// before WebView2 serves the file from disk anyway. Measured here: 2551ms from responseEnd to
    /// domInteractive under `.local`, 397ms under `.invalid`, on an otherwise identical load. The
    /// WebView2 docs say the same ("using .local … can cause a delay during navigations. You should
    /// avoid using .local if you can") and point at RFC 6761's reserved names, of which this is
    /// one.</para>
    /// <para>The WebView has its own copy of the icon host in `core/icon-url.ts` — it builds URLs
    /// on the first render, before any message from us could have arrived. If the two ever drift
    /// apart the icons vanish on the next run, which is a loud enough failure.</para></summary>
    public const string WebViewHost = "cv4vs.invalid";
    public const string IconHost = "cv4vs-icons.invalid";

    /// <summary>
    /// Cache folder for file-type icons rasterised from VS KnownMonikers, served
    /// over <see cref="IconHost"/> and generated lazily the first time an
    /// extension is requested.
    /// </summary>
    public static readonly string IconCacheFolder = Path.Combine(DataFolder, "icons");

    /// <summary>Where the per-project folders live. ProjectStore names what goes inside — it needs
    /// the root without going back through ProjectFolder, which delegates to it.</summary>
    public static readonly string ProjectsRoot = Path.Combine(DataFolder, "data", "projects");

    public static string WebViewHtml()
    {
        // The TS+Lit WebView is built from WebViewSrc/ into WebView2/ at
        // build time (see csproj BuildWebViewSrc target). Sits next to our own DLL.
        var baseDir = Path.GetDirectoryName(typeof(AppPaths).Assembly.Location);
        return Path.Combine(baseDir, "WebView2", "index.html");
    }

    /// <summary>Root of OUR per-project data: <ProjectsRoot>/<folder>/. This is the PER-SOLUTION
    /// scope (independent of profile): workspace.json lives here; per-profile files live in the
    /// <config-id>/ subfolder below. ProjectStore names the folder and creates it — deliberately
    /// not this class, which is otherwise all Path.Combine and touches nothing.</summary>
    public static string ProjectFolder(string workingDirectory)
        => Core.Workspace.ProjectStore.FolderFor(workingDirectory);

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
}
