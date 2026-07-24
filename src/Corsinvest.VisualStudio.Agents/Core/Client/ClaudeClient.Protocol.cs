/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>
/// ClaudeClient, wire side: the control-protocol internals (request/response
/// correlation, timeouts, id minting) and the incoming NDJSON dispatcher that
/// routes each line (control responses/requests, MCP messages, assistant/user/
/// stream/result/rate-limit events) to handlers. The public API, lifecycle, and
/// process management live in ClaudeClient.cs.
/// </summary>
public sealed partial class ClaudeClient
{
    // ----- Control protocol internals -----

    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongRunningTimeout = TimeSpan.FromMinutes(5);

    private static TimeSpan TimeoutFor(string subtype)
        => subtype switch
        {
            "initialize" => InitializeTimeout,
            "generate_session_title" => LongRunningTimeout,
            "rewind_files" => LongRunningTimeout,
            _ => DefaultRequestTimeout,
        };

    /// <summary>
    /// Send a typed control_request. <paramref name="extra"/> can be a
    /// <see cref="JObject"/> or any anonymous/POCO object — it gets merged
    /// flat into the `request` envelope alongside the mandatory `subtype`.
    /// </summary>
    private Task<JObject> SendControlRequestAsync(string subtype, object extra, TimeSpan? timeout = null)
    {
        if (!_transport.IsRunning) { return Task.FromException<JObject>(new InvalidOperationException("CLI not running")); }

        var id = NextRequestId();
        var tcs = new TaskCompletionSource<JObject>();
        _pending[id] = tcs;

        var request = new JObject { ["subtype"] = subtype };
        if (extra != null)
        {
            var extraObj = extra as JObject ?? JObject.FromObject(extra);
            foreach (var prop in extraObj.Properties()) { request[prop.Name] = prop.Value; }
        }

        _transport.Write(new
        {
            type = "control_request",
            request_id = id,
            request,
        });

        var to = timeout ?? TimeoutFor(subtype);
        _ = Task.Delay(to).ContinueWith(_ =>
        {
            if (_pending.TryRemove(id, out var pending))
            {
                pending.TrySetException(new TimeoutException($"control_request {subtype} timed out after {to.TotalSeconds:0}s"));
            }
        });

        return tcs.Task;
    }

    private void SendControlResponse(string requestId, bool success, object response = null, string error = null)
    {
        if (!_transport.IsRunning) { return; }

        _transport.Write(new
        {
            type = "control_response",
            response = success
                ? (object)new
                {
                    subtype = "success",
                    request_id = requestId,
                    response = response ?? new { }
                }
                : new
                {
                    subtype = "error",
                    request_id = requestId,
                    error = error ?? "error"
                },
        });
    }

    private string NextRequestId()
    {
        var n = Interlocked.Increment(ref _requestCounter);
        // GUID suffix (first 8 hex chars) for uniqueness — just a correlation id, no crypto needed.
        return $"req_{n}_{Guid.NewGuid():N}".Substring(0, $"req_{n}_".Length + 8);
    }

    // ----- Incoming dispatcher -----

    private void HandleLine(JObject obj)
    {
        var type = obj.Val("type", "");
        // subtype lives at top level (system) or nested under `request` (control_request); log whichever exists.
        var subtype = obj.Val("subtype", "") is { Length: > 0 } s
                        ? s
                        : (obj["request"] as JObject).Val("subtype", "");

        OutputWindowLogger.Trace(() => $"[CLI line] type={type}{(string.IsNullOrEmpty(subtype) ? "" : $" subtype={subtype}")}");
        switch (type)
        {
            case ClientMessages.Type.ControlResponse: HandleControlResponse(obj); break;
            case ClientMessages.Type.ControlRequest: HandleIncomingControlRequest(obj); break;
            case ClientMessages.Type.ControlCancelRequest: HandleCancelRequest(obj); break;
            case ClientMessages.Type.System when obj.Val("subtype", "") == ClientMessages.SystemSubtype.Init: HandleInit(obj); break;
            case ClientMessages.Type.System: SystemMessageReceived?.Invoke(this, obj); break;
            case ClientMessages.Type.Assistant: HandleAssistant(obj); break;
            case ClientMessages.Type.User: HandleUser(obj); break;
            case ClientMessages.Type.Result: HandleResult(obj); break;
            case ClientMessages.Type.RateLimitEvent: HandleRateLimit(obj); break;
            case ClientMessages.Type.StreamEvent: HandleStreamEvent(obj); break;
            case ClientMessages.Type.ToolProgress: HandleToolProgress(obj); break;
            case ClientMessages.Type.ConversationReset:
                ConversationReset?.Invoke(this, obj.Val("new_conversation_id"));
                break;
            default: OutputWindowLogger.Trace(() => $"[unhandled] type={type}"); break;
        }
    }

