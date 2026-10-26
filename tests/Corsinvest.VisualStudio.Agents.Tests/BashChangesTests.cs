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
        public Task<bool> IsDirtyAsync(string path) => Task.FromResult(false);
        public Task ShowDiffAsync(string key, string path, string before, string after, string leftLabel, string rightLabel) => Task.CompletedTask;
        public Task OpenFileAsync(string path) => Task.CompletedTask;
        public Task<bool> ShowInPlaceAsync(ChangeSet set, string path) => Task.FromResult(false);
        public Task EndReviewAsync(string path) => Task.CompletedTask;
        public Task<bool> ShowAllAsync(ChangeSet set) => Task.FromResult(false);
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

    private SessionChanges Create(string workingDirectory, int? maxRowsPerCommand = null, Func<bool> trackCommands = null,
                                  Func<bool> checkpoints = null, long? maxBaselineBytes = null)
    {
        var claude = new ClaudePaths(Path.Combine(_scratch, "claude"));
        return new SessionChanges(claude, workingDirectory, new SessionChangesOptions
        {
            Baselines = new ChangeBaselines(Path.Combine(_scratch, "baselines"), OutputWindowLogger.Global),
            SnapshotsRoot = Path.Combine(_scratch, "snapshots"),
            Editor = new NoEditor(),
            MaxRowsPerCommand = maxRowsPerCommand,
            TrackCommands = trackCommands ?? (() => true),
            FileCheckpoints = checkpoints ?? (() => true),
            MaxBaselineBytes = maxBaselineBytes,
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
    public async Task A_command_in_a_linked_worktree_is_followed_like_any_other()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "one\n");
        _repo.Commit();
        // The usual way to work on a second branch: `.git` there is a file, and the index is not the main one.
        var linked = Path.Combine(_repo.Scratch, "linked");
        _repo.Git("worktree", "add", "-q", "-b", "other", linked);
        using var changes = await StartedAsync(Create(linked));

        var call = Call(linked);
        await changes.OnBashBeginAsync(call);
        Shell(linked, "echo two>> a.txt");
        await changes.OnBashEndAsync(call);

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(Path.Combine(linked, "a.txt"), row.Path, ignoreCase: true);
        Assert.Equal(ChangeSource.Bash, row.Source);
        Assert.Equal(1, row.Added);
        Assert.Equal("one\n", File.ReadAllText(row.Baseline.Path));
        Assert.Equal("one\n", File.ReadAllText(_repo.Path_("a.txt")));
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
    public async Task A_command_that_changes_more_files_than_the_cap_lists_the_first_ones_and_flags_them()
    {
        if (_repo is null) { return; }
        for (var i = 0; i < 6; i++) { _repo.Write("f" + i + ".txt", "x\n"); }
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root, maxRowsPerCommand: 4));

        await RunAsync(changes, "for %i in (0 1 2 3 4 5) do echo changed>> f%i.txt");

        var rows = changes.Set.Snapshot();
        Assert.Equal(new[] { "f0.txt", "f1.txt", "f2.txt", "f3.txt" }, rows.Select(r => Path.GetFileName(r.Path)));
        Assert.All(rows, r => Assert.True(r.Incomplete));
        Assert.All(rows, r => Assert.Equal(1, r.Added));
    }

    [Fact]
    public async Task What_the_person_saved_while_a_command_waited_for_permission_is_not_the_commands()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Write("mine.txt", "mine\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));
        var call = Call(_repo.Root);

        // The hook runs when the question is asked, before the person has answered.
        await changes.OnBashBeginAsync(call);
        File.AppendAllText(_repo.Path_("mine.txt"), "typed while the question was open\n");
        await System.Threading.Tasks.Task.Delay(400);
        // The answer: the command starts now.
        await changes.OnBashAllowedAsync(call.ToolUseId);
        Shell(_repo.Root, "echo b>> a.txt");
        await changes.OnBashEndAsync(call);

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(_repo.Path_("a.txt"), row.Path, ignoreCase: true);
        Assert.Equal("a\n", File.ReadAllText(row.Baseline.Path));
    }

    [Fact]
    public async Task Allowing_a_command_that_was_never_begun_changes_nothing()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await changes.OnBashAllowedAsync("toolu_never_seen");

        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task A_file_whose_name_changed_case_on_disk_is_a_change_and_not_a_new_file()
    {
        if (_repo is null) { return; }
        _repo.Git("config", "core.ignorecase", "true");
        _repo.Write("Readme.md", "one\n");
        _repo.Commit();
        // A rename that only changes the case: git, on Windows, does not see one.
        File.Move(_repo.Path_("Readme.md"), _repo.Path_("README.tmp"));
        File.Move(_repo.Path_("README.tmp"), _repo.Path_("README.md"));
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "echo two>> README.md");

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(ChangeState.Modified, row.State);
        Assert.True(row.Baseline.Owned);
        Assert.Equal("one\n", File.ReadAllText(row.Baseline.Path));
    }

    [Fact]
    public async Task A_file_another_program_held_open_at_the_start_is_not_taken_for_one_the_command_made()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var busy = _repo.Write("busy.dat", "held\n");
        var hold = new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var changes = await StartedAsync(Create(_repo.Root));
        var call = Call(_repo.Root);

        // The picture before cannot read busy.dat; the one after can. Told apart from a new file by
        // knowing which paths git could not read, and Undo would otherwise delete a file that was always there.
        await changes.OnBashBeginAsync(call);
        hold.Dispose();
        Shell(_repo.Root, "echo b>> a.txt");
        await changes.OnBashEndAsync(call);

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(_repo.Path_("a.txt"), row.Path, ignoreCase: true);
    }

    [Fact]
    public async Task A_file_the_gitignore_hid_until_the_command_changed_the_rules_is_not_taken_for_a_new_one()
    {
        if (_repo is null) { return; }
        _repo.Write(".gitignore", "secret.env\n");
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        // The person's own file, there for a long time and ignored: absent from the picture before.
        var secret = _repo.Write("secret.env", "KEY=1\n");
        File.SetCreationTimeUtc(secret, DateTime.UtcNow.AddMinutes(-10));
        using var changes = await StartedAsync(Create(_repo.Root));

        // The command rewrites the rules, and the file is no longer ignored.
        await RunAsync(changes, "echo # nothing is ignored now> .gitignore");

        var rows = changes.Set.Snapshot().ToDictionary(r => Path.GetFileName(r.Path), StringComparer.OrdinalIgnoreCase);
        Assert.True(rows.ContainsKey(".gitignore"));
        // Listed, because it did appear in the picture after, but with no copy to restore: Undo would
        // otherwise delete a file that was always there.
        Assert.True(rows.ContainsKey("secret.env"));
        Assert.False(rows["secret.env"].Baseline.IsKnown);
        Assert.False(rows["secret.env"].Baseline.IsEmpty);
    }

    [Fact]
    public async Task A_command_that_changes_more_files_than_the_list_can_hold_fills_it_and_says_so()
    {
        if (_repo is null) { return; }
        for (var i = 0; i < 4; i++) { _repo.Write("f" + i + ".txt", "x\n"); }
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));
        // Room for two more.
        for (var i = 0; i < ChangeSet.MaxFiles - 2; i++)
        {
            changes.Set.Add(new FileChange { Path = Path.Combine(_scratch, "other" + i + ".txt"), State = ChangeState.Modified });
        }

        await RunAsync(changes, "for %i in (0 1 2 3) do echo changed>> f%i.txt");

        Assert.Equal(ChangeSet.MaxFiles, changes.Set.Count);
        Assert.True(changes.Set.Overflow);
        // The copies of the files that did not fit are not left behind.
        var copies = Directory.Exists(Path.Combine(_scratch, "baselines"))
            ? Directory.GetFiles(Path.Combine(_scratch, "baselines"), "*", SearchOption.AllDirectories).Length
            : 0;
        Assert.Equal(2, copies);
    }

    [Fact]
    public async Task Following_commands_reads_nothing_until_the_chat_is_about_to_work()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var snapshots = Path.Combine(_scratch, "snapshots");
        using var changes = Create(_repo.Root);

        changes.StartTracking();
        await System.Threading.Tasks.Task.Delay(1500);
        Assert.False(Directory.Exists(snapshots));

        changes.WarmUp();
        Assert.True(SpinWait_Until(() => Directory.Exists(snapshots) && Directory.EnumerateDirectories(snapshots).Any()));
    }

    [Fact]
    public async Task A_file_a_second_command_changes_again_is_measured_again_and_one_it_puts_back_leaves_the_list()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "one\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));

        await RunAsync(changes, "echo two>> a.txt");
        Assert.Equal(1, Assert.Single(changes.Set.Snapshot()).Added);

        // The row keeps its baseline, from before the first command, and its numbers move.
        await RunAsync(changes, "echo three>> a.txt");
        Assert.Equal(2, Assert.Single(changes.Set.Snapshot()).Added);

        var call = Call(_repo.Root);
        await changes.OnBashBeginAsync(call);
        _repo.Git("checkout", "--", "a.txt");
        await changes.OnBashEndAsync(call);

        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task A_command_that_ends_in_another_conversation_than_it_began_in_adds_nothing()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var set = new ChangeSet();
        set.Reset("s1");
        var baselines = new ChangeBaselines(Path.Combine(_scratch, "baselines"), OutputWindowLogger.Global);
        using var tracker = await BashTracker.StartAsync(_repo.Root, Path.Combine(_scratch, "snapshots", "direct-session"), set, baselines, OutputWindowLogger.Global);
        await tracker.SnapshotAsync();

        await tracker.BeginAsync("toolu_old");
        File.AppendAllText(_repo.Path_("a.txt"), "command\n");
        // The person starts another conversation while it runs.
        set.Reset("s2");
        await tracker.EndAsync("toolu_old", "s2");

        Assert.Empty(set.Snapshot());
    }

    [Fact]
    public async Task Warming_up_does_nothing_while_the_list_itself_is_off()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var snapshots = Path.Combine(_scratch, "snapshots");
        using var changes = Create(_repo.Root, checkpoints: () => false);

        changes.WarmUp();
        await System.Threading.Tasks.Task.Delay(1500);

        Assert.False(changes.FollowsCommands);
        Assert.False(Directory.Exists(snapshots));
    }

    [Fact]
    public async Task A_full_list_still_measures_the_files_it_has_when_a_command_changes_them_again()
    {
        if (_repo is null) { return; }
        _repo.Write("z.txt", "one\n");
        _repo.Write("0.txt", "x\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root));
        await RunAsync(changes, "echo two>> z.txt");
        for (var i = 0; i < ChangeSet.MaxFiles - 1; i++)
        {
            changes.Set.Add(new FileChange { Path = Path.Combine(_scratch, "other" + i + ".txt"), State = ChangeState.Modified });
        }
        Assert.True(changes.Set.IsFull);

        // A new file that sorts first, and the listed one changed again: the first finds no room, which
        // must not stop the second being measured.
        await RunAsync(changes, "echo new> 0new.txt & echo three>> z.txt");

        var z = changes.Set.Snapshot().Single(r => r.Path.EndsWith("z.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, z.Added);
        Assert.True(changes.Set.Overflow);
    }

    [Fact]
    public async Task A_file_larger_than_the_limit_is_listed_without_a_copy_so_it_cannot_be_undone()
    {
        if (_repo is null) { return; }
        _repo.Write("big.dat", new string('x', 100) + "\n");
        _repo.Write("small.txt", "s\n");
        _repo.Commit();
        using var changes = await StartedAsync(Create(_repo.Root, maxBaselineBytes: 50));

        await RunAsync(changes, "echo more>> big.dat & echo more>> small.txt");

        var rows = changes.Set.Snapshot().ToDictionary(r => Path.GetFileName(r.Path), StringComparer.OrdinalIgnoreCase);
        Assert.False(rows["big.dat"].Baseline.IsKnown);
        Assert.True(rows["small.txt"].Baseline.Owned);
    }

    [Theory]
    [InlineData("fatal: not a git repository (or any of the parent directories): .git\n", "NoRepositoryNote")]
    [InlineData("fatal: detected dubious ownership in repository at 'D:/x'\n", "NotYoursNote")]
    [InlineData("", "NoAnswerNote")]
    [InlineData(null, "NoAnswerNote")]
    public void What_git_said_when_it_would_not_open_the_folder_is_told_apart(string said, string note)
    {
        var expected = note switch
        {
            "NoRepositoryNote" => BashTracker.NoRepositoryNote,
            "NotYoursNote" => BashTracker.NotYoursNote,
            _ => BashTracker.NoAnswerNote,
        };

        Assert.Equal(expected, BashTracker.FailureNote(said));
    }

    [Fact]
    public void Something_else_git_said_is_passed_on_in_its_own_words()
    {
        var note = BashTracker.FailureNote("fatal: unable to access the thing\nmore detail\n");

        Assert.Contains("git said fatal: unable to access the thing", note);
        Assert.DoesNotContain("more detail", note);
    }

    [Fact]
    public async Task A_command_that_could_not_wait_for_its_snapshot_is_not_followed_rather_than_followed_wrongly()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var set = new ChangeSet();
        set.Reset("s1");
        var baselines = new ChangeBaselines(Path.Combine(_scratch, "baselines"), OutputWindowLogger.Global);
        using var tracker = await BashTracker.StartAsync(_repo.Root, Path.Combine(_scratch, "snapshots", "direct"), set, baselines, OutputWindowLogger.Global);
        await tracker.SnapshotAsync();
        // The person's own edit makes the picture stale, and there is no time to take another.
        File.AppendAllText(_repo.Path_("a.txt"), "typed\n");
        await System.Threading.Tasks.Task.Delay(400);

        await tracker.BeginAsync("toolu_late", TimeSpan.Zero);
        File.AppendAllText(_repo.Path_("a.txt"), "command\n");
        await tracker.EndAsync("toolu_late", "s1");

        Assert.Empty(set.Snapshot());
    }

    [Fact]
    public async Task A_command_that_was_let_go_before_its_snapshot_was_ready_is_not_followed()
    {
        if (_repo is null) { return; }
        _repo.Write("a.txt", "a\n");
        _repo.Commit();
        var set = new ChangeSet();
        set.Reset("s1");
        var baselines = new ChangeBaselines(Path.Combine(_scratch, "baselines"), OutputWindowLogger.Global);
        using var tracker = await BashTracker.StartAsync(_repo.Root, Path.Combine(_scratch, "snapshots", "direct-late"), set, baselines, OutputWindowLogger.Global);
        await tracker.SnapshotAsync();
        // The hook has already answered the CLI, which is running the command: whatever is photographed
        // now was photographed with the command running.
        using var letGo = new System.Threading.CancellationTokenSource();
        letGo.Cancel();

        await tracker.BeginAsync("toolu_gone", late: letGo.Token);
        File.AppendAllText(_repo.Path_("a.txt"), "command\n");
        await tracker.EndAsync("toolu_gone", "s1");

        Assert.Empty(set.Snapshot());
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
