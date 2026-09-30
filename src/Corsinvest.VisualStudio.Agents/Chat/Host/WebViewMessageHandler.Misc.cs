/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// WebViewMessageHandler dispatchers that answer to nothing else: forking a session, and the `@`
/// picker's file suggestions. Grouped here so the base file holds just the switch and the class
/// lifecycle; the groups that hang together (Cli/Open/Chat/Plugins/Rewind) and the shared helpers
/// are their own partials.
/// <para>Fork sits in Session alongside the rewind messages but not with them: it writes a new
/// transcript and opens a pane, where those restore files in this one.</para>
/// </summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleFork(JObject data, int? id)
    {
        // Fork: write a brand-new JSONL truncated BEFORE the clicked message
        // (fresh uuids), open it in its OWN pane (this session stays untouched),
        // and pre-fill that message's text into the new composer for editing/resend.
        var p = data.ToObject<Contracts.ForkNotification>();
        var forkAtUuid = p.MessageUuid ?? "";
        var forkSourceId = p.SessionId ?? client.SessionId;
        var fork = Sessions.ForkSession(forkSourceId, forkAtUuid);
        if (fork != null)
        {
            Core.Panes.PaneLauncher.OpenNew(PaneKind.Chat, entry.Profile, forkSessionId: fork.NewSessionId,
                initialComposer: new Contracts.SetComposerNotification { Text = fork.ExcludedPrompt });
        }
        else
        {
            log.Debug(() => $"[fork] ForkSession returned null (uuid={forkAtUuid}), no pane opened");
        }
    }

    private void HandleGetSuggestions(JObject data, int? id)
    {
        if (id is not int suggId) { return; }
        var request = data.ToObject<Contracts.GetSuggestionsRequest>();
        var root = entry.WorkingDirectory;
        if (request.Refresh) { _fileIndex.Refresh(root); }
        var listingTask = _fileIndex.CurrentAsync(root);

        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var response = new Contracts.GetSuggestionsResponse { Items = [] };
            try
            {
                var listing = await listingTask.ConfigureAwait(false);
                if (request.Refresh)
                {
                    // Once per opening, not per keystroke: every request of an opening shares this listing.
                    if (listing.Ok) { log.Perf(() => $"[picker] listed {listing.Paths.Count} files in {listing.ElapsedMs} ms"); }
                    else { log.Warn($"[picker] {listing.Failure}"); }
                    if (listing.Warning != null) { log.Warn($"[picker] listed all but: {listing.Warning}"); }
                }
                if (listing.Ok)
                {
                    response.Items = [.. FileSuggestions.Filter(root, listing.Paths, request.Query ?? "")
                        .Select(s => new Contracts.AtItemDto { Name = s.Name, Path = s.Path, Dir = s.Dir, IsDir = s.IsDir })];
                }
                else
                {
                    response.Unavailable = "File list unavailable: see Output";
                }
            }
            // A newer opening replaced this listing; the WebView drops this answer as stale.
            catch (OperationCanceledException) { }
            // Answered anyway: without a response the picker waits out the 30 s request timeout.
            catch (Exception ex)
            {
                log.LogException("[picker] suggestions", ex);
                response.Items = [];
                response.Unavailable = "File list unavailable: see Output";
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.File.Suggestions, suggId, response);
        }).FileAndForget(nameof(WebViewMessageHandler));
    }
}
