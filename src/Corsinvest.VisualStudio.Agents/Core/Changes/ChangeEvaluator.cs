/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>What a file is now, measured against its baseline.</summary>
internal sealed class ChangeEvaluation
{
    /// <summary>Identical to the baseline (or created and removed again): not a change at all.</summary>
    public bool Unchanged { get; set; }

    /// <summary>The baseline names a file that is no longer there.</summary>
    public bool BaselineMissing { get; set; }

    public ChangeState State { get; set; }
    public int Added { get; set; }
    public int Deleted { get; set; }
    public CountsKind Counts { get; set; }
    public bool IsBinary { get; set; }

    // The file's stamps, so a refresh can tell that nothing moved.
    public long Length { get; set; }
    public DateTime WriteUtc { get; set; }
}

/// <summary>Decides, from a baseline and the file on disk, whether a file changed and by how much.
/// Reads bytes only: it never asks the editor, so it runs anywhere and sees what a build or a
/// <c>git</c> command would.</summary>
internal static class ChangeEvaluator
{
    // Above this a side is not read into memory to be counted. Equality is still decided, by
    // streaming, so a large file that did not change is not listed.
    public const long MaxCountedBytes = 8L * 1024 * 1024;

    /// <summary>What the file system says about a path. There is no async form of any of it, so it is read on the
    /// pool.</summary>
    private readonly struct Stat(bool exists, long length, DateTime writeUtc)
    {
        public bool Exists { get; } = exists;
        public long Length { get; } = length;
        public DateTime WriteUtc { get; } = writeUtc;
    }

    private static Task<Stat> StatAsync(string path) => Task.Run(() =>
    {
        var info = new FileInfo(path);
        var exists = info.Exists;
        return new Stat(exists, exists ? info.Length : 0, exists ? info.LastWriteTimeUtc : default);
    });

    public static async Task<ChangeEvaluation> EvaluateAsync(string path, BaselineRef baseline, CancellationToken cancel = default)
    {
        try
        {
            return await EvaluateCoreAsync(path, baseline, cancel).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked by a build, or gone between the check and the read: it cannot be measured now. One
            // such file must not stop the rest of a list being built, so it is listed as changed with no
            // numbers, and given a stamp no real file has so that the next refresh tries it again.
            var unreadable = NoBaseline(new ChangeEvaluation(), (await StatAsync(path).ConfigureAwait(false)).Exists);
            unreadable.Length = -1;
            return unreadable;
        }
    }

    private static async Task<ChangeEvaluation> EvaluateCoreAsync(string path, BaselineRef baseline, CancellationToken cancel)
    {
        var info = await StatAsync(path).ConfigureAwait(false);
        var result = new ChangeEvaluation
        {
            Length = info.Length,
            WriteUtc = info.WriteUtc,
        };

        if (!baseline.IsKnown) { return NoBaseline(result, info.Exists); }
        if (baseline.IsEmpty)
        {
            if (!info.Exists)
            {
                result.Unchanged = true;
                return result;
            }
            result.State = ChangeState.Added;
            await CountAsync(result, null, path, 0, info.Length, cancel).ConfigureAwait(false);
            return result;
        }

        var before = await StatAsync(baseline.Path).ConfigureAwait(false);
        if (!before.Exists)
        {
            result.BaselineMissing = true;
            return NoBaseline(result, info.Exists);
        }
        if (!info.Exists)
        {
            result.State = ChangeState.Deleted;
            await CountAsync(result, baseline.Path, null, before.Length, 0, cancel).ConfigureAwait(false);
            return result;
        }

        if (before.Length == info.Length && await SameBytesAsync(baseline.Path, path, cancel).ConfigureAwait(false))
        {
            result.Unchanged = true;
            return result;
        }
        result.State = ChangeState.Modified;
        await CountAsync(result, baseline.Path, path, before.Length, info.Length, cancel).ConfigureAwait(false);
        return result;
    }

    private static ChangeEvaluation NoBaseline(ChangeEvaluation result, bool exists)
    {
        result.State = exists ? ChangeState.Modified : ChangeState.Deleted;
        result.Counts = CountsKind.Unavailable;
        return result;
    }

