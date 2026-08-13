/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class SendConsoleArgs
{
    [Description("Text to type into the console. Leave empty when sending a key instead.")]
    public string Text { get; set; }

    [AllowedValues("enter", "escape", "tab", "backspace", "space", "up", "down", "left", "right",
                   "ctrl+c", "ctrl+break")]
    [Description("A single key to send instead of text. 'ctrl+c' and 'ctrl+break' interrupt a " +
        "program that is blocked reading input. The arrow keys carry no character, so a program " +
        "reading lines (Console.ReadLine) will not see them — they only reach one reading keys.")]
    public string Key { get; set; }

    [Description("Append Enter after the text (default true). Ignored when sending a key.")]
    public bool Newline { get; set; } = true;

    [Description("PID of the debugged process. Omit for the first one the debugger reports.")]
    public int ProcessId { get; set; }
}

/// <summary>MCP tool: type into the console of a debugged console app, so a program waiting on
/// Console.ReadLine can be answered instead of hanging the session.</summary>
internal sealed class SendConsoleTool : McpTool<SendConsoleArgs>
{
    public override string Name => "debug_console_send";
    public override string Description =>
        "Send input to a debugged console application, as if it were typed at its console. Use it " +
        "when debug_console_read shows the program waiting at a prompt — a Console.ReadLine that " +
        "nobody answers blocks the debug session indefinitely. Pass 'text' (Enter is appended " +
        "unless newline is false), or 'key' for a single named key; 'ctrl+c' and 'ctrl+break' " +
        "interrupt the program rather than typing a character. Needs a running debug session and " +
        "a project that has a console.";

    protected override async Task<object> InvokeAsync(SendConsoleArgs args)
    {
        var r = await IdeConsoleService.Instance.SendAsync(args.ProcessId, args.Text, args.Key, args.Newline);
        return r.Ok
            ? new { ok = true, processId = r.ProcessId, message = r.Content }
            : (object)new { ok = false, reason = r.Reason };
    }
}
