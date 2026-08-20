/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>WebViewMessageHandler: Cli-namespace message handlers (prompt, permissions, model/mode, stop).</summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleSendPrompt(JObject data, int? id)
    {
        // The WebView echoes the user bubble itself (stream-json doesn't
        // reflect the submitted message back); the host only forwards to the CLI.
        var p = data.ToObject<Contracts.SendPromptNotification>();
        log.Debug(() => $"[{BridgeMessages.FromWebView.Cli.SendPrompt}] text len={(p.Text ?? "").Length} sessionId={client.SessionId ?? "(none)"} running={client.IsRunning}");
        // attachments stays a raw JArray: BuildContentBlocks turns it into CLI blocks.
        var blocks = WebViewBridge.BuildContentBlocks(BuildIdeContextBlock() + (p.Text ?? ""),
                                                     data["attachments"] as JArray);
        client.SendPrompt(blocks, p.Uuid ?? "");
    }

    /// <summary>The `&lt;ide_*&gt;` block that goes in front of the prompt, or "" when there is no
    /// editor context to report. Composed here rather than in the composer so the selected code
    /// never crosses the bridge — the WebView only needs the file and the lines for its chip.</summary>
    private string BuildIdeContextBlock()
    {
        if (entry?.Options.SendSelection == false) { return ""; }
        var ctx = Ide.IdeContextService.Instance.GetCurrentContext();
        if (string.IsNullOrEmpty(ctx?.FilePath)) { return ""; }
        if (!ctx.HasSelection)
        {
            return $"<ide_opened_file>The user opened the file {ctx.FilePath} in the IDE. " +
                   "This may or may not be related to the current task.</ide_opened_file>\n";
        }
        var head = $"<ide_selection>The user selected the lines {ctx.StartLine} to {ctx.EndLine} " +
                   $"from {ctx.FilePath}";
        const string tail = "This may or may not be related to the current task.</ide_selection>\n";
        // With the text, the shape is the VS Code webview's — what the CLI's readers expect.
        // Without, the tag ends on the path rather than trailing a colon over nothing.
        return Options.AgentsOptions.Chat.SendSelectionText
            ? $"{head}:\n{ctx.SelectedText ?? ""}\n\n{tail}"
            : $"{head}. {tail}";
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
        // Re-emit after the flip so re-opening the eye recovers the current selection instead of
        // waiting for the next editor change.
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
        // Fire and forget: the WebView frees itself the moment it asks, since it can't wait on a
        // wedged CLI. InterruptAsync logs its own failure — there is nothing to roll back here,
        // unlike the model and permission handlers below.
        _ = client.InterruptAsync();
    }

    private void HandleSetPermissionMode(JObject data, int? id)
    {
        var newMode = data.ToObject<Contracts.SetPermissionModeNotification>().Mode ?? "default";
        // Hot-swap via set_permission_mode; every mode is supported so no respawn is
        // needed. The continuation runs off the UI thread, but bridge.Send marshals
        // CoreWebView2 access itself.
        _ = client.SetPermissionModeAsync(newMode).ContinueWith(_ =>
        {
            // Failure or not — the client logs that itself — tell the WebView what the mode REALLY
            // is. The selector switched optimistically before asking, and the client only advances
            // PermissionMode once the CLI has acked, so on failure this sends the old mode back and
            // the UI rolls itself back. Without it the selector reads "Plan" while the CLI is still
            // in bypass: the one lie that costs files.
            bridge.Send(
                BridgeMessages.ToWebView.Cli.PermissionModeChanged,
                new Contracts.PermissionModeChangedNotification { Mode = client.PermissionMode });
        });
    }

    private void HandleSetModel(JObject data, int? id)
    {
        var newModel = data.ToObject<Contracts.SetModelNotification>().Model;
        _ = client.SetModelAsync(string.IsNullOrEmpty(newModel) ? null : newModel).ContinueWith(_ =>
        {
            // Same rollback as the permission mode: the picker switched before asking, so echo
            // back what the client actually holds. Null means "Default", which the WebView
            // renders as such.
            bridge.Send(
                BridgeMessages.ToWebView.Cli.ModelChanged,
                new Contracts.ModelChangedNotification { Model = client.Model });
        });
    }

    private async Task HandleSetRemoteControlAsync(JObject data, int? id)
    {
        var on = data.Val("enabled", false);
        // Before the call: enabling takes a round-trip and the switch would sit still meanwhile.
        SendRemoteControl(on ? "connecting" : "disconnected");
        try
        {
            var url = await client.SetRemoteControlAsync(on);
            SendRemoteControl(on ? "connected" : "disconnected", url);
        }
        // The CLI's own refusals ("/login", "disabled by your organization's policy") read fine and
        // pass through below. These two don't: they name the control subtype and transport details.
        catch (TimeoutException)
        {
            SendRemoteControl("error", detail: "Remote Control did not answer in time.");
        }
        catch (Exception) when (!client.IsRunning)
        {
            SendRemoteControl("error", detail: "The Claude CLI isn't running.");
        }
        catch (Exception ex)
        {
            SendRemoteControl("error", detail: ex.Message);
        }
    }

    private void SendRemoteControl(string status, string url = null, string detail = null)
        => bridge.Send(BridgeMessages.ToWebView.Chat.RemoteControl, new Contracts.RemoteControlNotification
        {
            Status = status,
            Url = url,
            Detail = detail,
        });
}
