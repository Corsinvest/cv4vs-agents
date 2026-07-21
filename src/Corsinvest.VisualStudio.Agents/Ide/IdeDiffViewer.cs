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
    /// CLI's openDiff handler expects (see the VS Code extension). FileSaved =
    /// applied (user saved the proposal); TabClosed = closed without saving;
    /// Rejected = error / explicit reject. (2.1.169 maps TabClosed → rejected on
    /// the wire; see OpenDiffTool.)</summary>
    public const string FileSaved = "FILE_SAVED";
    public const string TabClosed = "TAB_CLOSED";
    public const string DiffRejected = "DIFF_REJECTED";

    private IVsWindowFrame _lastFrame;
    private string _lastFilePath;
    // _openFrames key of the last ShowFromContentsAsync diff, so CloseLast
    // can drop it from the registry (otherwise dead frames accumulate there).
    private string _lastKey;

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
        if (string.IsNullOrEmpty(oldFilePath)) { return new DiffResult { Status = DiffRejected }; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            var tempPath = WriteTemp(newFileContents, Path.GetFileName(newFilePath ?? oldFilePath));

            // If the original file has unsaved changes in the editor,
            // VS's diff service compares the dirty buffer (not the
            // on-disk content) against our proposed version — mixing
            // user edits with Claude's edits in the diff. Mirror what
            // the VS Code extension does: snapshot the on-disk version
            // to a separate temp and diff against THAT. The user can
            // still see / handle their dirty changes in the original
            // editor pane.
            var isNewFile = !File.Exists(oldFilePath);
            var leftPath = oldFilePath;
            var leftIsTemp = false;
            if (isNewFile)
            {
                // New file (Claude is creating it): diff the proposal against an
                // empty left side, like the VS Code extension does.
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
            if (frame == null) { return new DiffResult { Status = DiffRejected }; }

            // Track the frame in the global registry so close_tab and
            // closeAllDiffTabs can address it without caption matching
            // (which used to take down our own panes because their
            // caption contained "Claude Code").
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

            var status = await tcs.Task;
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
            catch (Exception ex) { OutputWindowLogger.LogException("IdeDiffViewer.AutoClose", ex); }

            // Cleanup pending + open-frames registration (file is
            // auto-removed by VS since we marked it temporary).
            _pending.Remove(tempPath);
            if (pending.RegistryKey != null) { _openFrames.Remove(pending.RegistryKey); }
            return new DiffResult { Status = status, SavedContent = saved };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("IdeDiffViewer.Open", ex);
            return new DiffResult { Status = DiffRejected };
        }
    }

    /// <summary>WebView-style entry point: the chat has BOTH contents in
    /// memory. Both sides go to temp; calling twice for the same
    /// <paramref name="filePath"/> closes the previous diff (toggle).</summary>
    public async Task ShowFromContentsAsync(string filePath, string oldContent, string newContent)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            if (_lastFrame != null && _lastFilePath == filePath) { CloseLast(); return; }
            CloseLast();

            var tempOld = WriteTemp(oldContent, Path.GetFileName(filePath) + ".old");
            var tempNew = WriteTemp(newContent, Path.GetFileName(filePath) + ".new");
            var caption = $"Claude Code — {Path.GetFileName(filePath)}";
            _lastFrame = OpenComparison(
                leftPath: tempOld, rightPath: tempNew,
                caption: caption,
                leftLabel: "Original", rightLabel: "Proposed",
                rightIsTemp: true, leftIsTemp: true);
            _lastFilePath = filePath;
            // Track in the open-frames registry too, so closeAllDiffTabs
            // sweeps WebView-shown diffs as well. Remember the key so
            // CloseLast can remove it (avoids leaking dead frames).
            if (_lastFrame != null) { _openFrames[caption] = _lastFrame; _lastKey = caption; }
        }
        catch (Exception ex) { OutputWindowLogger.LogException("IdeDiffViewer.ShowFromContents", ex); }
    }

    /// <summary>Close a specific diff frame by tab name. Lookup is
    /// against the open-frames registry — never against VS Window
    /// captions. Returns silently if the name doesn't match anything
    /// we opened.</summary>
    public async Task CloseTabAsync(string tabName)
    {
        if (string.IsNullOrEmpty(tabName)) { return; }
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!_openFrames.TryGetValue(tabName, out var frame)) { return; }
        try { frame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave); }
        catch (Exception ex) { OutputWindowLogger.LogException("IdeDiffViewer.CloseTab", ex); }
        _openFrames.Remove(tabName);
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
            catch (Exception ex) { OutputWindowLogger.LogException("IdeDiffViewer.CloseAll", ex); }
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

    /// <summary>Close the diff opened by the last
    /// <see cref="ShowFromContentsAsync"/>. No-op if none.</summary>
    public void CloseLast()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (_lastFrame != null)
            {
                _lastFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_NoSave);
                _lastFrame = null;
                _lastFilePath = null;
                if (_lastKey != null) { _openFrames.Remove(_lastKey); _lastKey = null; }
            }
        }
        catch (Exception ex) { OutputWindowLogger.LogException("IdeDiffViewer.CloseLast", ex); }
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

    //  Helpers

    private IVsWindowFrame OpenComparison(
        string leftPath, string rightPath, string caption,
        string leftLabel, string rightLabel,
        bool leftIsTemp, bool rightIsTemp)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (Package.GetGlobalService(typeof(SVsDifferenceService)) is not IVsDifferenceService svc)
        {
            OutputWindowLogger.Warn("[diff] difference service unavailable — diff cannot open");
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
