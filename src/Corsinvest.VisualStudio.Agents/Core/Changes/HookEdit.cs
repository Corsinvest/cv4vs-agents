/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The part of a PreToolUse / PostToolUse hook callback that says which file a tool is
/// changing. Measured on CLI 2.1.310 (see <c>tools/cli-probe</c>): the input carries
/// <c>session_id</c>, <c>transcript_path</c>, <c>cwd</c> and, for a sub-agent's call,
/// <c>agent_id</c>; the file is <c>tool_input.file_path</c>, or <c>notebook_path</c> for NotebookEdit.</summary>
internal sealed class HookEdit
{
    public string ToolName { get; private set; }

    /// <summary>The path as the tool call spelled it: absolute in practice, but not normalised.</summary>
    public string FilePath { get; private set; }

    public string ToolUseId { get; private set; }

    /// <summary>Empty for the main agent. A sub-agent's edit never reaches the CLI's file history, so
    /// it is the one case that needs a copy taken before the tool runs.</summary>
    public string AgentId { get; private set; }

    public string SessionId { get; private set; }
    public string Cwd { get; private set; }
    public string TranscriptPath { get; private set; }

    public bool IsSubAgent => !string.IsNullOrEmpty(AgentId);

    /// <summary>Null when the callback names no file (a tool this feature has no use for).</summary>
    public static HookEdit TryParse(JToken input, string toolUseId)
    {
        if (input is not JObject obj) { return null; }
        var toolInput = obj["tool_input"] as JObject;
        var path = (string)toolInput?["file_path"] ?? (string)toolInput?["notebook_path"];
        if (string.IsNullOrWhiteSpace(path)) { return null; }

        return new HookEdit
        {
            ToolName = (string)obj["tool_name"] ?? "",
            FilePath = path,
            ToolUseId = !string.IsNullOrEmpty(toolUseId) ? toolUseId : (string)obj["tool_use_id"] ?? "",
            AgentId = (string)obj["agent_id"] ?? "",
            SessionId = (string)obj["session_id"] ?? "",
            Cwd = (string)obj["cwd"] ?? "",
            TranscriptPath = (string)obj["transcript_path"] ?? "",
        };
    }
}
