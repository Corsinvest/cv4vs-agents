/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Which hunk "next" means, kept apart from the editor types so it can be tested.</summary>
internal static class HunkNavigation
{
    /// <summary>The index of the first hunk that starts after <paramref name="position"/>, wrapping to the
    /// first one when there is none further on; -1 when there are no hunks. <paramref name="starts"/> are
    /// where the hunks begin, in the order they appear, in the same text the position is in.</summary>
    public static int NextAfter(IReadOnlyList<int> starts, int position)
    {
        if (starts is null || starts.Count == 0) { return -1; }
        for (var i = 0; i < starts.Count; i++)
        {
            if (starts[i] > position) { return i; }
        }
        return 0;
    }
}
