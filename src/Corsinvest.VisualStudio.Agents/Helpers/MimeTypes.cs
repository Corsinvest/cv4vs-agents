/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Runtime.InteropServices;

namespace Corsinvest.VisualStudio.Agents.Helpers;

/// <summary>The media type Windows itself reports for a file, so no mime list of our own.</summary>
internal static class MimeTypes
{
    [DllImport("urlmon.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int FindMimeFromData(IntPtr pBC, string pwzUrl, IntPtr pBuffer, int cbSize,
                                               string pwzMimeProposed, int dwMimeFlags, out IntPtr ppwzMimeOut, int dwReserved);

    /// <summary>Not the registry: it has no entry for .cs and maps .ts to video/vnd.dlna.mpeg-tts.
    /// A failed HRESULT (.md, .razor) means "", as the browser reports for a type it cannot name
    /// either.</summary>
    internal static string Of(string path)
    {
        var mime = IntPtr.Zero;
        try
        {
            return FindMimeFromData(IntPtr.Zero, path, IntPtr.Zero, 0, null, 0, out mime, 0) != 0
                    ? ""
                    : Marshal.PtrToStringUni(mime) ?? "";
        }
        catch (Exception ex)
        {
            // A throw would climb into the caller's WPF drop handler and take the pane with it.
            OutputWindowLogger.Global.Warn($"[mime] media type for '{path}' — {ex.Message}");
            return "";
        }
        finally
        {
            // Allocated by the callee.
            if (mime != IntPtr.Zero) { Marshal.FreeCoTaskMem(mime); }
        }
    }
}
