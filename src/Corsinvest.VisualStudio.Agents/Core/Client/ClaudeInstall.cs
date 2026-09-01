/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Options;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    /// <summary>Session-identity variables to strip from a CLI we launch. A child starts from a
    /// copy of OUR environment, and ours is not always clean: start Visual Studio from inside a
    /// Claude Code session — which is how anyone working on this extension starts it — and VS
    /// inherits that session's identity, then hands it to every claude.exe it spawns. The child
    /// would read the markers of a conversation it has no part in.
    /// <para>Shared by both launch paths. <c>CLAUDE_CODE_ENTRYPOINT</c> is deliberately absent:
    /// both paths assign it straight after, and dropping it first would only be noise.</para>
    /// </summary>
    public static readonly string[] InheritedSessionEnvVars =
    [
        "CLAUDECODE",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_BRIDGE_SESSION_ID",
        "CLAUDE_CODE_MESSAGING_SOCKET",
        "CLAUDE_CODE_MESSAGING_TOKEN",
        "CLAUDE_PID",
    ];

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
                OutputWindowLogger.Global.Warn($"[cli] configured Claude executable must be an .exe (a .cmd/.bat/.ps1 shim can't be launched): {configured} — falling back to auto-detection");
            }
            else if (File.Exists(configured)) { return configured; }
            else
            {
                OutputWindowLogger.Global.Warn($"[cli] configured Claude executable not found: {configured} — falling back to auto-detection");
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

    /// <summary><para>Installed CLI version, or <c>null</c> if the binary can't be found or won't say.</para>
    /// <para>
    /// Asks the CLI rather than reading the npm <c>package.json</c> next to it: that manifest only
    /// exists for an npm install, so the native installer, WinGet and a hand-set
    /// <c>ClaudeExecutablePath</c> all came back "(unknown)". <c>--version</c> is a documented
    /// flag and answers whatever the layout. Costs a process start, and the one caller is the
    /// session-info dialog, opened by hand — no reason to cache it.
    /// </para></summary>
    public static string Version()
    {
        var exe = ResolveExecutable();
        if (exe == null) { return null; }
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { /* already gone */ }
                OutputWindowLogger.Global.Warn("[cli] `claude --version` timed out");
                return null;
            }
            // "2.1.245 (Claude Code)" — the version is the first token.
            var first = stdout.Result?.Trim().Split(' ')[0];
            return string.IsNullOrEmpty(first) ? null : first;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[cli] could not read the CLI version from {exe}: {ex.Message}");
            return null;
        }
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
