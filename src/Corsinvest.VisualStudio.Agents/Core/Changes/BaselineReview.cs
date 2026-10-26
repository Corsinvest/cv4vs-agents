/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>A row's baseline as the editor takes it: text, and the byte-order mark the file it came from had.</summary>
internal sealed class ReviewBaseline
{
    public ReviewBaseline(string text, ChangeEvaluator.Mark mark, bool isEmpty)
    {
        Text = text;
        Mark = mark;
        IsEmpty = isEmpty;
    }

    /// <summary>The text, without the mark. Line endings as the baseline had them: matching the document's is the
    /// caller's job, since only it can see the document.</summary>
    public string Text { get; }

    /// <summary>The mark the baseline bytes start with, so what is written back from text is written the way it was found.</summary>
    public ChangeEvaluator.Mark Mark { get; }

    /// <summary>The file did not exist before: the baseline is empty, and the whole file is one hunk.</summary>
    public bool IsEmpty { get; }
}

/// <summary>Whether a listed file can be reviewed in its own editor, and against what. The editor draws a
/// difference between two texts; what the list keeps is bytes, so the review is only offered where text and bytes
/// agree, because that is where a hunk undone in the editor changes exactly the hunk and nothing else about the
/// file.</summary>
internal static class BaselineReview
{
    /// <summary>Null when the row can be reviewed in place, with <paramref name="baseline"/> set; otherwise why not,
    /// in words for the log. Reads the baseline, so not for the UI thread.</summary>
    public static string Prepare(FileChange row, out ReviewBaseline baseline)
    {
        baseline = null;
        if (row.State == ChangeState.Deleted) { return "the file was deleted: there is no editor to draw in"; }
        if (row.IsBinary || row.Counts == CountsKind.Unavailable) { return "the file is not text, or too large to compare"; }
        if (!row.Baseline.IsKnown) { return "there is no copy of the file from before the change"; }
        if (row.Baseline.IsEmpty)
        {
            baseline = new ReviewBaseline("", ChangeEvaluator.Mark.None, isEmpty: true);
            return null;
        }

        byte[] bytes;
        try { bytes = ChangeEvaluator.ReadShared(row.Baseline.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the copy of the file from before the change cannot be read: {ex.Message}";
        }

        // Plain UTF-8 or UTF-16 only: the same text Undo hands to the editor, and for the same reason. A legacy code
        // page decoded as UTF-8 would lose its accents in the baseline, and keep or undo a hunk with the loss in it.
        var text = ChangeEvaluator.DecodeStrict(bytes);
        if (text is null) { return "the file is not plain UTF-8 or UTF-16 text"; }

        try
        {
            if (!File.Exists(row.Path)) { return "the file is gone"; }
            if (!ChangeEvaluator.SameMark(bytes, row.Path))
            {
                return "its byte-order mark is not the one the copy from before the change has: the editor would save it with the wrong one";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the file cannot be read: {ex.Message}";
        }

        baseline = new ReviewBaseline(text, ChangeEvaluator.ByteOrderMark(bytes), isEmpty: false);
        return null;
    }
}
