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
using System.Windows;
using System.Windows.Media;

namespace Corsinvest.VisualStudio.Agents.Helpers;

/// <summary><para>
/// Tab icons for the extension's tool windows: the logo, with a small glyph in its corner saying which
/// window it is. A crowded tab group hides the captions and shows only this icon, so with the bare logo
/// every tab of ours would read the same.
/// </para>
/// <para>
/// The glyph is the one the window already carries elsewhere: its menu entry, or the toolbar's list of
/// open panes, so the tab agrees with what the user clicked to open it.
/// </para>
/// <para>
/// The logo and the chat glyph are handed to the image library at run time, from the vector art in
/// Themes/Icons.xaml. An image manifest is the declarative route, but Visual Studio reads one into its
/// catalog only when it rebuilds the catalog's cache: an F5 deploy never does, an install can still leave
/// it out, and either way the tab showed the corner glyph over nothing.
/// </para>
/// <para>
/// Visual Studio draws the icon only where there is no room for the caption: on a tool window tab once its
/// group is squeezed, and in the document well's list of open windows. A document tab never shows one, so
/// the analytics windows docked there show theirs only in that list.
/// </para></summary>
internal static class ToolWindowIcons
{
    private const int CanvasSize = 16;

    // The smallest a VS glyph still reads at 100%. It covers the mascot's right arm and legs; both eyes,
    // which are what make it the mascot, stay above it.
    private const int GlyphSize = 9;

    // The image library keeps custom images in a weak collection: let the handle go and the moniker turns
    // blank at the next garbage collection, long after the pane set it. Held for the life of the process.
    private static readonly Dictionary<string, IImageHandle> Images = new();
    private static readonly Dictionary<(Guid, int), IImageHandle> Composites = new();

    // Set when Visual Studio failed to load its image library. It never retries that load, so neither
    // does this: later panes go straight to their stand-in.
    private static bool _libraryFailed;

    /// <summary>The glyph of a session pane's kind, as lists show it.</summary>
    public static ImageMoniker KindGlyph(PaneKind kind)
        => kind == PaneKind.Cli ? KnownMonikers.Console : KnownMonikers.Comment;

    /// <summary>The glyph of a session pane's kind, as its tab icon shows it over the logo. The chat's
    /// differs from <see cref="KindGlyph"/>: Comment is an outline, lost against the mascot at this size.</summary>
    public static ImageMoniker TabGlyph(PaneKind kind)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return kind == PaneKind.Chat
            ? Register(IconResources.ChatGlyphKey) ?? KnownMonikers.Comment
            : KindGlyph(kind);
    }

    /// <summary>The logo with <paramref name="glyph"/> in its bottom-right corner, made once per glyph.
    /// Without the logo, the glyph alone; without the composite, the bare logo. Either beats a blank tab.</summary>
    public static ImageMoniker WithAdorner(ImageMoniker glyph)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var key = (glyph.Guid, glyph.Id);
        if (Composites.TryGetValue(key, out var cached)) { return cached.Moniker; }

        if (Register(IconResources.LogoKey) is not ImageMoniker logo) { return glyph; }

        if (Package.GetGlobalService(typeof(SVsImageService)) is not IVsImageService2 imageService)
        {
            OutputWindowLogger.Global.Warn("[icons] no image service, tool window tabs get the bare logo");
            return logo;
        }

        try
        {
            ImageCompositionLayer[] layers =
            [
                new ImageCompositionLayer
                {
                    ImageMoniker = logo,
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
            // Read before caching: the handle is lazy, and this is where it actually does the work.
            var moniker = handle.Moniker;
            Composites[key] = handle;
            return moniker;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("[icons] WithAdorner", ex);
            return logo;
        }
    }

    /// <summary>Adds one image from Themes/Icons.xaml to the image library, once. Null when it can't, and
    /// the caller picks a stand-in.</summary>
    private static ImageMoniker? Register(string key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Images.TryGetValue(key, out var cached)) { return cached.Moniker; }
        if (_libraryFailed) { return null; }

        // Through the service, never ImageLibrary.Default. Visual Studio builds the library in the
        // background: early on Default does not exist yet, and while the library is being built from the
        // image manifests it exists but is half loaded. An image added then marks it loaded, the next
        // manifest throws, and Visual Studio goes without an image library for the rest of the session.
        // The service waits for the build to finish first.
        if (Package.GetGlobalService(typeof(SVsImageService)) is not IVsManagedImageService imageService)
        {
            OutputWindowLogger.Global.Warn($"[icons] no managed image service, no {key} on the tab icons");
            return null;
        }

        try
        {
            // canTheme off: brand colours, whose lightness the library would invert on a dark theme.
            var handle = imageService.AddCustomImage((ImageSource)IconResources.Load()[key], canTheme: false);
            var moniker = handle.Moniker;
            if (moniker.Guid == Guid.Empty)
            {
                // The service waits for its library without a time limit, so an empty moniker means the
                // load failed, and in that state the IDE's own icons are missing as well.
                _libraryFailed = true;
                OutputWindowLogger.Global.Warn($"[icons] Visual Studio's image library failed to load, no {key} on the tabs");
                return null;
            }
            Images[key] = handle;
            return moniker;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException($"[icons] Register {key}", ex);
            return null;
        }
    }
}

/// <summary>Themes/Icons.xaml, loaded once. Kept apart from <see cref="ToolWindowIcons"/> so a test can load
/// it without touching the Visual Studio types that class does.</summary>
internal static class IconResources
{
    public const string LogoKey = "MascotImage";
    public const string ChatGlyphKey = "ChatGlyphImage";

    private static ResourceDictionary _icons;

    public static ResourceDictionary Load()
        => _icons ??= new ResourceDictionary
        {
            Source = new Uri("/Corsinvest.VisualStudio.Agents;component/Themes/Icons.xaml", UriKind.Relative),
        };
}
