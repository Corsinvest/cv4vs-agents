/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Core.FileHistory;

/// <summary>
/// The File history tool window. A left tree (config-dir → project → session) lists what the
/// CLI's file backups occupy; the right panel lists the selected session's files, with a diff on
/// double click. Checked sessions can be deleted.
/// <para>Same shape as Context usage — tree, splitter, code-behind panel — but the tree is built
/// here from `file-history/` rather than by StatsService: see <see cref="FileHistoryService"/> for why.
/// Nothing is indexed, so there is no loading overlay and no Recreate button.</para>
/// </summary>
public partial class FileHistoryControl : UserControl
{
    private List<FileHistoryTreeNode> _roots = [];
    private FileHistoryTreeNode _selected;
    private bool _loaded;

    public FileHistoryControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // Loaded fires again on every re-activation; build once.
            if (_loaded) { return; }
            _loaded = true;
            Reload();
        };
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    /// <summary>Which header column the tree is ordered by.</summary>
    private enum SortBy { Name, Date, Size }

    // Size descending to open on: the pane's first question is what costs the most.
    private SortBy _sortBy = SortBy.Size;
    private bool _descending = true;

    private void OnSortByName(object sender, RoutedEventArgs e) => SortByColumn(SortBy.Name);

    private void OnSortByDate(object sender, RoutedEventArgs e) => SortByColumn(SortBy.Date);

    private void OnSortBySize(object sender, RoutedEventArgs e) => SortByColumn(SortBy.Size);

    // Clicking the sorted column reverses it; clicking another switches to it. A name starts
    // ascending (A→Z reads as sorted), a size or a date descending (biggest and newest first).
    private void SortByColumn(SortBy column)
    {
        if (_sortBy == column) { _descending = !_descending; }
        else { _sortBy = column; _descending = column != SortBy.Name; }
        if (!_loaded) { return; }
        Sort(_roots);
        foreach (var node in _roots.SelectMany(AllNodes)) { Sort(node.Children); }
        ShowArrows();
        // The TreeView binds to the same list objects, so a re-sort in place is invisible to it:
        // re-seating ItemsSource is what makes it re-read the order.
        Tree.ItemsSource = null;
        Tree.ItemsSource = _roots;
    }

    // An arrow on the sorted column only — three of them would say nothing about which one wins.
    private void ShowArrows()
    {
        var arrow = _descending ? "▾" : "▴";
        NameArrow.Text = _sortBy == SortBy.Name ? arrow : "";
        DateArrow.Text = _sortBy == SortBy.Date ? arrow : "";
        SizeArrow.Text = _sortBy == SortBy.Size ? arrow : "";
    }

    // Scan off the UI thread — a config-dir can sit on a network share — then rebuild the tree.
    private void Reload()
    {
        RefreshButton.IsEnabled = false;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            List<FileHistoryService.ConfigDirBackups> scan = null;
            string error = null;
            try
            {
                await System.Threading.Tasks.Task.Run(() => scan = FileHistoryService.Scan());
            }
            catch (Exception ex)
            {
                OutputWindowLogger.Global.LogException("FileHistoryControl.Reload", ex);
                error = "Could not read the backup folders — see the output log.";
            }
            // Back on the UI thread before touching anything WPF — including re-enabling the
            // button, which is why that isn't in a finally around the scan.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (error != null) { ShowMessage(error); } else { BuildTree(scan); }
            RefreshButton.IsEnabled = true;
        });
    }

    private void BuildTree(List<FileHistoryService.ConfigDirBackups> scan)
    {
        var live = FileHistoryService.LiveSessionIds();
        _roots = [];

        foreach (var config in scan ?? [])
        {
            // One config-dir is the common case (several profiles usually share ~/.claude), and a
            // root level with a single child is noise: the projects become the roots instead.
            var configNode = new FileHistoryTreeNode
            {
                Kind = FileHistoryNodeKind.ConfigDir,
                Label = ShortConfigDir(config.Paths.ClaudeFolder),
                Tooltip = config.Profiles.Count > 0
                    ? $"{config.Paths.ClaudeFolder}\n{string.Join(", ", config.Profiles.Select(p => p.Name))}"
                    : config.Paths.ClaudeFolder,
                Paths = config.Paths,
                Profile = config.Profiles.FirstOrDefault(),
                IsExpanded = true,
            };

            foreach (var group in GroupByProject(config))
            {
                configNode.Children.Add(group);
                group.Parent = configNode;
            }
            configNode.Bytes = configNode.Children.Sum(c => c.Bytes);
            configNode.LastWrite = configNode.Children.Count == 0
                ? default
                : configNode.Children.Max(c => c.LastWrite);
            configNode.CanDelete = configNode.Children.Any(c => c.CanDelete);
            _roots.Add(configNode);
        }
        Sort(_roots);

        var single = _roots.Count == 1 ? _roots[0] : null;
        if (single != null)
        {
            foreach (var c in single.Children) { c.Parent = null; }
            _roots = single.Children;
        }

        Tree.ItemsSource = null;
        Tree.ItemsSource = _roots;
        _selected = null;
        ShowMessage(_roots.Count == 0
            ? "No file backups on disk. The CLI writes them only for sessions started with file history enabled."
            : "Select a session to see the files it backed up.");
        UpdateStatus(live);
        ShowArrows();

        // Sessions with backups are the only rows here, and a session driven by an open pane is not
        // deletable — say so once, rather than leaving a disabled checkbox unexplained.
        var blocked = _roots.SelectMany(AllNodes)
                            .Count(n => n.Kind == FileHistoryNodeKind.Session && !n.CanDelete);
        LiveText.Visibility = blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Text = blocked == 1
            ? "1 session is open in a pane — not deletable"
            : $"{blocked} sessions are open in panes — not deletable";
    }

    // Projects, then their sessions. Orphans — backups whose transcript is gone — get their own
    // group: they are the primary target of a clean-up, and no project can hold them.
    private IEnumerable<FileHistoryTreeNode> GroupByProject(FileHistoryService.ConfigDirBackups config)
    {
        var live = FileHistoryService.LiveSessionIds();
        var groups = new List<FileHistoryTreeNode>();
        FileHistoryTreeNode orphans = null;

        foreach (var byProject in config.Sessions.GroupBy(s => s.ProjectDir, StringComparer.OrdinalIgnoreCase))
        {
            var isOrphan = byProject.Key == null;
            var node = new FileHistoryTreeNode
            {
                Kind = isOrphan ? FileHistoryNodeKind.Orphans : FileHistoryNodeKind.Project,
                Label = isOrphan ? $"Sessions no longer on disk ({byProject.Count()})" : ProjectLabel(byProject.Key),
                Tooltip = isOrphan
                    ? "The transcript these backups belong to is gone. Nothing else refers to them."
                    : byProject.Key,
                Paths = config.Paths,
                Profile = config.Profiles.FirstOrDefault(),
            };

            foreach (var session in byProject)
            {
                var child = new FileHistoryTreeNode
                {
                    Kind = FileHistoryNodeKind.Session,
                    Label = Short(session.SessionId),
                    // The full id and the full stamp: the row shows eight characters and a day.
                    Tooltip = $"{session.SessionId}\n{session.CopyCount} copies of {session.FileCount} files\n"
                            + $"Last backup {session.LastWrite:g}\n{session.Folder}",
                    Session = session,
                    Paths = config.Paths,
                    Profile = config.Profiles.FirstOrDefault(),
                    Bytes = session.Bytes,
                    LastWrite = session.LastWrite,
                    CanDelete = !live.Contains(session.SessionId),
                    Parent = node,
                };
                node.Children.Add(child);
            }

            node.Bytes = node.Children.Sum(c => c.Bytes);
            node.LastWrite = node.Children.Max(c => c.LastWrite);
            node.CanDelete = node.Children.Any(c => c.CanDelete);
            Sort(node.Children);
            if (isOrphan) { orphans = node; } else { groups.Add(node); }
        }

        Sort(groups);
        // Orphans last whatever the ordering: the group answers a different question than "which
        // project cost me the most" / "what did I touch recently", and reads as a footnote to both.
        if (orphans != null) { groups.Add(orphans); }
        return groups;
    }

    /// <summary>Apply the current header ordering in place. One method for every level of the tree
    /// — sorting the sessions by date while their projects stayed by size read as a bug.
    /// <para>Size and date both fall back to the name when they tie, so two sessions of the same
    /// size keep a stable order between one sort and the next instead of swapping about.</para></summary>
    private void Sort(List<FileHistoryTreeNode> nodes)
    {
        var sign = _descending ? -1 : 1;
        nodes.Sort((a, b) =>
        {
            var by = _sortBy switch
            {
                SortBy.Name => 0,
                SortBy.Date => a.LastWrite.CompareTo(b.LastWrite),
                _ => a.Bytes.CompareTo(b.Bytes),
            };
            return by != 0
                ? sign * by
                : sign * string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static IEnumerable<FileHistoryTreeNode> AllNodes(FileHistoryTreeNode node)
    {
        yield return node;
        foreach (var n in node.Children.SelectMany(AllNodes)) { yield return n; }
    }

    // ~\.claude rather than the full profile path: the home part is the same on every row.
    private static string ShortConfigDir(string folder)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && folder.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + folder.Substring(home.Length)
            : folder;
    }

    // The CLI's project folders are the working directory with every non-alphanumeric char turned
    // into a dash, so the tail is the readable part.
    private static string ProjectLabel(string projectDir)
    {
        var name = Path.GetFileName(projectDir) ?? projectDir;
        var dash = name.LastIndexOf('-');
        return dash > 0 && dash < name.Length - 1 ? name.Substring(dash + 1) : name;
    }

    private static string Short(string sessionId)
        => sessionId != null && sessionId.Length > 8 ? sessionId.Substring(0, 8) : sessionId;

    private void OnNodeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as FileHistoryTreeNode;
        if (_selected?.Kind == FileHistoryNodeKind.Session) { ShowSession(_selected); }
        else { ShowGroup(_selected); }
    }

    private void OnCheckChanged(object sender, RoutedEventArgs e) => UpdateStatus(FileHistoryService.LiveSessionIds());

    private void UpdateStatus(HashSet<string> live)
    {
        var checkedSessions = _roots.SelectMany(r => r.CheckedSessions()).ToList();
        var bytes = checkedSessions.Sum(n => n.Bytes);
        var total = _roots.Sum(r => r.Bytes);
        var sessions = _roots.SelectMany(AllNodes).Count(n => n.Kind == FileHistoryNodeKind.Session);

        StatusText.Text = checkedSessions.Count == 0
            ? $"{FileHistoryService.Format.Size(total)} in {sessions} session(s)"
            : $"{checkedSessions.Count} checked · {FileHistoryService.Format.Size(bytes)} of {FileHistoryService.Format.Size(total)}";
        DeleteButton.IsEnabled = checkedSessions.Count > 0;
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessageText.Visibility = Visibility.Visible;
        FilesList.Visibility = Visibility.Collapsed;
        TilesPanel.Children.Clear();
        FilesHead.Text = "FILES";
    }
}
