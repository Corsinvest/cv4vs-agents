/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>WebViewMessageHandler: the ExitPlanMode plan — opening it in the editor, answering with
/// what the editor holds, and telling the banner when the plan file is saved.</summary>
internal sealed partial class WebViewMessageHandler
{
    // Advised lazily on the first editable plan open, unadvised in Dispose.
    private IVsRunningDocumentTable _planRdt;
    private uint _planRdtCookie;

    private void HandleOpenPlan(JObject data, int? id) => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var p = data.ToObject<Contracts.OpenPlanNotification>();
        var toolUseId = p.ToolUseId ?? "";
        if (string.IsNullOrEmpty(toolUseId)) { return; }

        JObject input = null;
        if (client.TryGetPendingToolRequest(toolUseId, out var toolName, out var pendingInput)
            && toolName == PlanApproval.ToolName)
        {
            input = pendingInput;
            var path = PlanApproval.PlanFilePathOf(input);
            // Rooted and present, or not at all: ResolveFilePath would fall back to walking the whole
            // working directory by name on this thread, and could open a same-named file instead.
            if (path != null && Path.IsPathRooted(path) && File.Exists(path))
            {
                EnsurePlanSaveListener();
                await OpenFileInEditorAsync(path, 0, 0);
                return;
            }
        }

