/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Reflection;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// WebViewBridge, payload-building side: the pure static builders that assemble the DTOs sent
/// to the WebView (the ui.init payload and its spinner-verbs/extension config) and the CLI
/// content blocks from composer text + attachments. The WebView2 transport lives in
/// WebViewBridge.cs. Stateless — also called from ChatPaneControl / WebViewMessageHandler.
/// </summary>
internal sealed partial class WebViewBridge
{
    /// <summary>The VS Options block — the whole ui_init payload's VsOptions, and the standalone
    /// vs_settings payload pushed on Options → Apply / at boot (no model/permission/CLI state; the
    /// live client is untouched).</summary>
    public static Contracts.VsOptionsDto BuildVsOptions()
    {
        // Static facade returns a fresh instance (with page-initializer defaults) if the package is missing.
        var chat = AgentsOptions.Chat;
        var dbg = AgentsOptions.Debug;
        return new Contracts.VsOptionsDto
        {
            ShowCostAndDuration = chat.ShowCostAndDuration,
            PreviewLines = chat.PreviewLines,
            ChatFontSize = chat.ChatFontSize,
            ShowRelativePaths = chat.ShowRelativePaths,
            StickyUserMessages = chat.StickyUserMessages,
            ShowInlineToolErrors = chat.ShowInlineToolErrors,
            UseCtrlEnterToSend = chat.UseCtrlEnterToSend,
            CompactOutputAskAnswers = chat.CompactOutputAskAnswers,
            AllowDangerouslySkipPermissions = chat.AllowDangerouslySkipPermissions,
            DiffContextLines = chat.DiffContextLines,
            DiffIgnoreWhitespace = chat.DiffIgnoreWhitespace,
            ShowOpenDiffInVsButton = chat.ShowOpenDiffInVsButton,
            AllowedUploadExtensions = NormalizeExtensions(chat.AllowedUploadFileExtensions),
            AppVersion = GetExtensionVersion(),
            PerfEnabled = dbg.EnablePerfLog,
            // Always honour the Debug-page LogLevel setting (default None); previously DEBUG forced Trace ignoring it.
            LogLevel = (int)dbg.LogLevel,
        };
    }

    /// <summary>Normalize allowed upload extensions to lowercase, dot-prefixed,
    /// de-duplicated — so the webview can match `fileName.split('.').pop()` cleanly.</summary>
    private static string[] NormalizeExtensions(string[] exts)
    {
        if (exts == null) { return []; }
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in exts)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var e = raw.Trim().ToLowerInvariant();
            if (!e.StartsWith(".")) { e = "." + e; }
            set.Add(e);
        }
        return [.. set];
    }

    /// <summary>Extension version for display (matches the About dialog): the
    /// informational version, falling back to the assembly version.</summary>
    private static string GetExtensionVersion()
    {
        var asm = typeof(WebViewBridge).Assembly;
        return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
               ?? asm.GetName().Version?.ToString()
               ?? "1.0.0";
    }

    public static JArray BuildContentBlocks(string text, JArray attachments)
    {
        var blocks = new JArray();
        if (attachments != null)
        {
            foreach (var att in attachments)
            {
                // The webview always sends base64; we decide the block shape by
                // extension here (single source of truth). Images → image block;
                // .pdf → pdf document; everything else → text document (decoded).
                var name = att.Val("name", "");
                var base64 = att.Val("base64", "");
                var ext = Path.GetExtension(name).ToLowerInvariant();

                if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp")
                {
                    var mediaType = ext switch
                    {
                        ".jpg" or ".jpeg" => "image/jpeg",
                        ".gif" => "image/gif",
                        ".webp" => "image/webp",
                        _ => "image/png",
                    };
                    blocks.Add(new JObject
                    {
                        ["type"] = "image",
                        ["source"] = new JObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = mediaType,
                            ["data"] = base64
                        }
                    });
                }
                else if (ext == ".pdf")
                {
                    blocks.Add(new JObject
                    {
                        ["type"] = "document",
                        ["source"] = new JObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = "application/pdf",
                            ["data"] = base64
                        },
                        ["title"] = name
                    });
                }
                else
                {
                    // Text-like file: decode the base64 back to UTF-8 (same as the
                    // VS Code extension's `atob(...)` before the text block).
                    string textData;
                    try
                    {
                        textData = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    }
                    catch
                    {
                        textData = "";
                    }
                    blocks.Add(new JObject
                    {
                        ["type"] = "document",
                        ["source"] = new JObject
                        {
                            ["type"] = "text",
                            ["media_type"] = "text/plain",
                            ["data"] = textData
                        },
                        ["title"] = name
                    });
                }
            }
        }
        blocks.Add(new JObject { ["type"] = "text", ["text"] = text });
        return blocks;
    }
}
