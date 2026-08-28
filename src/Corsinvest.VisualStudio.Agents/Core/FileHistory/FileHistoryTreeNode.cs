/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Corsinvest.VisualStudio.Agents.Core.FileHistory;

/// <summary>What a node stands for. Drives its icon and what selecting it shows.</summary>
internal enum FileHistoryNodeKind { ConfigDir, Project, Session, Orphans }

/// <summary>
/// One node of the file-history tree (config-dir → project → session, plus an Orphans group).
/// <para>Not <see cref="Stats.StatsTreeNode"/>: this tree carries a size and a tri-state check per
/// node, and its levels come from `file-history/` rather than from the stats index. It notifies,
/// because checking a parent has to move its children in the UI.</para>
/// </summary>
internal sealed class FileHistoryTreeNode : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    public string Label { get; set; }
    public string Tooltip { get; set; }
    public FileHistoryNodeKind Kind { get; set; }

    /// <summary>Set on Session nodes only; null on the grouping levels.</summary>
    public FileHistoryService.SessionBackups Session { get; set; }

    /// <summary>The config-dir this node lives under — a session needs it to find its transcript.</summary>
    public ClaudePaths Paths { get; set; }

    /// <summary>Any profile resolving to <see cref="Paths"/>. Several may share one config-dir, and
    /// which of them this is does not matter: it is only what StatsService.CwdForProject takes.</summary>
    public Profiles.Profile Profile { get; set; }

    public List<FileHistoryTreeNode> Children { get; } = [];

    public long Bytes { get; set; }
    public string SizeText => FileHistoryService.Format.Size(Bytes);

    /// <summary>Newest backup under this node — the session's own on a leaf, the newest of its
    /// children on a group. What the date orderings sort on at every level.</summary>
    public DateTime LastWrite { get; set; }

    /// <summary>The date as its own column, so it lines up down the tree. Kept out of
    /// <see cref="Label"/>: appended there it moved with the length of every name before it.</summary>
    public string LastWriteText => LastWrite == default ? "" : LastWrite.ToString("d MMM");

    /// <summary>False on a session an open pane is driving: deleting the copies a live rewind would
    /// restore from breaks it half-way. The checkbox is disabled rather than merely warned about —
    /// the consequence belongs in the control, not in a dialog the user can click past.</summary>
    public bool CanDelete { get; set; } = true;

    public bool IsExpanded { get; set; }
    public bool IsSelected { get; set; }

    // Set while a parent pushes its state down, so the children's echo back up is ignored.
    private bool _updating;
    private bool? _isChecked = false;

    /// <summary>Tri-state: null when only some descendants are checked. Setting it on a parent
    /// checks every child that can be deleted, and re-reads the parents on the way up.</summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) { return; }
            _isChecked = value;
            Raise(nameof(IsChecked));
            if (_updating) { return; }

            if (value.HasValue)
            {
                foreach (var c in Children.Where(c => c.CanDelete)) { c.SetFromParent(value.Value); }
            }
            Parent?.RefreshFromChildren();
        }
    }

    public FileHistoryTreeNode Parent { get; set; }

    private void SetFromParent(bool value)
    {
        _updating = true;
        IsChecked = value;
        _updating = false;
        foreach (var c in Children.Where(c => c.CanDelete)) { c.SetFromParent(value); }
    }

    private void RefreshFromChildren()
    {
        var checkable = Children.Where(c => c.CanDelete).ToList();
        if (checkable.Count == 0) { return; }
        var state = checkable.All(c => c.IsChecked == true) ? true
                  : checkable.All(c => c.IsChecked == false) ? (bool?)false
                  : null;
        _updating = true;
        IsChecked = state;
        _updating = false;
        Parent?.RefreshFromChildren();
    }

    /// <summary>Every checked session under this node, itself included.</summary>
    public IEnumerable<FileHistoryTreeNode> CheckedSessions()
    {
        if (Kind == FileHistoryNodeKind.Session)
        {
            if (IsChecked == true && CanDelete) { yield return this; }
            yield break;
        }
        foreach (var s in Children.SelectMany(c => c.CheckedSessions())) { yield return s; }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Maps a tree node to its KnownMoniker. Monikers verified against the image catalog:
/// an absent one renders as a silent empty CrispImage.</summary>
internal sealed class FileHistoryIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not FileHistoryTreeNode node
            ? default(ImageMoniker)
            : node.Kind switch
            {
                FileHistoryNodeKind.ConfigDir => KnownMonikers.FolderOpened,
                FileHistoryNodeKind.Project => KnownMonikers.FolderClosed,
                FileHistoryNodeKind.Orphans => KnownMonikers.StatusWarning,
                _ => KnownMonikers.Document,
            };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
