/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>The `@` picker's Symbols tab: a NavigateTo search per pause in typing, and the extent of
/// the symbol picked, so the composer can write <c>@path#Lstart-end</c>.</summary>
internal sealed partial class WebViewMessageHandler
{
    // One search at a time per pane: a new query supersedes the one still running.
    private CancellationTokenSource _symbolSearch;

    // NavigateTo stops collecting at the cap, in the order it finds things, so the picker collects
    // more than it shows and ranks before trimming: an exact match found 120th still comes first.
    private const int SymbolSearchCollect = 200;
    private const int SymbolSearchShow = 50;

    private void HandleGetSymbolSuggestions(JObject data, int? id)
    {
        if (id is not int requestId) { return; }
        ThreadHelper.ThrowIfNotOnUIThread();
        var query = data.ToObject<Contracts.GetSymbolSuggestionsRequest>()?.Query ?? "";

        _symbolSearch?.Cancel();
        _symbolSearch?.Dispose();
        var cts = new CancellationTokenSource();
        _symbolSearch = cts;
        var ct = cts.Token;
        var activeFile = IdeContextService.Instance.GetCurrentFilePath();

        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var response = new Contracts.GetSymbolSuggestionsResponse { Items = [] };
            try
            {
                await TaskScheduler.Default;
                var result = await IdeNavigationService.Instance
                    .SearchWorkspaceAsync(query, SymbolSearchCollect, explainSkipped: false, ct)
                    .ConfigureAwait(false);
                if (!result.Supported)
                {
                    response.Unavailable = result.Reason;
                }
                else
                {
                    response.Items = [.. SymbolRanking.Rank(result.Hits, query, activeFile)
                        .Take(SymbolSearchShow)
                        .Select(h => new Contracts.SymbolItemDto
                        {
                            Name = h.Name,
                            Kind = h.Kind,
                            Container = h.ContainerName,
                            Path = h.File,
                            Line = h.Line,
                        })];
                    // Partial coverage only matters when it may be why nothing was found.
                    if (response.Items.Length == 0) { response.Unavailable = result.Reason; }
                }
            }
            // A newer query superseded this one: the WebView drops anything but the latest answer.
            catch (OperationCanceledException) { return; }
            // Answered anyway: without a response the picker waits out the 30 s request timeout.
            catch (Exception ex)
            {
                log.LogException("[picker] symbols", ex);
                response.Items = [];
                response.Unavailable = "Symbol search unavailable: see Output";
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.File.SymbolSuggestions, requestId, response);
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleResolveSymbolRange(JObject data, int? id)
    {
        if (id is not int requestId) { return; }
        var request = data.ToObject<Contracts.ResolveSymbolRangeRequest>();

        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var line = request?.Line ?? 0;
            var response = new Contracts.ResolveSymbolRangeResponse
            {
                StartLine = line,
                EndLine = line,
                Label = request?.Name,
            };
            try
            {
                await TaskScheduler.Default;
                var extent = await IdeNavigationService.Instance
                    .GetSymbolExtentAsync(request?.Path, line, request?.Name, CancellationToken.None)
                    .ConfigureAwait(false);
                if (extent is { } range)
                {
                    response.StartLine = range.Start;
                    response.EndLine = range.End;
                    response.Label = range.Label;
                }
                else
                {
                    log.Debug(() => $"[picker] no extent for {request?.Name} at {request?.Path}:{line}, declaration line only");
                }
            }
            catch (Exception ex)
            {
                log.LogException("[picker] symbol range", ex);
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.File.SymbolRange, requestId, response);
        }).FileAndForget(nameof(WebViewMessageHandler));
    }
}
