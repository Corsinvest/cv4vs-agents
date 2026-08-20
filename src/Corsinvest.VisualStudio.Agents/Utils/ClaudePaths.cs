/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Corsinvest.VisualStudio.Agents;

/// <summary>Filesystem paths of the CLI's config dir (~/.claude by default),
/// per config-dir. Extracted from AppPaths so a pane driving a profile with its
/// own CLAUDE_CONFIG_DIR reads/writes the SAME dir the claude.exe uses — no
/// mismatch between the extension (SessionManager, StatsService, MCP lock) and the process.
/// Per-pane and global operations alike go through <see cref="ForProfile"/> — a profile always
/// exists (the native "Claude" profile included), so there is no "no profile" case.</summary>
public sealed class ClaudePaths
{
    /// <summary>The CLI's config-dir env var — the single knob a profile sets to isolate its
    /// sessions/auth/settings. Shared so profile creation and path resolution use one spelling.</summary>
    public const string ConfigDirEnvVar = "CLAUDE_CONFIG_DIR";
    public string ClaudeFolder { get; }
    public string SettingsFile { get; }
    public string ProjectsFolder { get; }
    public string IdeFolder { get; }

    /// <summary>Where the CLI keeps the copies it takes before editing a file — one sub-folder per
    /// session, holding whole files (not diffs) named by a hash of the path plus a version. Which
    /// copy belongs to which message is NOT derivable from here: the transcript's
    /// `file-history-snapshot` records name it, which is why SessionManager reads them.</summary>
    public string FileHistoryFolder { get; }

    public ClaudePaths(string configDir)
    {
        // NFC-normalize, take raw (no ~ expansion) — matches the CLI's getClaudeConfigHomeDir.
        ClaudeFolder = configDir.Normalize(NormalizationForm.FormC);
        SettingsFile = Path.Combine(ClaudeFolder, "settings.json");
        ProjectsFolder = Path.Combine(ClaudeFolder, "projects");
        IdeFolder = Path.Combine(ClaudeFolder, "ide");
        FileHistoryFolder = Path.Combine(ClaudeFolder, "file-history");
    }

    // Mirrors the CLI folder-naming: the CLI resolves the cwd to an absolute path, then
    // `replace(/[^a-zA-Z0-9]/g, "-")` — every non-alphanumeric char becomes '-', case PRESERVED.
    // So C:\Users\jane.doe → C--Users-jane-doe (the dot in the username becomes a dash too).
    // Not replicated (rare on Windows): the CLI also realpath's the cwd (symlink/junction
    // canonicalization). The >200-char case IS handled, by SessionFolder rather than here.
    public static string ProjectFolderName(string workingDirectory)
        => NonAlphanumeric.Replace(Path.GetFullPath(workingDirectory), "-");

    /// <summary>The CLI's character class verbatim: it is the contract, so it stays legible as the
    /// rule it mirrors rather than becoming a hand-written char walk. Static because ConfigId is a
    /// property and re-ran the shared-cache lookup on every read.</summary>
    private static readonly Regex NonAlphanumeric = new("[^a-zA-Z0-9]", RegexOptions.Compiled);

    /// <summary>Longest folder name the CLI writes before it truncates — filesystems cap a single
    /// path component at 255 bytes, and it leaves room for the suffix it adds.</summary>
    private const int MaxSanitizedLength = 200;

    /// <summary>The CLI's session folder for a working directory.
    /// <para>Past 200 characters the name is truncated and a hash appended, and the hash is not
    /// reproducible from here: it varies with how the CLI was built. So the folder is found by its
    /// prefix rather than computed — which also survives the day that suffix changes shape. Short
    /// names, the overwhelming majority, never reach the directory listing.</para></summary>
    public string SessionFolder(string workingDirectory)
    {
        var name = ProjectFolderName(workingDirectory);
        if (name.Length <= MaxSanitizedLength) { return Path.Combine(ProjectsFolder, name); }

        // The untruncated name, on the chance an older CLI wrote one.
        var verbatim = Path.Combine(ProjectsFolder, name);
        if (Directory.Exists(verbatim)) { return verbatim; }

        var prefix = name.Substring(0, MaxSanitizedLength) + "-";
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ProjectsFolder))
            {
                if (Path.GetFileName(dir).StartsWith(prefix, StringComparison.Ordinal)) { return dir; }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("ClaudePaths.SessionFolder", ex); }
        // Nothing there yet. Return the truncated stem without a hash: no session exists to be
        // found, and a path this long is only ever read from — the CLI creates the real folder,
        // hash and all, the first time it writes one.
        return Path.Combine(ProjectsFolder, name.Substring(0, MaxSanitizedLength));
    }

    /// <summary>Stable filesystem-safe id for this config-dir, used to namespace our own per-config-dir
    /// data (e.g. stats). Derived from the resolved config-dir path with the same folder-name rule, so it
    /// stays stable across profile renames and two profiles on the SAME config-dir share the same id.</summary>
    public string ConfigId => NonAlphanumeric.Replace(ClaudeFolder, "-");

    /// <summary>Paths for a profile's config-dir. A profile always exists (native "Claude" included),
    /// so there is no null case — the config-dir comes from <see cref="GetConfigDir"/>.</summary>
    public static ClaudePaths ForProfile(Profile profile) => new(GetConfigDir(profile));

    /// <summary>The profile's config-dir: its CLAUDE_CONFIG_DIR when set, else the system default
    /// (the system CLAUDE_CONFIG_DIR env var, or <c>~/.claude</c>) — the CLI's own rule. Case-insensitive
    /// key lookup (env var names are case-insensitive on Windows).</summary>
    public static string GetConfigDir(Profile profile)
    {
        var hit = profile.Env.FirstOrDefault(kv => string.Equals(kv.Key, ConfigDirEnvVar, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrEmpty(hit.Value) ? SystemConfigDir() : hit.Value;
    }

    private static string SystemConfigDir() =>
        Environment.GetEnvironmentVariable(ConfigDirEnvVar) is string cfg && cfg.Length > 0
            ? cfg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
}
