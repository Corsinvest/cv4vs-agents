/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class GetDiagnosticsArgs
{
    [Description("Optional file URI (file://...) to filter diagnostics by.")]
    public string Uri { get; set; }
}

/// <summary>MCP tool: read diagnostics from the IDE's Error List in the
/// LSP-shape the Claude CLI expects (DiagnosticFile[] — see
/// <c>parseDiagnosticResult</c> in the CLI source).</summary>
internal sealed class GetDiagnosticsTool : McpTool<GetDiagnosticsArgs>
{
    public override string Name => "ide_get_diagnostics";
    public override string Description =>
        "Get language diagnostics from the IDE's Error List. " +
        "Pass uri (file://...) to limit to one file; omit it to get all. " +
        "Returns an array of files, each with its diagnostics ([] when there are none).";
    public override bool AlwaysLoad => true;

    protected override async Task<object> InvokeAsync(GetDiagnosticsArgs args)
    {
        var files = await IdeContextService.Instance.GetDiagnosticsAsync(args.Uri);
        // Return a top-level array (not {diagnostics: [...]}): the CLI's
        // parseDiagnosticResult expects DiagnosticFile[].
        return files;
    }
}
