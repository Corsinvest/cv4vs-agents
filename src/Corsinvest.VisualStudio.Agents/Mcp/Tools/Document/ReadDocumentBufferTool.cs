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

    [Description("Max number of lines to return from the top of the file (default 2000, 0 for all). Ignored when startLine/endLine are given.")]
    public int MaxLines { get; set; } = 2000;

    [Description("Optional. 1-based first line to return. Use with endLine to read one region of a large file instead of everything above it.")]
    public int StartLine { get; set; }

    [Description("Optional. 1-based last line to return, inclusive. Defaults to the end of the file when startLine is given without it.")]
    public int EndLine { get; set; }
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
        "Returns isDirty so you can tell whether what you read differs from disk. " +
        "startLine/endLine read one region: on a large file that is the difference between fifty " +
        "lines and everything above them, and startLine comes back so the text can be placed.";

    public override bool ReadOnly => true;
    public override bool Idempotent => true;

    protected override async Task<object> InvokeAsync(ReadDocumentBufferArgs args)
    {
        var r = await IdeContextService.Instance.ReadDocumentBufferAsync(args.FilePath, args.MaxLines, args.StartLine, args.EndLine);
        if (!r.Ok) { return new { ok = false, reason = r.Reason }; }
        return new
        {
            ok = true,
            path = r.Path,
            isDirty = r.IsDirty,
            content = r.Content,
            totalLines = r.TotalLines,
            startLine = r.StartLine,
            truncated = r.Truncated,
        };
    }
}
