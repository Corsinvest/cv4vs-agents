/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Core.FileHistory;

/// <summary>The right-hand panel and the actions on it: the tiles, the file list, the diff, the
/// save-a-copy, and the delete. Split from the tree half the way Context usage splits its render,
/// so neither file has to be read to work on the other.</summary>
public partial class FileHistoryControl
{
    /// <summary>One row of the file list — a backed-up file with the copy that holds it.</summary>
    internal sealed class BackupRow
    {
        /// <summary>As the CLI spelled it: relative to the working directory when it sits under it.</summary>
        public string RecordedPath { get; set; }

        /// <summary>Absolute, for the diff and for opening the file.</summary>
        public string FullPath { get; set; }

        /// <summary>The copy under `file-history/&lt;session&gt;/`.</summary>
        public string BackupPath { get; set; }

        public int Version { get; set; }
        public long Bytes { get; set; }
        public DateTime BackupTime { get; set; }

        /// <summary>Directory dimmed to a leading ellipsis: the interesting half is the file name,
        /// and the paths share most of their length.</summary>
        public string DisplayPath
        {
            get
            {
                var dir = Path.GetDirectoryName(RecordedPath) ?? "";
                var name = Path.GetFileName(RecordedPath);
                if (dir.Length == 0) { return name; }
                return dir.Length <= 34 ? Path.Combine(dir, name) : "…" + dir.Substring(dir.Length - 34) + Path.DirectorySeparatorChar + name;
            }
        }

        public string SizeText => FileHistoryService.Format.Size(Bytes);
        public string BackupTimeText => BackupTime == default ? "" : BackupTime.ToString("d MMM HH:mm");
    }

    private void ShowGroup(FileHistoryTreeNode node)
    {
        if (node == null) { ShowMessage("Select a session to see the files it backed up."); return; }

        TilesPanel.Children.Clear();
        var sessions = node.Children.Count(c => c.Kind == FileHistoryNodeKind.Session);
        AddTile(FileHistoryService.Format.Size(node.Bytes), "on disk");
        if (sessions > 0) { AddTile(sessions.ToString(), "sessions"); }

        MessageText.Text = node.Kind == FileHistoryNodeKind.Orphans
            ? "These backups belong to transcripts that are no longer on disk. Nothing else refers to them."
            : "Select a session to see the files it backed up.";
        MessageText.Visibility = Visibility.Visible;
        FilesList.Visibility = Visibility.Collapsed;
        FilesHead.Text = "FILES";
    }

    // A session was picked: tiles from the scan (already in hand), the file list from its transcript.
    private void ShowSession(FileHistoryTreeNode node)
    {
        var session = node.Session;
        TilesPanel.Children.Clear();
        AddTile(FileHistoryService.Format.Size(session.Bytes), "on disk");
        AddTile(session.FileCount.ToString(), "files");
        AddTile(session.CopyCount.ToString(), "copies");
        AddTile(session.LastWrite.ToString("d MMM"), "last");

        FilesHead.Text = $"FILES · {session.SessionId}";

        if (session.IsOrphan)
        {
            // No transcript, so no path for any of the copies — only the folder is left to open.
            MessageText.Text = "The transcript is gone, so the real paths of these copies are unknown. "
                             + "The folder can still be opened or deleted.";
            MessageText.Visibility = Visibility.Visible;
            FilesList.Visibility = Visibility.Collapsed;
            FilesList.ItemsSource = null;
            return;
        }

        // The recorded paths are relative to the session's working directory when they sit under
        // it, so the absolute form needs the cwd the project was run from.
        var cwd = Stats.StatsService.CwdForProject(node.Profile, session.ProjectDir);

        List<BackupRow> rows;
        try
        {
            rows = ReadRows(node.Paths, cwd, session);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("FileHistoryControl.ShowSession", ex);
            MessageText.Text = "Could not read the transcript — see the output log.";
            MessageText.Visibility = Visibility.Visible;
            FilesList.Visibility = Visibility.Collapsed;
            return;
        }

        if (rows.Count == 0)
        {
            MessageText.Text = "The transcript records no backups for this session.";
            MessageText.Visibility = Visibility.Visible;
            FilesList.Visibility = Visibility.Collapsed;
            FilesList.ItemsSource = null;
            return;
        }

        FilesList.ItemsSource = rows;
        FilesList.Visibility = Visibility.Visible;
        MessageText.Visibility = Visibility.Collapsed;
    }

