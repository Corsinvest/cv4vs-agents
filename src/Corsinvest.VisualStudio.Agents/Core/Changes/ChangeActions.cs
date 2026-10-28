/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>A file an action could not deal with, and why, in words for the person who clicked.</summary>
internal sealed class ChangeFailure
{
    public ChangeFailure(string path, string reason)
    {
        Path = path;
        Reason = reason;
    }

    public string Path { get; }
    public string Reason { get; }
}

/// <summary>What the buttons of the Changes list do.
/// <para><b>Keep</b> accepts the file as it is: the row goes, and nothing is written, to the file or
/// anywhere else. The next edit of it is compared with a copy taken just before that edit, which is
/// the same as moving the baseline up to now.</para>
/// <para><b>Undo</b> puts the file back as it was before Claude's first edit of it, then forgets the
/// row. An open document is replaced through its buffer and saved, so Ctrl+Z gives back what was
/// there; a closed one is written on disk from the baseline's bytes, which keeps its encoding and
/// line endings (the baseline is a copy, not a re-encoding). A file Claude created is deleted, a file
/// it deleted is recreated.</para></summary>
internal sealed class ChangeActions(ChangeSet set, ChangeBaselines baselines, IChangeEditor editor, OutputWindowLogger log)
{
    /// <summary>Show the file against how it was before Claude's first edit, read-only, in VS's
    /// comparison window. A file with no baseline, or one that is not text, is opened instead.</summary>
    public async Task OpenAsync(string path)
    {
        // Only a listed path: the WebView names it, and a name is not a licence to open any file.
        if (!set.TryGet(path, out var row)) { return; }
        if (!row.Baseline.IsKnown)
        {
            await editor.OpenFileAsync(path);
            return;
        }

        var before = row.Baseline.IsEmpty ? "" : ChangeEvaluator.Decode(await AsyncFile.ReadAllBytesAsync(row.Baseline.Path).ConfigureAwait(false));
        var current = await ReadCurrentTextAsync(path).ConfigureAwait(false);

        // A comparison of two walls of bytes says nothing; the file itself is the better answer.
        if (before is null || current is null)
        {
            await editor.OpenFileAsync(path);
            return;
        }
        await editor.ShowDiffAsync("changes:" + path, path, before, current, "Before Claude", "Current");
    }

    // A file Claude deleted has no text now, which is an empty side of the comparison, not a failure.
    private static async Task<string> ReadCurrentTextAsync(string path)
    {
        try { return ChangeEvaluator.Decode(await AsyncFile.ReadAllBytesAsync(path).ConfigureAwait(false)); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return ""; }
    }

    /// <summary>Accept the file as it is. Never fails: there is nothing to write.</summary>
    public Task KeepAsync(string path)
    {
        Forget(path);
        return Task.CompletedTask;
    }

    /// <summary>Put the file back. Null on success, or what went wrong; the row stays on a failure,
    /// so the person can see it did not happen.</summary>
    public async Task<ChangeFailure> UndoAsync(string path)
    {
        if (!set.TryGet(path, out var row)) { return null; }
        var reason = await RestoreAsync(row).ConfigureAwait(false);
        if (reason is not null)
        {
            log.Warn($"[changes] could not undo {path}: {reason}");
            return new ChangeFailure(path, reason);
        }
        Forget(path);
        return null;
    }

    /// <summary>Keep every listed file.</summary>
    public async Task KeepAllAsync()
    {
        foreach (var row in set.Snapshot()) { await KeepAsync(row.Path).ConfigureAwait(false); }
    }

    /// <summary>Undo every listed file. One that cannot be restored does not stop the rest.</summary>
    public async Task<IReadOnlyList<ChangeFailure>> UndoAllAsync()
    {
        var failed = new List<ChangeFailure>();
        foreach (var row in set.Snapshot())
        {
            var failure = await UndoAsync(row.Path).ConfigureAwait(false);
            if (failure is not null) { failed.Add(failure); }
        }
        return failed;
    }

    /// <summary>The row goes, its copy with it, and the next edit gets a fresh one.</summary>
    private void Forget(string path)
    {
        var removed = set.Remove(path);
        if (removed is not null) { baselines.Release(removed.Baseline); }
        set.SetCaptureNext(path);
    }

    private async Task<string> RestoreAsync(FileChange row)
    {
        var path = row.Path;
        if (!row.Baseline.IsKnown)
        {
            return row.BaselineMissing
                ? "The copy of this file from before Claude changed it is gone."
                : "There is no copy of this file from before Claude changed it.";
        }

        try
        {
            if (row.Baseline.IsEmpty)
            {
                // Created by Claude: put it back by taking it away.
                await editor.CloseDocumentAsync(path);
                await Task.Run(() => { if (File.Exists(path)) { File.Delete(path); } }).ConfigureAwait(false);
                return null;
            }

            var bytes = await AsyncFile.ReadAllBytesAsync(row.Baseline.Path).ConfigureAwait(false);
            var text = ChangeEvaluator.Decode(bytes);
            if (text is not null && await Task.Run(() => File.Exists(path)).ConfigureAwait(false))
            {
                switch (await editor.ReplaceDocumentTextAsync(path, text))
                {
                    case DocumentReplaceResult.Replaced:
                        return null;
                    case DocumentReplaceResult.ReplacedNotSaved:
                        return "It was restored in the editor, but saving the file failed.";
                    case DocumentReplaceResult.Unsupported:
                        return "The file is open in an editor that cannot be restored from here. Close it and try again.";
                }
            }

            // Not open (or deleted, or not text): the bytes go back as they were.
            await Task.Run(() => Directory.CreateDirectory(Path.GetDirectoryName(path))).ConfigureAwait(false);
            await AsyncFile.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
