/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Globalization;
using System.Windows.Data;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Maps a tree node to the KnownMoniker shown next to it. The Kind wins first (the
/// Days/Sessions grouping nodes share the Project scope but need distinct icons), then the scope:
/// All = Statistics, Profile = User, Folder = open folder, Project = folder, Day = calendar,
/// Session = document. All monikers verified to exist in the image catalog.</summary>
internal sealed class ScopeIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StatsTreeNode node) { return default(ImageMoniker); }
        return node.Kind switch
        {
            StatsNodeKind.DaysGroup => KnownMonikers.Calendar,
            StatsNodeKind.SessionsGroup => KnownMonikers.History,
            _ => node.Selection?.Scope switch
            {
                StatsScope.All => KnownMonikers.Statistics,
                StatsScope.Profile => KnownMonikers.User,
                StatsScope.Folder => KnownMonikers.FolderOpened,
                StatsScope.Project => KnownMonikers.FolderClosed,
                StatsScope.Day => KnownMonikers.Calendar,
                StatsScope.Session => KnownMonikers.Document,
                _ => KnownMonikers.Document,
            },
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
