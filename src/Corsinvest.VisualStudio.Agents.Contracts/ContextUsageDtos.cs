/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Contracts;

// Typed model of the CLI's get_context_usage control response (a snapshot of the CURRENT
// session's context window). Unlike /usage (opaque, volatile), this shape is stable and
// structured, so we type it end-to-end. Wire fields are camelCase → TypeGen default matches.

/// <summary>One context category (chat_context_usage): a slice of the window with its token
/// count and a symbolic color the UI maps to a CSS var.</summary>
public class ContextCategoryDto
{
    public string Name { get; set; }
    public int Tokens { get; set; }
    public string Color { get; set; }
}

/// <summary>One cell of the 10x20 memory-map grid. The last partially-filled cell of a
/// category carries a fractional <see cref="SquareFullness"/> (0..1).</summary>
public class ContextGridCellDto
{
    public string Color { get; set; }
    public bool IsFilled { get; set; }
    public string CategoryName { get; set; }
    public int Tokens { get; set; }
    public int Percentage { get; set; }
    public double SquareFullness { get; set; }
}

/// <summary>A memory file included in context (path + type Project/AutoMem/... + tokens).</summary>
public class ContextMemoryFileDto
{
    public string Path { get; set; }
    public string Type { get; set; }
    public int Tokens { get; set; }
}

/// <summary>A custom agent loaded into context (agentType + source userSettings/... + tokens).</summary>
public class ContextAgentDto
{
    public string AgentType { get; set; }
    public string Source { get; set; }
    public int Tokens { get; set; }
}

/// <summary>A skill's frontmatter entry (name + source + tokens).</summary>
public class ContextSkillDto
{
    public string Name { get; set; }
    public string Source { get; set; }
    public int Tokens { get; set; }
}

/// <summary>Skills summary: totals plus the per-skill frontmatter list.</summary>
public class ContextSkillsDto
{
    public int TotalSkills { get; set; }
    public int IncludedSkills { get; set; }
    public int Tokens { get; set; }
    public ContextSkillDto[] SkillFrontmatter { get; set; }
}

/// <summary>Slash-commands summary (totals + tokens).</summary>
public class ContextCommandsDto
{
    public int TotalCommands { get; set; }
    public int IncludedCommands { get; set; }
    public int Tokens { get; set; }
}

/// <summary>An MCP tool in context: full name (mcp__server__tool), its server, tokens, and
/// whether it's loaded (deferred tools are false with 0 tokens until first used).</summary>
public class ContextMcpToolDto
{
    public string Name { get; set; }
    public string ServerName { get; set; }
    public int Tokens { get; set; }
    public bool IsLoaded { get; set; }
}

/// <summary>A named token group (used by toolCallsByType / attachmentsByType).</summary>
public class ContextTokenGroupDto
{
    public string Name { get; set; }
    public int Tokens { get; set; }
}

/// <summary>Breakdown of the "Messages" category into its parts — the detail the VS Code
/// dialog doesn't surface.</summary>
public class ContextMessageBreakdownDto
{
    public int ToolCallTokens { get; set; }
    public int ToolResultTokens { get; set; }
    public int AttachmentTokens { get; set; }
    public int AssistantMessageTokens { get; set; }
    public int UserMessageTokens { get; set; }
    public int RedirectedContextTokens { get; set; }
    public int UnattributedTokens { get; set; }
    public ContextTokenGroupDto[] ToolCallsByType { get; set; }
    public ContextTokenGroupDto[] AttachmentsByType { get; set; }
}

/// <summary>The full get_context_usage response (chat_context_usage): a snapshot of how the
/// current session fills its context window, broken down every way the CLI reports.</summary>
public class GetContextUsageResponse
{
    public string Model { get; set; }
    public int TotalTokens { get; set; }
    public int MaxTokens { get; set; }
    public int RawMaxTokens { get; set; }
    public int Percentage { get; set; }
    public string AutocompactSource { get; set; }
    public int AutoCompactThreshold { get; set; }
    public bool IsAutoCompactEnabled { get; set; }
    public ContextCategoryDto[] Categories { get; set; }
    public ContextGridCellDto[][] GridRows { get; set; }
    public ContextMemoryFileDto[] MemoryFiles { get; set; }
    public ContextAgentDto[] Agents { get; set; }
    public ContextMcpToolDto[] McpTools { get; set; }
    public ContextSkillsDto Skills { get; set; }
    public ContextCommandsDto SlashCommands { get; set; }
    public ContextMessageBreakdownDto MessageBreakdown { get; set; }
}
