/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Host;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Ide;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Pane;

/// <summary>
/// ChatPaneControl, client-event side: subscribes to the ClaudeClient events
/// (Attach/Detach) and translates each one (result, models, deltas, rate-limit,
/// process lifecycle, …) into WebView bridge messages. The control/lifecycle and
/// the UI→CLI direction live in ChatPaneControl.xaml.cs.
/// </summary>
public partial class ChatPaneControl
{
    private void AttachClientEvents(IClaudeClient c)
    {
        c.CliStateReceived += OnCliStateReceived;
        c.ModelsReceived += OnModelsReceived;
        c.AssistantMessageReceived += OnAssistantMessage;
        c.UserMessageReceived += OnUserMessage;
        c.ResultReceived += OnResult;
        c.ToolPermissionRequested += OnToolPermissionRequested;
        c.ToolPermissionCancelled += OnToolPermissionCancelled;
        c.AssistantTextDelta += OnAssistantTextDelta;
        c.AssistantThinkingDelta += OnAssistantThinkingDelta;
        c.ToolProgressReceived += OnToolProgress;
        c.SystemMessageReceived += OnSystemMessage;
        c.SessionIdChanged += OnSessionIdChanged;
        c.ConversationReset += OnConversationReset;
        c.Error += OnClientError;
        c.ProcessStarted += OnProcessStarted;
        c.ProcessExited += OnProcessExited;
        c.HookCallbackRequested += OnHookCallback;
        c.RateLimitReceived += OnRateLimit;
    }

    private void DetachClientEvents(IClaudeClient c)
    {
        c.CliStateReceived -= OnCliStateReceived;
        c.ConversationReset -= OnConversationReset;
        c.ModelsReceived -= OnModelsReceived;
        c.AssistantMessageReceived -= OnAssistantMessage;
        c.UserMessageReceived -= OnUserMessage;
        c.ResultReceived -= OnResult;
        c.ToolPermissionRequested -= OnToolPermissionRequested;
        c.ToolPermissionCancelled -= OnToolPermissionCancelled;
        c.AssistantTextDelta -= OnAssistantTextDelta;
        c.AssistantThinkingDelta -= OnAssistantThinkingDelta;
        c.ToolProgressReceived -= OnToolProgress;
        c.SystemMessageReceived -= OnSystemMessage;
        c.SessionIdChanged -= OnSessionIdChanged;
        c.Error -= OnClientError;
        c.ProcessStarted -= OnProcessStarted;
        c.ProcessExited -= OnProcessExited;
        c.HookCallbackRequested -= OnHookCallback;
        c.RateLimitReceived -= OnRateLimit;
    }

    /// <summary>PreToolUse/PostToolUse hooks (Edit/Write/[Multi]Edit/Read): autosave (save the target
    /// file if it's open dirty, so Claude sees live edits) plus post-edit diagnostics (baseline on
    /// PreToolUse, new-diagnostics check on PostToolUse — always on, like VS Code). async void because
    /// the diagnostics check awaits the Error List settling; the try/catch guarantees a response is
    /// always sent so our failure never blocks Claude's tool.</summary>
    private async void OnHookCallback(object sender, HookCallbackEventArgs e)
    {
        try
        {
            var filePath = (e.Input as JObject)?["tool_input"]?["file_path"]?.ToString();

            if (e.CallbackId == ClaudeClient.AutosaveHookId && AgentsOptions.Chat.Autosave)
            {
                if (!string.IsNullOrEmpty(filePath)) { IdeContextService.Instance.SaveIfDirty(filePath); }
            }
            else if (e.CallbackId == ClaudeClient.DiagBaselineHookId && AgentsOptions.Chat.PostEditDiagnostics)
            {
                if (!string.IsNullOrEmpty(filePath)) { IdeDiagnosticsTracker.Instance.CaptureBaseline(e.ToolUseId, filePath); }
            }
            else if (e.CallbackId == ClaudeClient.DiagCheckHookId && AgentsOptions.Chat.PostEditDiagnostics)
            {
                if (!string.IsNullOrEmpty(filePath))
                {
                    // Bound our own work: if the diagnostics read wedges (e.g. a main-thread stall),
                    // don't leave Claude waiting on the CLI's 60s hook timeout — respond {continue}
                    // and drop the feedback. The inner wait is already ≤1.5s, so 3s only trips on a hang.
                    var work = DiagnosticsContextAsync(e.ToolUseId, filePath);
                    // await the completed task (not .Result) to avoid a sync-wait deadlock (VSTHRD002).
                    // If this times out, `work` is left running unobserved — that's fine: it only wraps
                    // IsFileVisible + ReadFileDiagnosticsAsync, and the latter already swallows its own
                    // exceptions, so there's no unobserved-exception fallout from abandoning it here.
                    var timedOut = await Task.WhenAny(work, Task.Delay(3000)) != work;
                    var ctx = timedOut ? null : await work;
                    if (timedOut) { OutputWindowLogger.Debug(() => $"[diag] check timed out (3s) for {filePath} — feedback dropped"); }
                    if (!string.IsNullOrEmpty(ctx))
                    {
                        _client?.RespondToHookCallback(e.RequestId, new
                        {
                            @continue = true,
                            hookSpecificOutput = new { hookEventName = "PostToolUse", additionalContext = ctx },
                        });
                        return;
                    }
                }
            }
            _client?.RespondToHookCallback(e.RequestId, new { @continue = true });
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("ChatPaneControl.OnHookCallback", ex);
            _client?.RespondToHookCallback(e.RequestId, new { @continue = true });
        }
    }

