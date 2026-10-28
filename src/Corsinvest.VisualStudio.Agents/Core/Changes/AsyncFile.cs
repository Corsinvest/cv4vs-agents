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

/// <summary>Reading and copying a file's content without holding a thread while the disk answers.
/// .NET Framework has no <c>File.ReadAllBytesAsync</c> and the like, so this is a
/// <see cref="FileStream"/> opened with <see cref="FileOptions.Asynchronous"/>. Opening the file, like asking
/// whether it exists, how long it is, or deleting it, has no async form in any .NET: those are made on the pool, so
/// a caller on the UI thread never waits for them.</summary>
internal static class AsyncFile
{
    private const int BufferSize = 16 * 1024;

    /// <summary>Open for reading, shared: a file the editor or a build still has open reads the same as any other.</summary>
    public static Task<FileStream> OpenReadAsync(string path)
        => Task.Run(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize,
                                         FileOptions.Asynchronous | FileOptions.SequentialScan));

    /// <summary>Create or replace a file for writing.</summary>
    public static Task<FileStream> CreateAsync(string path)
        => Task.Run(() => new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous));

    /// <summary>Read until <paramref name="buffer"/> is full or the stream ends; how much was read.</summary>
    public static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken cancel = default)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, total, buffer.Length - total, cancel).ConfigureAwait(false);
            if (read == 0) { break; }
            total += read;
        }
        return total;
    }

    public static async Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancel = default)
    {
        using var stream = await OpenReadAsync(path).ConfigureAwait(false);
        var bytes = new byte[stream.Length];
        var read = await FillAsync(stream, bytes, cancel).ConfigureAwait(false);
        // A file that shrank while it was read.
        if (read != bytes.Length) { Array.Resize(ref bytes, read); }
        return bytes;
    }

    /// <summary>The first <paramref name="count"/> bytes of a file, or fewer when it is shorter.</summary>
    public static async Task<byte[]> ReadHeadAsync(string path, int count, CancellationToken cancel = default)
    {
        using var stream = await OpenReadAsync(path).ConfigureAwait(false);
        var head = new byte[count];
        var read = await FillAsync(stream, head, cancel).ConfigureAwait(false);
        if (read != count) { Array.Resize(ref head, read); }
        return head;
    }

    /// <summary>Write a file, replacing what was there.</summary>
    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancel = default)
    {
        using var stream = await CreateAsync(path).ConfigureAwait(false);
        await stream.WriteAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false);
    }

    /// <summary>Write a file as <c>File.WriteAllText</c> would: the encoding's preamble, then the text.</summary>
    public static Task WriteAllTextAsync(string path, string text, Encoding encoding, CancellationToken cancel = default)
        => WriteAllBytesAsync(path, BaselineText.Encode(text, encoding), cancel);

    /// <summary>Copy a file, reading it shared. Replaces <paramref name="target"/>.</summary>
    public static async Task CopyAsync(string path, string target, CancellationToken cancel = default)
    {
        using var source = await OpenReadAsync(path).ConfigureAwait(false);
        using var destination = await CreateAsync(target).ConfigureAwait(false);
        await source.CopyToAsync(destination, BufferSize, cancel).ConfigureAwait(false);
    }
}
