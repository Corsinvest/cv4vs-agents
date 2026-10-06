/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using System;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>Which chat a prompt sent from an editor menu lands in.
/// <para>The bug these pin down was silent: with two chats open "Add to chat" wrote into the one
/// opened last and brought it forward, whichever one was being worked in.</para></summary>
public class ActiveChatTrackerTests
{
    private static PaneEntry Pane(PaneKind kind, bool withComposer = true)
    {
        var entry = new PaneEntry(kind, new Profile { Name = "Claude" }, new PaneOptions(), @"C:\work");
        if (withComposer) { entry.SetComposerAction = _ => { }; }
        return entry;
    }

    private static readonly Func<PaneEntry, bool> HasComposer = e => e.SetComposerAction != null;

    [Fact]
    public void Nothing_activated_yet_falls_back_to_the_newest_chat()
    {
        var (a, b) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat));

        Assert.Same(b, new ActiveChatTracker().Pick([a, b], HasComposer));
    }

    [Fact]
    public void The_chat_in_use_wins_over_a_newer_one()
    {
        var (a, b) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat));
        var tracker = new ActiveChatTracker();

        tracker.Activated(a);

        Assert.Same(a, tracker.Pick([a, b], HasComposer));
    }

    [Fact]
    public void The_most_recent_activation_is_the_one_kept()
    {
        var (a, b) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat));
        var tracker = new ActiveChatTracker();

        tracker.Activated(b);
        tracker.Activated(a);

        Assert.Same(a, tracker.Pick([a, b], HasComposer));
    }

    [Fact]
    public void A_cli_pane_activated_afterwards_does_not_displace_the_chat()
    {
        var (a, b, cli) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat), Pane(PaneKind.Cli, withComposer: false));
        var tracker = new ActiveChatTracker();

        tracker.Activated(a);
        tracker.Activated(cli);

        Assert.Same(a, tracker.Pick([a, b], HasComposer));
    }

    [Fact]
    public void Closing_the_chat_in_use_falls_back_to_the_newest_left()
    {
        var (a, b, c) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat), Pane(PaneKind.Chat));
        var tracker = new ActiveChatTracker();

        tracker.Activated(a);
        tracker.Removed(a);

        Assert.Same(c, tracker.Pick([b, c], HasComposer));
    }

    [Fact]
    public void Closing_another_chat_keeps_the_one_in_use()
    {
        var (a, b) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat));
        var tracker = new ActiveChatTracker();

        tracker.Activated(a);
        tracker.Removed(b);

        Assert.Same(a, tracker.Pick([a], HasComposer));
    }

    [Fact]
    public void A_chat_in_use_that_cannot_take_the_prompt_is_passed_over()
    {
        // The composer action is set once the page has loaded: a chat clicked while still
        // initialising is the active one and has nowhere to put the text yet.
        var (a, b) = (Pane(PaneKind.Chat), Pane(PaneKind.Chat, withComposer: false));
        var tracker = new ActiveChatTracker();

        tracker.Activated(b);

        Assert.Same(a, tracker.Pick([a, b], HasComposer));
    }

    [Fact]
    public void No_chat_open_gives_nothing_so_the_caller_opens_one()
    {
        var tracker = new ActiveChatTracker();

        tracker.Activated(null);

        Assert.Null(tracker.Pick([], HasComposer));
    }
}
