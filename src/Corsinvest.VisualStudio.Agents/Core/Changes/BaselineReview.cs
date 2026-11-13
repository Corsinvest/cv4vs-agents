/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>A row's baseline as the editor takes it: text, and the byte-order mark and line ending the file it came from had.</summary>
internal sealed class ReviewBaseline
{
    public ReviewBaseline(string text, ChangeEvaluator.Mark mark, bool isEmpty, string lineEnding)
    {
        Text = text;
        Mark = mark;
        IsEmpty = isEmpty;
        LineEnding = lineEnding;
    }

    /// <summary>The text, without the mark. Line endings as the baseline had them: showing it in the document's is the
    /// caller's job, since only it can see the document.</summary>
    public string Text { get; }

    /// <summary>The mark the baseline bytes start with, so what is written back from text is written the way it was found.</summary>
    public ChangeEvaluator.Mark Mark { get; }

    /// <summary>The file did not exist before: the baseline is empty, and the whole file is one hunk.</summary>
    public bool IsEmpty { get; }

    /// <summary>The line ending the baseline uses throughout (a baseline with two kinds is not reviewed), or null when it has no
    /// line break. What a baseline changed by Keep is written back in: the editor shows it in the document's ending, but the
    /// copy the list restores from has to be the file as it was, or an Undo of the whole file would change every line.</summary>
    public string LineEnding { get; }
}

/// <summary>Whether a listed file can be reviewed in its own editor, and against what. The editor draws a
/// difference between two texts; what the list keeps is bytes, so the review is only offered where text and bytes
/// agree, because that is where a hunk undone in the editor changes exactly the hunk and nothing else about the
/// file.</summary>
internal static class BaselineReview
{
    /// <summary>Why the row cannot be reviewed in place, in words for the log, or null with the baseline it is reviewed
    /// against. Reads the baseline and the file; asynchronously, so a caller on the UI thread does not wait for the disk.</summary>
    public static async Task<(string Why, ReviewBaseline Baseline)> PrepareAsync(FileChange row)
    {
        if (row.State == ChangeState.Deleted) { return Not("the file was deleted: there is no editor to draw in"); }
        if (row.IsBinary || row.Counts == CountsKind.Unavailable) { return Not("the file is not text, or too large to compare"); }
        if (!row.Baseline.IsKnown) { return Not("there is no copy of the file from before the change"); }
        if (row.Baseline.IsEmpty)
        {
            return (null, new ReviewBaseline("", ChangeEvaluator.Mark.None, isEmpty: true, lineEnding: null));
        }

        byte[] bytes;
        try { bytes = await AsyncFile.ReadAllBytesAsync(row.Baseline.Path).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Not($"the copy of the file from before the change cannot be read: {ex.Message}");
        }

        // Plain UTF-8 or UTF-16 only: the same text Undo hands to the editor, and for the same reason. A legacy code
        // page decoded as UTF-8 would lose its accents in the baseline, and keep or undo a hunk with the loss in it.
        var text = ChangeEvaluator.DecodeStrict(bytes);
        if (text is null) { return Not("the file is not plain UTF-8 or UTF-16 text"); }

        // The editor shows the baseline in the document's line ending and writes a kept hunk back as one: a baseline with
        // two kinds could not be put back as it was.
        if (BaselineText.HasMixedLineEndings(text)) { return Not("the copy from before the change mixes line endings, which keeping a hunk would level"); }

        byte[] current;
        try { current = await AsyncFile.ReadAllBytesAsync(row.Path).ConfigureAwait(false); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Not("the file is gone");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Not($"the file cannot be read: {ex.Message}");
        }

        // The editor saves a document with the mark it has: a baseline with another would come back with one the file never
        // had, or without the one it did.
        var mark = ChangeEvaluator.ByteOrderMark(bytes);
        if (ChangeEvaluator.ByteOrderMark(current) != mark)
        {
            return Not("its byte-order mark is not the one the copy from before the change has: the editor would save it with the wrong one");
        }

        // The file as it is must be text the same way: a code page decoded as UTF-8 is not what the editor would save.
        var now = ChangeEvaluator.DecodeStrict(current);
        if (now is null) { return Not("the file is not plain UTF-8 or UTF-16 text now"); }
        if (BaselineText.HasMixedLineEndings(now)) { return Not("the file mixes line endings, which the review would level"); }

        return (null, new ReviewBaseline(text, mark, isEmpty: false, BaselineText.DominantLineEnding(text)));
    }

    private static (string Why, ReviewBaseline Baseline) Not(string why) => (why, null);
}
