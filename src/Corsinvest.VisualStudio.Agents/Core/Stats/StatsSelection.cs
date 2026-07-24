/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>A selection made in the stats tree — what a clicked node asks BuildResponse to
/// aggregate. Profile is null only for the cross-profile All scope.</summary>
internal sealed class StatsSelection
{
    public StatsScope Scope { get; set; }
    public Profile Profile { get; set; }
    // Folder: every project dir beneath it. Project/Day/Session use ProjectDir.
    public System.Collections.Generic.List<string> ProjectDirs { get; set; }
    public string ProjectDir { get; set; }
    // Day: the calendar day (yyyy-MM-dd) — aggregates the REAL per-day tokens across the project's
    // sessions (a multi-day session contributes only that day's slice), so it matches the chart.
    public string Date { get; set; }
    // Session: the single session id (aggregates the whole file, across all its days).
    public System.Collections.Generic.List<string> SessionIds { get; set; }
}
