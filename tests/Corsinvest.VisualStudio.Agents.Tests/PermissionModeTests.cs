/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Options;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What the Options choice becomes on the CLI's command line.
/// <para>Default and Manual differ only by a flag that is there or not, and a session started in
/// the wrong one shows nothing: it just asks, or does not ask, before editing.</para></summary>
public class PermissionModeTests
{
    [Fact]
    public void Default_leaves_the_mode_to_the_cli()
    {
        Assert.Null(PermissionMode.FromInitial(InitialPermissionMode.Default, false));
        Assert.Equal("", PermissionMode.LaunchArg(null));
    }

    [Fact]
    public void Manual_is_forced_whatever_settings_json_says()
    {
        var mode = PermissionMode.FromInitial(InitialPermissionMode.Manual, false);
        Assert.Equal("default", mode);
        Assert.Equal(" --permission-mode default", PermissionMode.LaunchArg(mode));
    }

    [Theory]
    [InlineData(InitialPermissionMode.AcceptEdits, "acceptEdits")]
    [InlineData(InitialPermissionMode.Plan, "plan")]
    public void The_other_modes_go_out_under_their_wire_name(InitialPermissionMode option, string wire)
        => Assert.Equal(wire, PermissionMode.FromInitial(option, false));

    [Fact]
    public void Bypass_needs_the_allow_option_and_falls_back_to_Manual_without_it()
    {
        Assert.Equal("bypassPermissions", PermissionMode.FromInitial(InitialPermissionMode.BypassPermissions, true));
        // Manual, not null: the CLI's own default could be a mode this pane must not start in.
        Assert.Equal("default", PermissionMode.FromInitial(InitialPermissionMode.BypassPermissions, false));
    }

    [Theory]
    [InlineData("acceptEdits")]
    [InlineData("plan")]
    [InlineData("auto")]
    [InlineData("dontAsk")]
    [InlineData("bypassPermissions")]
    public void LaunchArg_passes_a_mode_the_cli_knows(string mode)
        => Assert.Equal(" --permission-mode " + mode, PermissionMode.LaunchArg(mode));

    [Theory]
    // Read back from a .jsonl: the CLI exits on a mode it doesn't know.
    [InlineData("somethingNew")]
    [InlineData("")]
    [InlineData("plan --dangerously-skip-permissions")]
    public void LaunchArg_starts_in_Manual_on_anything_else(string mode)
        => Assert.Equal(" --permission-mode default", PermissionMode.LaunchArg(mode));
}
