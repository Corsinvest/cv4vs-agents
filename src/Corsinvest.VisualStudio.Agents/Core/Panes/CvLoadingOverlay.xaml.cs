/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Core.Panes;

/// <summary>A centered indeterminate progress bar with a caption, shared by the document-tabs
/// (Statistics, Usage, Context usage) while they index or fetch. The caller places it in its grid and
/// toggles Visibility; <see cref="Message"/> sets the line under the bar.</summary>
public partial class CvLoadingOverlay : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(CvLoadingOverlay), new PropertyMetadata(""));

    /// <summary>The caption shown below the progress bar.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public CvLoadingOverlay()
    {
        InitializeComponent();
    }
}
