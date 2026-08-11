/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp.Tools;

internal sealed class ReadDocumentBufferArgs
{
    [Description("Path to an open file. Omit to read the document the user is currently looking at.")]
    public string FilePath { get; set; }

    [Description("Max number of lines to return from the top of the file (default 2000, 0 for all).")]
    public int MaxLines { get; set; } = 2000;
}

/// <summary>MCP tool: read the editor buffer instead of the file on disk, so unsaved
/// changes are visible. Without it the user has to save before asking about what they
/// are writing — and the autosave hook, when it is on, saves for them, which is not the
/// same as being able to look without touching.</summary>
internal sealed class ReadDocumentBufferTool : McpTool<ReadDocumentBufferArgs>
{
    public override string Name => "document_read_buffer";
    public override string Description =>
        "Read an open document's editor buffer, including changes the user hasn't saved. " +
        "Omit filePath to read the document they are currently looking at. Use the Read tool " +
        "instead for the version on disk, or when the file isn't open in the IDE. " +
        "Returns isDirty so you can tell whether what you read differs from disk.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ReadDocumentBufferArgs args)
    {
        var r = await IdeContextService.Instance.ReadDocumentBufferAsync(args.FilePath, args.MaxLines);
        if (!r.Ok) { return new { ok = false, reason = r.Reason }; }
        return new
        {
            ok = true,
            path = r.Path,
            isDirty = r.IsDirty,
            content = r.Content,
            totalLines = r.TotalLines,
            truncated = r.Truncated,
        };
    }
}
