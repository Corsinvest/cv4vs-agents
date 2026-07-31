/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Panes;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Options;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using System.IO;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>WebViewMessageHandler: Open-namespace message handlers (jump to file/edit, tool output, diff, external url, options, CLI terminal).</summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleIdeFile(JObject data, int? id)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = data.ToObject<Contracts.IdeFileNotification>();
            var raw = p.FilePath ?? "";
            var filePath = ResolveFilePath(raw);
            if (filePath == null)
            {
                // Nothing was found anywhere ResolveFilePath looks, so name the path the link
                // carried rather than one we resolved — that is what the user can act on. The
                // reason stays open: moved, renamed, deleted, or a temp file Windows cleared out
                // are all the same miss from here.
                NoticeOpenFailed(raw, "not found");
                return;
            }
            // endLine defaults to startLine when the WebView omits it (single-line open).
            var endLine = p.EndLine != 0 ? p.EndLine : p.StartLine;
            await OpenFileInEditorAsync(filePath, p.StartLine, endLine);
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleToolOutput(JObject data, int? id)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var p = data.ToObject<Contracts.ToolOutputNotification>();
            var toolUseId = p.ToolUseId ?? "";
            var title = p.Title ?? "Tool Output";
            var which = p.Which ?? "out";
            var agentId = p.AgentId;
            var toolName = p.ToolName ?? "";
            if (string.IsNullOrEmpty(toolUseId)) { return; }
            // Read full content from the JSONL — the WebView only holds
            // preview-capped text, so this gives the untruncated output.
            // agentId routes lookup to the sub-agent transcript when present.
            // But the Agent ROW itself carries an agentId (for fetching its
            // children) while its OWN result lives in the main transcript — so
            // fall back to the main file when the sub-agent file has no match.
            var paths = PaneClaudePaths;
            var content = which == "in"
                            ? FindToolInput(client.WorkingDirectory, client.SessionId, toolUseId, paths, toolName, agentId)
                            : FindToolResult(client.WorkingDirectory, client.SessionId, toolUseId, paths, agentId);
            if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(agentId))
            {
                content = which == "in"
                            ? FindToolInput(client.WorkingDirectory, client.SessionId, toolUseId, paths, toolName)
                            : FindToolResult(client.WorkingDirectory, client.SessionId, toolUseId, paths);
            }
            if (string.IsNullOrEmpty(content))
            {
                OutputWindowLogger.Debug(() => $"[open-output] no content found for tool_use_id={toolUseId}");
                return;
            }
            // Strip the CLI's <tool_use_error> wrapper (protocol detail).
            // Same regex as the WebView's `_cleanResult`.
            var m = System.Text.RegularExpressions.Regex.Match(
                content,
                @"<tool_use_error>([\s\S]*?)</tool_use_error>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success) { content = m.Groups[1].Value.Trim(); }
            var ext = title.StartsWith("[Bash]") || title.StartsWith("[PowerShell]") ? ".sh" : ".txt";
            var suffix = which == "in" ? "_in" : "_out";
            var tmpPath = Path.Combine(Path.GetTempPath(), $"claude_{SanitizeFileName(title)}{suffix}{ext}");
            File.WriteAllText(tmpPath, content, System.Text.Encoding.UTF8);
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, tmpPath,
                Microsoft.VisualStudio.VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var frame);
            frame?.Show();
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleDiffDialog(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.DiffDialogNotification>();
        _ = Ide.IdeContextService.Instance.ShowDiffAsync(
            p.ToolUseId ?? "", p.FilePath ?? "", p.OldString ?? "", p.NewString ?? "");
    }

    private void HandleIdeOutputWindow(JObject data, int? id)
    {
        OutputWindowLogger.ActivatePane();
    }

    private void HandleExternalUrl(JObject data, int? id)
    {
        ShellHelpers.OpenExternal(data.ToObject<Contracts.ExternalUrlNotification>().Url ?? "");
    }

    private void HandleOptions(JObject data, int? id)
    {
        AgentsPackage.Instance?.ShowOptionPage(typeof(AgentsGeneralPage));
    }

    private void HandleCliTerminal(JObject data, int? id)
    {
        // Same as the toolbar "+" for CLI: a fresh interactive terminal pane, inheriting
        // this chat's profile.
        Core.Panes.PaneLauncher.OpenNew(PaneKind.Cli, entry.Profile);
    }

    private void HandleChatPane(JObject data, int? id)
    {
        // Same as the toolbar "+" for Chat: a new pane on its own session, inheriting this
        // chat's profile. Distinct from /clear, which restarts the conversation in place.
        Core.Panes.PaneLauncher.OpenNew(PaneKind.Chat, entry.Profile);
    }

    private void HandleSessionHistory(JObject data, int? id)
    {
        // The picker is WPF and lives in the toolbar; the entry carries the callback so the
        // command opens the very same popup as the History button.
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            entry.ShowHistoryAction?.Invoke();
        }).FileAndForget(nameof(WebViewMessageHandler));
    }
}
