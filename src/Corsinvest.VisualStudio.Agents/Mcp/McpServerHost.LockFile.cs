/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// McpServerHost, lock-file side: writes/syncs/removes the &lt;port&gt;.lock discovery file in the
/// ide/ folder of every enabled profile's config-dir, and cleans up orphan locks from dead VS
/// processes. The WebSocket server, client loops, and context broadcast live in McpServerHost.cs.
/// </summary>
internal sealed partial class McpServerHost
{
    private void WriteLockFile(string ideFolder)
    {
        Directory.CreateDirectory(ideFolder);
        // Synchronous lookup avoids re-entrancy with the dispatcher (called from Start and on solution change).
        var folders = ResolveWorkspaceFoldersBlocking();
        var lockObj = new
        {
            pid = Process.GetCurrentProcess().Id,
            workspaceFolders = folders,
            ideName = ResolveIdeNameBlocking(),
            transport = "ws",
            authToken = _authToken,
        };
        var newPath = Path.Combine(ideFolder, $"{_port}.lock");
        // Serialised against other writers of the same file: a solution reload fires
        // RewriteLockFileAsync twice within a second (close, then open) and both land on the pool,
        // so two WriteAllText calls can meet on this path — the second one throwing IOException
        // because the first still has it open.
        lock (_writeGate)
        {
            try
            {
                File.WriteAllText(newPath, JsonConvert.SerializeObject(lockObj));
            }
            catch (IOException ex)
            {
                // Whoever is holding it writes the same contents — same pid, same port, same token —
                // so losing this write costs nothing. Worth a line, though: a lock that cannot be
                // written for any other reason leaves the CLI unable to discover this server.
                OutputWindowLogger.Global.Warn($"[mcp] lock file busy, write skipped: {ex.Message}");
                return;
            }
        }
        lock (_lockFoldersGate) { _lockFolders.Add(ideFolder); }
    }

    /// <summary>Held across the lock-file write only. Separate from _lockFoldersGate, which guards
    /// the folder set and is deliberately not held across file I/O. Static: one server per process,
    /// so there is nothing per-instance to serialise, and a type initialiser runs even for an object
    /// that was already alive — which an instance field's initialiser does not, under Hot Reload.</summary>
    private static readonly object _writeGate = new();

    private static string[] ResolveWorkspaceFoldersBlocking() =>
        // Sync entry point for off-UI-thread callers; JTF avoids COM deadlocks in DTE.
        Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async () =>
        {
            var f = await IdeContextService.Instance.GetWorkspaceFoldersAsync();
            var arr = new string[f.Count];
            for (int i = 0; i < f.Count; i++) { arr[i] = f[i]; }
            return arr;
        });

    private static string ResolveIdeNameBlocking() => Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(
            async () => await IdeContextService.Instance.GetIdeNameAsync());

    private void DeleteAllLockFiles()
    {
        string[] folders;
        lock (_lockFoldersGate) { folders = [.. _lockFolders]; _lockFolders.Clear(); }
        foreach (var folder in folders)
        {
            try
            {
                var path = Path.Combine(folder, $"{_port}.lock");
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch { /* tolerated: stale lock is ignored by the CLI via pid check */ }
        }
    }

    /// <summary>Publish/remove the lock so it lives in exactly the ide/ folders of ALL profiles'
    /// config-dirs (native "Claude" included), while the server runs.</summary>
    private void SyncLockFolders()
    {
        if (_port == 0) { return; } // not running
        var wanted = new HashSet<string>(ProfileIdeFolders(), StringComparer.OrdinalIgnoreCase);

        // Compute the add/remove diff under the gate, then do the (slow) file I/O outside it.
        List<string> toAdd, toRemove;
        lock (_lockFoldersGate)
        {
            toAdd = [.. wanted.Where(f => !_lockFolders.Contains(f))];
            toRemove = [.. _lockFolders.Where(f => !wanted.Contains(f))];
            foreach (var f in toRemove) { _lockFolders.Remove(f); }
        }

        foreach (var folder in toAdd) { WriteLockFile(folder); } // re-adds to _lockFolders under the gate
        foreach (var folder in toRemove)
        {
            try
            {
                var path = Path.Combine(folder, $"{_port}.lock");
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch { /* tolerated */ }
        }
    }

    private void OnProfilesChanged() => SyncLockFolders();

    /// <summary>Remove lock files whose owning pid is no longer alive (crashed/killed VS), so the
    /// CLI doesn't pick a dead server and hang on connect. Runs only at Start, over every known
    /// config-dir's ide/ folder (system default + currently-registered panes').</summary>
    private static void CleanupOrphanLockFiles(IEnumerable<string> ideFolders)
    {
        foreach (var ideFolder in ideFolders)
        {
            if (!Directory.Exists(ideFolder)) { continue; }
            foreach (var f in Directory.GetFiles(ideFolder, "*.lock"))
            {
                try
                {
                    var json = File.ReadAllText(f);
                    var obj = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                    var pid = (int?)obj?["pid"] ?? 0;
                    if (pid > 0 && IsProcessAlive(pid)) { continue; }
                    File.Delete(f);
                    OutputWindowLogger.Global.Debug(() => $"Mcp: cleaned orphan lock {Path.GetFileName(f)}");
                }
                catch { /* unreadable / locked: skip */ }
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { Process.GetProcessById(pid); return true; }
        catch { return false; }
    }
}
