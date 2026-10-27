/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class SessionChangesTests : IDisposable
{
    private readonly string _baselinesRoot = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        foreach (var folder in new[] { _baselinesRoot, KeptFolder })
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { /* a scratch folder; absent when nothing was copied or kept */ }
        }
    }

    // Beside the copies and not under them: CopiesTaken counts everything under the root.
    private string KeptFolder => _baselinesRoot + "-kept";

    private SessionChanges Create(SessionFixture fx, Func<bool> checkpoints = null, int[] poll = null, Func<string, Task> prepare = null,
                                  KeptLedger kept = null)
        => new(fx.Paths, fx.WorkingDirectory, new SessionChangesOptions
        {
            FileCheckpoints = checkpoints ?? (() => true),
            PollMilliseconds = poll ?? new[] { 0, 20, 40, 80 },
            PrepareFile = prepare,
            Baselines = new ChangeBaselines(_baselinesRoot, OutputWindowLogger.Global),
            Kept = kept ?? new KeptLedger(KeptFolder, OutputWindowLogger.Global),
        }, OutputWindowLogger.Global);

    private static HookEdit Hook(SessionFixture fx, string file, string toolUseId, string agentId = null, string sessionId = null)
    {
        var input = new JObject
        {
            ["session_id"] = sessionId ?? fx.SessionId,
            ["transcript_path"] = fx.TranscriptPath,
            ["cwd"] = fx.WorkingDirectory,
            ["tool_name"] = "Edit",
            ["tool_input"] = new JObject { ["file_path"] = Path.Combine(fx.WorkingDirectory, file) },
        };
        if (agentId is not null) { input["agent_id"] = agentId; }
        return HookEdit.TryParse(input, toolUseId);
    }

    private static string Work(SessionFixture fx, string file) => Path.Combine(fx.WorkingDirectory, file);

    private static SessionFixture Transcript(params JObject[] lines) => SessionFixture.Compact(lines);

    private int CopiesTaken => Directory.Exists(_baselinesRoot)
        ? Directory.GetFiles(_baselinesRoot, "*", SearchOption.AllDirectories).Length
        : 0;

    [Fact]
    public async Task An_edit_the_cli_backed_up_is_listed_against_its_backup()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        fx.WriteBackup("h@v1", "one\ntwo\n");
        fx.WriteWork("a.txt", "one\nTWO\nthree\n");
        using var changes = Create(fx);

        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(Work(fx, "a.txt"), row.Path);
        Assert.Equal(ChangeState.Modified, row.State);
        Assert.Equal(2, row.Added);
        Assert.Equal(1, row.Deleted);
        Assert.False(row.Baseline.Owned);
        Assert.Equal(Path.Combine(fx.Paths.FileHistoryFolder, fx.SessionId, "h@v1"), row.Baseline.Path);
        Assert.Equal("toolu_1", row.FirstToolUseId);
    }

    [Fact]
    public async Task A_file_the_cli_found_missing_is_listed_as_added()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("new.txt", null, 1));
        fx.WriteWork("new.txt", "a\nb\nc\n");
        using var changes = Create(fx);

        await changes.OnPostEditAsync(Hook(fx, "new.txt", "toolu_1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(ChangeState.Added, row.State);
        Assert.Equal(3, row.Added);
        Assert.True(row.Baseline.IsEmpty);
    }

    [Fact]
    public async Task A_record_the_cli_writes_a_moment_after_the_hook_is_waited_for()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteBackup("h@v1", "a\n");
        fx.WriteWork("a.txt", "b\n");
        using var changes = Create(fx, poll: new[] { 0, 50, 100, 200, 400, 800 });

        var pending = changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        await Task.Delay(120);
        fx.Append(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        await pending;

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.True(row.Baseline.IsKnown);
        Assert.Equal(1, row.Added);
        Assert.Equal(1, row.Deleted);
    }

    [Fact]
    public async Task A_file_the_cli_has_no_record_of_is_listed_without_a_baseline()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "x\n");
        using var changes = Create(fx, poll: new[] { 0, 10 });

        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.False(row.Baseline.IsKnown);
        Assert.Equal(CountsKind.Unavailable, row.Counts);
        Assert.Equal(ChangeState.Modified, row.State);
    }

    [Fact]
    public async Task A_file_tracked_in_an_earlier_turn_is_found_in_the_history()
    {
        // Only a snapshot remembers it: no fresh delta, because the CLI writes one the first time only.
        using var fx = Transcript(Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("a.txt", "h@v2", 2)));
        fx.WriteBackup("h@v2", "before\n");
        fx.WriteWork("a.txt", "after\n");
        using var changes = Create(fx, poll: new[] { 0, 10 });

        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.EndsWith("h@v2", row.Baseline.Path);
        Assert.Equal(1, row.Added);
    }

    [Fact]
    public async Task A_backup_the_cli_has_pruned_is_reported_not_guessed()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "gone@v1", 1));
        fx.WriteWork("a.txt", "x\n");
        using var changes = Create(fx);

        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.True(row.BaselineMissing);
        Assert.False(row.Baseline.IsKnown);
    }

    [Fact]
    public async Task A_second_edit_moves_the_numbers_and_keeps_the_first_baseline()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        fx.WriteBackup("h@v1", "one\n");
        fx.WriteWork("a.txt", "one\ntwo\n");
        using var changes = Create(fx);
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        fx.WriteWork("a.txt", "one\ntwo\nthree\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_2"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(2, row.Added);
        Assert.Equal("toolu_1", row.FirstToolUseId);
        Assert.Equal("toolu_2", row.LastToolUseId);
        Assert.EndsWith("h@v1", row.Baseline.Path);
    }

    [Fact]
    public async Task A_file_edited_back_to_its_baseline_leaves_the_list_and_wants_a_copy_next_time()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        fx.WriteBackup("h@v1", "one\n");
        fx.WriteWork("a.txt", "one\ntwo\n");
        using var changes = Create(fx);
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        fx.WriteWork("a.txt", "one\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_2"));

        Assert.Empty(changes.Set.Snapshot());
        // The CLI writes no backup for the next edit of a file it already tracks.
        Assert.True(changes.Set.WantsCapture(Work(fx, "a.txt")));
    }

    [Fact]
    public async Task A_sub_agents_edit_is_compared_with_the_copy_taken_before_it_ran()
    {
        // The CLI keeps no history of a sub-agent's edit, so there is no record to wait for.
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "before\n");
        using var changes = Create(fx, poll: new[] { 0 });

        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));
        Assert.Equal(1, CopiesTaken);
        fx.WriteWork("a.txt", "after\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.True(row.Baseline.Owned);
        Assert.Equal("before\n", File.ReadAllText(row.Baseline.Path));
        Assert.Equal(ChangeState.Modified, row.State);
        Assert.Equal(1, row.Added);
        Assert.Equal(1, row.Deleted);
    }

    [Fact]
    public async Task A_sub_agent_creating_a_file_is_an_added_file()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        using var changes = Create(fx, poll: new[] { 0 });

        await changes.OnPreEditAsync(Hook(fx, "made.txt", "toolu_1", agentId: "agent1"));
        fx.WriteWork("made.txt", "a\nb\n");
        await changes.OnPostEditAsync(Hook(fx, "made.txt", "toolu_1", agentId: "agent1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(ChangeState.Added, row.State);
        Assert.Equal(2, row.Added);
        Assert.Equal(0, CopiesTaken);
    }

    [Fact]
    public async Task The_main_agent_needs_no_copy_because_the_cli_makes_one()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "x\n");
        using var changes = Create(fx);

        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_1"));

        Assert.Equal(0, CopiesTaken);
    }

    [Fact]
    public async Task The_first_edit_after_a_file_was_dealt_with_is_compared_with_a_fresh_copy()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "kept\n");
        using var changes = Create(fx, poll: new[] { 0 });
        // What Keep and Undo will do: forget the row and ask for a copy before the next edit.
        changes.Set.SetCaptureNext(Work(fx, "a.txt"));

        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_1"));
        fx.WriteWork("a.txt", "kept\nmore\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.True(row.Baseline.Owned);
        Assert.Equal("kept\n", File.ReadAllText(row.Baseline.Path));
        Assert.False(changes.Set.WantsCapture(Work(fx, "a.txt")));
    }

    [Fact]
    public async Task A_copy_that_finishes_after_the_edit_was_let_go_is_not_kept_because_it_would_hold_the_edit()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "before\n");
        using var changes = Create(fx, poll: new[] { 0 });
        using var letGo = new CancellationTokenSource();
        letGo.Cancel();

        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"), letGo.Token);

        Assert.Equal(0, CopiesTaken);
    }

    [Fact]
    public async Task A_copy_is_not_taken_for_an_edit_that_names_no_tool_call_because_nothing_could_claim_it()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "before\n");
        using var changes = Create(fx, poll: new[] { 0 });

        await changes.OnPreEditAsync(Hook(fx, "a.txt", "", agentId: "agent1"));

        Assert.Equal(0, CopiesTaken);
    }

    [Fact]
    public async Task A_file_that_was_kept_and_whose_next_copy_is_missing_is_listed_without_the_one_from_before_the_session()
    {
        // The CLI's backup is from before the whole session: comparing the edit after a Keep with it would
        // offer to undo what the person already accepted.
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        fx.WriteBackup("h@v1", "the very first version\n");
        fx.WriteWork("a.txt", "accepted\nmore\n");
        using var changes = Create(fx, poll: new[] { 0 });
        changes.Set.SetCaptureNext(Work(fx, "a.txt"));

        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_2"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.False(row.Baseline.IsKnown);
    }

    [Fact]
    public async Task Unsaved_editor_changes_are_written_before_the_copy_is_taken()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "on disk\n");
        using var changes = Create(fx, poll: new[] { 0 }, prepare: path =>
        {
            File.WriteAllText(path, "as saved from the editor\n");
            return Task.CompletedTask;
        });

        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));
        fx.WriteWork("a.txt", "claude's\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal("as saved from the editor\n", File.ReadAllText(row.Baseline.Path));
    }

    [Fact]
    public async Task An_edit_whose_tool_call_never_reported_back_does_not_become_a_later_baseline()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "v1\n");
        using var changes = Create(fx, poll: new[] { 0 });
        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_refused", agentId: "agent1"));

        // A different call, later, after the file has moved on: it must not find the old copy.
        fx.WriteWork("a.txt", "v2\n");
        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_2", agentId: "agent1"));
        fx.WriteWork("a.txt", "v3\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_2", agentId: "agent1"));

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal("v2\n", File.ReadAllText(row.Baseline.Path));
    }

    [Fact]
    public async Task Nothing_is_tracked_while_file_checkpoints_are_off()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        fx.WriteBackup("h@v1", "a\n");
        fx.WriteWork("a.txt", "b\n");
        var on = false;
        using var changes = Create(fx, checkpoints: () => on);

        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        Assert.Empty(changes.Set.Snapshot());
        Assert.Equal(SessionChanges.NoCheckpoints, changes.Set.DisabledReason);

        on = true;
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_2"));
        Assert.Single(changes.Set.Snapshot());
        Assert.Null(changes.Set.DisabledReason);
    }

    [Fact]
    public async Task A_hook_from_another_session_starts_the_list_over()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "h@v1", 1));
        fx.WriteBackup("h@v1", "a\n");
        fx.WriteWork("a.txt", "b\n");
        fx.WriteWork("other.txt", "x\n");
        using var changes = Create(fx, poll: new[] { 0 });
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        Assert.Single(changes.Set.Snapshot());

        await changes.OnPostEditAsync(Hook(fx, "other.txt", "toolu_2", sessionId: "another-session"));

        Assert.Equal("another-session", changes.Set.SessionId);
        Assert.DoesNotContain(changes.Set.Snapshot(), r => r.Path == Work(fx, "a.txt"));
    }

    [Fact]
    public async Task Beginning_a_session_clears_the_list_and_drops_the_copies_it_owned()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        var copy = await new ChangeBaselines(_baselinesRoot, OutputWindowLogger.Global).CaptureAsync("s1", fx.WriteWork("a.txt", "x\n"));
        using var changes = Create(fx);
        changes.Set.Add(new FileChange { Path = Work(fx, "a.txt"), Baseline = copy });
        Assert.True(File.Exists(copy.Path));

        changes.BeginSession(null, resume: false);

        Assert.Empty(changes.Set.Snapshot());
        Assert.False(File.Exists(copy.Path));
    }

    [Fact]
    public void Resuming_a_session_lists_what_still_differs_from_the_history()
    {
        using var fx = Transcript(
            Jsonl.FileHistoryDelta("a.txt", "a@v1", 1),
            Jsonl.FileHistoryDelta("b.txt", null, 1),
            Jsonl.TrackedSnapshot("u2", Jsonl.Tracked("c.txt", "c@v2", 2)));
        fx.WriteBackup("a@v1", "a\n");
        fx.WriteBackup("c@v2", "same\n");
        fx.WriteWork("a.txt", "a\nb\n");
        fx.WriteWork("b.txt", "new\n");
        fx.WriteWork("c.txt", "same\n");
        using var changes = Create(fx);

        changes.BeginSession(fx.SessionId, resume: true);

        Assert.True(SpinWait.SpinUntil(() => changes.Set.Count == 2, TimeSpan.FromSeconds(10)));
        var rows = changes.Set.Snapshot();
        Assert.Equal(new[] { Work(fx, "a.txt"), Work(fx, "b.txt") }, rows.Select(r => r.Path));
        Assert.Equal(ChangeState.Modified, rows[0].State);
        Assert.Equal(ChangeState.Added, rows[1].State);
    }

    [Fact]
    public async Task A_baseline_replaced_for_the_row_it_names_is_measured_again_and_the_copy_it_had_goes()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "a@v1", 1));
        var backup = fx.WriteBackup("a@v1", "a\nb\n");
        fx.WriteWork("a.txt", "a\nB\nc\n");
        using var changes = Create(fx);
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        var row = Assert.Single(changes.Set.Snapshot());

        // The first changed line was kept in the editor: the baseline is now "a" with the B in it.
        var first = await changes.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("a\nB\n"));

        Assert.NotNull(first);
        var after = Assert.Single(changes.Set.Snapshot());
        Assert.True(after.Baseline.Owned);
        Assert.Equal(first, after.Baseline.Path);
        Assert.Equal(1, after.Added);
        Assert.Equal(0, after.Deleted);
        Assert.True(File.Exists(backup), "the CLI's own backup is never touched");

        // The second keep makes the baseline the file: the copy of the first goes, and the file is nothing to review.
        var second = await changes.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("a\nB\nc\n"));

        Assert.NotNull(second);
        Assert.False(File.Exists(first));
        Assert.Empty(changes.Set.Snapshot());
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public async Task A_baseline_is_not_replaced_for_a_row_other_than_the_one_named_or_one_that_is_gone()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "a@v1", 1));
        fx.WriteBackup("a@v1", "a\n");
        fx.WriteWork("a.txt", "a\nb\n");
        using var changes = Create(fx);
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        var row = Assert.Single(changes.Set.Snapshot());

        Assert.Null(await changes.ReplaceBaselineAsync(row.Path, row.Serial + 1, Encoding.UTF8.GetBytes("x\n")));
        var untouched = Assert.Single(changes.Set.Snapshot());
        Assert.False(untouched.Baseline.Owned);
        Assert.Equal(0, CopiesTaken);

        await changes.Actions.KeepAsync(row.Path);
        Assert.Null(await changes.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("x\n")));
        Assert.Equal(0, CopiesTaken);
    }

    [Fact]
    public async Task Two_replacements_of_one_file_at_once_leave_one_copy_not_two()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "a@v1", 1));
        fx.WriteBackup("a@v1", "a\n");
        fx.WriteWork("a.txt", "a\nb\nc\nd\n");
        using var changes = Create(fx);
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        var row = Assert.Single(changes.Set.Snapshot());

        var one = changes.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("a\nb\n"));
        var two = changes.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("a\nb\nc\n"));
        await Task.WhenAll(one, two);

        Assert.NotNull(one.Result);
        Assert.NotNull(two.Result);
        Assert.Equal(1, CopiesTaken);
        var after = Assert.Single(changes.Set.Snapshot());
        Assert.True(File.Exists(after.Baseline.Path));
        Assert.False(after.BaselineMissing);
    }

    [Fact]
    public async Task A_file_that_was_kept_is_not_listed_again_when_the_session_is_resumed_unless_it_changed_since()
    {
        using var fx = Transcript(
            Jsonl.FileHistoryDelta("a.txt", "a@v1", 1),
            Jsonl.FileHistoryDelta("b.txt", "b@v1", 1));
        fx.WriteBackup("a@v1", "a\n");
        fx.WriteBackup("b@v1", "b\n");
        fx.WriteWork("a.txt", "a\nclaude\n");
        fx.WriteWork("b.txt", "b\nclaude\n");

        // The first run of Visual Studio: both are listed, and the person keeps one.
        using (var first = Create(fx))
        {
            first.BeginSession(fx.SessionId, resume: true);
            Assert.True(SpinWait.SpinUntil(() => first.Set.Count == 2, TimeSpan.FromSeconds(10)));
            await first.Actions.KeepAsync(Work(fx, "a.txt"));
        }

        // The next run resumes the session: only the one that was not kept comes back.
        using (var second = Create(fx))
        {
            second.BeginSession(fx.SessionId, resume: true);
            Assert.True(SpinWait.SpinUntil(() => second.Set.Count >= 1, TimeSpan.FromSeconds(10)));
            await Task.Delay(300);
            Assert.Equal(new[] { Work(fx, "b.txt") }, second.Set.Snapshot().Select(r => r.Path));
        }

        // Claude changes the kept file afterwards: it is a change again, against the CLI's baseline as before.
        fx.WriteWork("a.txt", "a\nclaude\nand again\n");
        using var third = Create(fx);
        third.BeginSession(fx.SessionId, resume: true);
        Assert.True(SpinWait.SpinUntil(() => third.Set.Count == 2, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task A_file_whose_changes_were_all_kept_one_by_one_in_the_editor_stays_kept_when_the_session_is_resumed()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "a@v1", 1));
        fx.WriteBackup("a@v1", "a\nb\n");
        fx.WriteWork("a.txt", "a\nB\nc\n");

        using (var first = Create(fx))
        {
            first.BeginSession(fx.SessionId, resume: true);
            Assert.True(SpinWait.SpinUntil(() => first.Set.Count == 1, TimeSpan.FromSeconds(10)));
            var row = Assert.Single(first.Set.Snapshot());

            // Each change kept in the editor in turn: the baseline is replaced until it is the file, and the row leaves the list
            // without Keep having been pressed.
            Assert.NotNull(await first.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("a\nB\n")));
            Assert.NotNull(await first.ReplaceBaselineAsync(row.Path, row.Serial, Encoding.UTF8.GetBytes("a\nB\nc\n")));
            Assert.Empty(first.Set.Snapshot());
        }

        // The next run compares the file with the CLI's copy, which it differs from: it is not listed again, because it was kept.
        using var second = Create(fx);
        second.BeginSession(fx.SessionId, resume: true);
        await Task.Delay(500);
        Assert.Empty(second.Set.Snapshot());
    }

    [Fact]
    public async Task A_file_kept_before_the_restart_is_copied_before_its_next_edit_and_not_compared_with_the_clis_old_copy()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "a@v1", 1));
        fx.WriteBackup("a@v1", "a\n");
        fx.WriteWork("a.txt", "a\nclaude\n");
        using (var first = Create(fx))
        {
            first.BeginSession(fx.SessionId, resume: true);
            Assert.True(SpinWait.SpinUntil(() => first.Set.Count == 1, TimeSpan.FromSeconds(10)));
            await first.Actions.KeepAsync(Work(fx, "a.txt"));
        }

        // The next run resumes the session: the file is not listed, and the next edit of it is to be copied first.
        using var second = Create(fx);
        second.BeginSession(fx.SessionId, resume: true);
        Assert.True(SpinWait.SpinUntil(() => second.Set.WantsCapture(Work(fx, "a.txt")), TimeSpan.FromSeconds(10)));
        Assert.Empty(second.Set.Snapshot());

        await second.OnPreEditAsync(Hook(fx, "a.txt", "toolu_2"));
        fx.WriteWork("a.txt", "a\nclaude\nand more\n");
        await second.OnPostEditAsync(Hook(fx, "a.txt", "toolu_2"));

        // Against what was accepted: one line added. Against the CLI's copy it would be two, and Undo would take back the kept one.
        var row = Assert.Single(second.Set.Snapshot());
        Assert.True(row.Baseline.Owned);
        Assert.Equal(1, row.Added);
        Assert.Equal(0, row.Deleted);
    }

    [Fact]
    public void Resuming_with_file_checkpoints_off_says_why_nothing_is_listed()
    {
        using var fx = Transcript(Jsonl.FileHistoryDelta("a.txt", "a@v1", 1));
        using var changes = Create(fx, checkpoints: () => false);

        changes.BeginSession(fx.SessionId, resume: true);

        Assert.True(SpinWait.SpinUntil(() => changes.Set.DisabledReason is not null, TimeSpan.FromSeconds(10)));
        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task Refreshing_drops_a_file_that_was_put_back_and_updates_one_that_moved()
    {
        using var fx = Transcript(
            Jsonl.FileHistoryDelta("a.txt", "a@v1", 1),
            Jsonl.FileHistoryDelta("b.txt", "b@v1", 1));
        fx.WriteBackup("a@v1", "a\n");
        fx.WriteBackup("b@v1", "b\n");
        fx.WriteWork("a.txt", "a\nchanged\n");
        fx.WriteWork("b.txt", "b\nchanged\n");
        using var changes = Create(fx);
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        await changes.OnPostEditAsync(Hook(fx, "b.txt", "toolu_2"));
        Assert.Equal(2, changes.Set.Count);

        // The user reverts one file and extends the other, outside any tool call.
        fx.WriteWork("a.txt", "a\n");
        fx.WriteWork("b.txt", "b\nchanged\nand more\nand more\n");
        await changes.RefreshAsync();

        var row = Assert.Single(changes.Set.Snapshot());
        Assert.Equal(Work(fx, "b.txt"), row.Path);
        Assert.Equal(3, row.Added);
    }

    [Fact]
    public async Task Disposing_removes_the_copies_it_took()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "before\n");
        var changes = Create(fx, poll: new[] { 0 });
        await changes.OnPreEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));
        fx.WriteWork("a.txt", "after\n");
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));
        Assert.Equal(1, CopiesTaken);

        changes.Dispose();

        Assert.Equal(0, CopiesTaken);
    }

    [Fact]
    public async Task A_pane_closed_while_waiting_for_the_cli_record_does_not_throw()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "x\n");
        var changes = Create(fx, poll: new[] { 0, 5000 });

        var waiting = changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1"));
        changes.Dispose();

        await waiting;
        Assert.Empty(changes.Set.Snapshot());
    }

    [Fact]
    public async Task A_sub_agent_edit_with_no_copy_is_listed_at_once_without_a_baseline()
    {
        using var fx = Transcript(Jsonl.UserPrompt("u1", "go"));
        fx.WriteWork("a.txt", "x\n");
        // A long wait in the schedule: the CLI writes no record for a sub-agent, so none is waited for.
        using var changes = Create(fx, poll: new[] { 0, 60_000 });

        var started = DateTime.UtcNow;
        await changes.OnPostEditAsync(Hook(fx, "a.txt", "toolu_1", agentId: "agent1"));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
        var row = Assert.Single(changes.Set.Snapshot());
        Assert.False(row.Baseline.IsKnown);
    }
}
