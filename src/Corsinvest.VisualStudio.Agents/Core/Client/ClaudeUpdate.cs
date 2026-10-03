/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// <para>
/// Tells the user a newer Claude Code exists, and runs the CLI's own updater when asked. The CLI
/// is not ours: the update is `claude update`, whatever that does for the install at hand.
/// </para>
/// <para>
/// The remote truth is the npm registry's dist-tags: the address the user installs from, so it
/// does not move. The CLI's own updater also reads a GCS bucket holding a bare version string,
/// which is lighter, but that is an internal detail of their updater and can change with a
/// refactor of theirs; for a feature that only raises a notice it is not worth the coupling.
/// </para>
/// </summary>
internal static class ClaudeUpdate
{
    /// <summary>Just the tags, tens of bytes, as opposed to `/latest`, which answers with the
    /// whole package.json to read one field out of it. No auth.</summary>
    private const string DistTagsUrl =
        "https://registry.npmjs.org/-/package/@anthropic-ai/claude-code/dist-tags";

    /// <summary>`latest` is what `npm i -g` installs. `stable` trails it: comparing against that
    /// one would announce an "update" to someone already ahead of it.</summary>
    private const string Tag = "latest";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Once per VS, on whichever chat opens first. Not persisted across restarts: the
    /// user can act on the notice at a moment of their choosing, and telling them again next time
    /// they start is the reminder. Not per pane either: the second chat of a session would be
    /// repeating itself.</summary>
    private static bool _told;

    /// <summary>The newer version to announce, or <c>null</c> when there is nothing to say:
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

    private static readonly object RunGate = new();
    private static Task<ClaudeUpdateResult> _running;

    /// <summary>Raised when a run starts and when it ends, on a thread-pool thread: a subscriber
    /// that touches the WebView switches to the main thread itself.
    /// <para>Events rather than the return value alone: a chat showing the "is available" row has
    /// to follow a run started from the menu or from another pane, not only its own click.</para></summary>
    public static event Action Started;
    public static event Action<ClaudeUpdateResult> Completed;

    /// <summary>Runs `claude update` on the installed CLI. One run at a time: a call while one is
    /// under way gets that run's task, so two panes, or a pane and the menu, never start two.
    /// <para>Sessions already open are left alone. The updater renames the running binary and
    /// writes the new one beside it (seen on an npm install with ten sessions open), so they keep
    /// the old version until their process is started again.</para>
    /// <para>Never throws: every failure comes back as <see cref="ClaudeUpdateOutcome.Failed"/>.</para></summary>
    public static Task<ClaudeUpdateResult> RunAsync()
    {
        lock (RunGate)
        {
            return _running ??= Task.Run(RunCoreAsync);
        }
    }

    /// <summary>Whether a run is under way, for the places that offer to start one: a second
    /// start would only join it, so they show it as busy instead.</summary>
    public static bool IsRunning
    {
        get { lock (RunGate) { return _running != null; } }
    }

    private static async Task<ClaudeUpdateResult> RunCoreAsync()
    {
        ClaudeUpdateResult result;
        try
        {
            result = await ExecuteAsync();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("ClaudeUpdate.Run", ex);
            result = new ClaudeUpdateResult(ClaudeUpdateOutcome.Failed, null, null, [ex.Message]);
        }
        finally
        {
            lock (RunGate) { _running = null; }
        }

        if (result.Outcome is ClaudeUpdateOutcome.Failed or ClaudeUpdateOutcome.StillRunning)
        {
            OutputWindowLogger.Global.Warn($"[cli] update {result.Outcome} ({result.Before} -> {result.After}): {result.Detail}");
        }
        else
        {
            OutputWindowLogger.Global.Info($"[cli] update {result.Outcome}: {result.Before} -> {result.After}");
        }
        Raise(() => Completed?.Invoke(result));
        return result;
    }

