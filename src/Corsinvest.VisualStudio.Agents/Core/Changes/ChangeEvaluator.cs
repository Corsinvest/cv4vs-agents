/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Text;

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

    public static ChangeEvaluation Evaluate(string path, BaselineRef baseline)
    {
        var info = new FileInfo(path);
        var exists = info.Exists;
        var result = new ChangeEvaluation
        {
            Length = exists ? info.Length : 0,
            WriteUtc = exists ? info.LastWriteTimeUtc : default,
        };

        if (!baseline.IsKnown) { return NoBaseline(result, exists); }
        if (baseline.IsEmpty)
        {
            if (!exists)
            {
                result.Unchanged = true;
                return result;
            }
            result.State = ChangeState.Added;
            Count(result, null, path, 0, info.Length);
            return result;
        }

        var before = new FileInfo(baseline.Path);
        if (!before.Exists)
        {
            result.BaselineMissing = true;
            return NoBaseline(result, exists);
        }
        if (!exists)
        {
            result.State = ChangeState.Deleted;
            Count(result, baseline.Path, null, before.Length, 0);
            return result;
        }

        if (before.Length == info.Length && SameBytes(baseline.Path, path))
        {
            result.Unchanged = true;
            return result;
        }
        result.State = ChangeState.Modified;
        Count(result, baseline.Path, path, before.Length, info.Length);
        return result;
    }

    private static ChangeEvaluation NoBaseline(ChangeEvaluation result, bool exists)
    {
        result.State = exists ? ChangeState.Modified : ChangeState.Deleted;
        result.Counts = CountsKind.Unavailable;
        return result;
    }

    private static void Count(ChangeEvaluation result, string beforePath, string afterPath, long beforeLength, long afterLength)
    {
        if (beforeLength > MaxCountedBytes || afterLength > MaxCountedBytes)
        {
            result.Counts = CountsKind.Unavailable;
            return;
        }

        var before = beforePath is null ? "" : Decode(ReadShared(beforePath));
        var after = afterPath is null ? "" : Decode(ReadShared(afterPath));
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

    // Shared, so a file the editor or a build still has open reads the same as any other.
    private static FileStream OpenShared(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static byte[] ReadShared(string path)
    {
        using var stream = OpenShared(path);
        var bytes = new byte[stream.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = stream.Read(bytes, read, bytes.Length - read);
            if (n == 0) { break; }
            read += n;
        }
        if (read != bytes.Length) { Array.Resize(ref bytes, read); }
        return bytes;
    }

    private static bool SameBytes(string a, string b)
    {
        using var left = OpenShared(a);
        using var right = OpenShared(b);
        var x = new byte[16 * 1024];
        var y = new byte[16 * 1024];
        while (true)
        {
            var nx = Fill(left, x);
            var ny = Fill(right, y);
            if (nx != ny) { return false; }
            if (nx == 0) { return true; }
            for (var i = 0; i < nx; i++)
            {
                if (x[i] != y[i]) { return false; }
            }
        }
    }

    private static int Fill(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) { break; }
            total += n;
        }
        return total;
    }
}