    /// <summary>The post-edit diagnostics work (visibility probe + new-diagnostics diff), as one
    /// awaitable so OnHookCallback can race it against a timeout.</summary>
    private async Task<string> DiagnosticsContextAsync(string toolUseId, string filePath)
    {
        var visible = await IdeContextService.Instance.IsFileVisible(filePath);
        return await IdeDiagnosticsTracker.Instance.FindNewDiagnosticsAsync(toolUseId, filePath, visible);
    }

    // The ONE ui_init, seeded from the CLI's startup state (initialize + get_settings, gathered by
    // ClaudeClient.StartupAsync WITHOUT a user turn — system/init only arrives on the first turn, too
    // late to enable the toolbar). Fired on every startup (open + respawn). PermissionMode isn't in
    // the CLI's reply — we pass it via --permission-mode, so it's read from the client here.
    private void OnCliStateReceived(object sender, CliStateReceivedEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            _bridge.Send(BridgeMessages.ToWebView.Ui.Init, new Contracts.InitPayloadNotification
            {
                Config = new Contracts.InitConfigDto
                {
                    WorkingDirectory = Entry.WorkingDirectory ?? "",
#if DEBUG
                    InDev = true,
#endif
                },
                CliState = new Contracts.CliStateDto
                {
                    // Empty model → the webview shows "Default"; get_settings usually fills it in.
                    Model = e.Model ?? "",
                    // The CLI doesn't report permissionMode (it obeys the --permission-mode we passed);
                    // it's carried on the event, captured at this startup so a respawn can't stale it.
                    PermissionMode = e.PermissionMode ?? "default",
                    EffortLevel = Enum.TryParse<Contracts.EffortLevelDto>(e.EffortLevel, ignoreCase: true, out var lvl) ? lvl : (Contracts.EffortLevelDto?)null,
                    AlwaysThinkingEnabled = e.AlwaysThinkingEnabled,
                    SwitchModelsOnFlag = e.SwitchModelsOnFlag,
                    Ultracode = e.Ultracode,
                    FastModeState = e.FastModeState,
                    SpinnerVerbsConfig = e.SpinnerVerbs == null ? null : new Contracts.SpinnerVerbsConfigDto
                    {
                        Mode = e.SpinnerVerbs.Val("mode"),
                        Verbs = e.SpinnerVerbs["verbs"]?.ToObject<string[]>(),
                    },
                },
                VsOptions = WebViewBridge.BuildVsOptions(),
            });
        });

    private bool _catalogPublished;

    // The `initialize` catalogue: models (available + disabled) and the rich
    // slash commands, projected to the fields the webview uses (the CLI ships extras).
    private void OnModelsReceived(object sender, ModelsReceivedEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            // Publish the model catalogue only on the first init (fresh pane, no --resume).
            // --resume grafts the session model id onto the catalogue (e.g. duplicating "opus[1m]"),
            // dirtying the picker on later inits; VS Code avoids this the same way.
            // Slash commands are re-published every time (they don't dirty).
            if (!_catalogPublished)
            {
                _catalogPublished = true;
                _bridge.Send(BridgeMessages.ToWebView.Chat.Models, new Contracts.ModelsNotification
                {
                    Models = [.. ProjectModels(e.Models, disabled: false), .. ProjectModels(e.UnavailableModels, disabled: true)],
                });
            }
            if (e.Commands != null)
            {
                _bridge.Send(BridgeMessages.ToWebView.Chat.SlashCommands, new Contracts.SlashCommandsNotification { Commands = ProjectCommands(e.Commands) });
            }
        });

    private static Contracts.SlashCommandDto[] ProjectCommands(JArray commands)
        => [.. commands.OfType<JObject>()
            .Select(c => new Contracts.SlashCommandDto
            {
                Name = c.Val("name", ""),
                Description = c.Val("description", ""),
                ArgumentHint = c.Val("argumentHint", ""),
                Aliases = (c["aliases"] as JArray)?.Select(x => (string)x).ToArray() ?? [],
            })
            .Where(c => !string.IsNullOrEmpty(c.Name))];

    private static IEnumerable<Contracts.ModelInfoDto> ProjectModels(JArray models, bool disabled)
    {
        if (models == null) { yield break; }
        foreach (var m in models.OfType<JObject>())
        {
            yield return new Contracts.ModelInfoDto
            {
                Value = m.Val("value", ""),
                ResolvedModel = m.Val("resolvedModel", ""),
                DisplayName = m.Val("displayName", ""),
                Description = m.Val("description", ""),
                SupportsEffort = m.ValBool("supportsEffort") ?? false,
                SupportedEffortLevels = (m["supportedEffortLevels"] as JArray)?.Select(x => (string)x).ToArray() ?? [],
                SupportsFastMode = m.ValBool("supportsFastMode") ?? false,
                SupportsAdaptiveThinking = m.ValBool("supportsAdaptiveThinking") ?? false,
                SupportsAutoMode = m.ValBool("supportsAutoMode") ?? false,
                Disabled = disabled,
            };
        }
    }

    private void OnAssistantMessage(object sender, AssistantMessageEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            // Do NOT push the reply's `model` to the selector: sub-agents run on a different
            // model and would flip the selection. The selector reflects only the user's explicit
            // choice (set_model) plus init/history.
            ContentBlockTranslator.EmitAssistant(e.Content, (t, d) => _bridge.Send(t, d), e.ParentToolUseId, e.Usage);
        });

    private void OnUserMessage(object sender, UserMessageEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            // CLI meta entries (local-command-caveat etc.) are model-only —
            // never surface them in the chat UI.
            if (e.IsMeta) { return; }
            var previewLines = AgentsOptions.Chat.PreviewLines;
            var agentId = e.ToolUseResult?["agentId"]?.Value<string>();
            ContentBlockTranslator.EmitUser(e.Content, previewLines, (t, d) => _bridge.Send(t, d), e.ParentToolUseId, e.Uuid, agentId);
        });

    private void OnResult(object sender, ResultEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            // NOTE: do NOT clear active sub-agents here. `result` ends the main turn, but
            // background agents (run_in_background / async) outlive it and keep running —
            // clearing here would hide the chip while they still work. Each agent sends its
            // own task_notification (→ subagent_ended) when it actually finishes.
            // Any pending "needs input" bar is stale now the turn ended.
            PaneAttentionService.Clear(Entry);
            // Notify "finished" only when no background agents are still running — an async agent
            // makes the main turn's `result` arrive early; the real end comes with the later `result`
            // once background_tasks_changed has emptied.
            if (!_hasBackgroundTasks) { PaneAttentionService.NotifyFinished(Pane, Entry); }
            // The result carries the full picture: usage (tokens used) + the model's
            // context-window limits. Ship both so the gauge has numerator AND
            // denominator from one message (no static table, no extra round-trip).
            // modelUsage is keyed by the served id ("claude-opus-4-8[1m]"), but _client.Model
            // holds the catalogue value the UI sent via set_model ("opus[1m]") — they match only
            // on a fresh init. After any model switch the exact-key lookup misses, so fall back to
            // the single entry (a turn is single-model on the wire) to keep the gauge denominator.
            var mu = e.ModelUsage;
            var modelUsage = (mu?[_client?.Model ?? ""] as JObject)
                ?? (mu?.Count == 1 ? mu.Properties().First().Value as JObject : null);
            _bridge.Send(BridgeMessages.ToWebView.Chat.ExchangeEnded, new Contracts.ExchangeEndedNotification
            {
                CostUsd = e.TotalCostUsd ?? 0,
                DurationMs = e.DurationMs,
                IsError = e.IsError,
                Usage = e.Usage == null ? null : new Contracts.ContextUsageDto
                {
                    InputTokens = e.Usage.Val("input_tokens", 0),
                    OutputTokens = e.Usage.Val("output_tokens", 0),
                    CacheReadTokens = e.Usage.Val("cache_read_input_tokens", 0),
                    CacheCreationTokens = e.Usage.Val("cache_creation_input_tokens", 0),
                },
                ContextWindow = modelUsage?.Val<long>("contextWindow", 0) ?? 0,
                MaxOutputTokens = modelUsage?.Val<long>("maxOutputTokens", 0) ?? 0,
                ErrorText = e.ErrorText ?? "",
                // terminal_reason is the finer cause when present; the subtype always identifies
                // the failure family, so it's the fallback rather than nothing.
                ErrorKind = !string.IsNullOrEmpty(e.TerminalReason) ? e.TerminalReason : e.Subtype ?? "",
            });
            MaybeGenerateTitle();
            // Refresh the toolbar title from disk: catches the ai-title once written
            // and any later refinement. The scan returns custom-title first, so a
            // manual rename is never overwritten.
            RefreshTitleOnTurnEnd();
        });

    /// <summary>Re-read the title off the UI thread at turn end and push it to the
    /// toolbar. Skipped while the user is editing the title (the toolbar owns that).</summary>
    private void RefreshTitleOnTurnEnd()
    {
        var sid = _client?.SessionId;
        var wd = _client?.WorkingDirectory;
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(wd)) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var title = await Task.Run(() => Sessions.ScanTitle(sid));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!string.IsNullOrWhiteSpace(title) && _client?.SessionId == sid) { SetSessionTitle(title); }
        }).FileAndForget(nameof(ChatPaneControl));
    }

    // Auto-title the session after its first exchange (like VS Code): ask the CLI
    // for a short title from the first prompt and save it as the AI title. Done
    // once per session and only while the CLI is alive (the only time we can ask);
    // SetAiTitle no-ops if the user already set a custom title. The session list
    // (WPF) picks it up from disk next time it loads — no live refresh needed.
    private void MaybeGenerateTitle()
    {
        var client = _client;
        var sid = client?.SessionId;
        if (string.IsNullOrEmpty(sid) || sid == _titledSessionId) { return; }
        _titledSessionId = sid;

        // Captured client's workdir, not the live Sessions property — this runs async and
        // must stay keyed on the client that started it, even if the pane's client is swapped.
        var sessions = new SessionManager(PaneClaudePaths, client.WorkingDirectory);
        // Skip the CLI round-trip entirely if the session already has a title
        // (resumed session, or generated on a previous run).
        if (sessions.HasTitle(sid)) { return; }

        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var prompts = sessions.ReadUserPrompts(sid);
            // ReadUserPrompts is newest-first, so the first prompt is the last entry.
            var firstPrompt = prompts.Count > 0 ? prompts[prompts.Count - 1] : null;
            if (string.IsNullOrWhiteSpace(firstPrompt)) { return; }

            var title = await client.GenerateSessionTitleAsync(firstPrompt, persist: false);
            if (!string.IsNullOrWhiteSpace(title))
            {
                // SetAiTitle no-ops (returns true) if a title already exists — e.g. the user
                // renamed the session while generation was in flight. Don't overwrite the toolbar
                // in that case: read back what actually stuck.
                var wasNoOp = sessions.SetAiTitle(sid, title);
                var effective = wasNoOp ? sessions.ScanTitle(sid) : title;

                // Push it to this pane's toolbar now. The turn-end RefreshTitleOnTurnEnd already
                // ran and found nothing — the title is only written here, and asynchronously, so
                // without this a brand-new session shows no title until it is reopened. Guarded on
                // the session id so a client swap mid-generation cannot mistitle the new session.
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_client?.SessionId == sid && !string.IsNullOrWhiteSpace(effective))
                {
                    SetSessionTitle(effective);
                }
            }
        }).FileAndForget(nameof(ChatPaneControl));
    }

    private void OnToolPermissionRequested(object sender, ToolPermissionRequestEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            // Build a synthetic tool_use block so ContentBlockTranslator logic (startLine/endLine) can reuse it.
            var synthetic = new JArray(new JObject
            {
                ["type"] = "tool_use",
                ["id"] = e.ToolUseId,
                ["name"] = e.ToolName,
                ["input"] = e.Input ?? [],
            });
            // needsPermission: true → the WebView raises the permission banner (normal tool_use leaves it false).
            // Pass the CLI's permission_suggestions so the banner can offer "allow … for this session".
            ContentBlockTranslator.EmitAssistant(synthetic,
                                                 (t, d) => _bridge.Send(t, d),
                                                 needsPermission: true,
                                                 permissionSuggestions: e.PermissionSuggestions);
            // Draw the user's attention: this pane is blocked waiting for their answer.
            PaneAttentionService.NotifyInput(Pane, Entry);
        });

    // The CLI cancelled a pending can_use_tool (interrupt / superseded turn) → tell the WebView
    // to dismiss the banner for that tool_use, else it hangs waiting for an answer that won't come.
    private void OnToolPermissionCancelled(object sender, ToolPermissionCancelledEventArgs e)
        => Dispatcher.Invoke(() => _bridge.Send(BridgeMessages.ToWebView.Chat.ToolPermissionCancel,
            new Contracts.ToolPermissionCancelNotification { ToolUseId = e.ToolUseId }));

    private void OnAssistantTextDelta(object sender, AssistantTextDeltaEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            _bridge.Send(BridgeMessages.ToWebView.Chat.AssistantTextDelta, new Contracts.AssistantTextDeltaNotification
            {
                Text = e.Delta,
                Index = e.Index,
                ParentToolUseId = e.ParentToolUseId,
            });
        });

    private void OnAssistantThinkingDelta(object sender, AssistantThinkingDeltaEventArgs e)
        => Dispatcher.Invoke(() =>
            _bridge.Send(BridgeMessages.ToWebView.Chat.ThinkingDelta, new Contracts.ThinkingDeltaNotification
            {
                Uuid = "",                      // stream deltas have no message uuid yet; keyed by parentToolUseId
                Text = e.Delta,
                EstimatedTokens = e.EstimatedTokens,
                ParentToolUseId = e.ParentToolUseId,
            }));

    private void OnToolProgress(object sender, ToolProgressEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            _bridge.Send(BridgeMessages.ToWebView.Chat.ToolProgress, new Contracts.ToolProgressNotification
            {
                ToolUseId = e.ToolUseId,
                ToolName = e.ToolName,
                ElapsedSeconds = e.ElapsedSeconds,
                ParentToolUseId = e.ParentToolUseId,
            });
        });

    private void OnSystemMessage(object sender, JObject obj)
        => Dispatcher.Invoke(() =>
        {
            var subtype = obj.Val("subtype", "");
            if (subtype == ClientMessages.SystemSubtype.Status)
            {
                // Forward the raw work status ("compacting" at start, null→"" at end). The WebView
                // maps known values to a spinner label; VS Code does the same.
                _bridge.Send(BridgeMessages.ToWebView.Chat.Status, new Contracts.StatusNotification
                {
                    Status = obj.Val("status", "") ?? "",
                    // Present when this status closes a compaction; "failed" is the only case the
                    // UI surfaces (a silent failure would leave the user with a stale spinner).
                    CompactResult = obj.Val("compact_result", "") ?? "",
                    CompactError = obj.Val("compact_error", "") ?? "",
                });
            }
            else if (subtype == ClientMessages.SystemSubtype.CompactBoundary)
            {
                // The LIVE wire uses snake_case (compact_metadata/trigger/pre_tokens); the .jsonl
                // persists the same as camelCase (compactMetadata/preTokens — see SessionManager.History).
                // Read the live snake_case names here, else trigger/tokens fall back to auto/0.
                var meta = obj["compact_metadata"] as JObject;
                _bridge.Send(BridgeMessages.ToWebView.Chat.Compacted, new Contracts.CompactedNotification
                {
                    Uuid = obj.Val("uuid", ""),
                    Trigger = meta?.Val("trigger") ?? "auto",
                    PreTokens = meta?.Val("pre_tokens", 0) ?? 0,
                });
            }
            else if (subtype == ClientMessages.SystemSubtype.TaskStarted)
            {
                var taskType = obj.Val("task_type", (string)null);
                // Only local_agent tasks appear as sub-agent chips (matches VS Code extension behaviour).
                if (taskType != "local_agent") { return; }
                _bridge.Send(BridgeMessages.ToWebView.Chat.SubagentStarted, new Contracts.SubagentStartedNotification
                {
                    TaskId = obj.Val("task_id", ""),
                    Description = obj.Val("description", ""),
                    TaskType = taskType,
                    ToolUseId = obj.Val("tool_use_id", (string)null),
                });
            }
            else if (subtype == ClientMessages.SystemSubtype.TaskProgress)
            {
                var usage = obj["usage"] as JObject;
                _bridge.Send(BridgeMessages.ToWebView.Chat.SubagentProgress, new Contracts.SubagentProgressNotification
                {
                    TaskId = obj.Val("task_id", ""),
                    Description = obj.Val("description", ""),
                    LastToolName = obj.Val("last_tool_name", (string)null),
                    Summary = obj.Val("summary", (string)null),
                    ToolUseId = obj.Val("tool_use_id", (string)null),
                    Usage = new Contracts.SubagentUsageDto
                    {
                        TotalTokens = usage?.Val("total_tokens", 0) ?? 0,
                        ToolUses = usage?.Val("tool_uses", 0) ?? 0,
                        DurationMs = usage?.Val("duration_ms", 0L) ?? 0L,
                    },
                });
            }
            else if (subtype == ClientMessages.SystemSubtype.TaskNotification)
            {
                _bridge.Send(BridgeMessages.ToWebView.Chat.SubagentEnded, new Contracts.SubagentEndedNotification
                {
                    TaskId = obj.Val("task_id", ""),
                    Status = obj.Val("status", "completed"),
                });
            }
            else if (subtype == ClientMessages.SystemSubtype.CommandsChanged)
            {
                // Runtime refresh of the slash-command list (e.g. a new .md command file appears,
                // a plugin toggles). Same shape as the `initialize` catalogue — re-publish over the
                // same bridge channel; the WebView replaces its list.
                if (obj["commands"] is JArray commands)
                {
                    _bridge.Send(BridgeMessages.ToWebView.Chat.SlashCommands, new Contracts.SlashCommandsNotification { Commands = ProjectCommands(commands) });
                }
            }
            else if (subtype == ClientMessages.SystemSubtype.ThinkingTokens)
            {
                // Authoritative cumulative thinking-token estimate; the WebView prefers this over delta accumulation.
                _bridge.Send(BridgeMessages.ToWebView.Chat.ThinkingDelta, new Contracts.ThinkingDeltaNotification
                {
                    Uuid = "",
                    Text = "",
                    EstimatedTokens = obj.Val("estimated_tokens", -1),
                    ParentToolUseId = obj.Val("parent_tool_use_id"),
                });
            }
            else if (subtype == ClientMessages.SystemSubtype.BackgroundTasksChanged)
            {
                // Authoritative active-agent list. Track whether any are running so a `result` that
                // ends the MAIN turn (while async agents outlive it) doesn't fire a premature
                // "finished" — we notify only when this is empty (updates on finish AND on cancel).
                _hasBackgroundTasks = obj["tasks"] is JArray tasks && tasks.Count > 0;
            }
        });

    // Map rate_limit_info to a banner: "allowed" clears it, else a message
    // (wording mirrors the VS Code extension). Webview de-dupes by `key`.
    private void OnRateLimit(object sender, RateLimitEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            var info = e.RateLimitInfo;
            var status = info.Val("status", "");
            var type = info.Val("rateLimitType", "");
            var key = status + ":" + type;
            if (status == "allowed" || string.IsNullOrEmpty(status))
            {
                // Empty message = clear the banner (the WebView keys off falsy message).
                _bridge.Send(BridgeMessages.ToWebView.Chat.RateLimit, new Contracts.RateLimitNotification { Key = key, Message = "" });
                return;
            }
            _bridge.Send(BridgeMessages.ToWebView.Chat.RateLimit, new Contracts.RateLimitNotification
            {
                Key = key,
                Severity = status == "rejected" ? Contracts.NoticeVariantDto.Error : Contracts.NoticeVariantDto.Warning,
                Message = FormatRateLimit(status, type, info.Val<double?>("utilization", null), info.Val<long?>("resetsAt", null)),
            });
        });

    private static string FormatRateLimit(string status, string type, double? utilization, long? resetsAt)
    {
        var label = RateLimitLabel(type);
        var reset = resetsAt is null ? "" : $" · resets {RelativeReset(resetsAt.Value)}";
        if (status == "rejected") { return $"You've hit your {label}{reset}"; }
        return utilization is > 0 ? $"You've used {(int)(utilization.Value * 100)}% of your {label}{reset}" : $"Approaching {label}{reset}";
    }

    private static string RateLimitLabel(string type) => type switch
    {
        "five_hour" => "session limit",
        "seven_day" => "weekly limit",
        "seven_day_opus" => "weekly Opus limit",
        "seven_day_sonnet" => "weekly Sonnet limit",
        "overage" => "usage credit limit",
        _ => "usage limit",
    };

    // rate_limit_event ships resetsAt as Unix epoch seconds (verified on the wire), not an ISO string.
    private static string RelativeReset(long resetsAt)
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(resetsAt);
        var mins = (when - DateTimeOffset.Now).TotalMinutes;
        if (mins < 1) { return "soon"; }
        if (mins < 60) { return $"in {(int)mins}m"; }
        var hours = mins / 60;
        return hours < 24 ? $"in {(int)hours}h" : $"in {(int)(hours / 24)}d";
    }

    // When the CLI emits its init message with a (possibly new) session_id,
    // align the toolbar title with it (a resumed/forked session already has one;
    // a fresh one stays blank until its first turn generates an ai-title).
    private void OnSessionIdChanged(object sender, string id)
        => Dispatcher.Invoke(() =>
        {
            // Keep the entry's session current so the workspace snapshot (saved on solution close)
            // reflects a mid-life session change, not just the one from ProcessStarted. Entry is
            // never null (set-once via Init).
            Entry.ActiveSessionId = id;
            RefreshTitleFromDisk();
        });

    // The CLI reset the conversation (/clear): empty the transcript now. The respawn's
    // StartupAsync re-seeds the UI (OnCliStateReceived); system/init updates the session id.
    private void OnConversationReset(object sender, string newConversationId)
        => Dispatcher.Invoke(() =>
        {
            _bridge?.Send(BridgeMessages.ToWebView.Chat.Cleared, null);
            SetSessionTitle(null);
        });

    private void OnClientError(object sender, string msg)
        => Dispatcher.Invoke(() => _bridge.Send(BridgeMessages.ToWebView.Cli.Error, new Contracts.CliErrorNotification { Message = msg }));

    private void OnProcessStarted(object sender, ProcessStartedEventArgs e)
        => Dispatcher.Invoke(() =>
        {
            // Workdir is fixed on the entry at creation; only the attached session changes here.
            // Track it so the History picker marks the live session with a ✓ (CLI sets it too).
            Entry.ActiveSessionId = e.SessionId;
            // The banner only needs the "process is back up" signal to clear its error — it reads
            // no payload, so send a bare notification.
            _bridge.Send(BridgeMessages.ToWebView.Cli.Started, null);
        });

    private void OnProcessExited(object sender, ProcessExitedEventArgs e)
    {
        // The CLI can exit during teardown when the dispatcher is gone, so `Invoke` would throw.
        // Use fire-and-forget `BeginInvoke` and swallow the exception.
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _bridge?.Send(BridgeMessages.ToWebView.Cli.Exited, new Contracts.CliExitedNotification
                {
                    ExitCode = e.ExitCode,
                    Intentional = e.Intentional,
                });
            }));
        }
        catch (TaskCanceledException) { }
        catch (InvalidOperationException) { }
    }
}
