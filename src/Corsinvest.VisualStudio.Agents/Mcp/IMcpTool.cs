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
    /// <summary>Method name used by the CLI in <c>tools/call</c>. camelCase, and for the tools the
    /// CLI special-cases it must be the name the CLI already knows, so it needs no IDE-specific
    /// handling (e.g. <c>getCurrentSelection</c>, <c>openFile</c>).</summary>
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

    /// <summary><para>
    /// MCP <c>readOnlyHint</c>: the tool changes nothing — no file written, no IDE state
    /// touched, no process affected — so a client may run it without asking. The three hints are
    /// independent axes, not levels: a tool can be read-only AND idempotent, or destructive AND
    /// idempotent.
    /// </para>
    /// <para>
    /// Getting it wrong is asymmetric, which is why every default is false: an unmarked read-only
    /// tool merely costs the user a permission prompt, while an unmarked-as-writing tool that
    /// actually writes would be auto-approved. When in doubt, leave it false.
    /// </para></summary>
    bool ReadOnly { get; }

    /// <summary>MCP <c>destructiveHint</c>: the tool can destroy or interrupt something the user
    /// would miss — a debug session, build outputs, a symbol's name across the solution. Only
    /// meaningful when <see cref="ReadOnly"/> is false. Default false.</summary>
    bool Destructive { get; }

    /// <summary>MCP <c>idempotentHint</c>: calling it again with the same arguments changes nothing
    /// beyond the first call, so a client may retry after a timeout without asking again. Default
    /// false.</summary>
    bool Idempotent { get; }

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
