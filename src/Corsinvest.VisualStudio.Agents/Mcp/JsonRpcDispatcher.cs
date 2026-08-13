/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// JSON-RPC 2.0 protocol layer for MCP: handles <c>initialize</c>, <c>tools/list</c>,
/// <c>tools/call</c>; ignores notifications. Stateless and shared across clients.
/// </summary>
internal sealed class JsonRpcDispatcher
{
    /// <summary>MCP protocol versions we can speak, newest first. The client's own version is
    /// echoed back when it is one of these — a server that answers with a version the client did
    /// not ask for is entitled to be rejected — and the oldest is the floor we fall back to.</summary>
    private static readonly string[] SupportedProtocolVersions =
    [
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    ];

    /// <summary>Version used when the client asks for one we don't know, or asks for nothing.
    /// Deliberately the oldest: an unknown version is more likely newer than older, and the floor
    /// is the one every client understands.</summary>
    private const string ProtocolVersion = "2024-11-05";

    /// <summary>How long a single tool call may run before the caller is told the IDE is not
    /// answering. Long enough for a solution-wide build to finish on a slow machine — the point is
    /// to break a deadlock, not to police slow work.</summary>
    private static readonly TimeSpan ToolCallTimeout = TimeSpan.FromMinutes(10);

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
            OutputWindowLogger.Global.Trace(() => $"Mcp: notification '{method}' (ignored)");
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => Result(id, BuildInitializeResult(paramsToken)),
                "tools/list" => Result(id, BuildToolsList()),
                "tools/call" => Result(id, await CallToolAsync(paramsToken)),
                // Liveness check: the spec requires an empty result, and a client that gets
                // "method not found" for it may conclude the server is gone.
                "ping" => Result(id, new { }),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (McpToolException tex)
        {
            return Error(id, -32602, tex.Message);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException($"Mcp.{method}", ex);
            return Error(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    /// <summary>Guidance delivered once at handshake instead of repeated in every tool's
    /// description. It says what this server is and which of its tools beat the shell equivalent —
    /// the things a caller would otherwise have to infer tool by tool, paying for the prose in
    /// every catalogue listing.</summary>
    private const string ServerInstructions =
        "These tools reach the Visual Studio instance that has this solution open — its compiler, " +
        "its symbol graph, its running debugger. They report what the IDE knows, not what the " +
        "files say.\n\n" +
        "Prefer them to the shell where both work: build_solution over msbuild or dotnet build " +
        "(no path to resolve, no clash with a debug session, errors come back with file and " +
        "line), nav_find_references and nav_go_to_definition over grep (the symbol graph, not " +
        "text), ide_get_diagnostics over parsing build output.\n\n" +
        "Things that are easy to get wrong: build tools compile whichever configuration the IDE " +
        "has active and report it back — build_set_configuration changes it, and the change stays " +
        "for the user's next manual build. Debug inspection needs the debugger paused; " +
        "debug_get_state says whether it is. Output panes are listed under the IDE's display " +
        "language, but the built-in ones also answer to their English names.";

    private object BuildInitializeResult(JObject paramsToken) => new
    {
        protocolVersion = NegotiateProtocolVersion((string)paramsToken?["protocolVersion"]),
        capabilities = new
        {
            tools = new { listChanged = false },
        },
        serverInfo = new
        {
            name = AppConstants.AppId,
            version = BuildInfo.Version
        },
        instructions = ServerInstructions,
    };

    /// <summary>Echo the client's protocol version when we speak it, else our floor.</summary>
    private static string NegotiateProtocolVersion(string requested)
        => Array.IndexOf(SupportedProtocolVersions, requested) >= 0 ? requested : ProtocolVersion;

    private object BuildToolsList()
    {
        var list = new List<object>(_tools.Count);
        // Sorted, like every other list this server returns: registration order is an accident of
        // the registry's source order, and a catalogue that reshuffles between runs is harder to
        // diff when a tool goes missing.
        foreach (var t in _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            // Built key by key rather than as an anonymous type: each optional field would
            // otherwise need its own variant of the whole literal, and the variants multiply
            // (two optional fields already mean four near-identical copies to keep in sync).
            var entry = new JObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = JToken.FromObject(t.InputSchema),
            };

            // _meta.anthropic/alwaysLoad opts the tool out of the CLI's tool-search deferral
            // (otherwise it's hidden until discovered via ToolSearch, costing a round-trip).
            // Set only on tools likely invoked every turn.
            if (t.AlwaysLoad)
            {
                entry["_meta"] = new JObject { ["anthropic/alwaysLoad"] = true };
            }

            // Standard MCP annotations, emitted only where true: an absent hint already means
            // false, so a tool that declares none is byte-identical to what we sent before.
            var annotations = new JObject();
            if (t.ReadOnly) { annotations["readOnlyHint"] = true; }
            if (t.Destructive) { annotations["destructiveHint"] = true; }
            if (t.Idempotent) { annotations["idempotentHint"] = true; }
            if (annotations.HasValues) { entry["annotations"] = annotations; }

            list.Add(entry);
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

        object payload;
        try
        {
            var call = tool.InvokeAsync(args);
            // A tool that never returns takes the whole conversation with it: the CLI has no
            // timeout of its own and simply waits. The usual cause is the UI thread parked behind
            // a modal dialog, which is why the message names it — the caller can't see the IDE.
            if (await Task.WhenAny(call, Task.Delay(ToolCallTimeout)) != call)
            {
                OutputWindowLogger.Global.Warn($"[mcp] '{tool.Name}' did not return within {ToolCallTimeout.TotalMinutes:0} minutes");
                return ToolFailure($"'{tool.Name}' did not return within {ToolCallTimeout.TotalMinutes:0} minutes. " +
                    "Visual Studio may be waiting on a modal dialog, or the operation may still be running — " +
                    "it was not cancelled.");
            }
            payload = await call;
        }
        catch (McpToolException)
        {
            throw; // bad arguments are a protocol error (-32602), not a tool result
        }
        catch (Exception ex)
        {
            // The tool threw. That is a failure OF the tool, not of the protocol, so it belongs in
            // the result where the model can read it and try something else — a JSON-RPC error
            // reaches the client as a hard fault instead.
            OutputWindowLogger.Global.LogException($"Mcp.tool.{tool.Name}", ex);
            return ToolFailure($"{tool.Name} failed: {ex.Message}");
        }

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
            // Tools report their own failures as { ok: false } in the payload. Mirroring that onto
            // isError costs nothing and lets a client count failed calls instead of seeing every
            // call succeed and the word "false" somewhere in the text.
            isError = IsFailurePayload(payload),
        };
    }

    /// <summary>Whether a tool's own payload says it failed — the <c>ok: false</c> convention the
    /// tools use. Read off the serialized shape rather than an interface so the 16 tools that
    /// already follow it need no change.</summary>
    private static bool IsFailurePayload(object payload)
    {
        if (payload == null) { return false; }
        try
        {
            return JToken.FromObject(payload, Newtonsoft.Json.JsonSerializer.Create(CamelCaseSettings))
                is JObject obj && obj["ok"]?.Type == JTokenType.Boolean && !obj["ok"].Value<bool>();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"[mcp] could not inspect a tool payload for ok=false: {ex.Message}");
            return false;
        }
    }

    /// <summary>A failed tool call, in the shape the spec asks for: a normal result carrying
    /// <c>isError: true</c>, not a JSON-RPC error.</summary>
    private static object ToolFailure(string message) => new
    {
        content = new[] { new { type = "text", text = message } },
        isError = true,
    };

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
