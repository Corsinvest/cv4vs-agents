/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class BaselineReviewTests : IDisposable
{
    private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));

    public BaselineReviewTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { /* a scratch folder */ }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string Write(string name, string content) => Write(name, new UTF8Encoding(false).GetBytes(content));

    private static FileChange Row(string path, BaselineRef baseline, ChangeState state = ChangeState.Modified, CountsKind counts = CountsKind.Exact, bool binary = false)
        => new() { Path = path, State = state, Baseline = baseline, Counts = counts, IsBinary = binary };

    [Fact]
    public async Task A_modified_text_file_with_a_copy_can_be_reviewed()
    {
        var file = Write("a.txt", "new\r\n");
        var copy = Write("backup@v1", "old\r\n");

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Null(why);
        Assert.Equal("old\r\n", baseline.Text);
        Assert.Equal(ChangeEvaluator.Mark.None, baseline.Mark);
        Assert.Equal("\r\n", baseline.LineEnding);
        Assert.False(baseline.IsEmpty);
    }

    [Fact]
    public async Task A_created_file_is_reviewed_against_nothing()
    {
        var file = Write("made.txt", "new\n");

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Empty, ChangeState.Added));

        Assert.Null(why);
        Assert.Equal("", baseline.Text);
        Assert.True(baseline.IsEmpty);
    }

    [Fact]
    public async Task The_baseline_keeps_the_byte_order_mark_it_came_with_and_the_text_loses_it()
    {
        var file = Write("a.cs", Bom.Concat(Encoding.UTF8.GetBytes("new\n")).ToArray());
        var copy = Write("backup@v1", Bom.Concat(Encoding.UTF8.GetBytes("old\n")).ToArray());

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Null(why);
        Assert.Equal("old\n", baseline.Text);
        Assert.Equal(ChangeEvaluator.Mark.Utf8, baseline.Mark);
    }

    [Fact]
    public async Task A_file_whose_mark_changed_is_left_to_the_comparison_window()
    {
        var file = Write("a.cs", "claude wrote it without the mark\n");
        var copy = Write("backup@v1", Bom.Concat(Encoding.UTF8.GetBytes("original\n")).ToArray());

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Contains("byte-order mark", why);
        Assert.Null(baseline);
    }

    [Fact]
    public async Task A_code_page_that_is_not_utf8_is_left_to_the_comparison_window()
    {
        var file = Write("a.txt", "new\n");
        var copy = Write("backup@v1", new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A });

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Contains("UTF-8 or UTF-16", why);
        Assert.Null(baseline);
    }

    [Fact]
    public async Task A_deleted_file_a_binary_one_and_one_with_no_figures_are_not_reviewed()
    {
        var file = Write("a.txt", "x\n");
        var copy = Write("backup@v1", "y\n");

        Assert.NotNull((await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy), ChangeState.Deleted))).Why);
        Assert.NotNull((await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy), binary: true))).Why);
        Assert.NotNull((await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy), counts: CountsKind.Unavailable))).Why);
    }

    [Fact]
    public async Task A_row_with_no_copy_or_a_copy_that_is_gone_is_not_reviewed()
    {
        var file = Write("a.txt", "x\n");

        Assert.Contains("no copy", (await BaselineReview.PrepareAsync(Row(file, BaselineRef.None))).Why);
        Assert.Contains("cannot be read", (await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(Path.Combine(_folder, "gone@v1"))))).Why);
    }

    [Theory]
    [InlineData("old\nlines\n", "\n")]
    [InlineData("old\r\nlines\r\n", "\r\n")]
    [InlineData("one line", null)]
    public async Task The_baseline_says_which_line_ending_it_is_written_back_in(string before, string ending)
    {
        var file = Write("a.txt", "new\n");
        var copy = Write("backup@v1", before);

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));
        Assert.Null(why);

        Assert.Equal(ending, baseline.LineEnding);
    }

    [Fact]
    public async Task A_baseline_that_mixes_line_endings_is_left_to_the_comparison_window()
    {
        var file = Write("a.txt", "new\n");
        var copy = Write("backup@v1", "one\r\ntwo\nthree\r\n");

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Contains("mixes line endings", why);
        Assert.Null(baseline);
    }

    [Fact]
    public async Task A_file_that_mixes_line_endings_is_left_to_the_comparison_window()
    {
        var file = Write("a.txt", "one\r\ntwo\nthree\r\n");
        var copy = Write("backup@v1", "one\ntwo\n");

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Contains("mixes line endings", why);
        Assert.Null(baseline);
    }

    [Fact]
    public async Task A_file_that_is_not_plain_text_now_is_left_to_the_comparison_window_though_its_copy_was()
    {
        // Claude's edit left the file in a legacy code page (an e with an acute accent as one byte, which is not UTF-8).
        var file = Write("a.txt", new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A });
        var copy = Write("backup@v1", "cafe\n");

        var (why, baseline) = await BaselineReview.PrepareAsync(Row(file, BaselineRef.Cli(copy)));

        Assert.Contains("not plain UTF-8 or UTF-16 text now", why);
        Assert.Null(baseline);
    }

    [Fact]
    public async Task A_file_that_has_vanished_is_not_reviewed()
    {
        var copy = Write("backup@v1", "y\n");

        var (why, _) = await BaselineReview.PrepareAsync(Row(Path.Combine(_folder, "vanished.txt"), BaselineRef.Cli(copy)));

        Assert.Contains("gone", why);
    }
}
