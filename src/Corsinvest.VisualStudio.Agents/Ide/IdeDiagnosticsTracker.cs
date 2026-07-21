/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>Tracks per-file diagnostics across a Claude edit: capture a baseline on PreToolUse,
/// then on PostToolUse re-read and report only the diagnostics the edit introduced. Mirrors the
/// VS Code extension's captureBaseline/findDiagnosticsProblems pair. Diff is essential — without it
/// Claude would be spammed with pre-existing errors it didn't cause.</summary>
internal sealed class IdeDiagnosticsTracker
{
    public static readonly IdeDiagnosticsTracker Instance = new();

    // tool_use_id → (baseline diagnostics task, captured-at). Keyed by tool_use_id (not file path):
    // PreToolUse and PostToolUse for the SAME tool call share the same tool_use_id on the wire, so this
    // pairs them exactly. Keying by path would collide on two rapid edits of the same file — Pre(A)
    // then Pre(B) overwrites A's baseline, so Post(A) would consume B's baseline and report every
    // pre-existing diagnostic as new. The task is stored the instant capture starts (not after it
    // completes) so there is never a window where a lookup finds no entry; FindNewDiagnosticsAsync
    // awaits it, closing the capture-vs-check race. TTL/cap guard orphaned baselines (an edit that
    // never fires PostToolUse would otherwise leak entries forever).
    private readonly ConcurrentDictionary<string, (Task<List<Diagnostic>> Diags, DateTime At)> _baseline =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan BaselineTtl = TimeSpan.FromSeconds(60);
    private const int MaxBaselines = 64;

    private IdeDiagnosticsTracker() { }

    public void CaptureBaseline(string toolUseId, string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return; }
        EvictStale();
        // Fallback to filePath when toolUseId is missing (edge case) — collision-prone but better
        // than dropping the baseline outright.
        _baseline[Key(toolUseId ?? filePath)] = (ReadFileDiagnosticsAsync(filePath), DateTime.UtcNow);
        OutputWindowLogger.Debug(() => $"[diag] baseline capture started for {filePath}");
    }

    public async Task<string> FindNewDiagnosticsAsync(string toolUseId, string filePath, bool editorVisible)
    {
        if (string.IsNullOrEmpty(filePath)) { return null; }
        _baseline.TryRemove(Key(toolUseId ?? filePath), out var b);           // consume the baseline
        var before = b.Diags != null ? await b.Diags : [];
        OutputWindowLogger.Debug(() => $"[diag] check start {filePath} visible={editorVisible} baseline={before.Count} items");

        // The Error List refreshes after the edit lands; wait like VS Code. Visible editors settle
        // faster (2×750ms, bail at first hit); background files get a single 1000ms wait.
        var steps = editorVisible ? new[] { 750, 750 } : new[] { 1000 };
        for (int i = 0; i < steps.Length; i++)
        {
            await Task.Delay(steps[i]);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var after = await ReadFileDiagnosticsAsync(filePath);
            sw.Stop();
            var step = i;
            OutputWindowLogger.Debug(() => $"[diag] step {step + 1}: read {after.Count} items in {sw.ElapsedMilliseconds}ms (after {steps[step]}ms wait)");
            var added = after.Where(a => !before.Any(x => SameDiag(x, a))).ToList();
            if (added.Count > 0)
            {
                OutputWindowLogger.Debug(() => $"[diag] {added.Count} new diagnostics for {filePath}");
                return Format(filePath, added);
            }
        }
        OutputWindowLogger.Debug(() => $"[diag] no new diagnostics for {filePath}");
        return null;
    }

    private static async Task<List<Diagnostic>> ReadFileDiagnosticsAsync(string filePath)
    {
        try
        {
            var uri = PathHelpers.ToFileUri(filePath);
            var files = await IdeContextService.Instance.GetDiagnosticsAsync(uri);
            return files.SelectMany(f => f.Diagnostics).ToList();
        }
        catch (Exception ex)
        {
            // Must not fault: this task is awaited both as the baseline and inside the post-edit
            // read, and a faulted baseline would throw out of FindNewDiagnosticsAsync's await.
            OutputWindowLogger.LogException("IdeDiagnosticsTracker.ReadFileDiagnosticsAsync", ex);
            return [];
        }
    }

    // Identity for the diff: same position + message + severity. (No stable id in the Error List.)
    private static bool SameDiag(Diagnostic a, Diagnostic b)
        => a.Message == b.Message && a.Severity == b.Severity
           && a.Range?.Start?.Line == b.Range?.Start?.Line
           && a.Range?.Start?.Character == b.Range?.Start?.Character;

    private static string Format(string filePath, List<Diagnostic> diags)
    {
        // 1-based line/column (our LSP shape is 0-based); shape matches VS Code so Claude reads it as trained.
        var items = diags.Select(d => new
        {
            filePath,
            line = (d.Range?.Start?.Line ?? 0) + 1,
            column = (d.Range?.Start?.Character ?? 0) + 1,
            message = d.Message ?? "",
            code = d.Code ?? "",
            severity = d.Severity ?? "",
        });
        return $"<ide_diagnostics>{JsonExtensions.ToIndentedString(JToken.FromObject(items))}</ide_diagnostics>";
    }

    private static string Key(string path) => path.Trim();

    private void EvictStale()
    {
        var cutoff = DateTime.UtcNow - BaselineTtl;
        foreach (var kv in _baseline.Where(kv => kv.Value.At < cutoff).ToList())
        {
            _baseline.TryRemove(kv.Key, out _);
        }
        // Hard cap: if still over, drop the oldest.
        if (_baseline.Count > MaxBaselines)
        {
            foreach (var kv in _baseline.OrderBy(kv => kv.Value.At).Take(_baseline.Count - MaxBaselines).ToList())
            {
                _baseline.TryRemove(kv.Key, out _);
            }
        }
    }
}