        // Answered already, or a CLI that names no file: the plan as it was in THAT call, read-only.
        // The file has moved on since — a later plan in the same session is written over it.
        if (input == null)
        {
            // The sub-agent transcript first when there is one, then the main file — as HandleDiffDialog.
            var agentId = p.AgentId ?? "";
            foreach (var lookIn in string.IsNullOrEmpty(agentId) ? [null] : new[] { agentId, null })
            {
                input = FindToolInputRaw(entry.WorkingDirectory, client.SessionId, toolUseId, PaneClaudePaths, lookIn);
                if (input != null) { break; }
            }
        }
        var plan = PlanApproval.PlanOf(input);
        if (plan.Length == 0)
        {
            // Two different misses: the call is not in the transcript at all, or it is and carries no
            // plan — the model left plan mode without writing a plan file, so the CLI injected none.
            var reason = input == null ? "Plan not found in the transcript" : "Claude sent no plan with this request";
            log.Warn($"[plan] can't open the plan of {toolUseId} — {reason}");
            // Not NoticeOpenFailed: that one names a file, and there is none here (its GetFileName also
            // eats anything up to a ':'). Keyed by toolUseId so two dead plans don't share one notice.
            bridge.Send(BridgeMessages.ToWebView.Chat.Notice, new Contracts.NoticeNotification
            {
                Key = "openplan:" + toolUseId,
                Severity = Contracts.NoticeVariantDto.Error,
                Message = reason,
                Position = Contracts.NoticePositionDto.Top,
            });
            return;
        }
        OpenReadOnlyTemp(TempFileName(PlanApproval.ToolName, "in", null, toolUseId), plan);
    }).FileAndForget(nameof(WebViewMessageHandler));

    /// <summary>Answer a pending ExitPlanMode with the plan as the editor holds it. The CLI re-reads
    /// the file on an empty updatedInput, so an unsaved edit would be lost without this; a real edit
    /// goes back as the plan, which the CLI writes over the file and labels "edited by user".
    /// <para>Always answers: the banner is already gone and a can_use_tool never times out, so a
    /// missed answer would leave the CLI waiting with nothing on screen to click.</para></summary>
    private void RespondToPlan(string toolUseId, JObject input, ToolPermissionResponse response)
        => ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            // Out of the WebView2 message callback first: a save that raises a dialog (read-only file,
            // encoding) would otherwise pump a nested message loop inside it. Mode-before-allow still
            // holds — set_permission_mode reached stdin before this message was even dispatched.
            await Task.Yield();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var path = PlanApproval.PlanFilePathOf(input);
            try
            {
                var snapshot = PlanApproval.PlanOf(input);
                if (response.Allow)
                {
                    // Best effort, bounded like the autosave hook: it keeps the disk in step, but what
                    // gets approved is read from the buffer below, saved or not.
                    var save = Ide.IdeContextService.Instance.SaveIfDirtyAsync(path);
                    var saved = await Task.WhenAny(save, Task.Delay(3000)) == save && await save;
                    if (!saved) { log.Warn($"[plan] could not save {path} before approving — sending the editor's text instead"); }
                    response.UpdatedInput = PlanApproval.UpdatedInputFor(snapshot, await ReadPlanTextAsync(path));
                }
                else if (PlanApproval.IsEdited(snapshot, await ReadPlanTextAsync(path)))
                {
                    // Not saved on a deny — that is the autosave hook's call, under the user's option. But
                    // the model keeps planning from its own copy unless it is told the file changed.
                    response.DenyMessage += $" The user edited the plan file {path} in the editor; read it before revising the plan.";
                }
            }
            catch (Exception ex) { log.LogException("[plan] answer", ex); }

            if (!client.RespondToToolPermission(toolUseId, response))
            {
                log.Debug(() => $"[plan] {toolUseId} was cancelled while its answer was being prepared");
            }
            Ide.IdeContextService.Instance.CloseDiffFor(toolUseId);
        }).FileAndForget(nameof(WebViewMessageHandler));

    /// <summary>The plan as the user sees it: the editor buffer when the file is open — unsaved edits
    /// included, and decoded whatever encoding the file is in — the file otherwise. Null when neither
    /// can be read, which leaves the CLI to its own read.</summary>
    private async Task<string> ReadPlanTextAsync(string path)
    {
        var buffer = await Ide.IdeContextService.Instance.ReadDocumentBufferAsync(path, 0, 0, 0);
        if (buffer.Ok) { return buffer.Content; }
        try { return File.Exists(path) ? File.ReadAllText(path, System.Text.Encoding.UTF8) : null; }
        catch (Exception ex)
        {
            log.Warn($"[plan] could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Hear saves in the IDE, from the first editable plan open until the pane closes.
    /// Stateless: which save matters is asked of the client when it happens, and the client already
    /// drops a request on answer, cancel, exit and respawn — so nothing here unsubscribes along the way.</summary>
    private void EnsurePlanSaveListener()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_planRdt != null) { return; }
        _planRdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
        _planRdt?.AdviseRunningDocTableEvents(new PlanSaveListener(this), out _planRdtCookie);
    }

    private void DisposePlanSaveListener()
    {
        if (_planRdt == null) { return; }
        ThreadHelper.ThrowIfNotOnUIThread();
        try { _planRdt.UnadviseRunningDocTableEvents(_planRdtCookie); }
        catch (Exception ex) { log.LogException("[plan] unadvise", ex); }
        _planRdt = null;
    }

    private void OnDocumentSaved(uint docCookie)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        // Every save in the IDE lands here: out before touching the RDT unless a plan is waiting.
        if (_planRdt == null || !client.HasPendingPlan) { return; }
        _planRdt.GetDocumentInfo(docCookie, out _, out _, out _, out var moniker, out _, out _, out _);
        if (!client.TryFindPendingPlan(moniker, out var toolUseId, out var input)) { return; }
        string text;
        try { text = File.ReadAllText(moniker, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            log.Warn($"[plan] could not read the saved {moniker}: {ex.Message}");
            return;
        }
        bridge.Send(BridgeMessages.ToWebView.Chat.PlanUpdated, new Contracts.PlanUpdatedNotification
        {
            ToolUseId = toolUseId,
            Plan = PlanApproval.Normalize(text),
            // Decided here, by the rule the answer uses, so the banner and the approval never disagree.
            Edited = PlanApproval.IsEdited(PlanApproval.PlanOf(input), text),
        });
    }

    /// <summary>RDT sink for <see cref="OnDocumentSaved"/>; every other event is a no-op.</summary>
    private sealed class PlanSaveListener(WebViewMessageHandler owner) : IVsRunningDocTableEvents
    {
        public int OnAfterSave(uint docCookie)
        {
            try { owner.OnDocumentSaved(docCookie); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("[plan] OnAfterSave", ex); }
            return VSConstants.S_OK;
        }

        public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
    }
}
