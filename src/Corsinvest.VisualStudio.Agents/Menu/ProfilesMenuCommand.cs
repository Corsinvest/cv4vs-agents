/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Menu;

/// <summary>
/// View-menu entry point: an always-present "cv4vs Agents" submenu listing one item per
/// enabled profile (Options → Profiles) in the user's saved order — the native "Claude" profile
/// prepended by ProfileStore.WithNative is one of them. A single submenu + DynamicItemStart range
/// avoids the fragile button↔submenu visibility toggle.
/// </summary>
internal sealed class ProfilesMenuCommand : OleMenuCommand
{
    private readonly int _baseId;

    private ProfilesMenuCommand(CommandID rootId)
        : base(OnInvoke, changeHandler: null, OnBeforeQueryStatus, rootId)
    {
        _baseId = rootId.ID;
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;

        var rootId = new CommandID(PackageGuids.AgentsCommandSet, PackageIds.ShowToolWindowCommandId);
        commandService.AddCommand(new ProfilesMenuCommand(rootId));

        // VS polls this DynamicVisibility command constantly (idle loop), so re-reading profiles.json
        // on every query would hammer the disk. Cache the list; drop the cache when the user saves
        // Options (the only way profiles change). Named handler so the package can -= it on dispose.
        AgentsOptions.Applied += InvalidateCache;
    }

    /// <summary>Drop the cached profile list (package dispose unsubscribes this from Options.Applied).</summary>
    internal static void InvalidateCache() => _items = null;

    private static IReadOnlyList<Profile> _items;

    /// <summary>Submenu entries: the enabled profiles (native "Claude" prepended) in saved order.
    /// Cached — VS's constant polling must not re-read the file each time (see InitializeAsync).</summary>
    private static IReadOnlyList<Profile> Items() => _items ??= ProfileStore.Load(forEdit: false);

    private static PaneKind DefaultKind() =>
        AgentsOptions.General.DefaultNewSession == NewSessionKind.Cli ? PaneKind.Cli : PaneKind.Chat;

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
        var cmd = (ProfilesMenuCommand)sender;
        var items = Items();
        var index = GetIndex(cmd);

        if (index < items.Count)
        {
            cmd.Enabled = true;
            cmd.Visible = true;
            cmd.Text = items[index].Name;
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
        var cmd = (ProfilesMenuCommand)sender;
        var items = Items();
        var index = GetIndex(cmd);
        var profile = index >= 0 && index < items.Count ? items[index] : null;
        PaneLauncher.OpenNew(DefaultKind(), profile);
        cmd.MatchedCommandId = 0;
    }

    private static int GetIndex(ProfilesMenuCommand cmd)
    {
        var matched = cmd.MatchedCommandId;
        return matched == 0 ? 0 : matched - cmd._baseId;
    }
}