    private static async Task<ClaudeUpdateResult> ExecuteAsync()
    {
        var exe = ClaudeInstall.ResolveExecutable();
        if (exe == null)
        {
            return new ClaudeUpdateResult(ClaudeUpdateOutcome.Failed, null, null, ["Claude Code was not found."]);
        }

        var before = ClaudeInstall.Version();
        OutputWindowLogger.Global.Info($"[cli] update starting: {exe} (now {before ?? "unknown"})");
        Raise(() => Started?.Invoke());

        var psi = new ProcessStartInfo(exe)
        {
            Arguments = "update",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        // A VS started from inside a Claude session would hand that session's identity on.
        foreach (var name in ClaudeInstall.InheritedSessionEnvVars)
        {
            psi.EnvironmentVariables.Remove(name);
        }

        int exitCode;
        bool timedOut;
        string stdout, stderr;
        using (var p = Process.Start(psi))
        {
            // Both pipes read while it runs: a process blocks once either one fills.
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            // Past the wait it is left running, not killed: Kill() reaches claude.exe alone, so
            // the npm it started would go on writing the package with nothing waiting for it, and
            // a native install could be stopped halfway through replacing the binary.
            timedOut = !p.WaitForExit((int)RunTimeout.TotalMilliseconds);
            // Bounded: a child of the updater can hold the pipes open after it has exited.
            if (!timedOut)
            {
                await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(TimeSpan.FromSeconds(5)));
            }
            stdout = outTask.IsCompleted ? await outTask : "";
            stderr = errTask.IsCompleted ? await errTask : "";
            exitCode = timedOut ? -1 : p.ExitCode;
        }
        OutputWindowLogger.Global.Debug(() => $"[cli] update exit={exitCode} timedOut={timedOut}\nstdout: {stdout}\nstderr: {stderr}");

        var after = ClaudeInstall.Version();
        if (after == null)
        {
            // Version() gives the binary five seconds, and the first start of a freshly written
            // executable of this size can take longer while the antivirus reads it.
            await Task.Delay(TimeSpan.FromSeconds(2));
            after = ClaudeInstall.Version();
        }
        return Classify(before, after, exitCode, timedOut, stdout, stderr);
    }

    /// <summary>A subscriber that throws must not turn a finished update into a failed one, nor
    /// keep the other subscribers from hearing about it.</summary>
    private static void Raise(Action raise)
    {
        try { raise(); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("ClaudeUpdate.Event", ex); }
    }

