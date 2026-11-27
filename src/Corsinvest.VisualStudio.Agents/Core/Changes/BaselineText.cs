/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Text;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Text-level helpers for putting a baseline into an editor buffer and back out of it. The
/// editor works in text and the list works in bytes, and the two have to agree on line endings and on
/// the byte-order mark, or a hunk kept or undone in the editor would change more of the file than the
/// hunk.</summary>
internal static class BaselineText
{
    public const string Crlf = "\r\n";
    public const string Lf = "\n";

    /// <summary>The line ending <paramref name="text"/> mostly uses, or null when it has no line break at
    /// all. Counted, as the editor's status bar and the comparison window do: a mixture gets whichever it
    /// has more of, and a tie goes to CRLF.</summary>
    public static string DominantLineEnding(string text)
    {
        if (string.IsNullOrEmpty(text)) { return null; }
        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') { continue; }
            if (i > 0 && text[i - 1] == '\r') { crlf++; } else { lf++; }
        }
        if (crlf == 0 && lf == 0) { return null; }
        return crlf >= lf ? Crlf : Lf;
    }

    /// <summary>Whether the text has both kinds of line break, CRLF and a lone LF. Such a file cannot be shown in one line ending
    /// and written back as it was, so it is not reviewed in the editor.</summary>
    public static bool HasMixedLineEndings(string text)
    {
        if (string.IsNullOrEmpty(text)) { return false; }
        var crlf = false;
        var lf = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') { continue; }
            if (i > 0 && text[i - 1] == '\r') { crlf = true; } else { lf = true; }
            if (crlf && lf) { return true; }
        }
        return false;
    }

    /// <summary>Every line break of <paramref name="text"/> written as <paramref name="lineEnding"/>. Null
    /// leaves the text alone: a file with no line break gives no reason to pick one.</summary>
    public static string WithLineEnding(string text, string lineEnding)
    {
        if (string.IsNullOrEmpty(text) || lineEnding is null) { return text; }
        var normalized = text.Replace(Crlf, Lf);
        return lineEnding == Crlf ? normalized.Replace(Lf, Crlf) : normalized;
    }

    /// <summary>What a file holding <paramref name="text"/> looks like in the encoding the byte-order
    /// mark names: UTF-8 with the mark, UTF-8 without it, or UTF-16 of either order. The same families
    /// <see cref="ChangeEvaluator.DecodeStrict"/> hands back, so what was read can be written the way it was
    /// found.</summary>
    public static byte[] Encode(string text, ChangeEvaluator.Mark mark)
    {
        Encoding encoding;
        switch (mark)
        {
            case ChangeEvaluator.Mark.None: encoding = new UTF8Encoding(false); break;
            case ChangeEvaluator.Mark.Utf8: encoding = new UTF8Encoding(true); break;
            case ChangeEvaluator.Mark.Utf16LittleEndian: encoding = new UnicodeEncoding(false, true); break;
            case ChangeEvaluator.Mark.Utf16BigEndian: encoding = new UnicodeEncoding(true, true); break;
            default: throw new NotSupportedException($"A file with the byte-order mark {mark} is not written back from text.");
        }
        return Encode(text, encoding);
    }

    /// <summary>The text in <paramref name="encoding"/>, its preamble in front: the bytes <c>File.WriteAllText</c> would write.
    /// One array, since the result is handed on and kept, so there is nothing to give back to a pool.</summary>
    public static byte[] Encode(string text, Encoding encoding)
    {
        text ??= "";
        var preamble = encoding.GetPreamble();
        var bytes = new byte[preamble.Length + encoding.GetByteCount(text)];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        encoding.GetBytes(text, 0, text.Length, bytes, preamble.Length);
        return bytes;
    }
}
