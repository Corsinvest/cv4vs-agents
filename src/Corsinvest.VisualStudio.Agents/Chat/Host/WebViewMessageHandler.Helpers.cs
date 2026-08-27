/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Sessions;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// WebViewMessageHandler shared helpers: JSONL tool-input/result lookup and projection, temp-file
/// naming, VS open/format utilities, and stats scope/range mapping — used across the Handle*
/// dispatchers. The dispatch switch, the two single-case handlers, and lifecycle live in
/// WebViewMessageHandler.cs.
/// </summary>
internal sealed partial class WebViewMessageHandler
{
    /// <summary>Project a tool's raw input JSON to the text shown when opening its IN:
    /// the tool's main field for the verbose ones (Agent→prompt, Bash→command, Write→content),
    /// else the whole indented JSON. Mirrors the per-tool renderers on the WebView side — a tool
    /// missing here opens as JSON with its content escaped onto one line.</summary>
    private static string ProjectInput(string toolName, JObject input)
    {
        var field = toolName switch
        {
            "Bash" or "PowerShell" => input.Val("command"),
            "Agent" => input.Val("prompt"),
            "Skill" => input.Val("args"),
            "Write" => input.Val("content"),
            _ => null,
        };
        return !string.IsNullOrEmpty(field) ? field : input.ToIndentedString();
    }
    // The static helpers below log through OutputWindowLogger, not the pane logger: a primary
    // constructor parameter isn't in scope in a static member (CS9105).

