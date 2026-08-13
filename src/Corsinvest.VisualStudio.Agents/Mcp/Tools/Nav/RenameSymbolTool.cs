/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class RenameSymbolArgs
{
    [Required, Description("Path to the file that contains the symbol.")]
    public string FilePath { get; set; }

    [Required, Description("1-based line number where the symbol appears (the line you read).")]
    public int Line { get; set; }

    [Required, Description("The current symbol name on that line.")]
    public string SymbolName { get; set; }

    [Required, Description("The new name for the symbol.")]
    public string NewName { get; set; }
}

/// <summary>MCP tool: rename a symbol across the whole solution (semantic, not text replace) —
/// updates every reference and the definition. Applies the changes directly (no editor popup),
/// atomically: if it would cause unresolved conflicts nothing is written. Multi-language where
/// the VS language service supports it (C#/VB/F#/… ); returns supported=false otherwise so the
/// model can fall back to manual edits.
/// <para>Closed files are written to disk, open ones go dirty — the workspace's own behaviour,
/// deliberately left alone. See the comment on TryApplyChanges in IdeNavigationService.Rename.cs
/// for why opening them first was tried and dropped.</para></summary>
internal sealed class RenameSymbolTool : McpTool<RenameSymbolArgs>
{
    public override string Name => "nav_rename_symbol";
    public override string Description =>
        "Rename a symbol everywhere it's used across the solution (semantic, not text " +
        "replace): give the file, the 1-based line where the symbol appears, its current " +
        "name, and the new name. Updates the definition and every reference. Atomic — if the " +
        "rename would cause unresolved conflicts nothing is applied. " +
        "Writes every file it touches to disk immediately — files already open in the editor get " +
        "a dirty buffer instead, but nothing is held back waiting for document_save. There is no " +
        "Ctrl+Z for it: undoing means renaming back. A build, a git diff or a shell command sees " +
        "the new name straight away. " +
        "It reaches further than the file you name — a solution-wide rename touched four files " +
        "in a two-project solution — so read changedFiles (path plus how many occurrences each, " +
        "sorted by path) and totalOccurrences for what actually changed, rather than assuming it " +
        "was the one file. " +
        "The file must belong to a project in the open solution. Returns supported=false for " +
        "languages this isn't available for; applied=false (with a reason) when the symbol can't " +
        "be renamed or the new name is invalid. It changes every file it touches, so ask before " +
        "running it — nav_find_references shows the same set without changing anything.";

    public override bool Destructive => true;

    protected override async Task<object> InvokeAsync(RenameSymbolArgs args)
    {
        var r = await IdeNavigationService.Instance.RenameSymbolAsync(
            args.FilePath, args.Line, args.SymbolName, args.NewName, CancellationToken.None);

        if (!r.Supported)
        {
            return new { supported = false, applied = false, reason = r.Reason };
        }
        return new
        {
            supported = true,
            applied = r.Applied,
            reason = r.Reason,
            newName = r.NewName,
            totalOccurrences = r.TotalOccurrences,
            changedFiles = r.ChangedFiles.Select(c => new { filePath = c.FilePath, count = c.Count }).ToArray(),
            conflicts = r.Conflicts.Select(c => new { filePath = c.FilePath, line = c.Line }).ToArray(),
        };
    }
}
