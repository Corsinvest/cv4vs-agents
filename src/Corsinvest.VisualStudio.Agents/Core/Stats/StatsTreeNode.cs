/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>One node of the stats navigation tree (All → Profile → Project → Session). Every node
/// is clickable: its <see cref="Selection"/> is what BuildResponse aggregates for that level.</summary>
/// <summary>Overrides a node's icon when its scope alone isn't enough — the "Days"/"Sessions"
/// grouping nodes both carry the Project scope but want distinct icons.</summary>
internal enum StatsNodeKind { Default, DaysGroup, SessionsGroup }

internal sealed class StatsTreeNode
{
    public string Label { get; set; }
    // Hover tooltip (null = none). Project nodes carry the full working-directory path here.
    public string Tooltip { get; set; }
    public StatsNodeKind Kind { get; set; }
    public StatsSelection Selection { get; set; }
    public List<StatsTreeNode> Children { get; } = new();

    // Bound to the TreeViewItem (ItemContainerStyle) so the code-behind can expand/select a node.
    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }
}
