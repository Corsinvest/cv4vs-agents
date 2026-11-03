/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public sealed class AsyncFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cv4vs-tests-" + Guid.NewGuid().ToString("N"));

    public AsyncFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); }
        catch (IOException) { /* a scratch folder; the next run uses another */ }
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public async Task A_file_is_read_whole()
    {
        var bytes = new byte[100_000];
        new Random(7).NextBytes(bytes);

        Assert.Equal(bytes, await AsyncFile.ReadAllBytesAsync(Write("a.bin", bytes)));
    }

    [Fact]
    public async Task An_empty_file_is_read_as_no_bytes()
    {
        Assert.Empty(await AsyncFile.ReadAllBytesAsync(Write("empty.bin", new byte[0])));
    }

    [Fact]
    public async Task A_file_another_handle_holds_open_for_writing_is_still_read()
    {
        var path = Write("busy.bin", new byte[] { 1, 2, 3 });
        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);

        Assert.Equal(new byte[] { 1, 2, 3 }, await AsyncFile.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_file_that_is_not_there_throws_the_way_file_io_does()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => AsyncFile.ReadAllBytesAsync(Path.Combine(_folder, "absent.bin")));
    }

    [Fact]
    public async Task Filling_a_buffer_stops_at_the_end_of_the_stream_and_says_how_much_it_got()
    {
        using var stream = await AsyncFile.OpenReadAsync(Write("short.bin", new byte[] { 9, 8, 7 }));
        var buffer = new byte[10];

        Assert.Equal(3, await AsyncFile.FillAsync(stream, buffer));
        Assert.Equal(new byte[] { 9, 8, 7 }, new[] { buffer[0], buffer[1], buffer[2] });
        Assert.Equal(0, await AsyncFile.FillAsync(stream, buffer));
    }

    [Fact]
    public async Task A_copy_has_the_bytes_and_replaces_what_was_there()
    {
        var source = Write("source.bin", new byte[] { 1, 2, 3, 4 });
        var target = Write("target.bin", new byte[] { 9, 9, 9, 9, 9, 9 });

        await AsyncFile.CopyAsync(source, target);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(target));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(source));
    }

    [Fact]
    public async Task A_cancelled_read_stops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AsyncFile.ReadAllBytesAsync(Write("a.bin", new byte[] { 1 }), cancelled.Token));
    }
}
