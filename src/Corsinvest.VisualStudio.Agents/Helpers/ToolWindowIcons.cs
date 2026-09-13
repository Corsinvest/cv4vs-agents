/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Helpers;

/// <summary><para>
/// Tab icons for the extension's tool windows: the logo, with a small glyph in its corner saying which
/// window it is. A crowded tab group hides the captions and shows only this icon, so with the bare logo
/// every tab of ours would read the same.
/// </para>
/// <para>
/// The glyph is the one the window already carries elsewhere — its menu entry, or the toolbar's list of
/// open panes — so the tab agrees with what the user clicked to open it.
/// </para></summary>
internal static class ToolWindowIcons
{
    private const int CanvasSize = 16;

    // The smallest a VS glyph still reads at 100%. It covers the mascot's right arm and legs; both eyes,
    // which are what make it the mascot, stay above it.
    private const int GlyphSize = 9;

    // The image service keeps custom images in a weak collection: let the handle go and the moniker turns
    // blank at the next garbage collection, long after the pane set it. Held for the life of the process.
    private static readonly Dictionary<(Guid, int), IImageHandle> Composites = new();

    /// <summary>The glyph of a session pane's kind, as lists show it.</summary>
    public static ImageMoniker KindGlyph(PaneKind kind)
        => kind == PaneKind.Cli ? KnownMonikers.Console : KnownMonikers.Comment;

    /// <summary>The glyph of a session pane's kind, as its tab icon shows it over the logo. The chat's
    /// differs from <see cref="KindGlyph"/>: Comment is an outline, lost against the mascot at this size.</summary>
    public static ImageMoniker TabGlyph(PaneKind kind)
        => kind == PaneKind.Chat ? PackageMonikers.ChatGlyph : KindGlyph(kind);

    /// <summary>The logo with <paramref name="glyph"/> in its bottom-right corner, made once per glyph.
    /// The bare logo when the composite can't be made: a tab without its glyph beats an empty one.</summary>
    public static ImageMoniker WithAdorner(ImageMoniker glyph)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var key = (glyph.Guid, glyph.Id);
        if (Composites.TryGetValue(key, out var cached)) { return cached.Moniker; }

        if (Package.GetGlobalService(typeof(SVsImageService)) is not IVsImageService2 imageService)
        {
            OutputWindowLogger.Global.Warn("[icons] no image service — tool window tabs get the bare logo");
            return PackageMonikers.Logo;
        }

        try
        {
            ImageCompositionLayer[] layers =
            [
                new ImageCompositionLayer
                {
                    ImageMoniker = PackageMonikers.Logo,
                    VirtualWidth = CanvasSize,
                    VirtualHeight = CanvasSize,
                    HorizontalAlignment = (uint)_UIImageHorizontalAlignment.IHA_Left,
                    VerticalAlignment = (uint)_UIImageVerticalAlignment.IVA_Top,
                },
                new ImageCompositionLayer
                {
                    ImageMoniker = glyph,
                    VirtualWidth = GlyphSize,
                    VirtualHeight = GlyphSize,
                    HorizontalAlignment = (uint)_UIImageHorizontalAlignment.IHA_Right,
                    VerticalAlignment = (uint)_UIImageVerticalAlignment.IVA_Bottom,
                },
            ];
            var handle = imageService.AddCustomCompositeImage(CanvasSize, CanvasSize, layers.Length, layers);
            Composites[key] = handle;
            return handle.Moniker;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("[icons] WithAdorner", ex);
            return PackageMonikers.Logo;
        }
    }
}
