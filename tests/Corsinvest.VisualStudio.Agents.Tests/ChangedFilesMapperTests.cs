/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Newtonsoft.Json;
using System.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class ChangedFilesMapperTests
{
    private static FileChange Row(string path, BaselineRef baseline) => new()
    {
        Path = path,
        Baseline = baseline,
        State = ChangeState.Modified,
        Source = ChangeSource.Edit,
    };

    [Theory]
    [InlineData("Modified", "modified")]
    [InlineData("Added", "added")]
    [InlineData("Deleted", "deleted")]
    public void The_state_goes_out_as_the_word_the_webview_expects(string state, string word)
    {
        var row = Row(@"C:\a.txt", BaselineRef.Empty);
        row.State = (ChangeState)System.Enum.Parse(typeof(ChangeState), state);

        Assert.Equal(word, ChangedFilesMapper.ToDto(row).State);
    }

    [Theory]
    [InlineData("Edit", "edit")]
    [InlineData("Bash", "bash")]
    public void The_source_goes_out_as_a_word(string source, string word)
    {
        var row = Row(@"C:\a.txt", BaselineRef.Empty);
        row.Source = (ChangeSource)System.Enum.Parse(typeof(ChangeSource), source);

        Assert.Equal(word, ChangedFilesMapper.ToDto(row).Source);
    }

    [Theory]
    [InlineData("Exact", "exact")]
    [InlineData("Approximate", "approximate")]
    [InlineData("Unavailable", "unavailable")]
    public void The_counts_kind_goes_out_as_a_word(string kind, string word)
    {
        var row = Row(@"C:\a.txt", BaselineRef.Empty);
        row.Counts = (CountsKind)System.Enum.Parse(typeof(CountsKind), kind);

        Assert.Equal(word, ChangedFilesMapper.ToDto(row).Counts);
    }

    [Fact]
    public void A_file_has_a_baseline_when_there_is_something_to_compare_with()
    {
        Assert.True(ChangedFilesMapper.ToDto(Row(@"C:\a.txt", BaselineRef.Cli(@"C:\b"))).HasBaseline);
        Assert.True(ChangedFilesMapper.ToDto(Row(@"C:\a.txt", BaselineRef.Own(@"C:\b"))).HasBaseline);
        // "Nothing" is a baseline too: the file did not exist, and undoing it deletes it.
        Assert.True(ChangedFilesMapper.ToDto(Row(@"C:\a.txt", BaselineRef.Empty)).HasBaseline);
        Assert.False(ChangedFilesMapper.ToDto(Row(@"C:\a.txt", BaselineRef.None)).HasBaseline);
    }

    [Fact]
    public void A_pruned_backup_is_not_a_baseline_and_says_so()
    {
        var row = Row(@"C:\a.txt", BaselineRef.Cli(@"C:\gone"));
        row.BaselineMissing = true;

        var dto = ChangedFilesMapper.ToDto(row);

        Assert.False(dto.HasBaseline);
        Assert.True(dto.BaselineMissing);
    }

    [Fact]
    public void The_numbers_and_flags_are_carried_over()
    {
        var row = Row(@"C:\a.txt", BaselineRef.Empty);
        row.Added = 7;
        row.Deleted = 3;
        row.IsBinary = true;
        row.Incomplete = true;

        var dto = ChangedFilesMapper.ToDto(row);

        Assert.Equal(7, dto.Added);
        Assert.Equal(3, dto.Deleted);
        Assert.True(dto.IsBinary);
        Assert.True(dto.Incomplete);
    }

    [Fact]
    public void The_list_carries_the_session_the_overflow_the_cap_and_why_nothing_is_tracked()
    {
        var set = new ChangeSet();
        set.Reset("s1");
        set.Add(Row(@"C:\b.txt", BaselineRef.Empty));
        set.Add(Row(@"C:\a.txt", BaselineRef.Empty));
        set.SetDisabled("off");

        var list = ChangedFilesMapper.Build(set);

        Assert.Equal("s1", list.SessionId);
        Assert.Equal(new[] { @"C:\a.txt", @"C:\b.txt" }, list.Files.Select(f => f.Path));
        Assert.False(list.Overflow);
        Assert.Equal(ChangeSet.MaxFiles, list.MaxFiles);
        Assert.Equal("off", list.Disabled);
        Assert.Null(list.CommandsNote);
    }

    [Fact]
    public void The_note_about_commands_travels_with_the_list()
    {
        var set = new ChangeSet();

        Assert.Equal("git was not found.", ChangedFilesMapper.Build(set, "git was not found.").CommandsNote);
    }

    [Fact]
    public void The_wire_names_are_camel_case_as_the_typescript_expects()
    {
        var dto = ChangedFilesMapper.ToDto(Row(@"C:\a.txt", BaselineRef.Empty));

        var json = JsonConvert.SerializeObject(dto, new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        });

        Assert.Contains("\"hasBaseline\":true", json);
        Assert.Contains("\"baselineMissing\":false", json);
        Assert.Contains("\"isBinary\":false", json);
    }

    [Fact]
    public void Failures_become_the_dtos_the_webview_shows()
    {
        var dtos = ChangedFilesMapper.ToDtos(new[] { new ChangeFailure(@"C:\a.txt", "locked") });

        var one = Assert.Single(dtos);
        Assert.Equal(@"C:\a.txt", one.Path);
        Assert.Equal("locked", one.Reason);
    }
}
