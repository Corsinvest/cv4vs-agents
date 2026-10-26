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
/// row. An open document whose text is plain UTF-8 or UTF-16 is replaced through its buffer and saved, so
/// Ctrl+Z gives back what was there; anything else is written on disk from the baseline's bytes, which
/// keeps its encoding and line endings (the baseline is a copy, not a re-encoding). A file Claude created
/// is deleted, a file it deleted is recreated.</para>
/// <para>Undo discards whatever is in the file now, so it only acts on a file the list has measured: one
/// that changed since (the person saved it, another tool wrote it) is measured again and left for them to
/// look at, and a file that would be deleted while the editor holds unsaved changes to it is not.</para></summary>
/// <param name="remeasure">Measures a row's file again and updates the row. Null in a test that does not
/// need it: a file that has moved is then still refused.</param>
internal sealed class ChangeActions(ChangeSet set, ChangeBaselines baselines, IChangeEditor editor, OutputWindowLogger log,
                                    Func<string, Task> remeasure = null)
{
    /// <summary>Show the file against how it was before Claude's first edit: in its own editor, with Copilot's review
    /// drawn in it, where that can be done (see <see cref="IChangeEditor.ShowInPlaceAsync"/>), and
    /// otherwise read-only in VS's comparison window. A file with no baseline, or one that is not text, is
    /// opened instead.</summary>
    public async Task OpenAsync(string path)
    {
        // Only a listed path: the WebView names it, and a name is not a licence to open any file.
        if (!set.TryGet(path, out var row)) { return; }
        if (!row.Baseline.IsKnown)
        {
            await editor.OpenFileAsync(path);
            return;
        }

        if (await editor.ShowInPlaceAsync(set, path)) { return; }

        string before, current;
        try
        {
            before = row.Baseline.IsEmpty ? "" : ChangeEvaluator.Decode(await AsyncFile.ReadAllBytesAsync(row.Baseline.Path).ConfigureAwait(false));
            current = await ReadCurrentTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked, or gone since the list was measured: the file itself is the better answer.
            log.Warn($"[changes] could not read {path} to compare it: {ex.Message}");
            await editor.OpenFileAsync(path);
            return;
        }

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

    /// <summary>Show every listed file's changes together, in the one window Visual Studio 18 has for it. False when that could
    /// not be done, which the panel then says. Nothing is changed by it.</summary>
    public Task<bool> ReviewAllAsync() => editor.ShowAllAsync(set);

    /// <summary>Accept the file as it is. Never fails: there is nothing to write.</summary>
    public async Task KeepAsync(string path)
    {
        await editor.EndReviewAsync(path).ConfigureAwait(false);
        Forget(path);
    }

    /// <summary>Put the file back. Null on success, or what went wrong; the row stays on a failure,
    /// so the person can see it did not happen.</summary>
    public async Task<ChangeFailure> UndoAsync(string path)
    {
        if (!set.TryGet(path, out var row)) { return null; }
        var reason = await GuardAsync(row).ConfigureAwait(false);
        // Only now, when the file is going to be put back: a review is not ended for an Undo that is refused. And before the
        // file is rewritten, so that the review is not drawing against a document that is replaced under it.
        if (reason is null)
        {
            await editor.EndReviewAsync(path).ConfigureAwait(false);
            reason = await RestoreAsync(row).ConfigureAwait(false);
        }
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

    /// <summary>Why this is not the moment to put the file back, or null when it is. A row with no
    /// baseline is left to <see cref="RestoreAsync"/>, which says why it cannot be undone.</summary>
    private async Task<string> GuardAsync(FileChange row)
    {
        if (!row.Baseline.IsKnown) { return null; }

        // No async form: the pool, not the caller's thread, which is the UI thread when a button was clicked.
        var (length, written) = await Task.Run(() =>
        {
            var info = new FileInfo(row.Path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (0L, default(DateTime));
        }).ConfigureAwait(false);
        if (length != row.StampLength || written != row.StampWriteUtc)
        {
            if (remeasure is not null) { await remeasure(row.Path).ConfigureAwait(false); }
            // -1 is the stamp of a file the list could not read (locked, perhaps by a build) when it looked.
            return row.StampLength == -1
                ? "The list could not read this file when it last looked at it, so it has been measured again. Try again."
                : "The file changed after the list last looked at it, so it has been measured again. " +
                  "Check what is in it now, and undo it if you still want to.";
        }

        // Taking away a file the editor holds unsaved changes to would lose them with it.
        if (row.Baseline.IsEmpty && await editor.IsDirtyAsync(row.Path).ConfigureAwait(false))
        {
            return "The file has changes in the editor that are not saved, and undoing it deletes the file. " +
                   "Save or close it first.";
        }
        return null;
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
            // Through the editor only when the text is the file's exactly, and the editor would save it
            // with the same byte-order mark: a legacy code page decoded as UTF-8 loses its accents, and a
            // file whose mark was added or dropped since would come back without or with one it never had.
            var text = ChangeEvaluator.DecodeStrict(bytes);
            if (text is not null && await Task.Run(() => File.Exists(path)).ConfigureAwait(false)
                && await ChangeEvaluator.SameMarkAsync(bytes, path).ConfigureAwait(false))
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

            // Not open (or deleted, or not text this can hand back): the bytes go back as they were.
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
