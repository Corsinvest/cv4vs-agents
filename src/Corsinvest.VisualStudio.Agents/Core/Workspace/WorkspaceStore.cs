/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Workspace;

/// <summary>Reads/writes the per-solution <c>workspace.json</c> (see <see cref="WorkspaceState"/>).
/// Save snapshots the live registry; Load is validated by the restore path (missing profile/session
/// handled there).</summary>
internal static class WorkspaceStore
{
    /// <summary>Snapshot the currently-open panes (ordered by SeqNo) into the solution's workspace.json.
    /// No-op when solutionFolder is null (home-born panes have no per-solution workspace).</summary>
    public static void Save(string solutionFolder, string savedAtIso)
    {
        if (string.IsNullOrEmpty(solutionFolder)) { return; }
        try
        {
            var state = new WorkspaceState
            {
                Version = 1,
                SavedAt = savedAtIso,
                Panes = PaneRegistry.Instance.Entries
                    .OrderBy(e => e.SeqNo)
                    .Select(e => new PaneState
                    {
                        Kind = e.Kind == PaneKind.Cli ? "Cli" : "Chat",
                        Profile = e.Profile.Name,
                        SessionId = e.ActiveSessionId,
                    })
                    .ToList(),
            };

            var path = AppPaths.WorkspaceFile(solutionFolder);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = JsonConvert.SerializeObject(state, Formatting.Indented);
            // Atomic write (tmp + replace) so a crash mid-write can't corrupt the file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) { File.Delete(path); }
            File.Move(tmp, path);
        }
        catch (Exception ex) { OutputWindowLogger.LogException("WorkspaceStore.Save", ex); }
    }

    /// <summary>Load the solution's workspace, or null if absent/unreadable.</summary>
    public static WorkspaceState Load(string solutionFolder)
    {
        if (string.IsNullOrEmpty(solutionFolder)) { return null; }
        try
        {
            var path = AppPaths.WorkspaceFile(solutionFolder);
            if (!File.Exists(path)) { return null; }
            return JsonConvert.DeserializeObject<WorkspaceState>(File.ReadAllText(path));
        }
        catch (Exception ex) { OutputWindowLogger.LogException("WorkspaceStore.Load", ex); return null; }
    }
}
