/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Newtonsoft.Json.Linq;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>What a shell command changed, found by a git snapshot and a watcher, against real git
/// repositories and real commands: the point is that the pieces agree with each other and with the
/// operating system, which no fake could show.</summary>
public sealed class BashChangesTests : IDisposable
{
    private sealed class NoEditor : IChangeEditor
    {
        public Task<DocumentReplaceResult> ReplaceDocumentTextAsync(string path, string text) => Task.FromResult(DocumentReplaceResult.NotOpen);
        public Task CloseDocumentAsync(string path) => Task.CompletedTask;
        public Task ShowDiffAsync(string key, string path, string before, string after, string leftLabel, string rightLabel) => Task.CompletedTask;
        public Task OpenFileAsync(string path) => Task.CompletedTask;
    }

    private readonly GitRepoFixture _repo;
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));
    private int _calls;

    public BashChangesTests() => _repo = GitRepoFixture.Available ? new GitRepoFixture() : null;

    public void Dispose()
    {
        _repo?.Dispose();
        try { Directory.Delete(_scratch, true); }
        catch (Exception) { /* absent when nothing was copied */ }
    }

    private SessionChanges Create(string workingDirectory, int? maxWatchedPaths = null, Func<bool> trackCommands = null)
    {
        var claude = new ClaudePaths(Path.Combine(_scratch, "claude"));
        return new SessionChanges(claude, workingDirectory, new SessionChangesOptions
        {
            Baselines = new ChangeBaselines(Path.Combine(_scratch, "baselines"), OutputWindowLogger.Global),
            SnapshotsRoot = Path.Combine(_scratch, "snapshots"),
            Editor = new NoEditor(),
            MaxWatchedPaths = maxWatchedPaths,
            TrackCommands = trackCommands ?? (() => true),
        }, OutputWindowLogger.Global);
    }

    private HookBash Call(string workingDirectory)
        => HookBash.TryParse(new JObject { ["session_id"] = "s1", ["cwd"] = workingDirectory }, "toolu_bash_" + ++_calls);

    /// <summary>Run a real command, as the CLI's Bash tool would, between the two hooks.</summary>
    private static void Shell(string workingDirectory, string command)
    {
        var info = new ProcessStartInfo("cmd.exe", "/c " + command)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info);
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    /// <summary>Begin, run the command, end: one shell call as the pane sees it.</summary>
    private async Task RunAsync(SessionChanges changes, string command)
    {
        var call = Call(_repo.Root);
        await changes.OnBashBeginAsync(call);
        Shell(_repo.Root, command);
        await changes.OnBashEndAsync(call);
    }

    private static async Task<SessionChanges> StartedAsync(SessionChanges changes)
    {
        changes.StartTracking();
        // The first picture is taken in the background; the first command's hook waits for it, so a
        // test that begins at once needs nothing more.
        await Task.CompletedTask;
        return changes;
    }

    [Fact]
    public async Task A_command_that_edits_a_file_lists_it_against_the_file_as_it_was_before()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "one\ntwo\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "echo three>> a.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(_repo.Path_("a.txt"), row.Path, ignoreCase: true);
        Assert.Equal(ChangeSource.Bash, row.Source);
        Assert.Equal(ChangeState.Modified, row.State);
        Assert.Equal(1, row.Added);
        Assert.Equal(0, row.Deleted);
        Assert.True(row.Baseline.Owned);
        Assert.Equal("one\ntwo\n", File.ReadAllText(row.Baseline.Path));
    }

    [Fact]
    public async Task A_command_that_creates_a_file_lists_it_as_added()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "echo hello> made.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.EndsWith("made.txt", row.Path);
        Assert.Equal(ChangeState.Added, row.State);
        Assert.True(row.Baseline.IsEmpty);
    }

    [Fact]
    public async Task A_command_that_deletes_a_file_lists_it_and_undo_brings_it_back()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "precious\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "del a.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(ChangeState.Deleted, row.State);
        Assert.False(File.Exists(row.Path));

        Assert.Null(await changes.Actions.UndoAsync(row.Path));
        Assert.Equal("precious\n", File.ReadAllText(row.Path));
        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task Build_output_the_gitignore_names_is_not_listed_but_source_is()
    {
        if (_repo is null) { return; }
        _repo.Write(".gitignore", "obj/\n");
        _repo.Write("src.txt", "s\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "mkdir obj && echo built> obj\\out.dll && echo more>> src.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.EndsWith("src.txt", row.Path);
    }

    [Fact]
    public async Task A_command_that_only_reads_lists_nothing()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "type a.txt && dir");

        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task A_file_rewritten_with_what_it_already_held_is_not_a_change()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "same\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        var call = Call(_repo.Root);
        await changes.OnBashBeginAsync(call);
        _repo.Write("a.txt", "same\n");
        await changes.OnBashEndAsync(call);

        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task A_file_the_user_edited_before_the_command_is_not_blamed_on_it()
    {
        if (_repo is null) { return; }
        _repo.Write("mine.txt", "v1\n");
        _repo.Write("claude.txt", "c1\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));
        await RunAsync(changes, "echo nothing>NUL");

        // The user edits one file; then a command changes the other.
        _repo.Write("mine.txt", "v2, edited by hand\n");
        // The watcher reports a moment after the write; a person's save is seconds before the next command.
        await Task.Delay(300);
        await RunAsync(changes, "echo c2>> claude.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.EndsWith("claude.txt", row.Path);
    }

    [Fact]
    public async Task A_file_the_user_edited_and_the_command_then_changed_is_compared_with_the_users_version()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "committed\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));
        await RunAsync(changes, "echo nothing>NUL");

        _repo.Write("a.txt", "committed\nthe user added this\n");
        await Task.Delay(300);
        await RunAsync(changes, "echo the command added this>> a.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        // Undo must go back to what the person had, not to the commit, which would throw their line away.
        Assert.Equal("committed\nthe user added this\n", File.ReadAllText(row.Baseline.Path));
        Assert.Equal(1, row.Added);
    }

    [Fact]
    public async Task A_file_with_windows_line_endings_comes_back_byte_for_byte()
    {
        if (_repo is null) { return; }
        using var crlf = new GitRepoFixture(autocrlf: true);
        crlf.Write("a.txt", "line one\nline two\n", crlf: true);
        crlf.Commit();
        var original = File.ReadAllBytes(crlf.Path_("a.txt"));
        Assert.Contains((byte)'\r', original);
        using var changes = await StartedAsync(Create(crlf.Root));

        var call = Call(crlf.Root);
        await changes.OnBashBeginAsync(call);
        Shell(crlf.Root, "echo changed> a.txt");
        await changes.OnBashEndAsync(call);

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Null(await changes.Actions.UndoAsync(row.Path));
        // The blob git stores is LF: the file on disk is CRLF, and it has to be CRLF again.
        Assert.Equal(original, File.ReadAllBytes(crlf.Path_("a.txt")));
    }

    [Fact]
    public async Task A_file_the_edit_tools_already_changed_keeps_its_own_row()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "one\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));
        var existing = new FileChange
        {
            Path = _repo.Path_("a.txt"),
            Source = ChangeSource.Edit,
            Baseline = BaselineRef.Empty,
            State = ChangeState.Added,
        };
        changes.Set.Add(existing);

        await RunAsync(changes, "echo two>> a.txt");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(ChangeSource.Edit, row.Source);
    }

    [Fact]
    public async Task A_command_that_touches_more_than_the_watcher_can_hold_is_found_through_git_and_flagged()
    {
        if (_repo is null) { return; }
        for (var i = 0; i < 6; i++) { _repo.Write("f" + i + ".txt", "x\n"); }
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root, maxWatchedPaths: 2));

        await RunAsync(changes, "for %i in (0 1 2 3 4 5) do echo changed>> f%i.txt");

        var rows = changes.Set.Snapshot();
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.True(r.Incomplete));
        Assert.All(rows, r => Assert.Equal(1, r.Added));
    }

    [Fact]
    public async Task A_folder_that_is_not_in_a_repository_is_not_tracked()
    {
        if (_repo is null) { return; }
        var plain = Path.Combine(_scratch, "plain");
        Directory.CreateDirectory(plain);
        File.WriteAllText(Path.Combine(plain, "a.txt"), "a\n");
        using var changes = Create(plain);
        changes.StartTracking();

        var call = Call(plain);
        await changes.OnBashBeginAsync(call);
        File.AppendAllText(Path.Combine(plain, "a.txt"), "more\n");
        await changes.OnBashEndAsync(call);

        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task A_folder_that_is_not_in_a_repository_says_why_commands_are_not_followed()
    {
        if (_repo is null) { return; }
        var plain = Path.Combine(_scratch, "plain-note");
        Directory.CreateDirectory(plain);
        using var changes = Create(plain);
        changes.StartTracking();

        // The first command's hook waits for the look at the folder, so the answer is in by then.
        await changes.OnBashBeginAsync(Call(plain));

        Assert.Equal(BashTracker.NoRepositoryNote, changes.CommandsNote);
    }

    [Fact]
    public async Task A_repository_has_no_note_and_the_setting_off_says_so()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var follow = true;
        using var changes = Create(_repo.Root, trackCommands: () => follow);
        changes.StartTracking();
        await changes.OnBashBeginAsync(Call(_repo.Root));

        Assert.Null(changes.CommandsNote);

        follow = false;
        Assert.Equal(SessionChanges.CommandsOffNote, changes.CommandsNote);
    }

    [Fact]
    public async Task Disposing_removes_the_snapshot_store()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var changes = await StartedAsync(Create(_repo.Root));
        await RunAsync(changes, "echo b>> a.txt");
        var snapshots = Path.Combine(_scratch, "snapshots");
        Assert.True(Directory.Exists(snapshots));

        changes.Dispose();

        Assert.True(SpinWait_Until(() => !Directory.Exists(snapshots) || !Directory.EnumerateDirectories(snapshots).Any()));
    }

    private static bool SpinWait_Until(Func<bool> condition)
        => System.Threading.SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(10));
}