    private static async Task CountAsync(ChangeEvaluation result, string beforePath, string afterPath, long beforeLength, long afterLength,
                                         CancellationToken cancel)
    {
        if (beforeLength > MaxCountedBytes || afterLength > MaxCountedBytes)
        {
            result.Counts = CountsKind.Unavailable;
            return;
        }

        var before = beforePath is null ? "" : Decode(await AsyncFile.ReadAllBytesAsync(beforePath, cancel).ConfigureAwait(false));
        var after = afterPath is null ? "" : Decode(await AsyncFile.ReadAllBytesAsync(afterPath, cancel).ConfigureAwait(false));
        if ((beforePath is not null && before is null) || (afterPath is not null && after is null))
        {
            result.IsBinary = true;
            result.Counts = CountsKind.Unavailable;
            return;
        }

        var counts = LineDiff.Count(before, after);
        result.Added = counts.Added;
        result.Deleted = counts.Deleted;
        result.Counts = counts.Approximate ? CountsKind.Approximate : CountsKind.Exact;
    }

    /// <summary>The text of a file's bytes, or null when they are not text. A byte-order mark decides
    /// the encoding; without one a NUL in the first 8000 bytes means binary, anything else is read as
    /// UTF-8, which for an ANSI file merely swaps its accented letters for the same replacement
    /// character on both sides, leaving the line counts right.</summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) { return ""; }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        var probe = Math.Min(bytes.Length, 8000);
        for (var i = 0; i < probe; i++)
        {
            if (bytes[i] != 0) { continue; }
            return null;
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>The text of a file's bytes only when turning it back into those bytes gives the same ones: a
    /// valid UTF-8 or UTF-16 text, with or without its byte-order mark. Null for anything else (a legacy code
    /// page, UTF-32, binary). <see cref="Decode"/> is for counting lines, where a replaced letter does no
    /// harm; this is for putting a file back, where the letter would be the file's.</summary>
    internal static string DecodeStrict(byte[] bytes)
    {
        if (bytes.Length == 0) { return ""; }
        try
        {
            switch (ByteOrderMark(bytes))
            {
                case Mark.Utf8: return new UTF8Encoding(false, true).GetString(bytes, 3, bytes.Length - 3);
                case Mark.Utf16LittleEndian: return new UnicodeEncoding(false, false, true).GetString(bytes, 2, bytes.Length - 2);
                case Mark.Utf16BigEndian: return new UnicodeEncoding(true, false, true).GetString(bytes, 2, bytes.Length - 2);
                case Mark.Utf32LittleEndian: return null;
            }
            if (Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0) { return null; }
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>Which byte-order mark a file starts with. Two files that read alike but start differently
    /// would not be the same file once saved by an editor that decides the mark from what it loaded.</summary>
    internal enum Mark { None, Utf8, Utf16LittleEndian, Utf16BigEndian, Utf32LittleEndian }

    internal static Mark ByteOrderMark(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { return Mark.Utf8; }
        // FF FE 00 00 is a UTF-32 mark: it would read as UTF-16 with a stray NUL.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) { return Mark.Utf32LittleEndian; }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { return Mark.Utf16LittleEndian; }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { return Mark.Utf16BigEndian; }
        return Mark.None;
    }

    /// <summary>Whether the file on disk starts with the same byte-order mark as <paramref name="baseline"/>.
    /// What is put back into an editor is saved by it with the mark the document has, so a baseline whose mark
    /// differs would come back with one the file never had, or without the one it did.</summary>
    internal static async Task<bool> SameMarkAsync(byte[] baseline, string path)
    {
        var head = await AsyncFile.ReadHeadAsync(path, 4).ConfigureAwait(false);
        return ByteOrderMark(head) == ByteOrderMark(baseline);
    }

    private static async Task<bool> SameBytesAsync(string a, string b, CancellationToken cancel)
    {
        using var left = await AsyncFile.OpenReadAsync(a).ConfigureAwait(false);
        using var right = await AsyncFile.OpenReadAsync(b).ConfigureAwait(false);
        var x = new byte[16 * 1024];
        var y = new byte[16 * 1024];
        while (true)
        {
            var nx = await AsyncFile.FillAsync(left, x, cancel).ConfigureAwait(false);
            var ny = await AsyncFile.FillAsync(right, y, cancel).ConfigureAwait(false);
            if (nx != ny) { return false; }
            if (nx == 0) { return true; }
            for (var i = 0; i < nx; i++)
            {
                if (x[i] != y[i]) { return false; }
            }
        }
    }
}