    private void HandleControlResponse(JObject obj)
    {
        if (obj["response"] is not JObject resp) { return; }
        var rid = resp.Val("request_id");
        if (string.IsNullOrEmpty(rid)) { return; }

        if (!_pending.TryRemove(rid, out var tcs)) { return; }

        var sub = resp.Val("subtype", "");
        if (sub == "success")
        {
            tcs.TrySetResult(resp["response"] as JObject ?? []);
        }
        else
        {
            tcs.TrySetException(new Exception(resp.Val("error", "control_request failed")));
        }
    }

    private void HandleIncomingControlRequest(JObject obj)
    {
        var rid = obj.Val("request_id");
        var req = obj["request"] as JObject;
        var sub = req.Val("subtype", "");
        if (string.IsNullOrEmpty(rid) || req == null) { return; }

        switch (sub)
        {
            case ClientMessages.ControlSubtype.CanUseTool:
                {
                    var toolUseId = req.Val("tool_use_id", "");
                    if (!string.IsNullOrEmpty(toolUseId)) { _toolRequestIds[toolUseId] = rid; }
                    ToolPermissionRequested?.Invoke(this, new ToolPermissionRequestEventArgs
                    {
                        RequestId = rid,
                        ToolUseId = toolUseId,
                        ToolName = req.Val("tool_name", ""),
                        Input = req["input"] as JObject,
                        BlockedPath = req.Val("blocked_path"),
                        PermissionSuggestions = req["permission_suggestions"] as JArray,
                    });
                    break;
                }

            case ClientMessages.ControlSubtype.HookCallback:
                HookCallbackRequested?.Invoke(this, new HookCallbackEventArgs
                {
                    RequestId = rid,
                    CallbackId = req.Val("callback_id", ""),
                    ToolUseId = req.Val("tool_use_id"),
                    Input = req["input"],
                });
                break;

            case ClientMessages.ControlSubtype.McpMessage:
                // The CLI is calling a tool on our in-process SDK MCP server (the
                // chat's IDE channel). `request.message` is a JSON-RPC message; we
                // serve it via the dispatcher and reply with { mcp_response }.
                HandleMcpMessage(rid, req);
                break;

            case ClientMessages.ControlSubtype.Elicitation:
                // An MCP server asks the user for structured input (form) or to open a URL (OAuth).
                // We have no elicitation UI, so we decline cleanly — same as the VS Code extension,
                // which passes no onElicitation handler and always replies {action:"decline"}. A
                // decline is the protocol's expected "unsupported" answer; an error would be a bug.
                // Warn (not silent): from the user's side an MCP action silently didn't happen —
                // this line is the only trace of why (e.g. an MCP login that never prompts).
                OutputWindowLogger.Warn($"[client] elicitation from MCP server '{req.Val("mcp_server_name", "?")}' declined — no elicitation UI");
                SendControlResponse(rid, success: true, response: new { action = "decline" });
                break;

            default:
                // Unknown subtype — respond with error so CLI doesn't hang.
                SendControlResponse(rid, success: false, error: $"unknown subtype: {sub}");
                break;
        }
    }