    private static async Task<string> FetchLatestAsync()
    {
        try
        {
            // VS runs on .NET Framework, whose default is still SSL3/TLS1.0: the registry needs 1.2.
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
    /// A local build carries `+SHA` build metadata, which SemVer excludes from precedence, and
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

    public const string RunningMessage = "Updating Claude Code...";

    /// <summary>The "is available" row, for the chat. Encoded like every other text sent to the
    /// notice stack, which renders HTML: both versions come from outside, one from the registry's
    /// answer and one from the CLI's output.</summary>
    internal static string AvailableChatMessage(string latest, string local)
        => WebUtility.HtmlEncode($"Claude Code {latest} is available (you have {local})");

    internal static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    // Room for the CLI's longest explanation; the cap is only against output that runs on.
    private const int MaxDetailLines = 15;
    private const int DetailLineLength = 200;

    // CSI sequences (colour, cursor): the CLI colours its messages when it thinks it has a terminal.
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static readonly char[] LineEnds = ['\r', '\n'];

    /// <summary>What happened, decided by the version read back rather than by the exit code: a
    /// package-manager install and a busy update lock both exit 0 without updating. The CLI's own
    /// lines go along as the explanation: they name the command to run, and parsing their wording
    /// to tell the cases apart would break on the next rewording.
    /// <para>The order of the checks is the rule: a version that changed wins over everything.</para></summary>
    internal static ClaudeUpdateResult Classify(string before, string after, int exitCode, bool timedOut, string stdout, string stderr)
    {
        var readable = !string.IsNullOrEmpty(before) && !string.IsNullOrEmpty(after);
        // Compared as text, not with IsNewer: the native installer follows its own channel and can
        // land below the npm tag, and "not updated (still X)" would then name a version it left.
        if (readable && !string.Equals(before, after, StringComparison.Ordinal))
        {
            return new ClaudeUpdateResult(ClaudeUpdateOutcome.Updated, before, after, []);
        }
        if (timedOut)
        {
            return new ClaudeUpdateResult(ClaudeUpdateOutcome.StillRunning, before, after, []);
        }
        if (exitCode != 0)
        {
            var err = CleanLines(stderr);
            return new ClaudeUpdateResult(ClaudeUpdateOutcome.Failed, before, after,
                err.Count > 0 ? err : CleanLines(stdout));
        }
        return new ClaudeUpdateResult(
            readable ? ClaudeUpdateOutcome.NotUpdated : ClaudeUpdateOutcome.Unknown,
            before, after, CleanLines(stdout));
    }

    /// <summary>The CLI's output as lines, in order: the message box shows them all, the chat one
    /// of them. A bare carriage return splits too, so a line rewritten in place is not one long
    /// line. Capped and cut: an npm failure can print many lines of several hundred characters.</summary>
    private static List<string> CleanLines(string output)
        => string.IsNullOrWhiteSpace(output)
            ? []
            : Ansi.Replace(output, "")
                .Split(LineEnds)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Select(l => l.Length > DetailLineLength ? l.Substring(0, DetailLineLength) : l)
                .Take(MaxDetailLines)
                .ToList();
}

internal enum ClaudeUpdateOutcome
{
    Updated,
    /// <summary>The CLI ran and the version is the same: already current, managed by a package
    /// manager, or another updater held the lock. Its own text says which.</summary>
    NotUpdated,
    /// <summary>The CLI ran, but no version could be read to compare. Not folded into
    /// <see cref="NotUpdated"/>: with nothing to compare, "was not updated" would be a guess.</summary>
    Unknown,
    /// <summary>The updater outlived the wait and was left to finish. Not a failure: nothing is
    /// known to have gone wrong, and the version may still change.</summary>
    StillRunning,
    Failed,
}

internal sealed class ClaudeUpdateResult(ClaudeUpdateOutcome outcome, string before, string after, IReadOnlyList<string> detailLines)
{
    public ClaudeUpdateOutcome Outcome { get; } = outcome;
    public string Before { get; } = before;
    public string After { get; } = after;

    /// <summary>What the CLI printed, cleaned, in its own order.</summary>
    public IReadOnlyList<string> DetailLines { get; } = detailLines ?? [];

    /// <summary>The one line of it the chat has room for. An error states its cause first and its
    /// hints after (seen: "Unable to fetch latest version from npm registry", then "Try:" and a
    /// list); a report ends with its conclusion ("is up to date", the command to run).</summary>
    public string Detail => DetailLines.Count == 0
        ? ""
        : Outcome == ClaudeUpdateOutcome.Failed ? DetailLines[0] : DetailLines[DetailLines.Count - 1];

    /// <summary>On one line, for the chat notice: its layout is a single row in a narrow pane.</summary>
    public string Message => Join(Join(Headline, Sentence(Detail), " "), Hint, " ");

    /// <summary>For the message box, which has room: the CLI's lines kept as it wrote them, each
    /// on its own line, under the headline.</summary>
    public string DialogMessage => Join(Join(Headline, string.Join("\n", DetailLines), "\n\n"), Hint, "\n\n");

    /// <summary>The chat text encoded: the notice stack renders its message as HTML, and the
    /// detail is whatever the CLI or npm printed.</summary>
    public string ChatMessage => WebUtility.HtmlEncode(Message);

    private string Headline => Outcome switch
    {
        ClaudeUpdateOutcome.Updated =>
            $"Claude Code updated to {After}. Sessions already open keep {Before} until they are restarted.",
        ClaudeUpdateOutcome.NotUpdated => $"Claude Code was not updated (still {After}).",
        ClaudeUpdateOutcome.Unknown => "Claude Code update finished, but its version could not be read.",
        ClaudeUpdateOutcome.StillRunning =>
            $"Claude Code update is still running after {ClaudeUpdate.RunTimeout.TotalMinutes:0} minutes. It was left to finish: check the version later.",
        _ => "Claude Code update failed.",
    };

    private string Hint => Outcome == ClaudeUpdateOutcome.Failed
        ? "Run `claude update` in a terminal to see the full output."
        : "";

    // A line of CLI output followed by more text needs its full stop, or the two run together.
    private static string Sentence(string text)
        => text.Length == 0 || ".!?:".IndexOf(text[text.Length - 1]) >= 0 ? text : text + ".";

    private static string Join(string first, string second, string separator)
        => string.IsNullOrEmpty(second) ? first : first + separator + second;
}
