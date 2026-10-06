/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using System.Collections.Generic;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Which chat is in front after the debugger starts or stops.
/// <para>Visual Studio keeps a front tab per layout, and swaps layouts on the way into and out of
/// design mode. Every test here is the shell putting another chat forward, and whether that is
/// undone.</para></summary>
public class FrontChatKeeperTests
{
    /// <summary>A tab group as the shell sees it: which chats are open, which one is in front,
    /// and a record of what the keeper asked to bring forward.</summary>
    private sealed class Group
    {
        public List<PaneEntry> Open = [];
        public PaneEntry Front;
        public List<PaneEntry> Brought = [];

        public PaneEntry Add()
        {
            var entry = new PaneEntry(PaneKind.Chat, new Profile { Name = "Claude" }, new PaneOptions(), @"C:\work");
            Open.Add(entry);
            return entry;
        }

        public FrontChatKeeper Keeper() => new(
            () => Open,
            entry => ReferenceEquals(entry, Front),
            entry => { Brought.Add(entry); Front = entry; });
    }

    private const bool Design = true;
    private const bool Debugging = false;

    [Fact]
    public void Starting_the_debugger_puts_back_the_chat_the_swap_displaced()
    {
        var group = new Group();
        var (a, b) = (group.Add(), group.Add());
        group.Front = a;
        var keeper = group.Keeper();

        keeper.ModeChanged(Debugging);
        group.Front = b;   // the run-time layout's own front tab
        keeper.Finish();

        Assert.Same(a, group.Front);
    }

    [Fact]
    public void Stopping_the_debugger_does_the_same_on_the_way_back()
    {
        var group = new Group();
        var (a, b) = (group.Add(), group.Add());
        group.Front = a;
        var keeper = group.Keeper();
        keeper.ModeChanged(Debugging);
        keeper.Finish();

        group.Front = b;   // picked by the user while debugging
        keeper.ModeChanged(Design);
        group.Front = a;   // the design layout's own front tab
        keeper.Finish();

        Assert.Same(b, group.Front);
    }

    [Fact]
    public void The_chat_comes_back_as_soon_as_the_swap_shows_another_not_only_when_it_is_over()
    {
        var group = new Group();
        var (a, b) = (group.Add(), group.Add());
        group.Front = a;
        var keeper = group.Keeper();

        keeper.ModeChanged(Debugging);
        group.Front = b;
        keeper.Reassert();

        Assert.Same(a, group.Front);
        Assert.True(keeper.Keeping);
    }

    [Fact]
    public void A_breakpoint_hit_during_the_swap_does_not_note_the_displaced_tab()
    {
        // Run to Break is no swap. Noting again here would record the chat the shell just put
        // forward, and the restore would then bring THAT one back.
        var group = new Group();
        var (a, b) = (group.Add(), group.Add());
        group.Front = a;
        var keeper = group.Keeper();

        keeper.ModeChanged(Debugging);
        group.Front = b;
        keeper.ModeChanged(Debugging);
        keeper.Finish();

        Assert.Same(a, group.Front);
    }

    [Fact]
    public void A_chat_left_in_front_by_the_swap_is_not_touched()
    {
        var group = new Group();
        var a = group.Add();
        group.Add();
        group.Front = a;
        var keeper = group.Keeper();

        keeper.ModeChanged(Debugging);
        keeper.Reassert();
        keeper.Finish();

        Assert.Empty(group.Brought);
    }

    [Fact]
    public void Once_the_swap_is_over_a_tab_the_user_picks_stays_picked()
    {
        var group = new Group();
        var (a, b) = (group.Add(), group.Add());
        group.Front = a;
        var keeper = group.Keeper();
        keeper.ModeChanged(Debugging);
        keeper.Finish();

        group.Front = b;
        keeper.Reassert();

        Assert.False(keeper.Keeping);
        Assert.Same(b, group.Front);
    }

    [Fact]
    public void A_chat_closed_during_the_swap_is_not_brought_back()
    {
        var group = new Group();
        var (a, b) = (group.Add(), group.Add());
        group.Front = a;
        var keeper = group.Keeper();

        keeper.ModeChanged(Debugging);
        group.Open.Remove(a);
        group.Front = b;
        keeper.Finish();

        Assert.Empty(group.Brought);
    }

    [Fact]
    public void No_chat_in_front_leaves_nothing_to_keep()
    {
        // The chats sit behind another tool window: the swap can do what it likes with them.
        var group = new Group();
        group.Add();
        var keeper = group.Keeper();

        keeper.ModeChanged(Debugging);

        Assert.False(keeper.Keeping);
    }

    [Fact]
    public void The_mode_is_followed_with_no_chat_open_so_one_opened_while_debugging_is_kept_on_stop()
    {
        var group = new Group();
        var keeper = group.Keeper();
        keeper.ModeChanged(Debugging);
        keeper.Finish();

        var a = group.Add();
        var b = group.Add();
        group.Front = a;
        keeper.ModeChanged(Design);
        group.Front = b;
        keeper.Finish();

        Assert.Same(a, group.Front);
    }
}
