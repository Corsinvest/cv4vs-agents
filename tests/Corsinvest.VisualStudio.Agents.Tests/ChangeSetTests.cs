/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class ChangeSetTests
{
    private static FileChange Row(string path, BaselineRef? baseline = null) => new()
    {
        Path = path,
        State = ChangeState.Modified,
        Source = ChangeSource.Edit,
        Baseline = baseline ?? BaselineRef.Cli(path + ".bak"),
    };

    [Fact]
    public void Rows_come_back_sorted_by_path_whatever_the_case()
    {
        var set = new ChangeSet();
        set.Add(Row(@"C:\b.txt"));
        set.Add(Row(@"C:\A.txt"));
        set.Add(Row(@"C:\c.txt"));

        Assert.Equal(new[] { @"C:\A.txt", @"C:\b.txt", @"C:\c.txt" }, set.Snapshot().Select(r => r.Path));
    }

    [Fact]
    public void A_snapshot_is_a_copy_that_later_changes_do_not_reach()
    {
        var set = new ChangeSet();
        set.Add(Row(@"C:\a.txt"));
        var snapshot = set.Snapshot();

        set.Update(@"C:\a.txt", r => r.Added = 42);

        Assert.Equal(0, snapshot[0].Added);
        Assert.True(set.TryGet(@"C:\a.txt", out var row));
        Assert.Equal(42, row.Added);
    }

    [Fact]
    public void A_path_is_one_row_whatever_its_case_and_the_first_baseline_stays()
    {
        var set = new ChangeSet();
        set.Add(Row(@"C:\a.txt", BaselineRef.Cli("first")));
        set.Add(Row(@"C:\A.TXT", BaselineRef.Cli("second")));

        Assert.Equal(1, set.Count);
        Assert.True(set.TryGet(@"c:\a.txt", out var row));
        Assert.Equal("first", row.Baseline.Path);
    }

    [Fact]
    public void Rows_beyond_the_limit_are_refused_and_overflow_says_so()
    {
        var set = new ChangeSet();
        for (var i = 0; i < ChangeSet.MaxFiles; i++) { Assert.True(set.Add(Row(@"C:\f" + i))); }
        Assert.False(set.Overflow);

        Assert.False(set.Add(Row(@"C:\one-too-many")));

        Assert.True(set.Overflow);
        Assert.Equal(ChangeSet.MaxFiles, set.Count);
    }

    [Fact]
    public void Overflow_clears_once_there_is_room_again()
    {
        var set = new ChangeSet();
        for (var i = 0; i < ChangeSet.MaxFiles; i++) { set.Add(Row(@"C:\f" + i)); }
        set.Add(Row(@"C:\one-too-many"));

        set.Remove(@"C:\f0");

        Assert.False(set.Overflow);
    }

    [Fact]
    public void Changed_is_raised_for_every_change_and_never_under_the_lock()
    {
        var set = new ChangeSet();
        var raised = 0;
        set.Changed += () =>
        {
            Interlocked.Increment(ref raised);
            // From another thread: if the set were still holding its lock this would never return.
            var read = new Thread(() => set.Snapshot());
            read.Start();
            Assert.True(read.Join(TimeSpan.FromSeconds(5)));
        };

        set.Add(Row(@"C:\a.txt"));
        set.Update(@"C:\a.txt", r => r.Added = 1);
        set.Remove(@"C:\a.txt");
        set.SetDisabled("off");

        Assert.Equal(4, raised);
    }

    [Fact]
    public void Updating_or_removing_a_row_that_is_not_there_does_nothing()
    {
        var set = new ChangeSet();
        var raised = 0;
        set.Changed += () => raised++;

        Assert.False(set.Update(@"C:\absent.txt", r => r.Added = 1));
        Assert.Null(set.Remove(@"C:\absent.txt"));
        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_copy_waits_for_its_own_tool_call_and_is_taken_once()
    {
        var set = new ChangeSet();
        var copy = BaselineRef.Own(@"C:\copies\a");
        set.StorePending("toolu_1", @"C:\a.txt", copy);

        Assert.False(set.TakePending("toolu_other", @"C:\a.txt", out _));
        Assert.False(set.TakePending("toolu_1", @"C:\b.txt", out _));
        Assert.True(set.TakePending("toolu_1", @"C:\A.TXT", out var taken));
        Assert.Equal(copy.Path, taken.Path);
        Assert.False(set.TakePending("toolu_1", @"C:\a.txt", out _));
    }

    [Fact]
    public void Copies_nobody_came_back_for_are_pruned_and_handed_back()
    {
        var set = new ChangeSet();
        set.StorePending("toolu_1", @"C:\a.txt", BaselineRef.Own(@"C:\copies\a"));

        Assert.Empty(set.PrunePending(TimeSpan.FromHours(1)));
        var released = set.PrunePending(TimeSpan.Zero);

        Assert.Single(released);
        Assert.False(set.TakePending("toolu_1", @"C:\a.txt", out _));
    }

    [Fact]
    public void A_path_marked_for_a_copy_is_consumed_once()
    {
        var set = new ChangeSet();
        set.SetCaptureNext(@"C:\a.txt");

        Assert.True(set.WantsCapture(@"C:\A.TXT"));
        Assert.True(set.ConsumeCaptureNext(@"C:\a.txt"));
        Assert.False(set.ConsumeCaptureNext(@"C:\a.txt"));
        Assert.False(set.WantsCapture(@"C:\a.txt"));
    }

    [Fact]
    public void An_unknown_session_adopts_the_id_without_losing_its_rows()
    {
        var set = new ChangeSet();
        set.Add(Row(@"C:\a.txt"));

        var reset = set.EnsureSession("s1", out var released);

        Assert.False(reset);
        Assert.Null(released);
        Assert.Equal("s1", set.SessionId);
        Assert.Equal(1, set.Count);
    }

    [Fact]
    public void The_same_session_is_left_alone_and_a_different_one_starts_over()
    {
        var set = new ChangeSet();
        set.Reset("s1");
        set.Add(Row(@"C:\a.txt", BaselineRef.Own(@"C:\copies\a")));
        set.StorePending("toolu_1", @"C:\b.txt", BaselineRef.Own(@"C:\copies\b"));
        set.SetCaptureNext(@"C:\c.txt");

        Assert.False(set.EnsureSession("s1", out _));
        Assert.Equal(1, set.Count);

        Assert.True(set.EnsureSession("s2", out var released));
        Assert.Equal(0, set.Count);
        Assert.Equal("s2", set.SessionId);
        Assert.False(set.WantsCapture(@"C:\c.txt"));
        Assert.Equal(2, released.Count(b => b.Owned));
    }

    [Fact]
    public void An_empty_session_id_changes_nothing()
    {
        var set = new ChangeSet();
        set.Reset("s1");

        Assert.False(set.EnsureSession(null, out _));
        Assert.False(set.EnsureSession("", out _));
        Assert.Equal("s1", set.SessionId);
    }

    [Fact]
    public void Disabling_is_reported_once_per_reason()
    {
        var set = new ChangeSet();
        var raised = 0;
        set.Changed += () => raised++;

        set.SetDisabled("off");
        set.SetDisabled("off");
        set.SetDisabled(null);

        Assert.Equal(2, raised);
        Assert.Null(set.DisabledReason);
    }
}
