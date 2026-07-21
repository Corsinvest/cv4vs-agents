/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

// Internal aggregation model (NOT the wire DTO). Mirrors the CLI's stats.ts aggregation:
// tokens per model, per-day activity, sessions/messages — computed from the local .jsonl.

/// <summary>Token totals for one model.</summary>
internal sealed class ModelTokens
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    public long Total => InputTokens + OutputTokens;

    public void Add(ModelTokens other)
    {
        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CacheReadTokens += other.CacheReadTokens;
        CacheCreationTokens += other.CacheCreationTokens;
    }
}

/// <summary>Activity on a single calendar day (heatmap + per-model tokens for the chart).</summary>
internal sealed class DayActivity
{
    public int MessageCount { get; set; }
    public int SessionCount { get; set; }
    public int ToolCallCount { get; set; }
    // Per-model token total (input+output) for this day — feeds the stacked bar chart.
    public Dictionary<string, long> TokensByModel { get; } = new();

    public void Add(DayActivity other)
    {
        MessageCount += other.MessageCount;
        SessionCount += other.SessionCount;
        ToolCallCount += other.ToolCallCount;
        foreach (var kv in other.TokensByModel)
        {
            TokensByModel.TryGetValue(kv.Key, out var cur);
            TokensByModel[kv.Key] = cur + kv.Value;
        }
    }
}

/// <summary>Aggregate of a SINGLE .jsonl file — the cache entry's payload. Merged into the
/// total by summing. A subagent file contributes tokens/tool-calls but is not a session.</summary>
internal sealed class FileAggregate
{
    public string SessionId { get; set; }
    public bool IsSubagent { get; set; }
    public long FirstTimestampMs { get; set; }
    public long LastTimestampMs { get; set; }
    public int Messages { get; set; }
    // model → tokens
    public Dictionary<string, ModelTokens> ModelUsage { get; } = new();
    // dateKey (yyyy-MM-dd) → activity
    public Dictionary<string, DayActivity> Days { get; } = new();
    // The hour (0-23) of the session's first message — for the peak-hour metric (main sessions).
    public int FirstHour { get; set; } = -1;
    // Extra metrics collected in the same pass (cost ~0): attachments + tool usage.
    public int ImageCount { get; set; }
    public int FileCount { get; set; }
    public Dictionary<string, int> ToolCallsByName { get; } = new();
}

/// <summary>The summed aggregate over a scope+range — what the dialog renders.</summary>
internal sealed class StatsTotals
{
    public int TotalSessions { get; set; }
    public int TotalMessages { get; set; }
    public Dictionary<string, ModelTokens> ModelUsage { get; } = new();
    public Dictionary<string, DayActivity> Days { get; } = new();
    public Dictionary<int, int> HourCounts { get; } = new(); // hour → session count
    public int ImageCount { get; set; }
    public int FileCount { get; set; }
    public Dictionary<string, int> ToolCallsByName { get; } = new();
    // Subagent split: tokens/sessions attributable to agent-*.jsonl files.
    public long SubagentTokens { get; set; }
    public int SubagentFiles { get; set; }

    public long TotalTokens
    {
        get
        {
            long t = 0;
            foreach (var m in ModelUsage.Values) { t += m.Total; }
            return t;
        }
    }
}
