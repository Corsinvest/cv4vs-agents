/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RunCleanupArgs
{
    [Required, Description("Path to the file to clean up.")]
    public string FilePath { get; set; }
}

/// <summary>MCP tool: run VS's Code Cleanup on a file — the user's Ctrl+K,
/// Ctrl+E. Goes beyond <c>document_format</c>: besides formatting it applies
/// the fixers of the user's configured cleanup profile (remove unused
/// imports, apply code-style preferences, and so on).
/// The profile can't be chosen — VS runs the default one — and the set of
/// fixers is language-dependent (rich for C#/VB, minimal elsewhere), so on
/// other languages this may do no more than formatting.
/// Side effect: the file is opened in the editor (single tab).</summary>
internal sealed class RunCleanupTool : McpTool<RunCleanupArgs>
{
    public override string Name => "document_run_cleanup";
    public override string Description =>
        "Run the IDE's Code Cleanup on a file (Ctrl+K, Ctrl+E): formatting plus the fixers of " +
        "the user's default cleanup profile. Richer than document_format, but the extra fixers " +
        "are language-dependent (C#/VB get the most).";

    protected override async Task<object> InvokeAsync(RunCleanupArgs args)
    {
        var ok = await IdeContextService.Instance.RunCleanupAsync(args.FilePath);
        return new { success = ok };
    }
}
