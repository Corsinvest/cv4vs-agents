/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// Single diff-window helper used by both the WebView chat and the MCP
/// <c>openDiff</c> tool. Wraps VS's built-in <c>SVsDifferenceService</c>
/// with two extras worth keeping in one place:
///   • <c>VSDIFFOPT_*Temporary</c> flags so VS auto-cleans the temp
///     files we materialize for the comparison
///   • interactive resolution: <see cref="OpenAsync"/> blocks until the
///     user saves the proposed-side temp (FILE_SAVED) or closes the
///     diff frame (TAB_CLOSED). The Claude CLI uses these strings to
///     decide whether to apply the pending edit.
/// </summary>
internal sealed partial class IdeDiffViewer
{
    private static readonly Lazy<IdeDiffViewer> _instance = new(() => new IdeDiffViewer());
    public static IdeDiffViewer Instance => _instance.Value;

    /// <summary>Diff resolution status strings — the exact wire tokens the Claude
    /// CLI's openDiff handler expects. FileSaved = applied (user saved the
    /// proposal); TabClosed = closed without saving;
    /// Rejected = error / explicit reject. (2.1.169 maps TabClosed → rejected on
    /// the wire; see OpenDiffTool.)</summary>
    public const string FileSaved = "FILE_SAVED";
    public const string TabClosed = "TAB_CLOSED";
    public const string DiffRejected = "DIFF_REJECTED";

