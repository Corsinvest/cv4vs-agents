/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>One pane's file listing for the `@` picker: built when the picker opens, shared by every
/// keystroke until it opens again. No watcher, no expiry — reopening is the refresh.
/// <para>Called on the handler's thread; <c>list</c> runs synchronously there before it goes to the
/// thread pool, so it may read VS options.</para></summary>
internal sealed class FileIndex(Func<string, CancellationToken, Task<FileListing>> list) : IDisposable
{
    private readonly object _lock = new();
    private string _root;
    private Task<FileListing> _current;
    private CancellationTokenSource _cts;

    public void Refresh(string root)
    {
        lock (_lock) { Start(root); }
    }

    public Task<FileListing> CurrentAsync(string root)
    {
        lock (_lock)
        {
            if (_current == null || !string.Equals(_root, root, StringComparison.OrdinalIgnoreCase)) { Start(root); }
            return _current;
        }
    }

    // The old source is cancelled, not disposed: its build may still be registering on the token.
    private void Start(string root)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _root = root;
        _current = list(root, _cts.Token);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            _current = null;
        }
    }
}
