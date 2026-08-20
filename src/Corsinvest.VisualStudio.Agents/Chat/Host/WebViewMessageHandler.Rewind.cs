/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// WebViewMessageHandler, rewind side: restoring the files to the snapshot the CLI took before a
/// user message, and what the picker needs to offer that honestly — which messages have a snapshot
/// at all, what a given one would change, and a diff of one file against its backup.
/// <para>Two of the three reads never reach the CLI: it records its snapshots in the session
/// transcript, so which messages are rewindable and which backup belongs to which file are
/// answered by reading that. Only the verdict on one message, and the rewind itself, are its to
/// give.</para>
/// </summary>
internal sealed partial class WebViewMessageHandler
{
    /// <summary>Rewind the files to the CLI's snapshot before a user message — or, with dryRun,
    /// only ask whether it could and with what. "No checkpoint for this message" is an ordinary
    /// answer here, not a failure: the CLI keeps file history per session, and on our path only
    /// because we start it with CLAUDE_CODE_ENABLE_SDK_FILE_CHECKPOINTING.
    /// <para>async void because the switch that calls it is void, which is safe only because
    /// RewindFilesAsync swallows its own failures and answers null — nothing here can throw into
    /// a context with no one to catch it.</para></summary>
    private async void HandleRewind(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        var p = data.ToObject<Contracts.RewindRequest>();
        var uuid = p.MessageUuid ?? "";
        var res = await client.RewindFilesAsync(uuid, p.DryRun);

        // Null means the request itself was refused (already logged). Everything else is the CLI's
        // own verdict, error text included — pass it through rather than inventing wording.
        var reply = new Contracts.RewindResultNotification
        {
            MessageUuid = uuid,
            CanRewind = res?["canRewind"]?.Value<bool>() ?? false,
            Error = res?["error"]?.Value<string>() ?? (res == null ? "The CLI refused the request." : ""),
            FilesChanged = res?["filesChanged"]?.ToObject<string[]>(),
            Insertions = res?["insertions"]?.Value<int>() ?? 0,
            Deletions = res?["deletions"]?.Value<int>() ?? 0,
            SkippedLinks = res?["skippedLinks"]?.Value<int>() ?? 0,
        };
        log.Debug(() => $"[rewind] uuid={uuid} dryRun={p.DryRun} canRewind={reply.CanRewind} error={reply.Error}");
        bridge.SendResponse(BridgeMessages.ToWebView.Session.RewindResult, reqId, reply);
    }

    /// <summary>Which messages the rewind picker should offer. One read of the session file answers
    /// for all of them, where the CLI would have to be asked one message at a time.</summary>
    private void HandleGetRewindPoints(JObject data, int? id)
    {
        if (id is not int reqId) { return; }
        var uuids = Sessions.ReadRewindableUuids(client.SessionId);
        log.Debug(() => $"[rewind] {uuids.Count} message(s) with a file snapshot in {client.SessionId}");
        bridge.SendResponse(BridgeMessages.ToWebView.Session.RewindPoints, reqId,
            new Contracts.RewindPointsNotification { Uuids = [.. uuids] });
    }

    /// <summary>Show what rewinding to a message would undo for one file: the copy the CLI took
    /// before that turn against the file as it stands now, in VS's own diff viewer.
    /// <para>Both sides are read here, so no file content ever crosses the bridge — the WebView
    /// sends a path and gets nothing back.</para>
    /// <para>A file the turn CREATED has no backup to show: the CLI copies a file only before
    /// overwriting one. There the left side is empty on purpose — rewinding deletes it, and an
    /// empty "before" says exactly that.</para></summary>
    private async void HandleRewindDiff(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.RewindDiffNotification>();
        var uuid = p.MessageUuid ?? "";
        var wanted = p.FilePath ?? "";
        var sessionId = client.SessionId;

        var backup = Sessions.ReadFileBackups(sessionId, uuid)
            .FirstOrDefault(b => PathsMatch(b.Path, wanted, client.WorkingDirectory));

        // No backup at all, or one recorded as null: either way the file did not exist before this
        // message, and rewinding DELETES it rather than restoring anything. The CLI only copies a
        // file it is about to OVERWRITE — a file it creates has nothing to copy — so the dry run
        // lists it while no snapshot names it. An empty left-hand side is the honest picture of
        // that: everything on the right goes away.
        var before = "";
        if (backup?.BackupFileName != null)
        {
            var backupPath = Path.Combine(PaneClaudePaths.FileHistoryFolder, sessionId, backup.BackupFileName);
            if (!File.Exists(backupPath))
            {
                // The CLI prunes its own history; a transcript can outlive the copies it names.
                log.Warn($"[rewind] backup {backup.BackupFileName} is gone from disk — cannot diff {wanted}");
                return;
            }
            before = File.ReadAllText(backupPath);
        }
        else
        {
            log.Debug(() => $"[rewind] {wanted} has no backup at {uuid} — it would be deleted, diffing against nothing");
        }

        var current = File.Exists(wanted) ? File.ReadAllText(wanted) : "";
        await Ide.IdeContextService.Instance.ShowDiffAsync(
            $"rewind:{uuid}:{wanted}", wanted, before, current,
            leftLabel: "Before this message", rightLabel: "Current");
    }

    /// <summary>True when a backup's recorded path and the one the WebView asked about are the same
    /// file. The CLI stores paths relative to the working directory when they sit under it, so the
    /// two spellings have to be brought together before they can be compared.</summary>
    private static bool PathsMatch(string recorded, string wanted, string workingDirectory)
    {
        if (string.IsNullOrEmpty(recorded) || string.IsNullOrEmpty(wanted)) { return false; }
        var full = Path.IsPathRooted(recorded) ? recorded : Path.Combine(workingDirectory ?? "", recorded);
        try { return string.Equals(Path.GetFullPath(full), Path.GetFullPath(wanted), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
