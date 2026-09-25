/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Editor;
using System.Collections.Generic;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The prompts file behind the three context menus. What must never happen: a menu left
/// empty because the file could not be read, a list the user emptied coming back, or the prompts
/// an existing user wrote lost on the way to the new file.</summary>
public class EditorPromptStoreTests
{
    private static EditorPrompt Custom(string title) => new() { Title = title, Prompt = title + " prompt" };

    [Fact]
    public void Serialize_then_Parse_gives_back_every_list()
    {
        var saved = new Dictionary<PromptScope, List<EditorPrompt>>
        {
            [PromptScope.Editor] = [Custom("A"), Custom("B")],
            [PromptScope.ErrorList] = [Custom("C")],
            [PromptScope.Output] = [Custom("D")],
        };

        var read = EditorPromptStore.Parse(EditorPromptStore.Serialize(saved));

        Assert.Equal(new[] { "A", "B" }, read[PromptScope.Editor].ConvertAll(p => p.Title));
        Assert.Equal(new[] { "C" }, read[PromptScope.ErrorList].ConvertAll(p => p.Title));
        Assert.Equal(new[] { "D" }, read[PromptScope.Output].ConvertAll(p => p.Title));
    }

    [Fact]
    public void An_absent_key_takes_that_menus_defaults()
    {
        var read = EditorPromptStore.Parse("""{ "Editor": [ { "Title": "A", "Prompt": "a" } ] }""");

        Assert.Equal(new[] { "A" }, read[PromptScope.Editor].ConvertAll(p => p.Title));
        Assert.Equal(
            EditorPromptStore.Defaults(PromptScope.ErrorList).ConvertAll(p => p.Title),
            read[PromptScope.ErrorList].ConvertAll(p => p.Title));
    }

    // The distinction the object-of-lists shape exists for.
    [Fact]
    public void An_empty_list_stays_empty()
    {
        var read = EditorPromptStore.Parse("""{ "Output": [] }""");

        Assert.Empty(read[PromptScope.Output]);
    }

    [Fact]
    public void A_scope_missing_on_save_is_left_out_not_written_empty()
    {
        var json = EditorPromptStore.Serialize(new Dictionary<PromptScope, List<EditorPrompt>>
        {
            [PromptScope.Editor] = [Custom("A")],
        });

        Assert.NotEmpty(EditorPromptStore.Parse(json)[PromptScope.Output]);
    }

    [Fact]
    public void A_corrupt_list_falls_back_alone()
    {
        var read = EditorPromptStore.Parse("""{ "Editor": [ { "Title": "A", "Prompt": "a" } ], "Output": 42 }""");

        Assert.Equal(new[] { "A" }, read[PromptScope.Editor].ConvertAll(p => p.Title));
        Assert.NotEmpty(read[PromptScope.Output]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[ { \"Title\": \"A\", \"Prompt\": \"a\" } ]")]
    public void A_file_that_is_not_an_object_gives_every_menu_its_defaults(string json)
    {
        var read = EditorPromptStore.Parse(json);

        foreach (PromptScope scope in System.Enum.GetValues(typeof(PromptScope)))
        {
            Assert.Equal(
                EditorPromptStore.Defaults(scope).ConvertAll(p => p.Title),
                read[scope].ConvertAll(p => p.Title));
        }
    }

    [Fact]
    public void The_legacy_array_becomes_the_editor_list_and_the_rest_take_defaults()
    {
        var read = EditorPromptStore.FromLegacy("""[ { "Title": "Mine", "Prompt": "mine", "SendImmediately": true } ]""");

        Assert.Equal(new[] { "Mine" }, read[PromptScope.Editor].ConvertAll(p => p.Title));
        Assert.True(read[PromptScope.Editor][0].SendImmediately);
        Assert.NotEmpty(read[PromptScope.ErrorList]);
        Assert.NotEmpty(read[PromptScope.Output]);
    }

    [Fact]
    public void A_corrupt_legacy_file_gives_the_defaults()
    {
        var read = EditorPromptStore.FromLegacy("{ broken");

        Assert.Equal(
            EditorPromptStore.Defaults(PromptScope.Editor).ConvertAll(p => p.Title),
            read[PromptScope.Editor].ConvertAll(p => p.Title));
    }
}