    /// <summary>Pending interactive diffs keyed by the temp "right" path.
    /// The RDT save listener and the frame-close listener look up the
    /// pending entry here and resolve its TCS. Same dictionary used by
    /// both listeners — concurrent resolution is fine because TCS
    /// rejects the second TrySetResult.</summary>
    private readonly Dictionary<string, PendingDiff> _pending =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every diff frame we have opened (interactive + WebView
    /// path), keyed by tab name. Used by close_tab and closeAllDiffTabs
    /// so we close ONLY our own diffs and never touch user-opened tabs
    /// (or any of our own panes, whose caption used to match the old
    /// "Claude Code" substring filter).</summary>
    private readonly Dictionary<string, IVsWindowFrame> _openFrames =
        new(StringComparer.Ordinal);

    /// <summary>Diffs opened by the chat, keyed by the tool_use they preview. Separate from
    /// <see cref="_openFrames"/> because close_tab addresses that one by tab name: the two hold
    /// the same frames for different questions — "which tab?" vs "which request?".</summary>
    private readonly Dictionary<string, IVsWindowFrame> _chatDiffs =
        new(StringComparer.Ordinal);

    /// <summary>Single shared RDT subscription. Created lazily on the
    /// first interactive diff; never torn down (cheap to keep around).</summary>
    private IVsRunningDocumentTable _rdt;
    private uint _rdtCookie;
    private RdtSaveListener _rdtListener;

    private IdeDiffViewer() { }

    /// <summary>MCP-style entry point: existing file on disk + proposed
    /// new content. Used by the Claude CLI's <c>openDiff</c>. The
    /// returned task completes when the user resolves the diff:
    ///   • FILE_SAVED   — user saved the proposed (right) file
    ///   • TAB_CLOSED   — user closed the diff window without saving
    ///   • DIFF_REJECTED — error or service unavailable
    /// </summary>
    /// <summary>Result of an interactive diff: status string + (when
    /// FILE_SAVED) the user's actual saved content so the CLI can apply
    /// any manual edits they made on the proposed side.</summary>
    public sealed class DiffResult
    {
        public string Status;       // FILE_SAVED | TAB_CLOSED | DIFF_REJECTED
        public string SavedContent; // only set on FILE_SAVED
    }

    public async Task<DiffResult> OpenAsync(
        string oldFilePath, string newFilePath, string newFileContents, string tabName)
    {
        oldFilePath = PathHelpers.FromFileUri(oldFilePath);
        newFilePath = PathHelpers.FromFileUri(newFilePath);
        if (string.IsNullOrEmpty(oldFilePath))
        {
            OutputWindowLogger.Global.Warn("[diff] no old_file_path given — nothing to compare against");
            return new DiffResult { Status = DiffRejected };
        }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            // Match the original's line endings before writing. The proposed content arrives with
            // whatever the CLI put in the JSON — LF, as a rule — and a CRLF file diffed against it
            // makes VS open a MODAL "inconsistent line endings, normalise?" dialog. That blocks the
            // UI thread, and with it every other MCP tool, until somebody answers: a diff that
            // looked hung for ten minutes was this box waiting behind the editor.
            var tempPath = WriteTemp(
                MatchLineEndings(newFileContents, oldFilePath),
                Path.GetFileName(newFilePath ?? oldFilePath));

            var isNewFile = !File.Exists(oldFilePath);
            var leftPath = oldFilePath;
            var leftIsTemp = false;
            if (isNewFile)
            {
                // New file (Claude is creating it): diff the proposal against an
                // empty left side.
                leftPath = WriteTemp("", Path.GetFileName(oldFilePath) + ".empty");
                leftIsTemp = true;
            }
            else if (IsDocumentDirty(oldFilePath))
            {
                // Unsaved edits in the editor: VS's diff would compare the dirty
                // buffer (mixing user edits with Claude's). Snapshot the on-disk
                // version to a temp and diff against THAT; the user still sees
                // their dirty changes in the original editor pane.
                leftPath = WriteTemp(File.ReadAllText(oldFilePath),
                    Path.GetFileName(oldFilePath) + ".saved");
                leftIsTemp = true;
            }

            // Suffix the caption with a one-line keyboard hint so users
            // don't have to learn the (Ctrl+S = apply, X = reject)
            // convention from a tooltip. Cheap UX nudge while we don't
            // have a proper Accept/Reject toolbar over the diff editor.
            var baseCaption = tabName ?? ($"Claude Code — {Path.GetFileName(oldFilePath)}");
            var caption = baseCaption + "  ·  Ctrl+S to apply · close to reject";

            var frame = OpenComparison(
                leftPath: leftPath, rightPath: tempPath,
                caption: caption,
                leftLabel: Path.GetFileName(oldFilePath) + (isNewFile ? " (new file)" : " (current)"),
                rightLabel: Path.GetFileName(newFilePath ?? oldFilePath) + " (proposed)",
                rightIsTemp: false, leftIsTemp: leftIsTemp);
            if (frame == null)
            {
                // Nothing opened, so nothing will close and clean up after it. Logged because from
                // the outside this is indistinguishable from a diff the user rejected in a hurry:
                // both answer DIFF_REJECTED, one after a decision and one without ever asking.
                OutputWindowLogger.Global.Warn(
                    $"[diff] the comparison window did not open for '{caption}' — answering rejected without showing anything");
                try { File.Delete(tempPath); } catch (Exception) { /* best effort */ }
                return new DiffResult { Status = DiffRejected };
            }

            // Track the frame in the global registry so close_tab and closeAllDiffTabs can
            // address it by identity: matching on the caption also hits our own panes, whose
            // caption contains "Claude Code".
            var registryKey = tabName ?? caption;
            _openFrames[registryKey] = frame;

            // Register the pending diff and wire both resolution paths
            // (frame-close + RDT save). The TCS is awaited below; both
            // listeners race to TrySetResult — first one wins.
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pending = new PendingDiff
            {
                TempPath = tempPath,
                Frame = frame,
                RegistryKey = registryKey,
                Tcs = tcs,
            };
            _pending[tempPath] = pending;

            EnsureRdtAdvised();
            HookFrameClose(frame, tempPath);

            // Accept/Reject InfoBar — explicit alternative to the save/close
            // gestures (both still work).
            pending.InfoBar = DiffInfoBar.TryAttach(
                frame,
                Path.GetFileName(newFilePath ?? oldFilePath),
                onAccept: () => TryResolve(tempPath, FileSaved),
                onReject: () => TryResolve(tempPath, TabClosed));

            OutputWindowLogger.Global.Debug(() => $"[diff] waiting on '{registryKey}' — accept, reject or close resolves it");
            var status = await tcs.Task;
            OutputWindowLogger.Global.Debug(() => $"[diff] '{registryKey}' resolved as {status}");
            // Ship the user's content along with FILE_SAVED so the CLI
            // can apply exactly what they kept (they may have edited the
            // proposed side before saving).
            string saved = null;
            if (status == FileSaved)
            {
                try { saved = File.ReadAllText(tempPath); } catch { }
            }
            // Auto-close the diff frame after resolution so the user
            // doesn't end up with a stale tab they have to dismiss.
            // No-op if the user themselves closed the tab (that's the
            // TAB_CLOSED path; CloseFrame on a closed frame is harmless).
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try { pending.InfoBar?.Close(); } catch { /* best effort */ }
            try { pending.Frame?.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("IdeDiffViewer.AutoClose", ex); }

            _pending.Remove(tempPath);
            if (pending.RegistryKey != null) { _openFrames.Remove(pending.RegistryKey); }

            // Delete the proposed side ourselves. It is NOT passed as VSDIFFOPT_RightFileIsTemporary
            // — that would have VS remove it the moment the window closes, and the read above needs
            // it alive to carry the user's edits back. The comment here used to claim VS cleaned it
            // up; fourteen claude-diff-* files in %TEMP%, six from one afternoon, said otherwise.
            try { File.Delete(tempPath); }
            catch (Exception ex) { OutputWindowLogger.Global.Warn($"[diff] could not delete the temp file: {ex.Message}"); }

            return new DiffResult { Status = status, SavedContent = saved };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDiffViewer.Open", ex);
            return new DiffResult { Status = DiffRejected };
        }
    }

