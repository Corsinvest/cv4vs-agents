/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Windows;
using System.Windows.Controls;

namespace Corsinvest.VisualStudio.Agents.Core.Controls;

/// <summary>An Expander with a Fluent-style chevron (a "›" that rotates to "⌄" when expanded), like the
/// WebView context dialog. The default WPF/VS Expander toggle can't be re-iconed via a property, so we
/// subclass it and give it a template in Themes/Generic.xaml. Header + content follow the theme text
/// brush; the header stretches so a trailing token sits at the right edge.</summary>
public class CvExpander : Expander
{
    static CvExpander()
    {
        // Pick up the ControlTemplate defined for this type in Themes/Generic.xaml.
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(CvExpander), new FrameworkPropertyMetadata(typeof(CvExpander)));
    }
}
