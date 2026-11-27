/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class HookEditTests
{
    private static JObject Input(JObject toolInput, string agentId = null) => new()
    {
        ["session_id"] = "s1",
        ["transcript_path"] = "t.jsonl",
        ["cwd"] = "work",
        ["agent_id"] = agentId,
        ["hook_event_name"] = "PostToolUse",
        ["tool_name"] = "Edit",
        ["tool_input"] = toolInput,
        ["tool_use_id"] = "toolu_in_input",
    };

    [Fact]
    public void An_edit_names_its_file_session_and_transcript()
    {
        var edit = HookEdit.TryParse(Input(new JObject { ["file_path"] = "a.txt" }), "toolu_1");

        Assert.Equal("a.txt", edit.FilePath);
        Assert.Equal("Edit", edit.ToolName);
        Assert.Equal("toolu_1", edit.ToolUseId);
        Assert.Equal("s1", edit.SessionId);
        Assert.Equal("t.jsonl", edit.TranscriptPath);
        Assert.Equal("work", edit.Cwd);
        Assert.False(edit.IsSubAgent);
    }

    [Fact]
    public void A_notebook_edit_names_its_file_differently()
    {
        var edit = HookEdit.TryParse(Input(new JObject { ["notebook_path"] = "nb.ipynb" }), null);

        Assert.Equal("nb.ipynb", edit.FilePath);
    }

    [Fact]
    public void A_sub_agents_call_is_told_apart()
    {
        var edit = HookEdit.TryParse(Input(new JObject { ["file_path"] = "a.txt" }, agentId: "a6b0b22ab47aa0b5b"), "toolu_1");

        Assert.True(edit.IsSubAgent);
        Assert.Equal("a6b0b22ab47aa0b5b", edit.AgentId);
    }

    [Fact]
    public void The_tool_use_id_falls_back_to_the_one_in_the_input()
    {
        var edit = HookEdit.TryParse(Input(new JObject { ["file_path"] = "a.txt" }), null);

        Assert.Equal("toolu_in_input", edit.ToolUseId);
    }

    [Fact]
    public void A_call_that_names_no_file_is_not_an_edit()
    {
        Assert.Null(HookEdit.TryParse(Input(new JObject { ["command"] = "ls" }), "toolu_1"));
        Assert.Null(HookEdit.TryParse(Input(new JObject { ["file_path"] = "  " }), "toolu_1"));
        Assert.Null(HookEdit.TryParse(null, "toolu_1"));
        Assert.Null(HookEdit.TryParse(new JValue("not an object"), "toolu_1"));
    }
}