    /// <summary>WebView-style entry point: the chat has BOTH contents in memory. Both sides go to
    /// temp. Clicking the same <paramref name="toolUseId"/> twice closes it (toggle); a different
    /// one replaces it — keying on the file path would make two edits to the same file the
    /// same diff.</summary>
    public async Task ShowFromContentsAsync(string toolUseId, string filePath, string oldContent, string newContent)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (!string.IsNullOrEmpty(toolUseId) && _chatDiffs.ContainsKey(toolUseId))
            {
                CloseDiffFor(toolUseId);
                return;
            }
            CloseAllChatDiffs();

            var tempOld = WriteTemp(oldContent, Path.GetFileName(filePath) + ".old");
            var tempNew = WriteTemp(newContent, Path.GetFileName(filePath) + ".new");
            var caption = $"Claude Code — {Path.GetFileName(filePath)}";
            var frame = OpenComparison(
                leftPath: tempOld, rightPath: tempNew,
                caption: caption,
                leftLabel: "Original", rightLabel: "Proposed",
                rightIsTemp: true, leftIsTemp: true);
            if (frame != null)
            {
                _openFrames[caption] = frame;
                if (!string.IsNullOrEmpty(toolUseId)) { _chatDiffs[toolUseId] = frame; }
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("IdeDiffViewer.ShowFromContents", ex); }
    }

    /// <summary>Close a specific diff frame by tab name. Lookup is against the open-frames
    /// registry — never against VS window captions.
    /// <para>Returns whether a frame was actually closed. It used to return nothing and the tool
    /// answered success either way, so "closed it", "that one is already gone" and "that is the
    /// user's own document, not mine to touch" were the same reply.</para></summary>
    public async Task<bool> CloseTabAsync(string tabName)
    {
        if (string.IsNullOrEmpty(tabName)) { return false; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!_openFrames.TryGetValue(tabName, out var frame)) { return false; }
        try { frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeDiffViewer.CloseTab", ex);
            _openFrames.Remove(tabName);
            return false;
        }
        _openFrames.Remove(tabName);
        return true;
    }

    /// <summary>Close every diff frame currently in our open-frames
    /// registry. Returns the number of frames closed. Does NOT touch
    /// other VS windows — even ones whose caption happens to contain
    /// "Diff" or "Claude Code".</summary>
    public async Task<int> CloseAllAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_openFrames.Count == 0) { return 0; }
        var snapshot = new List<KeyValuePair<string, IVsWindowFrame>>(_openFrames);
        int closed = 0;
        foreach (var kv in snapshot)
        {
            try { kv.Value.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); closed++; }
            catch (Exception ex) { OutputWindowLogger.Global.LogException("IdeDiffViewer.CloseAll", ex); }
            _openFrames.Remove(kv.Key);
        }
        return closed;
    }

    /// <summary>Reject every still-pending interactive diff when the MCP
    /// transport loses its last client: a diff whose caller is gone can
    /// never be resolved. Resolving the TCS lets <see cref="OpenAsync"/>
    /// run its own frame/InfoBar/registry cleanup.</summary>
    public async Task CancelAllPendingAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_pending.Count == 0) { return; }
        foreach (var pending in new List<PendingDiff>(_pending.Values))
        {
            pending.Tcs.TrySetResult(DiffRejected);
        }
    }

    /// <summary>Close the diff opened for a given tool_use. Finding none is normal, not an error:
    /// the frame may be the user's own (never in the map) or the diff was never opened.</summary>
    public void CloseDiffFor(string toolUseId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (string.IsNullOrEmpty(toolUseId)) { return; }
        if (!_chatDiffs.TryGetValue(toolUseId, out var frame)) { return; }
        CloseChatFrame(toolUseId, frame);
    }

    /// <summary>One chat diff at a time: opening a new one closes the previous.</summary>
    private void CloseAllChatDiffs()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        foreach (var kv in new List<KeyValuePair<string, IVsWindowFrame>>(_chatDiffs))
        {
            CloseChatFrame(kv.Key, kv.Value);
        }
    }

    /// <summary>Close a frame and drop it from BOTH indexes — left in _openFrames it would be a
    /// dead entry that close_all later tries to close again.</summary>
    private void CloseChatFrame(string toolUseId, IVsWindowFrame frame)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try { frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("IdeDiffViewer.CloseChatFrame", ex); }
        _chatDiffs.Remove(toolUseId);
        foreach (var kv in new List<KeyValuePair<string, IVsWindowFrame>>(_openFrames))
        {
            if (ReferenceEquals(kv.Value, frame)) { _openFrames.Remove(kv.Key); }
        }
    }

    //  Interactive resolution plumbing

    /// <summary>Subscribe (once) to the RDT so we hear save events on
    /// our temp "right" files. Idempotent.</summary>
    private void EnsureRdtAdvised()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_rdt != null) { return; }
        _rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
        if (_rdt == null) { return; }
        _rdtListener = new RdtSaveListener(this);
        _rdt.AdviseRunningDocTableEvents(_rdtListener, out _rdtCookie);
    }

    /// <summary>Mark a frame so its close event resolves the matching
    /// pending diff to TAB_CLOSED. Each frame gets its own listener
    /// instance — VS supports a single notify per frame, but our
    /// listener is just a thin pass-through.</summary>
    private void HookFrameClose(IVsWindowFrame frame, string tempPath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var listener = new FrameCloseListener(this, tempPath);
        frame.SetProperty((int)__VSFPROPID.VSFPROPID_ViewHelper, listener);
    }

    /// <summary>Resolve a pending diff to <paramref name="status"/> if
    /// it's still pending. Called from both the RDT save listener and
    /// the frame close listener — first wins.</summary>
    internal void TryResolve(string tempPath, string status)
    {
        if (string.IsNullOrEmpty(tempPath)) { return; }
        if (_pending.TryGetValue(tempPath, out var pending))
        {
            pending.Tcs.TrySetResult(status);
        }
    }

    /// <summary>Look up the temp path for a given full path the RDT
    /// reports. Tolerant of case + slash differences across Windows
    /// path normalizations.</summary>
    internal string FindPendingTempPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) { return null; }
        foreach (var key in _pending.Keys)
        {
            if (string.Equals(key, fullPath, StringComparison.OrdinalIgnoreCase)) { return key; }
        }
        return null;
    }

    private IVsWindowFrame OpenComparison(
        string leftPath, string rightPath, string caption,
        string leftLabel, string rightLabel,
        bool leftIsTemp, bool rightIsTemp)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService svc)
        {
            OutputWindowLogger.Global.Warn("[diff] difference service unavailable — diff cannot open");
            return null;
        }
        uint opts = 0;
        if (leftIsTemp) { opts |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary; }
        if (rightIsTemp) { opts |= (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary; }
        return svc.OpenComparisonWindow2(
            leftPath, rightPath,
            caption, caption,
            leftLabel, rightLabel,
            inlineLabel: null, roles: null, grfDiffOptions: opts);
    }

    /// <summary>True if the given file is open in VS with unsaved
    /// changes. Used by the diff path to snapshot the on-disk version
    /// instead of comparing against a dirty buffer. Best-effort: any
    /// DTE error → returns false (we just diff against the live file).</summary>
    private static bool IsDocumentDirty(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            if (dte?.Documents == null) { return false; }
            foreach (EnvDTE.Document d in dte.Documents)
            {
                if (string.Equals(d?.FullName, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    return !d.Saved;
                }
            }
        }
        catch { /* DTE COM hiccup — treat as not dirty */ }
        return false;
    }

    private static string WriteTemp(string content, string namedFor)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"claude-diff-{Guid.NewGuid():N}-{namedFor}");
        File.WriteAllText(temp, content ?? string.Empty);
        return temp;
    }

    /// <summary>Rewrite <paramref name="content"/> with the line endings <paramref name="modelPath"/>
    /// uses, so the two sides of a diff agree and VS has nothing to ask about.
    /// <para>Decided by counting: a file with a mixture — and they exist — gets whichever it has
    /// more of, which is the same answer the editor's own status bar gives. A file with no line
    /// break at all, or one we cannot read, leaves the content alone: guessing CRLF on a one-line
    /// file would be a change made for nothing.</para></summary>
    private static string MatchLineEndings(string content, string modelPath)
    {
        if (string.IsNullOrEmpty(content)) { return content; }

        string model;
        try { model = File.Exists(modelPath) ? File.ReadAllText(modelPath) : null; }
        catch (Exception) { return content; }
        if (string.IsNullOrEmpty(model)) { return content; }

        var crlf = 0;
        var lf = 0;
        for (var i = 0; i < model.Length; i++)
        {
            if (model[i] != '\n') { continue; }
            if (i > 0 && model[i - 1] == '\r') { crlf++; } else { lf++; }
        }
        if (crlf == 0 && lf == 0) { return content; }

        var normalized = content.Replace("\r\n", "\n");
        return crlf >= lf ? normalized.Replace("\n", "\r\n") : normalized;
    }

    //  Pending-diff state (listener types live in IdeDiffViewer.Listeners.cs)

    private sealed class PendingDiff
    {
        public string TempPath;
        public IVsWindowFrame Frame;
        public string RegistryKey;
        public TaskCompletionSource<string> Tcs;
        public DiffInfoBar InfoBar;
    }

}
