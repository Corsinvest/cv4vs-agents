/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

/// <summary>The images the tool window tab icons are built from.
/// <para>In the IDE a missing or renamed key fails quietly: the tab just loses its logo or its glyph, and
/// the only trace is one line in an Output pane nobody has open. Loading the dictionary here is the one
/// place that failure is loud.</para></summary>
public class ToolWindowIconsTests
{
    [Fact]
    public void Icons_xaml_carries_every_tab_icon_image_as_vector_art()
    {
        var found = OnSta(() =>
        {
            // Application's static constructor registers the pack:// scheme the dictionary's Source needs,
            // and reading Current runs it. An instance would outlive this thread for the whole test run.
            _ = Application.Current;
            var icons = IconResources.Load();
            return new Dictionary<string, object>
            {
                [IconResources.LogoKey] = icons[IconResources.LogoKey],
                [IconResources.ChatGlyphKey] = icons[IconResources.ChatGlyphKey],
            };
        });

        // A DrawingImage stays vector in the image library; anything else would be a raster or refused.
        Assert.All(found, pair => Assert.IsType<DrawingImage>(pair.Value));
    }

    // WPF objects need an STA thread, and xUnit runs tests on MTA pool threads.
    private static T OnSta<T>(Func<T> body)
    {
        T result = default;
        ExceptionDispatchInfo error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
        return result;
    }
}
