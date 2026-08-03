/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Corsinvest.VisualStudio.Agents.Chat;

/// <summary>
/// <para>
/// Lazy on-demand cache that turns Visual Studio file-type icons into
/// 16×16 PNG files on disk. The WebView serves them via a dedicated
/// virtual host so file-suggestion items render with the same icons used
/// in Solution Explorer.
/// </para>
/// <para>
/// Resolution uses <c>IVsImageService2.GetImageMonikerForFile("x.&lt;ext&gt;")</c>
/// so we don't need to keep an extension→moniker map in sync with VS.
/// </para>
/// </summary>
internal static class IconCacheService
{
    /// <summary>
    /// Ensures the PNG for the given <paramref name="iconKey"/> exists on
    /// disk and returns its full path. <paramref name="iconKey"/> is the
    /// file extension without the dot ("cs", "ts", "json", …); the
    /// special key "folder" returns the closed-folder icon.
    /// </summary>
    public static string EnsureIconPng(string iconKey)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var key = string.IsNullOrEmpty(iconKey) ? "file" : iconKey.ToLowerInvariant();
        // Keyed by the background the PNG is rendered against (see RenderMonikerToPng), not by the
        // theme's name: that colour is what decides the glyph, so two themes sharing a background
        // can share the cache, and one that only LOOKS dark still gets its own. Before this the
        // cache was flat, so a dark-theme run left pale icons that stayed pale on white.
        // The light-/dark- prefix is for whoever opens the folder; the hex is what makes it correct.
        var themeBg = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
        var bucket = $"{(VsThemeReader.IsDark() ? "dark" : "light")}-{themeBg.R:x2}{themeBg.G:x2}{themeBg.B:x2}";
        var folder = Path.Combine(AppPaths.IconCacheFolder, bucket);
        Directory.CreateDirectory(folder);
        var pngPath = Path.Combine(folder, key + ".png");
        if (File.Exists(pngPath)) { return pngPath; }

        try
        {
            var bytes = RenderMonikerToPng(ResolveMoniker(key), themeBg);
            // A moniker VS knows but won't rasterise (".sh" is one) renders to nothing, and the
            // path returned below would then 404 into a broken-image glyph. Fall back to the
            // generic document rather than serve a file that isn't there.
            if (bytes == null && key != "file")
            {
                OutputWindowLogger.Global.Debug(() => $"[icons] no bitmap for '{key}' — using the generic document");
                bytes = RenderMonikerToPng(KnownMonikers.Document, themeBg);
            }
            if (bytes != null) { File.WriteAllBytes(pngPath, bytes); }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn($"!!! IconCache failed for '{key}': {ex.Message}");
        }
        return pngPath;
    }

    private static ImageMoniker ResolveMoniker(string key)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (key == "folder") { return KnownMonikers.FolderClosed; }
        if (key == "file") { return KnownMonikers.Document; }

        if (Package.GetGlobalService(typeof(SVsImageService)) is not IVsImageService2 imageService) { return KnownMonikers.Document; }

        // Try as extension "x.<key>", then as dotfile ".<key>".
        // GetImageMonikerForFile returns a blank moniker (Guid.Empty + 0) when unrecognised.
        var m = imageService.GetImageMonikerForFile("x." + key);
        if (m.Guid != Guid.Empty || m.Id != 0) { return m; }

        m = imageService.GetImageMonikerForFile("." + key);
        return m.Guid != Guid.Empty || m.Id != 0 ? m : KnownMonikers.Document;
    }

    /// <summary>Render at the given background — the same colour the caller keyed the cache folder
    /// on, so what is drawn and where it is filed can't drift apart.</summary>
    private static byte[] RenderMonikerToPng(ImageMoniker moniker, System.Drawing.Color themeBg)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsImageService)) is not IVsImageService2 imageService) { return null; }

        // The background is a theming hint: it makes the icon match Solution Explorer
        // (light glyphs on a dark theme, dark on light).
        uint bgUint = ((uint)themeBg.A << 24)
                    | ((uint)themeBg.R << 16)
                    | ((uint)themeBg.G << 8)
                    | themeBg.B;
        var attrs = new ImageAttributes
        {
            StructSize = Marshal.SizeOf(typeof(ImageAttributes)),
            Format = (uint)_UIDataFormat.DF_WPF,
            ImageType = (uint)_UIImageType.IT_Bitmap,
            LogicalWidth = 16,
            LogicalHeight = 16,
            Background = bgUint,
            Flags = unchecked((uint)(_ImageAttributesFlags.IAF_RequiredFlags
                                   | _ImageAttributesFlags.IAF_Background)),
        };

        var imgObj = imageService.GetImage(moniker, attrs);
        if (imgObj == null) { return null; }
        imgObj.get_Data(out object data);
        if (data is not BitmapSource src) { return null; }

        // Re-render onto a transparent Pbgra32 surface: gives the encoder a known
        // alpha-aware format and keeps surrounding pixels transparent (no solid
        // tool-window-colour block over the WebView row).
        var rtb = new RenderTargetBitmap(16, 16, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(src, new Rect(0, 0, 16, 16));
        }
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
