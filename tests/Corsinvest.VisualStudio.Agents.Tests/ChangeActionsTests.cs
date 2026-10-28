/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class ChangeActionsTests : IDisposable
{
    private sealed class FakeEditor : IChangeEditor
    {
        public DocumentReplaceResult Result = DocumentReplaceResult.NotOpen;
        public readonly List<string> ReplacedWith = new();
        public readonly List<string> Closed = new();
        public readonly List<string> Opened = new();
        public readonly List<Diff> Diffs = new();

        public Task<DocumentReplaceResult> ReplaceDocumentTextAsync(string path, string text)
        {
            ReplacedWith.Add(text);
            return Task.FromResult(Result);
        }

        public Task CloseDocumentAsync(string path)
        {
            Closed.Add(path);
            return Task.CompletedTask;
        }

        public Task ShowDiffAsync(string key, string path, string before, string after, string leftLabel, string rightLabel)
        {
            Diffs.Add(new Diff { Key = key, Path = path, Before = before, After = after, Left = leftLabel, Right = rightLabel });
            return Task.CompletedTask;
        }

        public Task OpenFileAsync(string path)
        {
            Opened.Add(path);
            return Task.CompletedTask;
        }
    }

    private sealed class Diff
    {
        public string Key, Path, Before, After, Left, Right;
    }

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeEditor _editor = new();
    private readonly ChangeSet _set = new();
    private readonly ChangeActions _actions;
    private readonly ChangeBaselines _baselines;

    public ChangeActionsTests()
    {
        Directory.CreateDirectory(_folder);
        _baselines = new ChangeBaselines(Path.Combine(_folder, "own"), OutputWindowLogger.Global);
        _actions = new ChangeActions(_set, _baselines, _editor, OutputWindowLogger.Global);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, content);
        return path;
    }

    private string Write(string name, string content) => Write(name, new UTF8Encoding(false).GetBytes(content));

    private FileChange Row(string path, BaselineRef baseline, ChangeState state = ChangeState.Modified)
    {
        var row = new FileChange { Path = path, State = state, Baseline = baseline, Source = ChangeSource.Edit };
        _set.Add(row);
        return row;
    }

    [Fact]
    public async Task Keep_forgets_the_row_and_writes_nothing()
    {
        var baseline = Write("backup@v1", "old\n");
        var file = Write("a.txt", "new\n");
        Row(file, BaselineRef.Cli(baseline));

        await _actions.KeepAsync(file);

        Assert.Empty(_set.Snapshot());
        Assert.Equal("new\n", File.ReadAllText(file));
        Assert.True(File.Exists(baseline));
        // The CLI has no backup to offer for the next edit, so that one is compared with a copy.
        Assert.True(_set.WantsCapture(file));
    }

    [Fact]
    public async Task Keep_deletes_a_copy_the_extension_took_but_never_the_clis_backup()
    {
        var own = await _baselines.CaptureAsync("s1", Write("a.txt", "before\n"));
        var theirs = Write("backup@v1", "x\n");
        var fileA = Write("a.txt", "after\n");
        var fileB = Write("b.txt", "after\n");
        Row(fileA, own);
        Row(fileB, BaselineRef.Cli(theirs));

        await _actions.KeepAllAsync();

        Assert.False(File.Exists(own.Path));
        Assert.True(File.Exists(theirs));
        Assert.Empty(_set.Snapshot());
    }

    [Fact]
    public async Task Undo_of_a_closed_file_writes_the_baselines_bytes_back_exactly()
    {
        // CRLF, a byte-order mark and an accent: all of it must come back as it was, which a
        // decode and re-encode would not promise.
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("café\r\nline two\r\n")).ToArray();
        var baseline = Write("backup@v1", original);
        var file = Write("a.txt", "claude's version\n");
        Row(file, BaselineRef.Cli(baseline));

        var failure = await _actions.UndoAsync(file);

        Assert.Null(failure);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Empty(_set.Snapshot());
        Assert.True(_set.WantsCapture(file));
    }

    [Fact]
    public async Task Undo_of_an_open_file_goes_through_the_editor_and_leaves_the_disk_to_it()
    {
        var baseline = Write("backup@v1", "original\n");
        var file = Write("a.txt", "claude's version\n");
        Row(file, BaselineRef.Cli(baseline));
        _editor.Result = DocumentReplaceResult.Replaced;

        var failure = await _actions.UndoAsync(file);

        Assert.Null(failure);
        Assert.Equal(new[] { "original\n" }, _editor.ReplacedWith);
        // The editor saves it; this code did not touch the file.
        Assert.Equal("claude's version\n", File.ReadAllText(file));
        Assert.Empty(_set.Snapshot());
    }

    [Fact]
    public async Task A_file_the_editor_says_is_not_open_is_written_on_disk()
    {
        var baseline = Write("backup@v1", "original\n");
        var file = Write("a.txt", "changed\n");
        Row(file, BaselineRef.Cli(baseline));
        _editor.Result = DocumentReplaceResult.NotOpen;

        await _actions.UndoAsync(file);

        Assert.Equal("original\n", File.ReadAllText(file));
    }

    [Theory]
    [InlineData("ReplacedNotSaved")]
    [InlineData("Unsupported")]
    public async Task An_editor_that_cannot_restore_the_file_fails_the_undo_and_the_row_stays(string result)
    {
        var baseline = Write("backup@v1", "original\n");
        var file = Write("a.txt", "changed\n");
        Row(file, BaselineRef.Cli(baseline));
        _editor.Result = (DocumentReplaceResult)Enum.Parse(typeof(DocumentReplaceResult), result);

        var failure = await _actions.UndoAsync(file);

        Assert.NotNull(failure);
        Assert.Equal(file, failure.Path);
        Assert.NotEmpty(failure.Reason);
        Assert.Single(_set.Snapshot());
        Assert.Equal("changed\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Undo_of_a_file_Claude_created_closes_it_and_deletes_it()
    {
        var file = Write("made.txt", "new\n");
        Row(file, BaselineRef.Empty, ChangeState.Added);

        var failure = await _actions.UndoAsync(file);

        Assert.Null(failure);
        Assert.False(File.Exists(file));
        Assert.Equal(new[] { file }, _editor.Closed);
        Assert.Empty(_set.Snapshot());
    }

    [Fact]
    public async Task Undo_of_a_file_Claude_deleted_brings_it_back_with_its_folder()
    {
        var baseline = Write("backup@v1", "kept in the backup\n");
        var file = Path.Combine(_folder, "gone", "deep", "a.txt");
        Row(file, BaselineRef.Cli(baseline), ChangeState.Deleted);

        var failure = await _actions.UndoAsync(file);

        Assert.Null(failure);
        Assert.Equal("kept in the backup\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_file_with_no_baseline_cannot_be_undone_and_says_why()
    {
        var file = Write("a.txt", "x\n");
        Row(file, BaselineRef.None);

        var failure = await _actions.UndoAsync(file);

        Assert.Contains("no copy", failure.Reason);
        Assert.Single(_set.Snapshot());
    }

    [Fact]
    public async Task A_pruned_backup_is_told_apart_from_none()
    {
        var file = Write("a.txt", "x\n");
        Row(file, BaselineRef.None);
        _set.Update(file, r => r.BaselineMissing = true);

        var failure = await _actions.UndoAsync(file);

        Assert.Contains("is gone", failure.Reason);
    }

    [Fact]
    public async Task Undo_of_a_path_that_is_not_listed_does_nothing()
    {
        var file = Write("elsewhere.txt", "x\n");

        Assert.Null(await _actions.UndoAsync(file));
        Assert.Equal("x\n", File.ReadAllText(file));
        Assert.Empty(_editor.ReplacedWith);
        Assert.Empty(_editor.Closed);
    }

    [Fact]
    public async Task Undo_all_carries_on_past_a_file_it_cannot_restore_and_reports_it()
    {
        var okBaseline = Write("ok@v1", "ok original\n");
        var fine = Write("a.txt", "changed\n");
        var stuck = Write("b.txt", "changed\n");
        var third = Write("c.txt", "changed\n");
        Row(fine, BaselineRef.Cli(okBaseline));
        Row(stuck, BaselineRef.None);
        Row(third, BaselineRef.Cli(okBaseline));

        var failed = await _actions.UndoAllAsync();

        var one = Assert.Single(failed);
        Assert.Equal(stuck, one.Path);
        Assert.Equal("ok original\n", File.ReadAllText(fine));
        Assert.Equal("ok original\n", File.ReadAllText(third));
        Assert.Equal(new[] { stuck }, _set.Snapshot().Select(r => r.Path));
    }

    [Fact]
    public async Task Undo_of_a_file_that_is_locked_fails_with_the_reason_and_the_row_stays()
    {
        var baseline = Write("backup@v1", "original\n");
        var file = Write("a.txt", "changed\n");
        Row(file, BaselineRef.Cli(baseline));

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failure = await _actions.UndoAsync(file);

            Assert.NotNull(failure);
            Assert.Single(_set.Snapshot());
        }
    }

    [Fact]
    public async Task Open_shows_the_file_against_how_it_was_before_Claude()
    {
        var baseline = Write("backup@v1", "before\n");
        var file = Write("a.txt", "after\n");
        Row(file, BaselineRef.Cli(baseline));

        await _actions.OpenAsync(file);

        var diff = Assert.Single(_editor.Diffs);
        Assert.Equal("changes:" + file, diff.Key);
        Assert.Equal(file, diff.Path);
        Assert.Equal("before\n", diff.Before);
        Assert.Equal("after\n", diff.After);
        Assert.Equal("Before Claude", diff.Left);
        Assert.Equal("Current", diff.Right);
        Assert.Empty(_editor.Opened);
    }

    [Fact]
    public async Task Open_of_a_created_file_compares_with_nothing_and_of_a_deleted_one_with_nothing_left()
    {
        var created = Write("made.txt", "new\n");
        var baseline = Write("backup@v1", "was here\n");
        var deleted = Path.Combine(_folder, "removed.txt");
        Row(created, BaselineRef.Empty, ChangeState.Added);
        Row(deleted, BaselineRef.Cli(baseline), ChangeState.Deleted);

        await _actions.OpenAsync(created);
        await _actions.OpenAsync(deleted);

        Assert.Equal("", _editor.Diffs[0].Before);
        Assert.Equal("new\n", _editor.Diffs[0].After);
        Assert.Equal("was here\n", _editor.Diffs[1].Before);
        Assert.Equal("", _editor.Diffs[1].After);
    }

    [Fact]
    public async Task Open_without_a_baseline_or_for_binary_content_opens_the_file_itself()
    {
        var noBaseline = Write("a.txt", "x\n");
        var binaryBackup = Write("bin@v1", new byte[] { 1, 0, 2 });
        var binary = Write("b.bin", new byte[] { 1, 0, 3 });
        Row(noBaseline, BaselineRef.None);
        Row(binary, BaselineRef.Cli(binaryBackup));

        await _actions.OpenAsync(noBaseline);
        await _actions.OpenAsync(binary);

        Assert.Equal(new[] { noBaseline, binary }, _editor.Opened);
        Assert.Empty(_editor.Diffs);
    }

    [Fact]
    public async Task Open_of_a_path_that_is_not_listed_opens_nothing()
    {
        await _actions.OpenAsync(Write("elsewhere.txt", "x\n"));

        Assert.Empty(_editor.Opened);
        Assert.Empty(_editor.Diffs);
    }
}
