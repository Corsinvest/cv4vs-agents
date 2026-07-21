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

    public ClaudePaths(string configDir)
    {
        // NFC-normalize, take raw (no ~ expansion) — matches the CLI's getClaudeConfigHomeDir.
        ClaudeFolder = configDir.Normalize(NormalizationForm.FormC);
        SettingsFile = Path.Combine(ClaudeFolder, "settings.json");
        ProjectsFolder = Path.Combine(ClaudeFolder, "projects");
        IdeFolder = Path.Combine(ClaudeFolder, "ide");
    }

    // Mirrors the CLI folder-naming: the CLI resolves the cwd to an absolute path, then
    // `replace(/[^a-zA-Z0-9]/g, "-")` — every non-alphanumeric char becomes '-', case PRESERVED.
    // So C:\Users\jane.doe → C--Users-jane-doe (the dot in the username becomes a dash too; an
    // earlier version left dots intact and lowercased, missing the folder).
    // Not replicated (rare on Windows): the CLI also realpath's the cwd
    // (symlink/junction canonicalization) and, for names >200 chars, truncates + appends a hash.
    // Shared so the CLI-reader path (SessionFolder) and our own data path use the SAME hash.
    public static string ProjectFolderName(string workingDirectory)
        => Regex.Replace(Path.GetFullPath(workingDirectory), "[^a-zA-Z0-9]", "-");

    public string SessionFolder(string workingDirectory)
        => Path.Combine(ProjectsFolder, ProjectFolderName(workingDirectory));

    /// <summary>Stable filesystem-safe id for this config-dir, used to namespace our own per-config-dir
    /// data (e.g. stats). Derived from the resolved config-dir path with the same folder-name rule, so it
    /// stays stable across profile renames and two profiles on the SAME config-dir share the same id.</summary>
    public string ConfigId => Regex.Replace(ClaudeFolder, "[^a-zA-Z0-9]", "-");

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
