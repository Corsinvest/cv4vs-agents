/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace Corsinvest.VisualStudio.Agents.Chat;

/// <summary>
/// <para>
/// Builds a tiny inline preview (PNG data-URI) from a chat image's original base64.
/// Shown in the attachment chip so a history image is visible immediately, without
/// fetching the full bytes — those stay lazy (fetched on click for the lightbox).
/// </para>
/// <para>
/// WPF decoding is used on purpose: <c>DecodePixelWidth</c> downsamples at decode
/// time, so the full-size bitmap is never allocated (a 4K screenshot never becomes a
/// 32 MB surface just to make a 16 px thumbnail). Output is always re-encoded to PNG
/// — the source may be jpeg/gif/webp, but the previews are tiny and PNG stays crisp on
/// the screenshots/text this chat mostly carries. Any failure returns null (the chip
/// falls back to its generic file-type icon).
/// </para>
/// </summary>
internal static class ThumbnailGenerator
{
    // Chip-sized: the attachment chip renders the icon at 16 px. A hair larger keeps
    // it sharp on high-DPI without materially growing the payload (~1 KB base64).
    private const int DecodeWidth = 24;

    /// <summary>
    /// Decode <paramref name="base64"/> to a ≤DecodeWidth PNG thumbnail and return it as a
    /// <c>data:image/png;base64,…</c> URI, or null if the input is empty or can't be decoded.
    /// Safe to call off the UI thread — the bitmaps are frozen and never touch the dispatcher.
    /// </summary>
    public static string Make(string base64)
    {
        if (string.IsNullOrEmpty(base64)) { return null; }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        try
        {
            var source = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                source.BeginInit();
                // OnLoad: decode fully within EndInit so the stream can be disposed right after.
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.CreateOptions = BitmapCreateOptions.None;
                source.DecodePixelWidth = DecodeWidth; // downsample at decode time
                source.StreamSource = ms;
                source.EndInit();
            }
            source.Freeze(); // detach from any thread affinity

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using (var outMs = new MemoryStream())
            {
                encoder.Save(outMs);
                return "data:image/png;base64," + Convert.ToBase64String(outMs.ToArray());
            }
        }
        catch (Exception ex)
        {
            // Unsupported codec (no WIC decoder), corrupt data, etc. — skip the preview.
            OutputWindowLogger.LogException("ThumbnailGenerator.Make", ex);
            return null;
        }
    }
}
