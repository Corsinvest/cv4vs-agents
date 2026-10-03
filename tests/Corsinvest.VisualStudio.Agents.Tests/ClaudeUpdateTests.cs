/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What the user is told after `claude update`. The exit code is not the answer: a
/// package-manager install and a busy update lock both exit 0 without updating, so the version
/// read back decides and the CLI's own words explain.</summary>
public class ClaudeUpdateTests
{
    private static ClaudeUpdateResult Run(string before, string after, int exitCode, string stdout, string stderr)
        => ClaudeUpdate.Classify(before, after, exitCode, timedOut: false, stdout, stderr);

    [Fact]
    public void NewerVersion_IsUpdated()
    {
        var r = Run("2.1.286", "2.1.287", 0, "Successfully updated", "");
        Assert.Equal(ClaudeUpdateOutcome.Updated, r.Outcome);
        Assert.Equal("Claude Code updated to 2.1.287. Sessions already open keep 2.1.286 until they are restarted.", r.Message);
    }

    [Fact]
    public void ChangedVersion_WithFailingExitCode_IsStillUpdated()
        => Assert.Equal(ClaudeUpdateOutcome.Updated, Run("2.1.286", "2.1.287", 1, "", "warning").Outcome);

    [Fact]
    public void LowerVersion_IsUpdated_NotReportedAsUnchanged()
    {
        var r = Run("2.1.287", "2.1.280", 0, "", "");
        Assert.Equal(ClaudeUpdateOutcome.Updated, r.Outcome);
        Assert.Contains("updated to 2.1.280", r.Message);
    }

    [Fact]
    public void BuildMetadata_IsComparedAsText()
        => Assert.Equal(ClaudeUpdateOutcome.Updated, Run("2.1.287+abc", "2.1.287+def", 0, "", "").Outcome);

    [Fact]
    public void SameVersion_ExitZero_IsNotUpdated_WithTheCliExplanation()
    {
        var stdout = "\nClaude is managed by winget.\nUpdate available: 2.1.286 -> 2.1.287\n\nTo update, run:\n  winget upgrade Anthropic.ClaudeCode\n";
        var r = Run("2.1.286", "2.1.286", 0, stdout, "");
        Assert.Equal(ClaudeUpdateOutcome.NotUpdated, r.Outcome);
        Assert.Equal("Claude Code was not updated (still 2.1.286). winget upgrade Anthropic.ClaudeCode.", r.Message);
        Assert.Equal(
            "Claude Code was not updated (still 2.1.286).\n\nClaude is managed by winget.\nUpdate available: 2.1.286 -> 2.1.287\nTo update, run:\nwinget upgrade Anthropic.ClaudeCode",
            r.DialogMessage);
    }

    /// <summary>Seen in the Exp instance: the CLI states the cause first and then a list of hints.
    /// Keeping the last lines kept only the hints, and the dialog never said what went wrong.</summary>
    [Fact]
    public void Failure_KeepsTheCauseFirst_AndTheDialogShowsEveryLine()
    {
        var stderr = "Unable to fetch latest version from npm registry\n\nPossible causes:\n  \u2022 Network connectivity issues\n\nTry:\n  \u2022 Run with --debug flag for more details\n  \u2022 Check if you need to login: npm whoami\n";
        var r = Run("2.1.288", "2.1.288", 1, "", stderr);
        Assert.Equal(ClaudeUpdateOutcome.Failed, r.Outcome);
        Assert.Equal(
            "Claude Code update failed. Unable to fetch latest version from npm registry. Run `claude update` in a terminal to see the full output.",
            r.Message);
        Assert.Equal(
            "Claude Code update failed.\n\nUnable to fetch latest version from npm registry\nPossible causes:\n\u2022 Network connectivity issues\nTry:\n\u2022 Run with --debug flag for more details\n\u2022 Check if you need to login: npm whoami\n\nRun `claude update` in a terminal to see the full output.",
            r.DialogMessage);
    }

    [Fact]
    public void UpToDate_SummarisesWithTheLastLine()
    {
        var stdout = "Current version: 2.1.288\r\nChecking for updates to latest version...\r\nClaude Code is up to date (2.1.288)\r\n";
        var r = Run("2.1.288", "2.1.288", 0, stdout, "");
        Assert.Equal("Claude Code was not updated (still 2.1.288). Claude Code is up to date (2.1.288).", r.Message);
    }

