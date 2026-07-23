/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Corsinvest.VisualStudio.Agents.Contracts;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>One segment of a stacked daily bar: a model's tokens for that day + its palette index
/// (so the segment colour matches the model's dot in the MODELS list).</summary>
internal sealed class BarSegment
{
    public string Model { get; set; }
    public long Tokens { get; set; }
    public int ColorIndex { get; set; }
}

/// <summary>Everything a hover card needs, shared by the heatmap, the bar chart and the donut so
/// they're identical: activity (messages/sessions/tools) + the per-model token segments + the total.
/// Title is a generic header (a project/session name for the donut); when null the header is the
/// formatted Date (heatmap/chart).</summary>
internal sealed class DayInfo
{
    public string Date { get; set; }
    public string Title { get; set; }
    public int Messages { get; set; }
    public int Sessions { get; set; }
    public int Tools { get; set; }
    public List<BarSegment> Segments { get; set; } = new();
    public long Total { get; set; }
}

/// <summary>One heatmap cell: its day info (for the tooltip), an intensity 0–4 (0 = none), and
/// whether it is a future/placeholder day.</summary>
internal sealed class HeatCell
{
    public DayInfo Info { get; set; }
    public string Date => Info?.Date;
    public int Intensity { get; set; }
    public bool Future { get; set; }
}

/// <summary>One daily bar: its day info (date, activity, per-model segments, total).</summary>
internal sealed class DayBar
{
    public DayInfo Info { get; set; }
    public string Date => Info?.Date;
    public List<BarSegment> Segments => Info?.Segments ?? new List<BarSegment>();
    public long Total => Info?.Total ?? 0;
}

/// <summary>Ports the WebView dialog's chart math to C# (no plotting library — the WPF UI draws the
/// squares/bars itself). buildHeatmap mirrors cv-stats-dialog's percentile bucketing; BuildBars
/// gives the per-day token totals for the bar chart.</summary>
internal static class StatsChart
{
    /// <summary>Per-date DayInfo, cross-joining activity (messages/sessions/tools) with the per-model
    /// token segments — the single source both the heatmap and the bar chart read for their tooltips.</summary>
    public static Dictionary<string, DayInfo> BuildDayInfos(StatsResponse r)
    {
        var infos = new Dictionary<string, DayInfo>();

        foreach (var a in r.DailyActivity ?? Array.Empty<StatsDayDto>())
        {
            if (a.Date == null) { continue; }
            if (!infos.TryGetValue(a.Date, out var info))
            {
                info = new DayInfo { Date = a.Date };
                infos[a.Date] = info;
            }
            info.Messages += a.MessageCount;
            info.Sessions += a.SessionCount;
            info.Tools += a.ToolCallCount;
        }

        var order = new Dictionary<string, int>();
        if (r.ModelBreakdown != null)
        {
            for (var i = 0; i < r.ModelBreakdown.Length; i++) { order[r.ModelBreakdown[i].Model] = i; }
        }
        foreach (var d in r.DailyModelTokens ?? Array.Empty<StatsDayModelDto>())
        {
            if (d.Date == null) { continue; }
            if (!infos.TryGetValue(d.Date, out var info)) { info = new DayInfo { Date = d.Date }; infos[d.Date] = info; }
            var map = ToMap(d.TokensByModel);
            foreach (var kv in map.OrderBy(m => order.TryGetValue(m.Key, out var oi) ? oi : int.MaxValue))
            {
                if (kv.Value <= 0) { continue; }
                var idx = order.TryGetValue(kv.Key, out var oi) ? oi : 0;
                info.Segments.Add(new BarSegment { Model = kv.Key, Tokens = kv.Value, ColorIndex = idx });
                info.Total += kv.Value;
            }
        }
        return infos;
    }

    /// <summary>GitHub-style activity grid: columns of 7 cells (Sun→Sat), from the earliest active
    /// day to today (capped at 52 weeks). Intensity is bucketed by percentiles of the active days'
    /// message counts (0 = none, 1–4 rising), like the CLI's heatmap.ts.</summary>
    public static List<HeatCell[]> BuildHeatmap(StatsResponse r)
    {
        var cols = new List<HeatCell[]>();
        var activity = r.DailyActivity ?? Array.Empty<StatsDayDto>();
        if (activity.Length == 0) { return cols; }

        var infos = BuildDayInfos(r);
        var counts = activity.Select(a => a.MessageCount).Where(c => c > 0).OrderBy(c => c).ToArray();
        int At(double q) => counts.Length > 0 ? counts[Math.Min(counts.Length - 1, (int)(counts.Length * q))] : 0;
        int p25 = At(0.25), p50 = At(0.50), p75 = At(0.75);
        int Intensity(int c)
        {
            if (c == 0 || counts.Length == 0) { return 0; }
            if (c >= p75) { return 4; }
            if (c >= p50) { return 3; }
            if (c >= p25) { return 2; }
            return 1;
        }

        var today = DateTime.Now.Date;
        var earliest = today;
        foreach (var a in activity)
        {
            if (DateTime.TryParse(a.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d < earliest)
            {
                earliest = d;
            }
        }
        // Sunday of the current week, then step back to fit the span (capped at 52).
        var weekStart = today.AddDays(-(int)today.DayOfWeek);
        var spanWeeks = (int)Math.Ceiling((weekStart - earliest).TotalDays / 7.0) + 1;
        var weeks = Math.Min(52, Math.Max(1, spanWeeks));
        var cur = weekStart.AddDays(-(weeks - 1) * 7);

        for (var w = 0; w < weeks; w++)
        {
            var col = new HeatCell[7];
            for (var day = 0; day < 7; day++)
            {
                var ds = cur.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var future = cur > today;
                var count = future ? 0 : (infos.TryGetValue(ds, out var inf) ? inf.Messages : 0);
                var info = infos.TryGetValue(ds, out var i2) ? i2 : new DayInfo { Date = ds };
                col[day] = new HeatCell { Info = info, Intensity = future ? 0 : Intensity(count), Future = future };
                cur = cur.AddDays(1);
            }
            cols.Add(col);
        }
        return cols;
    }

    /// <summary>Per-day stacked bars: each day's tokens split per model, coloured by the model's
    /// palette index (same order as the MODELS list), so the stack matches the model dots.</summary>
    public static List<DayBar> BuildBars(StatsResponse r)
    {
        var days = r.DailyModelTokens ?? Array.Empty<StatsDayModelDto>();
        if (days.Length == 0) { return new List<DayBar>(); }

        var infos = BuildDayInfos(r);
        // One bar per day that has token data, in chronological order.
        return days
            .Where(d => d.Date != null && infos.ContainsKey(d.Date))
            .OrderBy(d => d.Date, StringComparer.Ordinal)
            .Select(d => new DayBar { Info = infos[d.Date] })
            .ToList();
    }

    private static Dictionary<string, long> ToMap(object tokensByModel)
    {
        var map = new Dictionary<string, long>();
        // In the WPF path this is the real Dictionary<string,long> (BuildResponse ran in-process);
        // over the WebView wire it comes back as a JObject. Handle both.
        switch (tokensByModel)
        {
            case IDictionary<string, long> d:
                foreach (var kv in d) { map[kv.Key] = kv.Value; }
                break;
            case Newtonsoft.Json.Linq.JObject jo:
                foreach (var p in jo.Properties()) { map[p.Name] = (long)p.Value; }
                break;
        }
        return map;
    }
}
