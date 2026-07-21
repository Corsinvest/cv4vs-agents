/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ExecuteCodeArgs
{
    [Required, Description("Code snippet to execute.")]
    public string Code { get; set; }
}

/// <summary>MCP tool: submit a snippet to VS's C# Interactive window.
/// Mirrors the VS Code extension's <c>executeCode</c> (Jupyter-targeted)
/// — same shape, IDE-appropriate backend.</summary>
internal sealed class ExecuteCodeTool : McpTool<ExecuteCodeArgs>
{
    public override string Name => "ide_execute_code";
    public override string Description =>
        "Submit a code snippet to the IDE's interactive REPL " +
        "(C# Interactive in Visual Studio). Returns whether the snippet was submitted; " +
        "it does not capture the REPL's output.";
    public override bool AlwaysLoad => true;

    protected override async Task<object> InvokeAsync(ExecuteCodeArgs args)
    {
        var ok = await IdeContextService.Instance.ExecuteInteractiveCodeAsync(args.Code);
        return new { submitted = ok };
    }
}
