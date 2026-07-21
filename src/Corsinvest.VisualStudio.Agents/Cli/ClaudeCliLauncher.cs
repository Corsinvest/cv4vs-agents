/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;

namespace Corsinvest.VisualStudio.Agents.Cli;

/// <summary>
/// CLI-pane-specific launch helper: builds the ConPTY command line. Finding the binary and the
/// shared "not installed" UI live in <see cref="ClaudeInstall"/> (core, used by both panes).
/// </summary>
public static class ClaudeCliLauncher
{
    /// <summary>Build a ConPTY-ready command line for the interactive CLI pane.
    /// Single layer of quoting so install-path spaces survive <c>CreateProcess</c>.
    /// When <paramref name="mcpPort"/>/<paramref name="mcpAuthToken"/> are supplied, also
    /// registers our in-process MCP server as a second "vs" server via --mcp-config so the
    /// CLI sees the full custom tool surface (mcp__vs__*) — the --ide channel alone is
    /// filtered by the CLI to just executeCode/getDiagnostics — and pre-approves them with
    /// --allowedTools (every MCP tool otherwise prompts on each call). Returns <c>null</c>
    /// if the binary is missing.</summary>
    public static string BuildConPtyCommandLine(bool ide = true, int mcpPort = 0, string mcpAuthToken = null)
    {
        var exe = ClaudeInstall.ResolveExecutable();
        if (exe == null) { return null; }
        var flags = ide ? " --ide" : "";
        var mcp = "";
        if (mcpPort > 0 && !string.IsNullOrEmpty(mcpAuthToken))
        {
            var config = new Newtonsoft.Json.Linq.JObject
            {
                ["mcpServers"] = new Newtonsoft.Json.Linq.JObject
                {
                    ["vs"] = new Newtonsoft.Json.Linq.JObject
                    {
                        ["type"] = "ws",
                        ["url"] = $"ws://127.0.0.1:{mcpPort}",
                        ["headers"] = new Newtonsoft.Json.Linq.JObject
                        {
                            // The generic type:"ws" path doesn't send the IDE auth header (that's
                            // exclusive to the internal ws-ide type); supply it so McpServerHost's
                            // EndsWith(authToken) check passes for THIS connection.
                            ["X-Claude-Code-Ide-Authorization"] = mcpAuthToken,
                        },
                    },
                },
            };
            // Compact (no spaces) so it stays one argv token; wrap in quotes and escape the inner
            // ones so CreateProcess keeps the JSON intact (MSVCRT rules: \" is a literal quote).
            var json = config.ToString(Newtonsoft.Json.Formatting.None);
            var jsonArg = "\"" + json.Replace("\"", "\\\"") + "\"";
            mcp = $" --mcp-config {jsonArg} --allowedTools mcp__vs__*";
        }
        return $"\"{exe}\"{flags}{mcp}";
    }
}
