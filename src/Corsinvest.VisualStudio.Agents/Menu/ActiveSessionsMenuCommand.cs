/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Menu;

/// <summary>
/// View-menu entry point: one item per open pane, listed under an "Active sessions" submenu, to
/// bring that pane forward. VS's own Window list mixes tool windows in with every open document,
/// and the docked tab caption is stuck at "Chat 1"/"Chat 2" — VS derives it from the window name
/// and ignores every caption property — so with several panes open this is the only place their
/// full titles (session and profile) are visible together.
///
/// Same DynamicItemStart mechanism as <see cref="ProfilesMenuCommand"/>, minus its cache: the
/// entries come from PaneRegistry, which is already in memory, so VS's constant polling costs
/// nothing to serve.
/// </summary>
internal sealed class ActiveSessionsMenuCommand : OleMenuCommand
{
    private readonly int _baseId;

    private ActiveSessionsMenuCommand(CommandID rootId)
        : base(OnInvoke, changeHandler: null, OnBeforeQueryStatus, rootId)
    {
        _baseId = rootId.ID;
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        var rootId = new CommandID(PackageGuids.AgentsCommandSet, PackageIds.ActiveSessionCommandId);
        commandService?.AddCommand(new ActiveSessionsMenuCommand(rootId));
    }

    /// <summary>The open panes, in the order they were opened. Read live — no cache, unlike the
    /// profiles menu: this list changes whenever a pane opens or closes, and it costs a field read.</summary>
    private static IReadOnlyList<PaneEntry> Items() => [.. PaneRegistry.Instance.Entries];

    /// <summary>Matches the seed id plus the whole dynamic range (base..base+Count-1),
    /// which is how VS discovers how many entries to render in the submenu.</summary>
    public override bool DynamicItemMatch(int cmdId)
    {
        var count = Items().Count;
        if (cmdId < _baseId || cmdId >= _baseId + count) { return false; }
        // Required by the VS dynamic-item-range contract: a matched command must
        // record its own id here so BeforeQueryStatus/Invoke below can read it back.
        MatchedCommandId = cmdId;
        return true;
    }

    private static void OnBeforeQueryStatus(object sender, EventArgs e)
    {
        var cmd = (ActiveSessionsMenuCommand)sender;
        var items = Items();
        var index = GetIndex(cmd);

        if (index < items.Count)
        {
            cmd.Enabled = true;
            cmd.Visible = true;
            cmd.Text = items[index].Title;
        }
        else
        {
            cmd.Enabled = false;
            cmd.Visible = false;
        }

        // Reset so the next query re-derives MatchedCommandId from DynamicItemMatch
        // instead of reusing this call's id — without this, VS keeps re-querying the
        // same matched id and only the first item in the range ever gets shown.
        cmd.MatchedCommandId = 0;
    }

    private static void OnInvoke(object sender, EventArgs e)
    {
        var cmd = (ActiveSessionsMenuCommand)sender;
        var items = Items();
        var index = GetIndex(cmd);
        if (index >= 0 && index < items.Count) { PaneLauncher.Activate(items[index]); }
        cmd.MatchedCommandId = 0;
    }

    private static int GetIndex(ActiveSessionsMenuCommand cmd)
    {
        var matched = cmd.MatchedCommandId;
        return matched == 0 ? 0 : matched - cmd._baseId;
    }
}