    [Fact]
    public void Dialog_KeepsTheFirstFifteenLines()
    {
        var stderr = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line{i}"));
        var r = Run("2.1.288", "2.1.288", 1, "", stderr);
        Assert.Contains("line15", r.DialogMessage);
        Assert.DoesNotContain("line16", r.DialogMessage);
    }

    [Fact]
    public void SameVersion_FailingExitCode_IsFailed_WithStderr()
    {
        var r = Run("2.1.286", "2.1.286", 1, "Using global installation update method...", "Error: Failed to install update");
        Assert.Equal(ClaudeUpdateOutcome.Failed, r.Outcome);
        Assert.Equal(
            "Claude Code update failed. Error: Failed to install update. Run `claude update` in a terminal to see the full output.",
            r.Message);
    }

    [Fact]
    public void FailingExitCode_WithEmptyStderr_FallsBackToStdout()
        => Assert.Contains("npm error code EPERM", Run("2.1.286", "2.1.286", 1, "npm error code EPERM", "").Message);

    /// <summary>Past the wait the updater is left alone, not killed: the kill would reach only
    /// claude.exe and leave the npm it started writing the package, or stop a native install
    /// halfway through replacing the binary. So this is not a failure, and must not read as one.</summary>
    [Fact]
    public void TimedOut_IsStillRunning_NotFailed()
    {
        var r = ClaudeUpdate.Classify("2.1.286", "2.1.286", exitCode: -1, timedOut: true, stdout: "", stderr: "");
        Assert.Equal(ClaudeUpdateOutcome.StillRunning, r.Outcome);
        Assert.Equal(
            "Claude Code update is still running after 5 minutes. It was left to finish: check the version later.",
            r.Message);
    }

    /// <summary>The "is available" row goes through the same HTML rendering as the outcome, and
    /// its two versions come from outside: the registry's answer and the CLI's own output.</summary>
    [Fact]
    public void AvailableChatMessage_EncodesBothVersions()
    {
        var text = ClaudeUpdate.AvailableChatMessage("9.9.9-<img src=x onerror=alert(1)>", "2.1.286<b>");
        Assert.DoesNotContain("<img", text);
        Assert.DoesNotContain("<b>", text);
        Assert.Equal(
            "Claude Code 9.9.9-&lt;img src=x onerror=alert(1)&gt; is available (you have 2.1.286&lt;b&gt;)",
            text);
    }

    [Theory]
    [InlineData(null, "2.1.287")]
    [InlineData("2.1.286", null)]
    [InlineData(null, null)]
    public void UnreadableVersion_ExitZero_IsUnknown(string before, string after)
    {
        var r = Run(before, after, 0, "done", "");
        Assert.Equal(ClaudeUpdateOutcome.Unknown, r.Outcome);
        Assert.Equal("Claude Code update finished, but its version could not be read. done.", r.Message);
    }

    [Fact]
    public void UnreadableVersion_FailingExitCode_IsFailed()
        => Assert.Equal(ClaudeUpdateOutcome.Failed, Run("2.1.286", null, 1, "", "boom").Outcome);

    [Fact]
    public void NoOutput_LeavesACleanSentence()
    {
        Assert.Equal("Claude Code was not updated (still 2.1.286).", Run("2.1.286", "2.1.286", 0, "  \n\n", "").Message);
        Assert.Equal(
            "Claude Code update failed. Run `claude update` in a terminal to see the full output.",
            Run("2.1.286", "2.1.286", 1, "", "").Message);
    }

    [Fact]
    public void DetailLines_StripAnsi_SplitOnAnyLineEnd_AndCutLongOnes()
    {
        var longLine = new string('x', 500);
        var stdout = "one\r\ntwo\rthree \u001b[32mgreen\u001b[0m\n\n" + longLine + "\nfive\n";
        var r = Run("2.1.286", "2.1.286", 0, stdout, "");
        Assert.Equal(new[] { "one", "two", "three green", new string('x', 200), "five" }, r.DetailLines);
    }

    [Fact]
    public void ChatMessage_IsHtmlEncoded_MessageIsNot()
    {
        var r = Run("2.1.286", "2.1.286", 1, "", "npm error <https://registry.npmjs.org/> & co");
        Assert.Contains("<https://registry.npmjs.org/> & co", r.Message);
        Assert.Contains("&lt;https://registry.npmjs.org/&gt; &amp; co", r.ChatMessage);
        Assert.DoesNotContain("<https", r.ChatMessage);
    }
}
