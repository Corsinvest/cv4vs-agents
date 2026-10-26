/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using System;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The copies this extension takes for itself, one folder per session under
/// <paramref name="root"/> (<see cref="AppPaths.BaselinesFolder"/> outside tests).
/// <para>Only where the CLI's own backups do not reach: the CLI writes none for a sub-agent's edit,
/// and none at the moment a file is kept, so the next edit after a Keep has to be compared with a
/// copy taken then. Everything else is read from the CLI's <c>file-history</c>, which is the point of
/// not capturing twice.</para></summary>
internal sealed class ChangeBaselines(string root, OutputWindowLogger log)
{
    /// <summary>The folder the running extension uses.</summary>
    public static ChangeBaselines Default { get; } = new(AppPaths.BaselinesFolder, OutputWindowLogger.Global);

    /// <summary>Copy <paramref name="path"/> as it is now. <see cref="BaselineRef.Empty"/> when it does
    /// not exist (the baseline is "nothing"), <see cref="BaselineRef.None"/> when it could not be read.</summary>
    public BaselineRef Capture(string sessionId, string path)
    {
        try
        {
            if (!File.Exists(path)) { return BaselineRef.Empty; }
            var folder = FolderFor(sessionId);
            Directory.CreateDirectory(folder);
            // The hash keeps two files of the same name apart; the name after it keeps the
            // extension, so the copy opens with the editor the original would.
            var target = Path.Combine(folder, ShortHash(path) + "~" + Path.GetFileName(path));
            File.Copy(path, target, true);
            return BaselineRef.Own(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[changes] could not keep a copy of {path}: {ex.Message}");
            return BaselineRef.None;
        }
    }

    /// <summary>Delete a copy this extension took. A CLI backup, or anything outside the baselines
    /// folder, is left alone whatever the caller believes it owns.</summary>
    public void Release(BaselineRef baseline)
    {
        if (!baseline.Owned || string.IsNullOrEmpty(baseline.Path) || !IsInside(baseline.Path)) { return; }
        try { File.Delete(baseline.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[changes] could not remove {baseline.Path}: {ex.Message}");
        }
    }

    /// <summary>Remove the folders of sessions nobody has touched for <paramref name="olderThan"/>:
    /// a pane that was killed with VS never got to clean up after itself.</summary>
    public void Sweep(TimeSpan olderThan)
    {
        try
        {
            if (!Directory.Exists(root)) { return; }
            var limit = DateTime.UtcNow - olderThan;
            foreach (var folder in Directory.GetDirectories(root))
            {
                if (Directory.GetLastWriteTimeUtc(folder) >= limit) { continue; }
                try { Directory.Delete(folder, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.Warn($"[changes] could not sweep {folder}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"[changes] could not sweep {root}: {ex.Message}");
        }
    }

    public string FolderFor(string sessionId)
        => Path.Combine(root, SessionManager.IsSafePathToken(sessionId) ? sessionId : "unsaved");

    private bool IsInside(string path)
    {
        try
        {
            var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // FNV-1a over the lower-cased path. Not a security hash: it only has to keep names apart, and
    // the framework's own hashes throw on a machine with FIPS mode on.
    internal static string ShortHash(string path)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in path.ToLowerInvariant())
            {
                hash = (hash ^ c) * 16777619u;
            }
            return hash.ToString("x8");
        }
    }
}
