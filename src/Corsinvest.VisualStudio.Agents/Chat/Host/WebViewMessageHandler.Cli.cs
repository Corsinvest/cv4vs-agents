/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>WebViewMessageHandler: Cli-namespace message handlers (prompt, permissions, model/mode, stop).</summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleSendPrompt(JObject data, int? id)
    {
        // The WebView echoes the user bubble itself (stream-json doesn't
        // reflect the submitted message back); the host only forwards to the CLI.
        var p = data.ToObject<Contracts.SendPromptNotification>();
        OutputWindowLogger.Debug(() => $"[{BridgeMessages.FromWebView.Cli.SendPrompt}] text len={(p.Text ?? "").Length} sessionId={client.SessionId ?? "(none)"} running={client.IsRunning}");
        // attachments stays a raw JArray: BuildContentBlocks turns it into CLI blocks.
        var blocks = WebViewBridge.BuildContentBlocks(p.Text ?? "", data["attachments"] as JArray);
        client.SendPrompt(blocks, p.Uuid ?? "");
    }

    private void HandleRespondPermission(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.RespondPermissionNotification>();
        // Correlate by tool_use_id so concurrent prompts each answer their own request.
        client.RespondToToolPermission(p.ToolUseId ?? "", new ToolPermissionResponse
        {
            Allow = p.Allowed,
            // Free-text "tell Claude what to do instead" → the deny message.
            DenyMessage = p.DenyMessage is { Length: > 0 } dm ? dm : "User denied",
            // Opaque CLI structures (tool args / PermissionUpdate) round-tripped
            // to the CLI verbatim; read raw so snake_case keys survive.
            UpdatedInput = data["updatedInput"] as JObject,
            // When the user picks "allow … for this session", the WebView
            // sends back the chosen permission_suggestion(s) to apply.
            UpdatedPermissions = data["updatedPermissions"] as JArray,
        });
        Ide.IdeContextService.Instance.CloseDiffFor(p.ToolUseId ?? "");
    }

    private void HandleSetSendSelection(JObject data, int? id)
    {
        // Eye toggle: flip this session's SendSelection. OnEditorContextChangedForChat
        // reads it and sends an empty selection while off. Re-emit the current context on
        // re-enable so the chat regains the selection without waiting for the next change.
        entry.Options.SendSelection = data.ToObject<Contracts.SetSendSelectionNotification>().Enabled;
        Ide.IdeContextService.Instance.ForceEmitCurrentContext();
    }

    private void HandleApplyFlagSettings(JObject data, int? id)
    {
        // Model menu controls (effortLevel / alwaysThinkingEnabled /
        // fastMode): merge the provided keys into the flag-settings layer.
        if (data["settings"] is JObject settings) { _ = client.ApplyFlagSettingsAsync(settings); }
    }

    private void HandleSetMaxThinkingTokens(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.SetMaxThinkingTokensNotification>();
        _ = client.SetMaxThinkingTokensAsync(p.MaxThinkingTokens, p.Display);
    }

    private void HandleStop(JObject data, int? id)
    {
        // The WebView frees itself the moment it asks (it can't wait on a wedged CLI), so a failed
        // interrupt is invisible from the UI: it reads as stopped while the turn runs on. Nothing
        // else observes this Task — the 10s request timeout would fault it into silence — so the
        // log is the only place the divergence can surface.
        _ = client.InterruptAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                OutputWindowLogger.Warn($"!!! interrupt failed: {t.Exception?.GetBaseException().Message}");
            }
        });
    }

    private void HandleSetPermissionMode(JObject data, int? id)
    {
        var newMode = data.ToObject<Contracts.SetPermissionModeNotification>().Mode ?? "default";
        // Hot-swap via set_permission_mode; every mode is supported so no respawn is
        // needed. The continuation runs off the UI thread, but bridge.Send marshals
        // CoreWebView2 access itself.
        _ = client.SetPermissionModeAsync(newMode).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                OutputWindowLogger.Warn($"!!! set_permission_mode failed: {t.Exception?.GetBaseException().Message}");
            }
            // Either way, tell the WebView what the mode REALLY is. The selector switched
            // optimistically before asking, and the client only advances PermissionMode once
            // the CLI has acked — so on failure this sends the old mode back and the UI rolls
            // itself back. Without it the selector reads "Plan" while the CLI is still in
            // bypass: the one lie that costs files.
            bridge.Send(
                BridgeMessages.ToWebView.Cli.PermissionModeChanged,
                new Contracts.PermissionModeChangedNotification { Mode = client.PermissionMode });
        });
    }

    private void HandleSetModel(JObject data, int? id)
    {
        var newModel = data.ToObject<Contracts.SetModelNotification>().Model;
        _ = client.SetModelAsync(string.IsNullOrEmpty(newModel) ? null : newModel).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                OutputWindowLogger.Warn($"!!! set_model failed: {t.Exception?.GetBaseException().Message}");
            }
            // Same rollback as the permission mode: the picker switched before asking, so echo
            // back what the client actually holds. Null means "Default", which the WebView
            // renders as such.
            bridge.Send(
                BridgeMessages.ToWebView.Cli.ModelChanged,
                new Contracts.ModelChangedNotification { Model = client.Model });
        });
    }
}
