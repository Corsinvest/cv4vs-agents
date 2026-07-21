/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// WebViewMessageHandler single-case dispatchers whose namespaces have only one message:
/// Session.Fork and File.GetSuggestions. Grouped here so the base file holds just the switch and
/// class lifecycle; the per-namespace groups (Cli/Open/Chat/Plugins) and shared helpers are their
/// own partials.
/// </summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleFork(JObject data, int? id)
    {
        // Fork like VS Code: write a brand-new JSONL truncated BEFORE the
        // clicked message (fresh uuids), open it in its OWN pane (this
        // session stays untouched), and pre-fill that message's text into
        // the new composer for editing/resend.
        var p = data.ToObject<Contracts.ForkNotification>();
        var forkAtUuid = p.MessageUuid ?? "";
        var forkSourceId = p.SessionId ?? client.SessionId;
        var fork = Sessions.ForkSession(forkSourceId, forkAtUuid);
        if (fork != null)
        {
            Core.Panes.PaneLauncher.OpenNew(PaneKind.Chat, entry.Profile, forkSessionId: fork.NewSessionId, initialPrompt: fork.ExcludedPrompt);
        }
        else
        {
            OutputWindowLogger.Debug(() => $"[fork] ForkSession returned null (uuid={forkAtUuid}) — no pane opened");
        }
    }

    private void HandleGetSuggestions(JObject data, int? id)
    {
        if (id is not int suggId) { return; }
        bridge.SendResponse(BridgeMessages.ToWebView.File.Suggestions, suggId, new Contracts.GetSuggestionsResponse
        {
            Items = [.. FileSuggestions.Get(client.WorkingDirectory, data.ToObject<Contracts.GetSuggestionsRequest>().Query ?? "")
                .Select(s => new Contracts.AtItemDto
                {
                    Name = s.Name,
                    Path = s.Path,
                    Dir = s.Dir,
                    IsDir = s.IsDir,
                })]
        });
    }
}
