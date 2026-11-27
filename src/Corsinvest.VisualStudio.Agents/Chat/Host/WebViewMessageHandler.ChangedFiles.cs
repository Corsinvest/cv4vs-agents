/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// WebViewMessageHandler, Changes side: the list of files Claude changed, and Open / Keep / Undo for
/// each or for all. The state lives in the pane's <see cref="SessionChanges"/>; this only carries
/// the WebView's clicks to it and its answers back.
/// <para>Each entry point returns at once and must not throw into the dispatcher, so the work runs
/// behind a wrapper that turns a failure into one the person can read.</para>
/// </summary>
internal sealed partial class WebViewMessageHandler
{
    private static readonly IReadOnlyList<ChangeFailure> NoFailures = Array.Empty<ChangeFailure>();

    private void HandleGetChangedFiles(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        _ = ReplyChangedFilesAsync(reqId);
    }

    private async Task ReplyChangedFilesAsync(int reqId)
    {
        try
        {
            // Opening the panel is the moment to check the numbers: a file the person edited or
            // put back since is not what the list last said.
            await changes.RefreshAsync();
        }
        catch (Exception ex) { log.LogException("[changes] refresh", ex); }
        bridge.SendResponse(BridgeMessages.ToWebView.ChangedFiles.List, reqId, ChangedFilesMapper.Build(changes.Set));
    }

    private void HandleOpenChangedFile(JObject data, int? id)
    {
        var path = data.ToObject<Contracts.OpenChangedFileNotification>()?.Path;
        if (string.IsNullOrEmpty(path)) { return; }
        _ = Guarded("open", () => changes.Actions.OpenAsync(path));
    }

    private void HandleKeepChangedFile(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        var path = data.ToObject<Contracts.ChangedFileRequest>()?.Path ?? "";
        _ = ReplyActionAsync(reqId, async () =>
        {
            await changes.Actions.KeepAsync(path);
            return NoFailures;
        });
    }

    private void HandleUndoChangedFile(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        var path = data.ToObject<Contracts.ChangedFileRequest>()?.Path ?? "";
        _ = ReplyActionAsync(reqId, async () =>
        {
            var failure = await changes.Actions.UndoAsync(path);
            return failure is null ? NoFailures : new[] { failure };
        });
    }

    private void HandleKeepAllChangedFiles(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        _ = ReplyActionAsync(reqId, async () =>
        {
            await changes.Actions.KeepAllAsync();
            return NoFailures;
        });
    }

    private void HandleUndoAllChangedFiles(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        _ = ReplyActionAsync(reqId, () => changes.Actions.UndoAllAsync());
    }

    private async Task ReplyActionAsync(int reqId, Func<Task<IReadOnlyList<ChangeFailure>>> run)
    {
        IReadOnlyList<ChangeFailure> failed;
        try { failed = await run(); }
        catch (Exception ex)
        {
            log.LogException("[changes] action", ex);
            failed = new[] { new ChangeFailure("", ex.Message) };
        }
        bridge.SendResponse(BridgeMessages.ToWebView.ChangedFiles.ActionResult, reqId,
            new Contracts.ChangedFilesActionResult { Failed = ChangedFilesMapper.ToDtos(failed) });
    }

    private async Task Guarded(string what, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex) { log.LogException($"[changes] {what}", ex); }
    }
}