    /// <summary>The CLI aborts one of its own in-flight control_requests to us (top-level
    /// `request_id`) — typically a can_use_tool whose turn was interrupted/superseded. No response
    /// is expected; we just drop the matching UI (the permission banner) so it doesn't hang.</summary>
    private void HandleCancelRequest(JObject obj)
    {
        var rid = obj.Val("request_id");
        if (string.IsNullOrEmpty(rid)) { return; }
        // The permission banner is keyed by tool_use_id in the WebView; map the cancelled
        // request_id back to it (the same map RespondToToolPermission uses to answer).
        var toolUseId = _toolRequestIds.FirstOrDefault(kv => kv.Value == rid).Key;
        if (!string.IsNullOrEmpty(toolUseId))
        {
            _toolRequestIds.TryRemove(toolUseId, out _);
            ToolPermissionCancelled?.Invoke(this, new ToolPermissionCancelledEventArgs { ToolUseId = toolUseId });
        }
        // If it was one of our own pending control_requests, fault it so the awaiter unblocks.
        if (_pending.TryRemove(rid, out var tcs))
        {
            tcs.TrySetException(new OperationCanceledException("control_request cancelled by CLI"));
        }
    }

    /// <summary>Serve an inbound `mcp_message` (CLI → us) via the in-process MCP
    /// dispatcher and reply with the JSON-RPC result wrapped in `mcp_response`.
    /// Fire-and-forget: the dispatcher is async, so we respond when it completes.</summary>
    private void HandleMcpMessage(string rid, JObject req)
    {
        var handler = McpMessageHandler;
        var message = req["message"];
        if (handler == null || message == null)
        {
            SendControlResponse(rid, success: false, error: "no MCP server registered");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var responseJson = await handler(message.ToString(Newtonsoft.Json.Formatting.None));
                // A null response means a JSON-RPC notification (no reply expected),
                // but mcp_message always wants a response envelope — send an empty one.
                var mcpResponse = string.IsNullOrEmpty(responseJson)
                    ? (object)new { }
                    : JObject.Parse(responseJson);
                SendControlResponse(rid, success: true, response: new { mcp_response = mcpResponse });
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("ClaudeClient.HandleMcpMessage", ex);
                SendControlResponse(rid, success: false, error: ex.Message);
            }
        });
    }

    private void HandleInit(JObject obj)
    {
        var sid = obj.Val("session_id");
        if (!string.IsNullOrEmpty(sid) && sid != SessionId)
        {
            SessionId = sid;
            SessionIdChanged?.Invoke(this, sid);
        }

        var cwd = obj.Val("cwd");
        if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd) && cwd != WorkingDirectory)
        {
            WorkingDirectory = cwd;
        }

        var model = obj.Val("model");
        if (!string.IsNullOrEmpty(model)) { Model = model; }
        var mode = obj.Val("permissionMode");
        if (!string.IsNullOrEmpty(mode)) { PermissionMode = mode; }

        Initialized?.Invoke(this, new InitializedEventArgs
        {
            SessionId = SessionId,
            WorkingDirectory = WorkingDirectory,
            Model = Model,
            PermissionMode = PermissionMode,
            FastModeState = obj.Val("fast_mode_state", "off"),
        });
    }

    private void HandleAssistant(JObject obj)
    {
        if (obj["message"] is not JObject m) { return; }
        AssistantMessageReceived?.Invoke(this, new AssistantMessageEventArgs
        {
            Model = m.Val("model", ""),
            StopReason = m.Val("stop_reason"),
            Content = m["content"],
            Usage = m["usage"] as JObject,
            Uuid = obj.Val("uuid"),
            Timestamp = obj.ValTimestampMs("timestamp"),
            ParentToolUseId = obj.Val("parent_tool_use_id"),
        });
    }

    private void HandleUser(JObject obj)
    {
        if (obj["message"] is not JObject m) { return; }
        UserMessageReceived?.Invoke(this, new UserMessageEventArgs
        {
            Content = m["content"],
            Uuid = obj.Val("uuid"),
            Timestamp = obj.ValTimestampMs("timestamp"),
            ToolUseResult = obj["tool_use_result"] as JObject,
            ParentToolUseId = obj.Val("parent_tool_use_id"),
            // The compaction summary ("This session is being continued…") rides as a role:user
            // line with plain-string content — skip it like a meta entry (shown lazily in the
            // compact banner, never as a user bubble). Since EmitUser now renders bare-string
            // content (slash commands), without this it would leak through as a user bubble.
            // On the live wire this is isSynthetic (the SDK folds isMeta || isVisibleInTranscriptOnly
            // into it — sdk.d.ts SDKUserMessage). The .jsonl-only isCompactSummary flag is handled
            // separately in SessionManager.History (it never rides the live stream).
            IsMeta = obj.Val("isMeta", false) || obj.Val("isSynthetic", false),
        });
    }

    private void HandleStreamEvent(JObject obj)
    {
        // Anthropic-style streaming envelope:
        // { type: "stream_event", event: { type: "content_block_delta", index, delta: { type:"text_delta", text:"..." } }, parent_tool_use_id }
        if (obj["event"] is not JObject ev) { return; }
        var evType = ev.Val("type", "");
        if (evType != "content_block_delta") { return; }
        if (ev["delta"] is not JObject delta) { return; }
        var deltaType = delta.Val("type", "");
        if (deltaType == "text_delta")
        {
            var text = delta.Val("text", "");
            if (string.IsNullOrEmpty(text)) { return; }
            AssistantTextDelta?.Invoke(this, new AssistantTextDeltaEventArgs
            {
                Delta = text,
                Index = ev.Val("index", 0),
                ParentToolUseId = obj.Val("parent_tool_use_id"),
            });
        }
        else if (deltaType == "thinking_delta")
        {
            // thinking text grows delta-by-delta; estimated_tokens rides some frames only (-1 = absent).
            AssistantThinkingDelta?.Invoke(this, new AssistantThinkingDeltaEventArgs
            {
                Delta = delta.Val("thinking", ""),
                Index = ev.Val("index", 0),
                EstimatedTokens = delta["estimated_tokens"] != null ? delta.Val("estimated_tokens", -1) : -1,
                ParentToolUseId = obj.Val("parent_tool_use_id"),
            });
        }
        // signature_delta and other delta types are intentionally ignored (not needed by the UI).
    }

    private void HandleToolProgress(JObject obj)
        => ToolProgressReceived?.Invoke(this, new ToolProgressEventArgs
        {
            ToolUseId = obj.Val("tool_use_id", ""),
            ToolName = obj.Val("tool_name", ""),
            ElapsedSeconds = obj.Val("elapsed_time_seconds", 0),
            ParentToolUseId = obj.Val("parent_tool_use_id"),
            TaskId = obj.Val("task_id"),
        });

    private void HandleResult(JObject obj)
    {
        var subtype = obj.Val("subtype", "");
        ResultReceived?.Invoke(this, new ResultEventArgs
        {
            Subtype = subtype,
            DurationMs = obj.Val("duration_ms", 0),
            DurationApiMs = obj.Val("duration_api_ms", 0),
            IsError = obj.Val("is_error", false),
            NumTurns = obj.Val("num_turns", 0),
            TotalCostUsd = obj["total_cost_usd"]?.Type == JTokenType.Float || obj["total_cost_usd"]?.Type == JTokenType.Integer
                ? (double?)obj.Val<double>("total_cost_usd", 0) : null,
            StopReason = obj.Val("stop_reason"),
            Usage = obj["usage"] as JObject,
            ModelUsage = obj["modelUsage"] as JObject,
            ErrorText = ResultErrorText(obj, subtype),
            TerminalReason = obj.Val("terminal_reason", ""),
        });
    }

    /// <summary>The failure text of a `result`: the error subtypes carry `errors[]`, while a
    /// success-subtype result that is still flagged is_error puts it in `result`. Same split
    /// VS Code applies. Empty when the turn didn't fail.</summary>
    private static string ResultErrorText(JObject obj, string subtype)
    {
        if (!obj.Val("is_error", false)) { return ""; }
        if (subtype == "success") { return obj.Val("result", "") ?? ""; }
        var errors = (obj["errors"] as JArray)?
            .Select(e => e?.ToString()?.Trim())
            .Where(e => !string.IsNullOrEmpty(e));
        return errors != null ? string.Join("; ", errors) : "";
    }

    private void HandleRateLimit(JObject obj)
        => RateLimitReceived?.Invoke(this, new RateLimitEventArgs { RateLimitInfo = obj["rate_limit_info"] as JObject });

    /// <summary>Fail every in-flight control_request and forget tracked tool
    /// requests. Called when the process exits (so awaiters don't hang to their
    /// timeout) and on Dispose. Mirrors VS Code's performCleanup.</summary>
    private void RejectPendingRequests(string reason)
    {
        foreach (var kv in _pending) { kv.Value.TrySetException(new InvalidOperationException(reason)); }
        _pending.Clear();
        _toolRequestIds.Clear();
    }
}
