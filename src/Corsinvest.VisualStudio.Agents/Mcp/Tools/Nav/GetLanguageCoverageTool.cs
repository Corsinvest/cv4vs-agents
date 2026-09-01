/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

/// <summary>MCP tool: which languages of the open solution the nav_* tools can answer for, and
/// which of them they cannot — asked once, instead of discovered one supported=false at a time.
/// </summary>
internal sealed class GetLanguageCoverageTool : McpTool<NoArgs>
{
    public override string Name => "nav_get_language_coverage";
    public override string Description =>
        "Report which languages in the open solution the other nav_* tools can answer for. For " +
        "each language: how many projects it has, and which of go_to_definition, find_references, " +
        "get_document_symbols, rename_symbol and search_workspace_symbols it provides. Also lists " +
        "the solution's projects that are outside the language workspace altogether, with what they " +
        "contain — C++ ones are, and no nav_* tool reaches them however the services answer — and, per project, the source " +
        "files whose extension its own language does not answer for, which is how a .csproj full of " +
        "TypeScript reports every tool as available and still answers for none of those files. " +
        "Ask this once when a nav_* " +
        "tool returns supported=false and you want to know whether to retry, use another tool, or " +
        "fall back to text search for the rest of the session.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override Task<object> InvokeAsync(NoArgs args)
    {
        var r = IdeNavigationService.Instance.GetCoverage();
        return (!r.Supported)
                ? Task.FromResult<object>(new { supported = false, reason = r.Reason })
                : Task.FromResult<object>(new
                {
                    supported = true,
                    languages = r.Languages.Select(l => new
                    {
                        language = l.Language,
                        projects = l.ProjectCount,
                        tools = l.Services,
                    }).ToArray(),
                    projects_outside_workspace = r.ProjectsOutsideWorkspace.Select(p => new
                    {
                        project = p.Project,
                        by_extension = p.ByExtension,
                    }).ToArray(),
                    uncovered_files = r.UncoveredFiles.Select(u => new
                    {
                        project = u.Project,
                        project_language = u.ProjectLanguage,
                        by_extension = u.ByExtension,
                    }).ToArray(),
                });
    }
}
