/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Workspace;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Which saved panes a restore opens when some are already there.
/// <para>Getting it wrong is not a missing pane but one too many: a second chat on a session
/// another claude.exe is already writing, or a fresh chat for every reload.</para></summary>
public class RestorePlanTests
{
    private static PaneState Chat(string sessionId = null) => new() { Kind = "Chat", Profile = "Claude", SessionId = sessionId };
    private static PaneState Cli(string sessionId = null) => new() { Kind = "Cli", Profile = "Claude", SessionId = sessionId };

    private static string[] Opened(PaneState[] saved, PaneState[] open)
        => [.. RestorePlan.ToOpen(saved, open).Select(p => $"{p.Kind}:{p.SessionId ?? "-"}")];

    [Fact]
    public void Nothing_open_restores_everything_in_saved_order()
        => Assert.Equal(["Chat:a", "Cli:b", "Chat:-"], Opened([Chat("a"), Cli("b"), Chat()], []));

    [Fact]
    public void A_session_still_open_after_a_reload_is_not_opened_again()
        => Assert.Equal(["Chat:b"], Opened([Chat("a"), Chat("b")], [Chat("a")]));

    [Fact]
    public void Session_ids_match_whatever_their_case()
        => Assert.Empty(Opened([Chat("ABC")], [Chat("abc")]));

    [Fact]
    public void A_fresh_chat_still_open_after_a_reload_is_not_doubled()
        => Assert.Empty(Opened([Chat()], [Chat()]));

    [Fact]
    public void Each_open_fresh_chat_accounts_for_one_saved()
        => Assert.Equal(["Chat:-"], Opened([Chat(), Chat()], [Chat()]));

    [Fact]
    public void A_fresh_cli_pane_does_not_stand_in_for_a_fresh_chat()
        => Assert.Equal(["Chat:-"], Opened([Chat()], [Cli()]));

    [Fact]
    public void A_chat_on_a_session_does_not_stand_in_for_a_fresh_one()
        => Assert.Equal(["Chat:-"], Opened([Chat()], [Chat("a")]));

    [Fact]
    public void A_session_saved_twice_is_opened_once()
        => Assert.Equal(["Chat:a"], Opened([Chat("a"), Chat("a")], []));

    [Fact]
    public void A_session_saved_twice_and_open_once_is_left_alone()
        => Assert.Empty(Opened([Chat("a"), Chat("a")], [Chat("a")]));

    [Fact]
    public void Asking_twice_opens_nothing_the_second_time()
    {
        PaneState[] saved = [Chat("a"), Cli("b"), Chat(), Chat()];

        var first = RestorePlan.ToOpen(saved, []);

        Assert.Empty(RestorePlan.ToOpen(saved, first));
    }
}
