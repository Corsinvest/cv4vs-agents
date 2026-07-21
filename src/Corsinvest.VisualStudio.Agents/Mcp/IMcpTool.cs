/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// Single MCP tool exposed to the Claude CLI over JSON-RPC (advertised via
/// <c>tools/list</c>, invoked via <c>tools/call</c>). Implementations live in
/// <c>Mcp/Tools/</c>. Tools return plain POCOs / anonymous types; the dispatcher
/// serializes them with <c>JToken.FromObject</c>, so the schema follows the C#
/// property names directly.
/// </summary>
internal interface IMcpTool
{
    /// <summary>Method name used by the CLI in <c>tools/call</c>. camelCase,
    /// mirrors the VS Code extension's tool names so the CLI needs no
    /// IDE-specific handling (e.g. <c>getCurrentSelection</c>, <c>openFile</c>).</summary>
    string Name { get; }

    /// <summary>Human-readable description shown in the CLI's tool catalog.
    /// Keep it short and action-oriented — the model uses it to decide when
    /// to call the tool.</summary>
    string Description { get; }

    /// <summary>JSON Schema for the tool's input parameters (<c>inputSchema</c>
    /// in <c>tools/list</c>). Most tools take none and return
    /// <c>new { type = "object", properties = new {} }</c>.</summary>
    object InputSchema { get; }

    /// <summary>If true, the dispatcher emits
    /// <c>_meta: { "anthropic/alwaysLoad": true }</c> so the CLI keeps the tool
    /// in the model's prompt every turn instead of deferring it to ToolSearch.
    /// Use sparingly: ~50 tokens of context per turn. Default false.</summary>
    bool AlwaysLoad { get; }

    /// <summary>Execute the tool and return the result payload as a POCO /
    /// anonymous type. The dispatcher serializes it into the standard MCP
    /// <c>content</c> envelope — UNLESS it is a <see cref="RawMcpContent"/>,
    /// whose blocks become the <c>content</c> array verbatim.</summary>
    Task<object> InvokeAsync(JObject arguments);
}

/// <summary>Opt-out of the dispatcher's default single-text-block wrapping; the
/// returned <see cref="Blocks"/> become the MCP <c>content</c> array as-is. Used
/// when a tool must control that array directly — e.g. <c>openDiff</c>, whose CLI
/// client (<c>useDiffInIDE</c>) expects two text blocks
/// (<c>["FILE_SAVED", &lt;content&gt;]</c>), not one serialized-JSON block.</summary>
internal sealed class RawMcpContent(object[] blocks)
{
    public object[] Blocks { get; } = blocks;
}
