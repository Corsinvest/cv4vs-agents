/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The filter that keeps CLI-injected meta out of the transcript.
/// <para>Both directions fail quietly. Miss a tag and machine noise renders as a user bubble;
/// match one tag too many and a real turn disappears, with the conversation still reading as
/// though the user never said it.</para></summary>
public class MetaInjectionTests
{
    [Theory]
    // The injections the CLI rides in on a role:user line. Hidden in Claude Code's own UI too.
    [InlineData("<task-notification>agent finished</task-notification>")]
    [InlineData("<system-reminder>remember the thing</system-reminder>")]
    [InlineData("<local-command-caveat>caveat</local-command-caveat>")]
    [InlineData("<bash-stdout>ls output</bash-stdout>")]
    [InlineData("<bash-stderr>command not found</bash-stderr>")]
    [InlineData("<tick>")]
    [InlineData("<teammate-message>hi</teammate-message>")]
    [InlineData("<post-tool-use-hook>hook said this</post-tool-use-hook>")]
    // Leading whitespace does not smuggle one past the filter.
    [InlineData("   <system-reminder>indented</system-reminder>")]
    [InlineData("\n\t<tick>")]
    public void IsMetaText_hides_a_CLI_injection(string text)
        => Assert.True(MetaInjection.IsMetaText(text));

    [Theory]
    // A real turn, plain or carrying the editor context the composer prepends.
    [InlineData("fix the bug in Program.cs")]
    [InlineData("<ide_selection>lines 10-20 of Foo.cs</ide_selection>\nwhat does this do?")]
    [InlineData("<ide_opened_file>Foo.cs</ide_opened_file>\nexplain")]
    // An interrupt IS a real turn: it renders with an orange bar. Callers that must skip it
    // (session titles) guard it themselves rather than having this call it meta.
    [InlineData("[Request interrupted by user]")]
    // The CLI's slash-command output is NOT filtered: the WebView renders it as a slash-result.
    [InlineData("<local-command-stdout>Set model to opus</local-command-stdout>")]
    [InlineData("<local-command-stderr>unknown command</local-command-stderr>")]
    // A tag in the MIDDLE of a real message is the user quoting one, not an injection.
    [InlineData("why does <system-reminder> show up in my logs?")]
    // An unknown tag is not meta by virtue of being a tag.
    [InlineData("<invoke name=\"Read\">")]
    // Nothing to classify.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsMetaText_leaves_a_real_turn_alone(string text)
        => Assert.False(MetaInjection.IsMetaText(text));

    [Theory]
    // The composer prepends the editor context; the user's own words are what follows.
    [InlineData("<ide_selection>Foo.cs:10-20</ide_selection>\nwhat does this do?", "what does this do?")]
    [InlineData("<ide_opened_file>Foo.cs</ide_opened_file>\nexplain this", "explain this")]
    // Multi-line context blocks: Singleline means the dot spans the newlines inside the block.
    [InlineData("<ide_selection>line one\nline two</ide_selection>\nthe question", "the question")]
    // Only the LEADING block goes: one quoted later in the message is the user's own text.
    [InlineData("look at <ide_selection>this</ide_selection> please", "look at <ide_selection>this</ide_selection> please")]
    // Nothing to strip.
    [InlineData("plain question", "plain question")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void StripIdeContext_removes_only_a_leading_context_block(string text, string expected)
        => Assert.Equal(expected, MetaInjection.StripIdeContext(text));

    [Fact]
    public void StripIdeContext_leaves_a_prompt_that_does_not_start_with_one()
    {
        // The reason this matters: title generation rejects text starting with '<', so a message
        // sent with editor context would otherwise be dropped and the session left untitled.
        const string withContext = "<ide_selection>Foo.cs</ide_selection>\nrename this method";

        var stripped = MetaInjection.StripIdeContext(withContext);

        Assert.False(stripped.StartsWith("<"));
        Assert.Equal("rename this method", stripped);
    }
}