    /// <summary>The transcript holding a tool call: the session file, or the sub-agent's own when
    /// agentId names one. Null when anything is missing or unsafe — sessionId and agentId compose a
    /// path and come from the WebView, so they are checked for traversal here, once, instead of at
    /// each lookup.</summary>
    private static string TranscriptPathFor(string workingDirectory, string sessionId, string toolUseId,
                                            ClaudePaths paths, string agentId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolUseId)) { return null; }
        if (!SessionManager.IsSafePathToken(sessionId)) { return null; }
        if (!string.IsNullOrEmpty(agentId) && !SessionManager.IsSafePathToken(agentId)) { return null; }
        var folder = paths.SessionFolder(workingDirectory);
        var path = string.IsNullOrEmpty(agentId)
            ? Path.Combine(folder, sessionId + ".jsonl")
            : Path.Combine(folder, sessionId, "subagents", $"agent-{agentId}.jsonl");
        return File.Exists(path) ? path : null;
    }

    /// <summary>What a tool was called with: the text to show, and the file it names when it names
    /// one. Both come off the same `input` object, so reading it twice would mean scanning the
    /// JSONL twice. FilePath is null for the tools that write no file — Bash, Grep, most MCP.</summary>
    private static (string Content, string FilePath) FindToolInput(string workingDirectory,
                                                                   string sessionId,
                                                                   string toolUseId,
                                                                   ClaudePaths paths,
                                                                   string toolName,
                                                                   string agentId)
    {
        var path = TranscriptPathFor(workingDirectory, sessionId, toolUseId, paths, agentId);
        if (path == null) { return default; }
        try
        {
            foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains(toolUseId)) { continue; }
                try
                {
                    var obj = JObject.Parse(line);
                    if (obj.Val("type", "") != "assistant") { continue; }
                    if (obj["message"]?["content"] is not JArray content) { continue; }
                    foreach (var item in content)
                    {
                        if (item.Val("type", "") == "tool_use" && item.Val("id") == toolUseId)
                        {
                            return item["input"] is not JObject input
                                ? default
                                : (ProjectInput(toolName, input), input.Val("file_path"));
                        }
                    }
                }
                catch { /* silent: skip malformed JSONL line */ }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("FindToolInput/Result", ex); }
        return default;
    }
    /// <summary>The tool call's raw `input` object. FindToolInput projects it for display — for an
    /// Edit that means the whole JSON as one string — and a diff needs the fields apart, so this
    /// returns the object itself. Null when the transcript has no such tool_use.</summary>
    private static JObject FindToolInputRaw(string workingDirectory,
                                            string sessionId,
                                            string toolUseId,
                                            ClaudePaths paths,
                                            string agentId)
    {
        var path = TranscriptPathFor(workingDirectory, sessionId, toolUseId, paths, agentId);
        if (path == null) { return null; }
        try
        {
            foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains(toolUseId)) { continue; }
                try
                {
                    var obj = JObject.Parse(line);
                    if (obj.Val("type", "") != "assistant") { continue; }
                    if (obj["message"]?["content"] is not JArray content) { continue; }
                    foreach (var item in content)
                    {
                        if (item.Val("type", "") == "tool_use" && item.Val("id") == toolUseId)
                        {
                            return item["input"] as JObject;
                        }
                    }
                }
                catch { /* silent: skip malformed JSONL line */ }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("FindToolInputRaw", ex); }
        return null;
    }

    /// <summary>What the tool answered. Unlike the IN side this is always text meant for the model,
    /// never a file: a Write reports "File created successfully at: …", a Read returns the content
    /// with line numbers prefixed. That is why the temp it opens into stays .txt.</summary>
    private static string FindToolResult(string workingDirectory, string sessionId, string toolUseId, ClaudePaths paths, string agentId)
    {
        var path = TranscriptPathFor(workingDirectory, sessionId, toolUseId, paths, agentId);
        if (path == null) { return null; }
        try
        {
            foreach (var line in File.ReadLines(path, System.Text.Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line) || !line.Contains(toolUseId)) { continue; }
                try
                {
                    var obj = JObject.Parse(line);
                    if (obj.Val("type", "") != "user") { continue; }
                    if (obj["message"]?["content"] is not JArray content) { continue; }
                    foreach (var item in content)
                    {
                        if (item.Val("type", "") == "tool_result"
                            && item.Val("tool_use_id") == toolUseId)
                        {
                            // Large outputs aren't inline: the CLI persists them to tool-results/<id>.txt
                            // and leaves a placeholder + 2KB preview in `content`. Read the real file.
                            var persisted = SessionManager.ToolUseResultField(obj, "persistedOutputPath");
                            if (!string.IsNullOrEmpty(persisted) && File.Exists(persisted))
                            {
                                return File.ReadAllText(persisted, System.Text.Encoding.UTF8);
                            }
                            var resultContent = item["content"];
                            return resultContent is JArray arr
                                ? string.Join("\n", arr.Where(c => c.Val("type", "") == "text")
                                                              .Select(c => c.Val("text", "")))
                                : (string)resultContent ?? "";
                        }
                    }
                }
                catch { /* silent: skip malformed JSONL line */ }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("FindToolInput/Result", ex); }
        return null;
    }
    private static bool TryOpenInVs(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, filePath,
                Microsoft.VisualStudio.VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var frame);
            frame?.Show();
            return frame != null;
        }
        catch (Exception ex)
        {
            // The other half of a file-link click, next to FindFileByNameUnderRoot: the path was
            // resolved and the open still failed. All the user sees is a link that does nothing.
            OutputWindowLogger.Global.LogException($"WebView.TryOpenInVs({filePath})", ex);
            return false;
        }
    }
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim('_');
        return safe.Length > 40 ? safe.Substring(0, 40) : safe;
    }

    /// <summary><para>
    /// Name for the temp file a tool's IN/OUT is opened from — readable in the tab, and
    /// carrying an extension the editor can colour.
    /// </para>
    /// <para>
    /// The temp is the right thing to open here and not a stand-in for the real file: clicking the
    /// row's TITLE already goes to the file. Clicking the content asks a different question — what
    /// was written in THAT turn — and the file on disk answers it wrongly three turns later, while
    /// a temp cannot change under you.
    /// </para></summary>
    private static string TempFileName(string toolName, string which, string filePath, string toolUseId)
    {
        // Last few chars of the tool_use_id: enough to keep two temps apart — two writes to
        // same-named files in different folders, or the same command run twice — without turning
        // the tab into a guid nobody can read back to a row.
        var id = SanitizeFileName(toolUseId ?? "");
        if (id.Length > 6) { id = id.Substring(id.Length - 6); }
        var side = which == "in" ? "in" : "out";

        // A tool that names a file gets that file's name and extension: "Modello_in_a3f21.cs" reads
        // back to the row at a glance and the editor colours it. Sanitizing the whole title instead
        // would mangle a path into something like "claude_[Write] C__Users_daniele_cors".
        if (which == "in" && !string.IsNullOrEmpty(filePath))
        {
            var name = SanitizeFileName(Path.GetFileNameWithoutExtension(filePath));
            var ext = Path.GetExtension(filePath);
            if (!string.IsNullOrEmpty(name))
            {
                return $"{name}_{side}_{id}{(string.IsNullOrEmpty(ext) ? ".txt" : ext)}";
            }
        }

        // A shell tool's IN is the command — script, so give it the script's extension, with
        // PowerShell told apart from Bash: .ps1 is what VS colours, and calling it .sh on Windows
        // highlights it as the wrong language. Its OUT is stdout, which is text and nothing else,
        // so it stays .txt like every other result.
        var fallbackExt = which == "in"
            ? toolName switch
            {
                "PowerShell" => ".ps1",
                "Bash" => ".sh",
                _ => ".txt",
            }
            : ".txt";
        var who = SanitizeFileName(string.IsNullOrEmpty(toolName) ? AppConstants.AppId : toolName);
        return $"{who}_{side}_{id}{fallbackExt}";
    }

    /// <summary>Write a chat document / composer attachment to a temp file and open it.
    /// Base64 content must be written as BYTES: writing it as text yields a corrupt file
    /// that won't open. Text-like content opens in VS, falling back to the shell.</summary>
    private static void WriteTempAndOpen(string title, string content, string mediaType, bool isBase64)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // The title always carries the original filename (with extension), so it decides the
        // temp file's extension; .txt is a harmless last resort.
        var ext = Path.GetExtension(title);
        if (string.IsNullOrEmpty(ext)) { ext = ".txt"; }
        var baseName = Path.GetFileNameWithoutExtension(title);
        var tmpPath = Path.Combine(Path.GetTempPath(),
                                   $"{AppConstants.AppId}-{SanitizeFileName(baseName)}{ext}");
        if (isBase64)
        {
            File.WriteAllBytes(tmpPath, Convert.FromBase64String(content));
            ShellHelpers.OpenExternal(tmpPath);
        }
        else
        {
            File.WriteAllText(tmpPath, content, System.Text.Encoding.UTF8);
            // Text-like documents open in VS; fall back to the shell if VS can't.
            if (mediaType == "text/plain" || mediaType.StartsWith("text/"))
            {
                if (!TryOpenInVs(tmpPath)) { ShellHelpers.OpenExternal(tmpPath); }
            }
            else
            {
                ShellHelpers.OpenExternal(tmpPath);
            }
        }
    }

    // The WebView dialog is single-profile (the pane's), so its "All" is this profile's whole set
    // of projects → the Profile scope, not the cross-profile All (that one is WPF-tree only).
    private static Core.Stats.StatsScope MapScope(Contracts.StatsScopeDto s) => s switch
    {
        Contracts.StatsScopeDto.Session => Core.Stats.StatsScope.Session,
        Contracts.StatsScopeDto.Project => Core.Stats.StatsScope.Project,
        _ => Core.Stats.StatsScope.Profile,
    };
    private static Core.Stats.StatsRange MapRange(Contracts.StatsRangeDto r) => r switch
    {
        Contracts.StatsRangeDto.Days30 => Core.Stats.StatsRange.Last30d,
        Contracts.StatsRangeDto.Days7 => Core.Stats.StatsRange.Last7d,
        _ => Core.Stats.StatsRange.All,
    };
    /// <summary>Open <paramref name="filePath"/> in the text editor, or null when VS won't.
    /// A file the solution itself owns — the .csproj of a loaded project — is not the shell's to
    /// hand out: it throws "already open as a project or solution", and asking for the primary
    /// view instead throws the same. There is no view kind that opens it, so that case is reported
    /// rather than retried; unloading the project to satisfy a chat link would be worse than the
    /// link not working.</summary>
    private static Window OpenWindow(DTE2 dte, string filePath)
    {
        try
        {
            return dte?.ItemOperations?.OpenFile(filePath, Constants.vsViewKindTextView);
        }
        catch (ArgumentException ex)
        {
            // What VS throws for a project/solution file; the caller's Warn already names the file.
            OutputWindowLogger.Global.Debug(() => $"[chat] OpenFile refused: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("OpenFileInEditor", ex);
            return null;
        }
    }

    /// <summary>Say that a click did not open the file. The Output window is gated behind a log
    /// level that defaults to None, so the Warn is invisible where it matters — this lands in the
    /// chat. Short on purpose: the reader wants to know the click failed, not how VS treats project
    /// files; the Warn keeps the detail for whoever is debugging. Keyed on the path so clicking the
    /// same dead link twice doesn't stack.</summary>
    private void NoticeOpenFailed(string path, string reason)
    {
        log.Warn($"[chat] can't open '{path}' — {reason}");
        bridge.Send(BridgeMessages.ToWebView.Chat.Notice, new Contracts.NoticeNotification
        {
            Key = "openfile:" + path,
            Severity = Contracts.NoticeVariantDto.Error,
            Message = $"{Path.GetFileName(path)} — {reason}",
            Position = Contracts.NoticePositionDto.Top,
        });
    }

    private async Task OpenFileInEditorAsync(string filePath, int startLine, int endLine)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        var window = OpenWindow(dte, filePath);
        if (window == null)
        {
            NoticeOpenFailed(filePath, "Visual Studio would not open it");
            return;
        }
        window.Activate();
        if (startLine <= 0) { return; }
        await Task.Yield();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (window.Document?.Selection is TextSelection sel)
        {
            if (AgentsOptions.Chat.SelectLinesOnOpen && endLine >= startLine && endLine > 0)
            {
                // Anchored at the END and extended upwards, so the caret — and with it the view —
                // lands on the first line. A range names a block by where it begins: selecting
                // downwards leaves the caret at the bottom, and an 80-line range then scrolls its
                // own first line off the top of the screen, which is the one the model meant.
                // MoveToLineAndOffset rather than GotoLine, as everywhere else here: it says which
                // column it wants instead of inheriting wherever the caret happened to be.
                sel.MoveToLineAndOffset(endLine, 1, false);
                sel.EndOfLine(false);
                sel.MoveToLineAndOffset(startLine, 1, true);
            }
            else
            {
                sel.GotoLine(startLine, false);
            }
        }
    }
    private string ResolveFilePath(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) { return null; }
        // Strip a file:// prefix (a chat file link may carry one) before path logic.
        var stripped = filePath;
        if (stripped.StartsWith("file:///", System.StringComparison.OrdinalIgnoreCase)) { stripped = stripped.Substring(8); }
        else if (stripped.StartsWith("file://", System.StringComparison.OrdinalIgnoreCase)) { stripped = stripped.Substring(7); }
        var normalized = stripped.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) && File.Exists(normalized)) { return normalized; }
        var combined = Path.Combine(client.WorkingDirectory ?? string.Empty, normalized);
        if (File.Exists(combined)) { return combined; }
        if (File.Exists(normalized)) { return normalized; }
        // Bare name (a "X.cs:20" link carries just the file name): search the workspace by name.
        // The model almost always means a file under the working directory (see the chat file-link
        // design), so a recursive search of the workdir resolves it. First unique match wins; a
        // deeper path in the link (Core/Stats/X.cs) already resolved above via the workdir-combine.
        var wd = client.WorkingDirectory;
        var name = Path.GetFileName(normalized);
        if (!string.IsNullOrEmpty(wd) && !string.IsNullOrEmpty(name) && Directory.Exists(wd))
        {
            var found = FindFileByNameUnderRoot(wd, name);
            if (found != null) { return found; }
        }
        log.Debug(() => $"[OpenFile] Path not found. raw='{filePath}' normalized='{normalized}' combined='{combined}' workingDir='{client.WorkingDirectory}'");
        return null;
    }

    /// <summary>First file named <paramref name="name"/> under <paramref name="root"/>, skipping the
    /// usual noise dirs (bin/obj/.git/node_modules) to keep the walk fast. Null if none.</summary>
    private static string FindFileByNameUnderRoot(string root, string name)
    {
        try
        {
            var stack = new System.Collections.Generic.Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string match = null;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir))
                    {
                        if (string.Equals(Path.GetFileName(f), name, System.StringComparison.OrdinalIgnoreCase))
                        {
                            match = f;
                            break;
                        }
                    }
                    if (match != null) { return match; }
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var leaf = Path.GetFileName(sub);
                        if (leaf.Length > 0 && leaf[0] == '.') { continue; }
                        if (string.Equals(leaf, "bin", System.StringComparison.OrdinalIgnoreCase)
                            || string.Equals(leaf, "obj", System.StringComparison.OrdinalIgnoreCase)
                            || string.Equals(leaf, "node_modules", System.StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        stack.Push(sub);
                    }
                }
                // Per-directory and deliberately silent: a folder we can't read is the normal shape
                // of a walk over a whole tree, and logging one line per skipped directory would
                // bury the entries that mean something.
                catch (System.UnauthorizedAccessException) { }
                catch (System.IO.IOException) { }
            }
        }
        catch (System.Exception ex)
        {
            // The outer one is a different matter: it catches whatever the walk itself failed on,
            // and the caller only sees a file-link that doesn't open. Without this there is nothing
            // to read afterwards.
            OutputWindowLogger.Global.LogException($"WebView.FindFileByNameUnderRoot({name})", ex);
        }
        return null;
    }
}
