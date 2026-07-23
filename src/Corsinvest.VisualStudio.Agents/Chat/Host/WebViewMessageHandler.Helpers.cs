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
    /// the tool's main field for the verbose ones (Agent→prompt, Bash→command), else the
    /// whole indented JSON. Mirrors the per-tool renderers on the WebView side.</summary>
    private static string ProjectInput(string toolName, JObject input)
    {
        var field = toolName switch
        {
            "Bash" or "PowerShell" => input.Val("command"),
            "Agent" => input.Val("prompt"),
            "Skill" => input.Val("args"),
            _ => null,
        };
        return !string.IsNullOrEmpty(field) ? field : input.ToIndentedString();
    }
    private static string FindToolInput(string workingDirectory, string sessionId, string toolUseId, ClaudePaths paths, string toolName = "", string agentId = null)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolUseId)) { return null; }
        // sessionId/agentId compose a file path and come from the WebView — reject traversal.
        if (!SessionManager.IsSafePathToken(sessionId)) { return null; }
        if (!string.IsNullOrEmpty(agentId) && !SessionManager.IsSafePathToken(agentId)) { return null; }
        var folder = paths.SessionFolder(workingDirectory);
        var path = string.IsNullOrEmpty(agentId)
            ? Path.Combine(folder, sessionId + ".jsonl")
            : Path.Combine(folder, sessionId, "subagents", $"agent-{agentId}.jsonl");
        if (!File.Exists(path)) { return null; }
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
                            return item["input"] is not JObject input ? null : ProjectInput(toolName, input);
                        }
                    }
                }
                catch { /* silent: skip malformed JSONL line */ }
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException("FindToolInput/Result", ex); }
        return null;
    }
    private static string FindToolResult(string workingDirectory, string sessionId, string toolUseId, ClaudePaths paths, string agentId = null)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolUseId)) { return null; }
        // sessionId/agentId compose a file path and come from the WebView — reject traversal.
        if (!SessionManager.IsSafePathToken(sessionId)) { return null; }
        if (!string.IsNullOrEmpty(agentId) && !SessionManager.IsSafePathToken(agentId)) { return null; }
        var folder = paths.SessionFolder(workingDirectory);
        var path = string.IsNullOrEmpty(agentId)
            ? Path.Combine(folder, sessionId + ".jsonl")
            : Path.Combine(folder, sessionId, "subagents", $"agent-{agentId}.jsonl");
        if (!File.Exists(path)) { return null; }
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
        catch (Exception ex) { OutputWindowLogger.LogException("FindToolInput/Result", ex); }
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
        catch { return false; }
    }
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim('_');
        return safe.Length > 40 ? safe.Substring(0, 40) : safe;
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
        var tmpPath = Path.Combine(Path.GetTempPath(), $"claude_{SanitizeFileName(baseName)}{ext}");
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
    private static async Task OpenFileInEditorAsync(string filePath, int startLine, int endLine)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
        var window = dte?.ItemOperations?.OpenFile(filePath, Constants.vsViewKindTextView);
        if (window == null) { return; }
        window.Activate();
        if (startLine <= 0) { return; }
        await Task.Yield();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (window.Document?.Selection is TextSelection sel)
        {
            if (AgentsOptions.Chat.SelectLinesOnOpen && endLine >= startLine && endLine > 0)
            {
                sel.GotoLine(startLine, true);
                if (endLine > startLine)
                {
                    sel.LineDown(true, endLine - startLine);
                }
                sel.EndOfLine(true);
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
        var normalized = filePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized) && File.Exists(normalized)) { return normalized; }
        var combined = Path.Combine(client.WorkingDirectory ?? string.Empty, normalized);
        if (File.Exists(combined)) { return combined; }
        if (File.Exists(normalized)) { return normalized; }
        OutputWindowLogger.Debug(() => $"[OpenFile] Path not found. raw='{filePath}' normalized='{normalized}' combined='{combined}' workingDir='{client.WorkingDirectory}'");
        return null;
    }
}
