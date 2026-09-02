/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Newtonsoft.Json.Linq;
using System;

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
            CollapseTools = chat.CollapseTools,
            ChatFontSize = chat.ChatFontSize,
            ShowRelativePaths = chat.ShowRelativePaths,
            StickyUserMessages = chat.StickyUserMessages,
            ShowInlineToolErrors = chat.ShowInlineToolErrors,
            UseCtrlEnterToSend = chat.UseCtrlEnterToSend,
            CompactOutputAskAnswers = chat.CompactOutputAskAnswers,
            AllowDangerouslySkipPermissions = chat.AllowDangerouslySkipPermissions,
            FileCheckpoints = chat.FileCheckpoints,
            SendSelectionText = chat.SendSelectionText,
            AllowedUploadExtensions = NormalizeExtensions(chat.AllowedUploadFileExtensions),
            // Bare (no dot, lowercase): the webview matches these against a parsed extension, not
            // against a file name, so the dot-prefixed shape used for uploads would never hit.
            ExtraLinkableExtensions = NormalizeBareExtensions(chat.ExtraLinkableExtensions),
            AppVersion = BuildInfo.Version,
            AppCopyright = BuildInfo.Copyright,
            PerfEnabled = dbg.EnablePerfLog,
            // The Debug-page LogLevel (default None) is honoured on every build, DEBUG included.
            LogLevel = (int)dbg.LogLevel,
        };
    }

    /// <summary>Normalize extra linkable extensions to lowercase, WITHOUT the dot, de-duplicated —
    /// the shape `findFileRefs` compares against. A user entry may be written either way (`zig` or
    /// `.zig`), and a leading `*.` glob is tolerated too.</summary>
    private static string[] NormalizeBareExtensions(string[] exts)
    {
        if (exts == null) { return []; }
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in exts)
        {
            if (string.IsNullOrWhiteSpace(raw)) { continue; }
            var e = raw.Trim().TrimStart('*').TrimStart('.').ToLowerInvariant();
            // Same shape the parser can produce: letters/digits only, and never all digits (an
            // all-digit tail is a version or a price, never a file).
            if (e.Length is 0 or > 10 || !System.Text.RegularExpressions.Regex.IsMatch(e, "^[a-z0-9+#-]*[a-z][a-z0-9+#-]*$")) { continue; }
            set.Add(e);
        }
        return [.. set];
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


    /// <summary>The content blocks for one user turn: the IDE context (when there is any), then the
    /// attachments, then the user's own text — LAST and in a block of its own.
    /// <para>The split is load-bearing, not tidiness. The CLI decides a message is a slash command
    /// by looking at whether its text block starts with "/", so anything glued in front of the
    /// prompt hides the command: "/config" becomes an ordinary sentence, the CLI never runs it, and
    /// the model answers it in prose instead. Same shape the VS Code webview sends.</para></summary>
    public static JArray BuildContentBlocks(string text, JArray attachments, string ideContext = null)
    {
        var blocks = new JArray();
        if (!string.IsNullOrEmpty(ideContext))
        {
            blocks.Add(new JObject { ["type"] = "text", ["text"] = ideContext });
        }
        if (attachments != null)
        {
            foreach (var att in attachments)
            {
                // The webview sends base64 plus the browser's media type, and only extensions the
                // user allowed get this far — so the type alone picks the block: image, text
                // (decoded back to characters), or a document carrying its own media type.
                var name = att.Val("name", "");
                var base64 = att.Val("base64", "");
                var mediaType = att.Val("mediaType", "");

                if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
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
                else if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    // Text arrives base64 like everything else; the CLI wants the characters.
                    string textData;
                    try
                    {
                        textData = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                    }
                    catch (Exception ex)
                    {
                        // Silence here reaches the user as an attachment Claude never sees: the chip
                        // is on the message, the document block is empty, and nothing says why.
                        OutputWindowLogger.Global.Warn($"[chat] attachment '{name}': base64 decode failed, sent empty — {ex.Message}");
                        textData = "";
                    }
                    blocks.Add(new JObject
                    {
                        ["type"] = "document",
                        ["source"] = new JObject
                        {
                            ["type"] = "text",
                            ["media_type"] = mediaType,
                            ["data"] = textData
                        },
                        ["title"] = name
                    });
                }
                else
                {
                    blocks.Add(new JObject
                    {
                        ["type"] = "document",
                        ["source"] = new JObject
                        {
                            ["type"] = "base64",
                            ["media_type"] = mediaType,
                            ["data"] = base64
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
