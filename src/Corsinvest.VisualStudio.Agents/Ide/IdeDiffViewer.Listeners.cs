/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>
/// IdeDiffViewer VS-callback listener types: the RDT save listener and per-frame close notify
/// that resolve a pending diff (FILE_SAVED / TAB_CLOSED), and the info-bar that surfaces the
/// accept/reject actions. They call back into the viewer via owner.TryResolve /
/// FindPendingTempPath; the viewer logic and the PendingDiff state live in IdeDiffViewer.cs.
/// </summary>
internal sealed partial class IdeDiffViewer
{
    /// <summary>Listens to RDT save events. When the document being
    /// saved matches one of our pending temp paths, resolves to
    /// FILE_SAVED. We use <c>OnAfterSave</c> (not <c>OnAfterSaveAll</c>)
    /// so single-file Save (Ctrl+S) is captured.</summary>
    private sealed class RdtSaveListener(IdeDiffViewer owner) : IVsRunningDocTableEvents3
    {
        int IVsRunningDocTableEvents3.OnAfterSave(uint docCookie)
        {
            try
            {
                if (Package.GetGlobalService(typeof(SVsRunningDocumentTable)) is not IVsRunningDocumentTable rdt) { return VSConstants.S_OK; }
                rdt.GetDocumentInfo(docCookie, out _, out _, out _, out var path,
                    out _, out _, out _);
                var match = owner.FindPendingTempPath(path);
                if (match != null) { owner.TryResolve(match, FileSaved); }
            }
            catch (Exception ex) { OutputWindowLogger.LogException("RdtSaveListener.OnAfterSave", ex); }
            return VSConstants.S_OK;
        }

        // Other RDT events — uninteresting for us.
        int IVsRunningDocTableEvents3.OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        int IVsRunningDocTableEvents3.OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        int IVsRunningDocTableEvents3.OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        int IVsRunningDocTableEvents3.OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        int IVsRunningDocTableEvents3.OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        int IVsRunningDocTableEvents3.OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        int IVsRunningDocTableEvents3.OnBeforeSave(uint docCookie) => VSConstants.S_OK;
        int IVsRunningDocTableEvents.OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        int IVsRunningDocTableEvents.OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        int IVsRunningDocTableEvents.OnAfterSave(uint docCookie) => ((IVsRunningDocTableEvents3)this).OnAfterSave(docCookie);
        int IVsRunningDocTableEvents.OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        int IVsRunningDocTableEvents.OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        int IVsRunningDocTableEvents.OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        int IVsRunningDocTableEvents2.OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        int IVsRunningDocTableEvents2.OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
        int IVsRunningDocTableEvents2.OnAfterSave(uint docCookie) => ((IVsRunningDocTableEvents3)this).OnAfterSave(docCookie);
        int IVsRunningDocTableEvents2.OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
        int IVsRunningDocTableEvents2.OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
        int IVsRunningDocTableEvents2.OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
        int IVsRunningDocTableEvents2.OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
    }

    /// <summary>Per-frame notify: when the diff frame closes, resolves
    /// the matching pending diff to TAB_CLOSED. If the close came after
    /// a save, the RDT listener already resolved to FILE_SAVED and our
    /// TrySetResult is a no-op.</summary>
    private sealed class FrameCloseListener(IdeDiffViewer owner, string tempPath) : IVsWindowFrameNotify3, IVsWindowFrameNotify2, IVsWindowFrameNotify
    {
        int IVsWindowFrameNotify3.OnClose(ref uint pgrfSaveOptions)
        {
            owner.TryResolve(tempPath, TabClosed);
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify2.OnClose(ref uint pgrfSaveOptions)
        {
            owner.TryResolve(tempPath, TabClosed);
            return VSConstants.S_OK;
        }

        int IVsWindowFrameNotify3.OnShow(int fShow) => VSConstants.S_OK;
        int IVsWindowFrameNotify3.OnMove(int x, int y, int w, int h) => VSConstants.S_OK;
        int IVsWindowFrameNotify3.OnSize(int x, int y, int w, int h) => VSConstants.S_OK;
        int IVsWindowFrameNotify3.OnDockableChange(int fDockable, int x, int y, int w, int h) => VSConstants.S_OK;
        int IVsWindowFrameNotify.OnShow(int fShow) => VSConstants.S_OK;
        int IVsWindowFrameNotify.OnMove() => VSConstants.S_OK;
        int IVsWindowFrameNotify.OnSize() => VSConstants.S_OK;
        int IVsWindowFrameNotify.OnDockableChange(int fDockable) => VSConstants.S_OK;
    }

