/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Globalization;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>
/// Aggregates usage statistics from the local session .jsonl files — the same computation the
/// CLI's stats.ts / Claude Desktop do (there is no server API for per-user history stats).
/// Reads line-by-line; supports resuming from a byte offset so a grown (append-only) file only
/// costs its delta. Synthetic assistant turns are skipped; subagent files contribute tokens and
/// tool calls but are not counted as sessions.
/// </summary>
internal static class StatsAggregator
{
    private const string SyntheticModel = "<synthetic>";

    /// <summary>Parse one .jsonl into a <see cref="FileAggregate"/>, reading from
    /// <paramref name="fromOffset"/> bytes. Pass a non-null <paramref name="seed"/> to accumulate
    /// onto an existing aggregate (append delta); null starts fresh. Returns the aggregate and
    /// the new byte length read, or null on I/O error.</summary>
    public static (FileAggregate agg, long newSize)? AggregateFile(
        string path, bool isSubagent, long fromOffset, FileAggregate seed)
    {
        FileAggregate agg = seed ?? new FileAggregate { IsSubagent = isSubagent };
        agg.IsSubagent = isSubagent;
        long size;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            size = fs.Length;
            if (fromOffset > 0 && fromOffset <= size) { fs.Seek(fromOffset, SeekOrigin.Begin); }
            using var reader = new StreamReader(fs);
            // If we seeked mid-stream the first partial line is a leftover of an already-counted
            // line; the CLI writes whole lines so a clean offset lands on a boundary. To be safe
            // when resuming, drop a possible partial first line.
            var dropFirst = fromOffset > 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (dropFirst) { dropFirst = false; continue; }
                if (line.Length == 0) { continue; }
                // Fast path: skip lines without a JSON parse via cheap substring checks. Only
                // assistant turns carry tokens/model/tool_use; user turns matter only when they
                // carry image/document attachments. Everything else (tool_result, system, stream
                // deltas, plain user text) is skipped — parsing every line of a 16 MB file is the
                // dominant cost otherwise.
                // Count both user and assistant turns as messages (like Claude Desktop's
                // mainMessages.length), so keep any line that carries either type. Non-message
                // lines (summaries, stream deltas, system) are still skipped for speed.
                bool isAssistant = line.IndexOf("\"type\":\"assistant\"", StringComparison.Ordinal) >= 0;
                bool isUser = !isAssistant && line.IndexOf("\"type\":\"user\"", StringComparison.Ordinal) >= 0;
                if (!isAssistant && !isUser) { continue; }
                JObject obj;
                try { obj = JObject.Parse(line); }
                catch { continue; }
                Accumulate(agg, obj);
            }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("StatsAggregator.AggregateFile", ex);
            return null;
        }
        return (agg, size);
    }

    // Called for assistant lines (tokens/model/tool) and user lines with media (image/document
    // attachments). The caller pre-filters everything else out.
    private static void Accumulate(FileAggregate agg, JObject obj)
    {
        var role = obj.Val("type", "");
        if (role != "user" && role != "assistant") { return; } // defensive: substring false-positive
        // Sidechain = sub-agent turns inside a main transcript; Claude Desktop excludes them from
        // a session's message/day counts (subagent files are handled separately, isSubagent).
        if (!agg.IsSubagent && obj.Val("isSidechain", false)) { return; }

        var message = obj["message"] as JObject;
        var content = message?["content"] as JArray;

        // Timestamp bookkeeping (first/last, peak hour) from every message, user or assistant.
        var tsMs = ParseTimestampMs(obj.Val("timestamp", ""));
        if (tsMs > 0)
        {
            if (agg.FirstTimestampMs == 0 || tsMs < agg.FirstTimestampMs)
            {
                agg.FirstTimestampMs = tsMs;
                agg.FirstHour = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(tsMs).ToLocalTime().Hour;
            }
            if (tsMs > agg.LastTimestampMs) { agg.LastTimestampMs = tsMs; }
        }

        // Count the message (both roles) into the totals and per-day activity — matches
        // Claude Desktop's mainMessages.length.
        agg.Messages++;
        var dateKey = DateKey(tsMs);
        var day = GetDay(agg, dateKey);
        day.MessageCount++;

        if (role == "user")
        {
            // User turns carry attachments (image/document blocks) but no tokens/tools.
            if (content != null)
            {
                foreach (var block in content)
                {
                    switch (block?["type"]?.Value<string>())
                    {
                        case "image": agg.ImageCount++; break;
                        case "document": agg.FileCount++; break;
                    }
                }
            }
            return;
        }
        // assistant: tokens + tool_use below.
        if (message == null) { return; }

        // tool_use blocks → per-day tool-call count + per-name total.
        if (content != null)
        {
            foreach (var block in content)
            {
                if (block?["type"]?.Value<string>() == "tool_use")
                {
                    day.ToolCallCount++;
                    var name = block.Val("name", "");
                    if (!string.IsNullOrEmpty(name))
                    {
                        agg.ToolCallsByName.TryGetValue(name, out var c);
                        agg.ToolCallsByName[name] = c + 1;
                    }
                }
            }
        }

        var usage = message["usage"] as JObject;
        var model = message.Val("model", "");
        if (usage == null || string.IsNullOrEmpty(model) || model == SyntheticModel) { return; }

        var mt = GetModel(agg, model);
        var inTok = usage.Val("input_tokens", 0L);
        var outTok = usage.Val("output_tokens", 0L);
        mt.InputTokens += inTok;
        mt.OutputTokens += outTok;
        mt.CacheReadTokens += usage.Val("cache_read_input_tokens", 0L);
        mt.CacheCreationTokens += usage.Val("cache_creation_input_tokens", 0L);

        var dayTokens = inTok + outTok;
        if (dayTokens > 0)
        {
            day.TokensByModel.TryGetValue(model, out var cur);
            day.TokensByModel[model] = cur + dayTokens;
        }
    }

    private static DayActivity GetDay(FileAggregate agg, string dateKey)
    {
        if (!agg.Days.TryGetValue(dateKey, out var d)) { d = new DayActivity(); agg.Days[dateKey] = d; }
        return d;
    }

    private static ModelTokens GetModel(FileAggregate agg, string model)
    {
        if (!agg.ModelUsage.TryGetValue(model, out var m)) { m = new ModelTokens(); agg.ModelUsage[model] = m; }
        return m;
    }

    private static long ParseTimestampMs(string iso)
    {
        return string.IsNullOrEmpty(iso)
            ? 0
            : DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0;
    }

    // yyyy-MM-dd in LOCAL time (the heatmap/day buckets are per local calendar day, like the CLI).
    private static string DateKey(long tsMs)
    {
        if (tsMs <= 0) { return "unknown"; }
        var dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(tsMs).ToLocalTime();
        return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
