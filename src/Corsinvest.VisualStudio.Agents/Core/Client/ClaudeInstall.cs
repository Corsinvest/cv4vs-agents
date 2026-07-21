/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using System;
using System.Collections.Generic;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// <para>
/// Finds the Claude Code CLI binary and the shared "not installed" UI. Core (not CLI-specific):
/// both the Chat pane (<c>ClaudeClient</c>) and the CLI pane (<c>ClaudeCliLauncher</c>) resolve
/// the executable through here.
/// </para>
/// <para>
/// We need the REAL <c>claude.exe</c> (a PE binary): ConPTY's CreateProcess and the Chat's
/// ProcessStartInfo both launch it directly, and neither runs a <c>.cmd</c>/<c>.ps1</c> shim. So
/// the resolver looks for <c>claude.exe</c> specifically — never the npm shims (<c>claude</c>,
/// <c>claude.cmd</c>, <c>claude.ps1</c>) that <c>where claude</c> returns.
/// </para>
/// </summary>
public static class ClaudeInstall
{
    /// <summary>Anthropic setup/quickstart page — shown when the binary can't be found.</summary>
    public const string DocsUrl = "https://code.claude.com/docs/en/setup";

    /// <summary>Resolve the real <c>claude.exe</c>, or <c>null</c> if not installed. Covers every
    /// current install method: the native installer and WinGet put <c>claude.exe</c> on PATH (found
    /// by the PATH scan); npm exposes only shims on PATH, so its real binary is picked up by the
    /// node_modules fallback below.</summary>
    public static string ResolveExecutable()
    {
        // A user-set path wins over auto-detection (CLI in a non-standard location, a specific
        // version, a custom build). Warn rather than fall back silently when it's set but unusable:
        // the user picked it on purpose, so a silent switch to another binary would be worse.
        var configured = AgentsOptions.General?.ClaudeExecutablePath?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            if (!configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                OutputWindowLogger.Warn($"[cli] configured Claude executable must be an .exe (a .cmd/.bat/.ps1 shim can't be launched): {configured} — falling back to auto-detection");
            }
            else if (File.Exists(configured)) { return configured; }
            else
            {
                OutputWindowLogger.Warn($"[cli] configured Claude executable not found: {configured} — falling back to auto-detection");
            }
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate)) { return candidate; }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 1) PATH: the native installer (~\.local\bin) and WinGet (WinGet\Links) drop the real
        //    claude.exe here. We scan for "claude.exe" only, so the npm shims (no extension / .cmd
        //    / .ps1, which CreateProcess can't launch) are skipped even though they're on PATH too.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) { continue; }
            string exe;
            try { exe = Path.Combine(dir.Trim(), "claude.exe"); }
            catch (ArgumentException) { continue; } // malformed PATH entry
            yield return exe;
        }

        // 2) Native installer's canonical launcher dir, in case PATH isn't refreshed in this VS
        //    session (env vars are captured at process start; a just-installed CLI may be missing).
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
        {
            yield return Path.Combine(userProfile, ".local", "bin", "claude.exe");
        }

        // 3) npm global: the shim on PATH points here, but we want the real binary directly.
        //    %APPDATA%\npm: npm's default Windows prefix; %LOCALAPPDATA%\npm: nvm-windows/fnm.
        foreach (var prefix in NpmPrefixes())
        {
            yield return Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        }
    }

    private static IEnumerable<string> NpmPrefixes()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(appData)) { yield return Path.Combine(appData, "npm"); }
        if (!string.IsNullOrEmpty(localAppData)) { yield return Path.Combine(localAppData, "npm"); }
    }

    /// <summary>Installed CLI version from the npm package.json next to a node_modules binary
    /// (<c>bin/claude.exe</c> → <c>../package.json</c>). Returns <c>null</c> for non-npm installs
    /// (native/WinGet have no package.json) or when the field is missing.</summary>
    public static string Version()
    {
        var exe = ResolveExecutable();
        if (exe == null) { return null; }
        var pkg = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(exe)), "package.json");
        if (!File.Exists(pkg)) { return null; }
        try
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(pkg));
            return (string)json["version"];
        }
        catch { return null; }
    }

    /// <summary>WPF panel explaining the CLI is not installed, with a button opening the setup page.
    /// Shared by both panes; the caller assigns it to <c>Content</c> when <see cref="ResolveExecutable"/>
    /// is null.</summary>
    public static System.Windows.UIElement BuildMissingPanel()
    {
        var stack = new System.Windows.Controls.StackPanel
        {
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(24),
        };
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Claude Code CLI not found",
            FontSize = 16,
            FontWeight = System.Windows.FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        stack.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "See the official setup guide for installation options.",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = System.Windows.TextAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });
        var btn = new System.Windows.Controls.Button
        {
            Content = "Open setup instructions",
            Padding = new System.Windows.Thickness(12, 4, 12, 4),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        };
        btn.Click += (_, __) => Helpers.ShellHelpers.OpenExternal(DocsUrl);
        stack.Children.Add(btn);
        return stack;
    }
}