    /// <summary>Accept/Reject InfoBar shown over a diff frame. VS InfoBars use
    /// plain hyperlink action items (no per-button icon or colour). Clicking an
    /// action invokes the supplied callback (which resolves the diff's pending
    /// TCS); the InfoBar then closes itself.</summary>
    internal sealed class DiffInfoBar : IVsInfoBarUIEvents
    {
        private readonly Action _onAccept;
        private readonly Action _onReject;
        private IVsInfoBarUIElement _element;
        private uint _cookie;
        // Identity tokens passed as each action's ActionContext, matched on click.
        private readonly object _accept = new();
        private readonly object _reject = new();

        private DiffInfoBar(Action onAccept, Action onReject)
        {
            _onAccept = onAccept;
            _onReject = onReject;
        }

        /// <summary>Create + attach the InfoBar to the frame's InfoBar host.
        /// Returns null (no-op) if the host or factory isn't available — the
        /// save/close gestures still resolve the diff, so the InfoBar is a
        /// visible convenience, never the only path.</summary>
        public static DiffInfoBar TryAttach(IVsWindowFrame frame, string fileName, Action onAccept, Action onReject)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (frame == null) { return null; }
                if (ErrorHandler.Failed(frame.GetProperty((int)__VSFPROPID7.VSFPROPID_InfoBarHost, out var hostObj))
                    || hostObj is not IVsInfoBarHost host)
                {
                    return null;
                }
                if (Package.GetGlobalService(typeof(SVsInfoBarUIFactory)) is not IVsInfoBarUIFactory factory) { return null; }

                var bar = new DiffInfoBar(onAccept, onReject);
                var model = new InfoBarModel(
                    textSpans:
                    [
                        // One line only: the VS info bar doesn't wrap, it truncates.
                        new InfoBarTextSpan($"Claude proposes changes to {fileName}. Ctrl+S to accept."),
                    ],
                    actionItems:
                    [
                        new InfoBarButton("Accept", bar._accept),
                        new InfoBarButton("Reject", bar._reject),
                    ],
                    image: KnownMonikers.StatusInformation,
                    // No close button: dismissing it would hide the only visible
                    // controls while the diff stays pending, reading as a cancel.
                    isCloseButtonVisible: false);

                bar._element = factory.CreateInfoBar(model);
                bar._element.Advise(bar, out bar._cookie);
                host.AddInfoBar(bar._element);
                return bar;
            }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("DiffInfoBar.TryAttach", ex);
                return null;
            }
        }

        public void Close()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try { _element?.Close(); } catch { /* best effort */ }
        }

        void IVsInfoBarUIEvents.OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // ActionContext is the token we passed (_accept/_reject).
            if (ReferenceEquals(actionItem.ActionContext, _accept)) { _onAccept?.Invoke(); }
            else if (ReferenceEquals(actionItem.ActionContext, _reject)) { _onReject?.Invoke(); }
            try { infoBarUIElement.Close(); } catch { /* best effort */ }
        }

        void IVsInfoBarUIEvents.OnClosed(IVsInfoBarUIElement infoBarUIElement)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_cookie != 0) { try { infoBarUIElement.Unadvise(_cookie); } catch { } _cookie = 0; }
        }
    }
}
