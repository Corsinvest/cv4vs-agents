/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// JSON-RPC 2.0 protocol layer for MCP: handles <c>initialize</c>, <c>tools/list</c>,
/// <c>tools/call</c>; ignores notifications. Stateless and shared across clients.
/// </summary>
internal sealed class JsonRpcDispatcher
{
    /// <summary>MCP protocol version we implement; sent in the <c>initialize</c> response.</summary>
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>Server name shown in the CLI's <c>/mcp</c>. Keep stable; bump the version field instead.</summary>
    private const string ServerName = "cv4vs-agents";
    private const string ServerVersion = "1.0.0";

    private readonly Dictionary<string, IMcpTool> _tools;

    /// <summary>camelCase serializer matching the MCP wire format the CLI expects; PascalCase POCOs
    /// would otherwise be unreadable. JsonProperty attributes still win (snake_case quirks).</summary>
    public static readonly Newtonsoft.Json.JsonSerializerSettings CamelCaseSettings = new()
    {
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
    };

    public JsonRpcDispatcher(IEnumerable<IMcpTool> tools)
    {
        _tools = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
        foreach (var t in tools) { _tools[t.Name] = t; }
    }

    /// <summary>Handle one inbound message; returns <c>null</c> for notifications. Never throws —
    /// errors become JSON-RPC error envelopes.</summary>
    public async Task<string> HandleMessageAsync(string raw)
    {
        JObject msg;
        try { msg = JObject.Parse(raw); }
        catch (Exception)
        {
            // Malformed JSON: spec requires id=null + parse error.
            return Error(null, -32700, "Parse error");
        }

        var id = msg["id"]; // null for notifications
        var method = (string)msg["method"];
        var paramsToken = msg["params"] as JObject;

        if (string.IsNullOrEmpty(method))
        {
            return id == null ? null : Error(id, -32600, "Invalid Request: missing method");
        }

        // Notifications (id absent): spec forbids any response.
        if (id == null)
        {
            OutputWindowLogger.Trace(() => $"Mcp: notification '{method}' (ignored)");
            return null;
        }

        try
        {
            switch (method)
            {
                case "initialize": return Result(id, BuildInitializeResult());
                case "tools/list": return Result(id, BuildToolsList());
                case "tools/call": return Result(id, await CallToolAsync(paramsToken));
                default: return Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (McpToolException tex)
        {
            return Error(id, -32602, tex.Message);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException($"Mcp.{method}", ex);
            return Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    //  Method handlers

    private object BuildInitializeResult() => new
    {
        protocolVersion = ProtocolVersion,
        capabilities = new
        {
            tools = new { listChanged = false },
        },
        serverInfo = new { name = ServerName, version = ServerVersion },
    };

    private object BuildToolsList()
    {
        var list = new List<object>(_tools.Count);
        foreach (var t in _tools.Values)
        {
            // _meta.anthropic/alwaysLoad opts the tool out of the CLI's tool-search deferral
            // (otherwise it's hidden until discovered via ToolSearch, costing a round-trip).
            // Set only on tools likely invoked every turn.
            if (t.AlwaysLoad)
            {
                list.Add(new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = t.InputSchema,
                    _meta = new Dictionary<string, object>
                    {
                        ["anthropic/alwaysLoad"] = true,
                    },
                });
            }
            else
            {
                list.Add(new
                {
                    name = t.Name,
                    description = t.Description,
                    inputSchema = t.InputSchema,
                });
            }
        }
        return new { tools = list };
    }

    private async Task<object> CallToolAsync(JObject paramsToken)
    {
        var name = (string)paramsToken?["name"];
        if (string.IsNullOrEmpty(name) || !_tools.TryGetValue(name, out var tool))
        {
            throw new McpToolException($"Unknown tool: {name}");
        }
        var args = paramsToken["arguments"] as JObject ?? [];
        var payload = await tool.InvokeAsync(args);

        // RawMcpContent (e.g. openDiff's two-block shape) controls the content array itself:
        // emit blocks verbatim, no wrapping.
        if (payload is RawMcpContent raw)
        {
            return new { content = raw.Blocks, isError = false };
        }

        // Default: wrap tool output as a single text block (CLI parses the JSON client-side).
        var json = payload == null ? "null" : Newtonsoft.Json.JsonConvert.SerializeObject(payload, CamelCaseSettings);
        return new
        {
            content = new[] { new { type = "text", text = json } },
            isError = false,
        };
    }

    //  JSON-RPC envelope helpers
    //
    // Use SerializeObject(anonymous), not JObject.ToString(Formatting): VS loads its own
    // Newtonsoft.Json that may lack that overload and throw MissingMethodException.

    private static string Result(JToken id, object result)
    {
        return Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id,
            result,
        }, CamelCaseSettings);
    }

    private static string Error(JToken id, int code, string message)
    {
        return Newtonsoft.Json.JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message },
        }, CamelCaseSettings);
    }
}

/// <summary>Tool-side validation failure. Surfaced to the CLI as an
/// invalid-params JSON-RPC error (-32602) instead of a generic 500.</summary>
internal sealed class McpToolException(string message) : Exception(message)
{
}