    private static List<BackupRow> ReadRows(ClaudePaths paths, string cwd, FileHistoryService.SessionBackups session)
    {
        var manager = new SessionManager(paths, cwd ?? "", OutputWindowLogger.Global);
        var rows = new List<BackupRow>();
        foreach (var backup in manager.ReadAllFileBackups(session.SessionId))
        {
            var backupPath = Path.Combine(session.Folder, backup.BackupFileName);
            // The CLI prunes its own history, so a transcript can name a copy that is gone. A row
            // for a file with nothing behind it could neither be diffed nor saved.
            if (!File.Exists(backupPath)) { continue; }
            var info = new FileInfo(backupPath);
            rows.Add(new BackupRow
            {
                RecordedPath = backup.Path,
                FullPath = Path.IsPathRooted(backup.Path) ? backup.Path : Path.Combine(cwd ?? "", backup.Path),
                BackupPath = backupPath,
                Version = backup.Version,
                Bytes = info.Length,
                BackupTime = info.LastWriteTime,
            });
        }
        rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return rows;
    }

    private void AddTile(string value, string label)
    {
        // No Foreground set: the theme inherits down the live visual tree (UseVsTheme), so runtime
        // text follows it too. Secondary text is dimmed with Opacity, never a hardcoded brush.
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)Resources["TileValueStyle"],
        });
        stack.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            Style = (Style)Resources["TileLabelStyle"],
        });
        TilesPanel.Children.Add(new Border
        {
            Style = (Style)Resources["TileStyle"],
            Child = stack,
        });
    }

    // A DataGrid raises this for its column headers and its empty area too, where SelectedItem is
    // still whatever was picked last — a double click on a header would re-open that row's diff.
    private void OnFileDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsInsideRow(e.OriginalSource as DependencyObject)) { return; }
        CompareSelected();
    }

    private static bool IsInsideRow(DependencyObject source)
    {
        for (var d = source; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is DataGridRow) { return true; }
        }
        return false;
    }

    private void OnCompareClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        CompareSelected();
    }

    private void CompareSelected()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (FilesList.SelectedItem is not BackupRow row) { return; }
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                var before = File.ReadAllText(row.BackupPath);
                var current = File.Exists(row.FullPath) ? File.ReadAllText(row.FullPath) : "";
                await Ide.IdeContextService.Instance.ShowDiffAsync(
                    $"filehistory:{row.BackupPath}", row.FullPath, before, current,
                    leftLabel: $"Backup v{row.Version}", rightLabel: "Current");
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("FileHistoryControl.Compare", ex);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShellHelpers.ShowMessage("Could not open the comparison — see the output log.",
                                         "File history", warning: true);
            }
        });
    }

    // "Save a copy as", not "Restore": this writes a file somewhere the user picks and leaves both
    // the project file and the conversation alone. A rewind is a different thing, and lives in the
    // chat pane.
    private void OnSaveCopyClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (FilesList.SelectedItem is not BackupRow row) { return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = Path.GetFileName(row.FullPath),
            InitialDirectory = Path.GetDirectoryName(row.FullPath),
            Title = "Save a copy of the backup",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) { return; }
        try
        {
            File.Copy(row.BackupPath, dialog.FileName, overwrite: true);
            OutputWindowLogger.Global.Info($"[filehistory] saved a copy of {row.RecordedPath} v{row.Version} to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("FileHistoryControl.SaveCopy", ex);
            ShellHelpers.ShowMessage("Could not save the copy — see the output log.",
                                     "File history", warning: true);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        var folder = _selected?.Session?.Folder;
        if (folder != null) { ShellHelpers.OpenExternal(folder); }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var targets = _roots.SelectMany(r => r.CheckedSessions()).ToList();
        if (targets.Count == 0) { return; }

        var bytes = targets.Sum(n => n.Bytes);
        var copies = targets.Sum(n => n.Session.CopyCount);
        var files = targets.Sum(n => n.Session.FileCount);

        // Name what goes, and what does not: the project files are the thing a user fears for here.
        var listed = string.Join("\n", targets.Take(10).Select(n => "  " + n.Label));
        if (targets.Count > 10) { listed += $"\n  … and {targets.Count - 10} more"; }
        var text = $"Delete the backups of {targets.Count} session(s): {copies} copies of {files} files, "
                 + $"{FileHistoryService.Format.Size(bytes)}.\n\n{listed}\n\n"
                 + "Your project files are not touched. The copies cannot be recovered, and rewinding "
                 + "these sessions will no longer work.";

        if (!ShellHelpers.Confirm(text, "Delete backups")) { return; }

        var failed = targets.Count(n => !FileHistoryService.Delete(n.Session));
        if (failed > 0)
        {
            ShellHelpers.ShowMessage(
                $"{failed} of {targets.Count} folder(s) could not be deleted — a running CLI may still hold them. "
                + "See the output log.",
                "File history", warning: true);
        }
        Reload();
    }
}
