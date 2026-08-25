/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// <para>
/// Tells the user a newer Claude Code exists. Only tells: the CLI is not ours, and updating it
/// under a live session would break that session.
/// </para>
/// <para>
/// The remote truth is the npm registry's dist-tags — the address the user installs from, so it
/// does not move. The CLI's own updater also reads a GCS bucket holding a bare version string,
/// which is lighter, but that is an internal detail of their updater and can change with a
/// refactor of theirs; for a feature that only raises a notice it is not worth the coupling.
/// </para>
/// </summary>
internal static class ClaudeUpdateCheck
{
    /// <summary>Just the tags, tens of bytes — as opposed to `/latest`, which answers with the
    /// whole package.json to read one field out of it. No auth.</summary>
    private const string DistTagsUrl =
        "https://registry.npmjs.org/-/package/@anthropic-ai/claude-code/dist-tags";

    /// <summary>`latest` is what `npm i -g` installs. `stable` trails it — comparing against that
    /// one would announce an "update" to someone already ahead of it.</summary>
    private const string Tag = "latest";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Once per VS, on whichever chat opens first. Not persisted across restarts: the
    /// user can act on the notice at a moment of their choosing, and telling them again next time
    /// they start is the reminder. Not per pane either — the second chat of a session would be
    /// repeating itself.</summary>
    private static bool _told;

    /// <summary>The newer version to announce, or <c>null</c> when there is nothing to say —
    /// already current, already told this VS session, offline, unparseable.
    /// <para>Never throws: a version check must not be able to break the pane that awaits it.</para></summary>
    public static async Task<(string Latest, string Local)?> CheckAsync()
    {
        if (_told) { return null; }

        var local = ClaudeInstall.Version();
        if (string.IsNullOrEmpty(local)) { return null; }

        var latest = await FetchLatestAsync();
        if (string.IsNullOrEmpty(latest) || !IsNewer(latest, local)) { return null; }

        _told = true;
        return (latest, local);
    }

    private static async Task<string> FetchLatestAsync()
    {
        try
        {
            // VS runs on .NET Framework, whose default is still SSL3/TLS1.0 — the registry needs 1.2.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using var http = new HttpClient { Timeout = Timeout };
            // The registry asks callers to identify themselves; an anonymous one may be throttled.
            http.DefaultRequestHeaders.Add("User-Agent", $"{AppConstants.AppId}/{BuildInfo.Version}");
            var json = await http.GetStringAsync(DistTagsUrl);
            return (string)Newtonsoft.Json.Linq.JObject.Parse(json)[Tag];
        }
        catch (Exception ex)
        {
            // Offline, proxy, registry down: the user asked for a chat, not for this.
            OutputWindowLogger.Global.Warn($"[cli] update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>SemVer compare on the numeric release, which is all the registry publishes.
    /// A local build carries `+SHA` build metadata, which SemVer excludes from precedence — and
    /// anyone running one is not waiting to be told about npm.</summary>
    private static bool IsNewer(string latest, string local)
    {
        var a = Parse(latest);
        var b = Parse(local);
        if (a == null || b == null) { return false; }
        for (var i = 0; i < 3; i++)
        {
            if (a[i] != b[i]) { return a[i] > b[i]; }
        }
        return false;
    }

    private static int[] Parse(string version)
    {
        // Drop build metadata and pre-release before splitting: "2.1.245+abc" / "2.1.245-beta.1".
        var core = version.Split('+')[0].Split('-')[0].Split('.');
        if (core.Length < 3) { return null; }
        var parts = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(core[i], out parts[i])) { return null; }
        }
        return parts;
    }
}
