/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
using Corsinvest.VisualStudio.Agents.Contracts;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Plugins;

/// <summary>
/// Runs <c>claude plugin &lt;subcommand&gt; --json</c> as a one-shot headless process and parses
/// the output. Plugins are global (~/.claude), so no session/control-protocol is involved: the
/// live chat process rejects plugin ops (verified: <c>list_plugins</c>/<c>install_plugin</c> as a
/// control_request return "Unsupported control request subtype" — only <c>reload_plugins</c> is
/// accepted). This mirrors how VS Code's extension manages plugins (spawn per action). Success is
/// detected by exit code 0 (✔); failure by exit code 1 (✘) — the CLI never emits JSON on error.
/// Analogous to <see cref="Sessions.SessionManager"/>: a static service reading data from outside
/// the protocol.
/// </summary>
internal static class PluginService
{
    // marketplace add clones a git repo (CLI's own timeout is 120s); allow headroom.
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);

    /// <summary>Installed + available plugins (<c>plugin list --available --json</c>), mapped to
    /// DTOs. Empty arrays on failure.</summary>
    public static async Task<(PluginDto[] installed, AvailablePluginDto[] available)> ListAsync()
    {
        var (ok, stdout, _) = await RunRawAsync("plugin", "list", "--available", "--json");
        if (!ok || !(ExtractJson(stdout) is JObject root))
        {
            OutputWindowLogger.Debug(() => "[plugins] list failed (process error or unparseable JSON) — returning empty");
            return (Array.Empty<PluginDto>(), Array.Empty<AvailablePluginDto>());
        }
        var installed = (root["installed"] as JArray ?? new JArray()).Select(MapInstalled).ToArray();
        var available = (root["available"] as JArray ?? new JArray()).Select(MapAvailable).ToArray();
        return (installed, available);
    }

    /// <summary>Configured marketplaces (<c>plugin marketplace list --json</c>), mapped to DTOs.</summary>
    public static async Task<MarketplaceDto[]> MarketplaceListAsync()
    {
        var (ok, stdout, _) = await RunRawAsync("plugin", "marketplace", "list", "--json");
        var arr = (ok ? ExtractJson(stdout) as JArray : null) ?? new JArray();
        return [.. arr.Select(MapMarketplace)];
    }

    // The installed list carries only the fused id "name@marketplace" — split on the LAST '@'
    // (marketplace names can't contain '@', plugin names shouldn't, but be defensive).
    private static PluginDto MapInstalled(JToken t)
    {
        var id = (string)t["id"] ?? "";
        var at = id.LastIndexOf('@');
        return new PluginDto
        {
            Id = id,
            Name = at > 0 ? id.Substring(0, at) : id,
            Marketplace = at > 0 ? id.Substring(at + 1) : "",
            Version = (string)t["version"] ?? "",
            Scope = (string)t["scope"] ?? "",
            Enabled = (bool?)t["enabled"] ?? false,
            InstalledAt = (string)t["installedAt"] ?? "",
            LastUpdated = (string)t["lastUpdated"] ?? "",
        };
    }

    private static AvailablePluginDto MapAvailable(JToken t)
    {
        // `source` is either an object {source,url,path,…} (remote/git-subdir/url plugins) or a
        // bare "./path" string (in-repo plugins). Classify it so the WebView knows whether to use
        // Source as a URL directly, or join the relative path onto the marketplace repo.
        var (kind, source) = ClassifySource(t["source"]);
        return new AvailablePluginDto
        {
            PluginId = (string)t["pluginId"] ?? "",
            Name = (string)t["name"] ?? "",
            Description = (string)t["description"] ?? "",
            MarketplaceName = (string)t["marketplaceName"] ?? "",
            Version = (string)t["version"] ?? "",
            InstallCount = (int?)t["installCount"] ?? 0,
            SourceKind = kind,
            Source = source,
        };
    }

    // Classify an available plugin's `source` (verified over all 255: dict{url|repo,+path for
    // git-subdir} or a bare "./path" string) into a kind + a ready-to-open URL (or the raw relative
    // path for the Relative kind, which the WebView joins onto the marketplace).
    private static (PluginSourceKindDto kind, string source) ClassifySource(JToken src)
    {
        if (src is JObject o)
        {
            // github sources carry `repo` (owner/name) instead of `url`; the rest carry `url`.
            var url = (string)o["url"];
            if (string.IsNullOrEmpty(url))
            {
                var repo = (string)o["repo"];
                url = string.IsNullOrEmpty(repo) ? null : $"https://github.com/{repo}";
            }
            if (string.IsNullOrEmpty(url)) { return (PluginSourceKindDto.None, ""); }
            // Normalize a git repo URL to a browsable https tree; git-subdir points at `path` inside.
            var https = NormalizeGitUrl(url);
            var path = (string)o["path"];
            var full = string.IsNullOrEmpty(path) ? https : $"{https}/tree/main/{path}";
            return (PluginSourceKindDto.Url, full);
        }
        var s = (string)src;
        if (string.IsNullOrEmpty(s)) { return (PluginSourceKindDto.None, ""); }
        return s.StartsWith("http") ? (PluginSourceKindDto.Url, s) : (PluginSourceKindDto.Relative, s);
    }

    // "https://github.com/owner/repo.git" → "https://github.com/owner/repo" (browsable).
    private static string NormalizeGitUrl(string url)
        => url.EndsWith(".git") ? url.Substring(0, url.Length - 4) : url;

    private static MarketplaceDto MapMarketplace(JToken t) => new MarketplaceDto
    {
        Name = (string)t["name"] ?? "",
        Source = (string)t["source"] ?? "",
        Location = (string)t["installLocation"] ?? "",
        Url = (string)t["url"],
        Repo = (string)t["repo"],
        Path = (string)t["path"],
    };

    /// <summary>Run an action subcommand (install/uninstall/enable/disable/marketplace add|remove).
    /// <c>ok</c> = exit code 0; <c>message</c> = the ✔/✘ line for the UI.</summary>
    public static async Task<(bool ok, string message)> RunOpAsync(params string[] args)
    {
        var (ok, stdout, stderr) = await RunRawAsync(args);
        var source = ok ? stdout : (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        return (ok, LastMeaningfulLine(source));
    }

    // The --json output is a single JSON value, but action commands print progress lines around
    // it. Find the first '[' or '{' that parses to the end — that's the JSON payload.
    private static JToken ExtractJson(string stdout)
    {
        var s = stdout?.TrimEnd();
        if (string.IsNullOrEmpty(s)) { return null; }
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '[' && s[i] != '{') { continue; }
            try { return JToken.Parse(s.Substring(i)); } catch { /* keep scanning */ }
        }
        return null;
    }

    // Quote an argument for CreateProcess (CommandLineToArgvW rules): wrap in quotes if it has
    // whitespace/quotes, escaping backslashes-before-quote and inner quotes. Simple args pass through.
    private static string QuoteArg(string arg)
    {
        if (!string.IsNullOrEmpty(arg) && arg.IndexOfAny([' ', '\t', '"']) < 0) { return arg; }
        var sb = new System.Text.StringBuilder("\"");
        for (int i = 0; i < arg.Length; i++)
        {
            int backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }
            if (i == arg.Length) { sb.Append('\\', backslashes * 2); break; }
            if (arg[i] == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); }
            else { sb.Append('\\', backslashes).Append(arg[i]); }
        }
        return sb.Append('"').ToString();
    }

    private static string LastMeaningfulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return ""; }
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var t = lines[i].Trim();
            if (t.Length > 0) { return t; }
        }
        return "";
    }

    private static Task<(bool ok, string stdout, string stderr)> RunRawAsync(params string[] args)
    {
        var exe = Core.Client.ClaudeInstall.ResolveExecutable();
        if (exe == null) { OutputWindowLogger.Warn("[plugins] claude.exe not found — plugin operations unavailable"); return Task.FromResult((false, "", "claude.exe not found")); }
        // Plugins are global, so CWD doesn't affect the result — just need a valid one.
        var cwd = AgentsPackage.Instance?.CurrentSolutionFolder
                  ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo(exe)
            {
                // .NET Framework 4.8 has no ProcessStartInfo.ArgumentList — build the string,
                // quoting each arg (plugin ids/sources/paths may contain spaces or special chars).
                Arguments = string.Join(" ", args.Select(QuoteArg)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // The CLI emits UTF-8 (✔/✘, emoji); without this the default console codepage
                // mangles them (✔ → "âˆš"). Read both streams as UTF-8.
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = cwd,
            };
            using var p = Process.Start(psi);
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)RunTimeout.TotalMilliseconds))
            {
                OutputWindowLogger.Warn("[plugins] operation timed out — killing the process");
                try { p.Kill(); } catch { /* already gone */ }
                return (false, "", "timeout");
            }
            return (p.ExitCode == 0, outTask.Result, errTask.Result);
        });
    }
}
