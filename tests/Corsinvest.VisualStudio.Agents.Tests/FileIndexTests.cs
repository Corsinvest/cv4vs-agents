/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.VisualStudio.Agents.Chat.Host;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>One listing per opening of the picker: keystrokes share it, a new opening cancels the one
/// still building, another working directory rebuilds.</summary>
public class FileIndexTests
{
    private readonly List<(string Root, CancellationToken Ct, TaskCompletionSource<FileListing> Tcs)> _calls = [];

    private FileIndex NewIndex() => new((root, ct) =>
    {
        var tcs = new TaskCompletionSource<FileListing>();
        ct.Register(() => tcs.TrySetCanceled());
        _calls.Add((root, ct, tcs));
        return tcs.Task;
    });

    [Fact]
    public async Task Keystrokes_during_a_build_share_it()
    {
        using var index = NewIndex();
        index.Refresh(@"C:\a");
        var first = index.CurrentAsync(@"C:\a");
        var second = index.CurrentAsync(@"C:\a");
        Assert.Single(_calls);
        _calls[0].Tcs.SetResult(FileListing.Success(["x.cs"], 1, null));
        Assert.Same(await first, await second);
    }

    [Fact]
    public async Task Refresh_cancels_the_previous_build()
    {
        using var index = NewIndex();
        index.Refresh(@"C:\a");
        var old = index.CurrentAsync(@"C:\a");
        index.Refresh(@"C:\a");
        Assert.True(_calls[0].Ct.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public void CurrentAsync_without_Refresh_builds_once()
    {
        using var index = NewIndex();
        index.CurrentAsync(@"C:\a");
        index.CurrentAsync(@"C:\a");
        Assert.Single(_calls);
    }

    [Fact]
    public void CurrentAsync_with_another_root_rebuilds()
    {
        using var index = NewIndex();
        index.Refresh(@"C:\a");
        index.CurrentAsync(@"C:\b");
        Assert.Equal(2, _calls.Count);
        Assert.Equal(@"C:\b", _calls[1].Root);
        Assert.True(_calls[0].Ct.IsCancellationRequested);
    }

    [Fact]
    public void Dispose_cancels_the_build_in_progress()
    {
        var index = NewIndex();
        index.Refresh(@"C:\a");
        index.Dispose();
        Assert.True(_calls[0].Ct.IsCancellationRequested);
    }
}
