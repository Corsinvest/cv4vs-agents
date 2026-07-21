/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Contracts;

// Wire DTOs for the Statistics dialog (chat_stats). Aggregated from the local .jsonl by
// StatsService — shape is stable, so we type it end-to-end.

/// <summary>Aggregation scope. Generated as a TS union (wire values lowercase).</summary>
public enum StatsScopeDto
{
    All,
    Project,
    Session,
}

/// <summary>Time range. Generated as a TS union ('all' | 'days30' | 'days7').</summary>
public enum StatsRangeDto
{
    All,
    Days30,
    Days7,
}

/// <summary>Per-model token breakdown row (the "Models" list + %).</summary>
public class StatsModelDto
{
    public string Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }
    public double Percentage { get; set; }
}

/// <summary>One calendar day's activity (heatmap).</summary>
public class StatsDayDto
{
    public string Date { get; set; }   // yyyy-MM-dd
    public int MessageCount { get; set; }
    public int SessionCount { get; set; }
    public int ToolCallCount { get; set; }
}

/// <summary>One day's per-model token totals (input+output) for the stacked bar chart.
/// TokensByModel: model name → tokens (dynamic keys → object, read as Record&lt;string,number&gt;).</summary>
public class StatsDayModelDto
{
    public string Date { get; set; }
    public object TokensByModel { get; set; }
}

/// <summary>A named tool + its call count (most-used tools).</summary>
public class StatsToolDto
{
    public string Name { get; set; }
    public int Count { get; set; }
}

/// <summary>The full stats response (chat_stats): totals + breakdowns for the current
/// scope+range.</summary>
public class StatsResponse
{
    // True while a full indexing pass is still running: the numbers below reflect only the
    // cache indexed so far (partial). The UI shows a loading bar and re-reads on stats_index_done.
    public bool Indexing { get; set; }
    public int TotalSessions { get; set; }
    public int TotalMessages { get; set; }
    public long TotalTokens { get; set; }
    public int ActiveDays { get; set; }
    public int CurrentStreak { get; set; }
    public int LongestStreak { get; set; }
    public int PeakHour { get; set; }          // 0-23, -1 if none
    public string FavoriteModel { get; set; }
    public int ImageCount { get; set; }
    public int FileCount { get; set; }
    public int SubagentSessions { get; set; }
    public long SubagentTokens { get; set; }
    public StatsModelDto[] ModelBreakdown { get; set; }
    public StatsDayDto[] DailyActivity { get; set; }
    public StatsDayModelDto[] DailyModelTokens { get; set; }
    public StatsToolDto[] TopTools { get; set; }
}
