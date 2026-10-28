/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What running git has to survive that git itself is not to blame for: a program it started that
/// outlives it. <c>cmd</c> stands for git here, as it exits at once and can leave a child behind.</summary>
public sealed class GitRunnerTests
{
    [Fact]
    public async Task What_the_program_printed_is_collected()
    {
        var runner = new GitRunner("cmd.exe", OutputWindowLogger.Global);

        var result = await runner.RunAsync(Environment.CurrentDirectory, new[] { "/c", "echo hello" });

        Assert.True(result.Ok);
        Assert.Equal("hello", result.Output.Trim());
    }

    [Fact]
    public async Task A_program_started_by_git_that_keeps_its_output_open_does_not_hold_the_result_for_ever()
    {
        var runner = new GitRunner("cmd.exe", OutputWindowLogger.Global, TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        // The child inherits the pipes and runs for a quarter of a minute after cmd has gone.
        var result = await runner.RunAsync(Environment.CurrentDirectory, new[] { "/c", "start /b ping -n 15 127.0.0.1 & exit 0" });

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), "took " + clock.Elapsed);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task A_program_that_runs_past_its_limit_is_killed_and_said_to_have_timed_out()
    {
        var runner = new GitRunner("cmd.exe", OutputWindowLogger.Global, TimeSpan.FromMilliseconds(500));

        var result = await runner.RunAsync(Environment.CurrentDirectory, new[] { "/c", "ping -n 30 127.0.0.1" }, timeout: TimeSpan.FromMilliseconds(400));

        Assert.True(result.TimedOut);
        Assert.False(result.Ok);
    }
}
