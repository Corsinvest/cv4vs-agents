/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

#if DEBUG

// IVsDifferenceCodeWindow2 is marked provisional ([Obsolete]) in the SDK. Whether it works on a
// given Visual Studio is exactly what these probes exist to find out.
#pragma warning disable CS0618

using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Ide;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>
/// Debug-build probe for issue #247, stage 3: can the review UI that GitHub Copilot draws in
/// Visual Studio 18 (the Keep/Undo buttons on a hunk and the panel at the bottom of the editor)
/// be driven from another extension, for a baseline that is ours?
/// <para>Nothing here is drawn by us. The probe reaches Copilot's own MEF parts by contract name and
/// reflection (<c>IAiChangesMarker</c>, <c>IBaselineStateRegistry</c>, <c>IFileBaselineState</c>) on the
/// active document and writes down what happens. Copilot gives no way to hand it a baseline text, so
/// the probe tries the two ways its decompiled code allows: A rewrites Copilot's own baseline buffer
/// and temp file after the state exists, B swaps the document's text inside one undo unit around the
/// call that copies it.</para>
/// <para>Each run does the next of six steps (the message box after a run says which and what to
/// look at by hand). Use a SCRATCH file of at least 12 lines, open, saved and not part of any
/// Claude change list: step 6 undoes all changes through Copilot, which rewrites and saves the
/// document (the probe puts the original text back afterwards).</para>
/// <para>The report goes to the Output window (Info level, tag below), the clipboard (all of it,
/// from the first step) and a message box (the verdicts only).</para>
/// </summary>
internal static class CopilotSpike
{
    private const string Tag = "[changes:spike]";
    private const string Title = "cv4vs Agents: Copilot review spike";
    private const string MarkerContract = "Microsoft.VisualStudio.Copilot.EditingStateTracking.IAiChangesMarker";
    private const string RegistryContract = "Microsoft.VisualStudio.Copilot.EditingStateTracking.IBaselineStateRegistry";
    private const string ActionPanelOption = "ubidiff/isactionpanelactive";
    private const string HunkLayer = "CopilotBaselineActions";
    private const string PanelLayer = "UbiDiffActionPanelAdornmentLayer";
    private const string ClientName = "cv4vs.claude";

    private static readonly object Gate = new object();
    private static readonly StringBuilder Log = new StringBuilder();
    private static readonly List<string> Summary = new List<string>();

    private sealed class Step
    {
        public string Title;
        public Func<Session, Task> Run;
        public string Hint;
    }

    private static readonly Step[] Steps =
    {
        new Step
        {
            Title = "Step 1 of 6: start a review, variant A (Copilot's own baseline buffer and temp file are rewritten)",
            Run = StartAAsync,
            Hint = "Look at the editor now: the inline diff, the Keep/Undo buttons on the hunks, the panel at the bottom, "
                + "and in the panel a button 'cv4vs probe' (click it). With the hunk buttons Keep ONE hunk, then Undo ONE hunk "
                + "(not the last one). Next run: end the review with RequestDismissal.",
        },
        new Step
        {
            Title = "Step 2 of 6: end the review (RequestDismissal)",
            Run = DismissAsync,
            Hint = "To test a reopened tab: close this file's tab and open it again now, then run the command (it will report "
                + "what Copilot did by itself and what Mark does for the new view). Otherwise next run: variant B.",
        },
        new Step
        {
            Title = "Step 3 of 6: start a review, variant B (the document is swapped inside one undo unit)",
            Run = StartBAsync,
            Hint = "Look at the editor again and press Ctrl+Z ONCE: tell what it undoes (then Ctrl+Y). "
                + "Next run: Keep all through the API (KeepAllAsync).",
        },
        new Step
        {
            Title = "Step 4 of 6: Keep all through the API (KeepAllAsync)",
            Run = KeepAllAsync,
            Hint = "Next run: a fresh review (variant A), so that Undo all can be tried.",
        },
        new Step
        {
            Title = "Step 5 of 6: start a review again, variant A",
            Run = StartAAsync,
            Hint = "Next run: Undo all through the API (DiscardAllAsync). The probe puts your text back and saves it afterwards.",
        },
        new Step
        {
            Title = "Step 6 of 6: Undo all through the API (DiscardAllAsync), then put the original text back",
            Run = DiscardAllAsync,
            Hint = "Finished. Paste the clipboard (the whole log) into the issue or the chat. Run again to start over.",
        },
    };

    private static Session _session;
    private static int _next;
    private static bool _running;

    /// <summary>True while a review started by the probe is on: the button in the editor's panel is
    /// only offered then.</summary>
    internal static bool IsLive => _session?.State is not null;

    private sealed class Session
    {
        public Api Api;
        public IWpfTextView View;
        public IVsDifferenceCodeWindow2 Window;
        public ITextUndoHistoryRegistry Undo;
        public ITextDocumentFactoryService Documents;
        public string FilePath;
        public string RealText;
        public string Wanted;
        public object Id;
        public object State;
        public int DiffEvents;
        public int ChangedEvents;
        public int DismissEvents;
        public readonly List<IDisposable> Subscriptions = new List<IDisposable>();
    }

    public static void Run()
        => ThreadHelper.JoinableTaskFactory.RunAsync(RunAsync).FileAndForget("CopilotSpike.Run");

    /// <summary>The button the probe parents into the editor's action panel. Whether the shell routes a
    /// click on it to this package is one of the things being found out.</summary>
    public static void ProbeClicked()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var line = $"ROUTED: the probe button in the editor's panel reached this package (review on: {_session?.FilePath ?? "none"})";
        Say(line);
        ShellHelpers.ShowMessage(line, Title, warning: false);
    }

    private static async Task RunAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_running)
        {
            ShellHelpers.ShowMessage("The previous run has not finished yet.", Title, warning: true);
            return;
        }
        _running = true;
        Summary.Clear();
        try
        {
            if (_session is not null && _session.View.IsClosed)
            {
                await ReopenAsync(_session);
            }
            else
            {
                if (_next == 0)
                {
                    _session = await BeginAsync();
                    if (_session is null) { return; }
                }
                var step = Steps[_next];
                Say("== " + step.Title);
                await step.Run(_session);
                _next++;
                if (_next >= Steps.Length) { End(); }
                Result("NEXT: " + step.Hint);
            }
        }
        catch (Exception ex)
        {
            Say($"FAILED: {ex.GetType().Name}: {ex.Message}");
            OutputWindowLogger.Global.LogException("CopilotSpike.Run", ex);
            Summary.Add($"FAILED: {ex.GetType().Name}: {ex.Message}. The probe was reset; run it again to start over.");
            await ResetAsync();
        }
        finally
        {
            _running = false;
            Publish();
        }
    }

    // --- the session: what is on the screen and what Copilot offers ---------------------------------------

    private static async System.Threading.Tasks.Task<Session> BeginAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        lock (Gate) { Log.Clear(); }
        Say($"Visual Studio: {Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion}");

        var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var api = Api.Resolve(components);
        if (api is null)
        {
            Summary.Add("Copilot's review API is not available here (see the log for which part): nothing to probe. "
                + "That is the answer a machine without Copilot gives.");
            return null;
        }

        var found = FindActiveDocument(components);
        if (found is null)
        {
            Summary.Add("No active text document: open a scratch file (12 lines or more), save it, and run the command again.");
            return null;
        }

        var session = new Session
        {
            Api = api,
            View = found.Value.View,
            Window = found.Value.Window,
            FilePath = found.Value.FilePath,
            Undo = components.GetService<ITextUndoHistoryRegistry>(),
            Documents = components.GetService<ITextDocumentFactoryService>(),
        };
        Say($"Document: {session.FilePath}");
        Say($"View roles: {string.Join(", ", session.View.Roles)}");
        Say($"TextViewModel: {session.View.TextViewModel?.GetType().FullName} "
            + $"(IDifferenceTextViewModel: {session.View.TextViewModel is IDifferenceTextViewModel})");
        Say($"IVsDifferenceCodeWindow2: {(session.Window is not null)}, SupportsDiff: {Read(() => session.Window.SupportsDiff)}, "
            + $"Baseline is null: {Read(() => session.Window.Baseline is null)}");
        Say($"view.TextBuffer is the document buffer: {ReferenceEquals(session.View.TextBuffer, session.View.TextDataModel.DocumentBuffer)}");

        if (session.Window is null || !session.Window.SupportsDiff)
        {
            Summary.Add("This document's window cannot show a difference (no IVsDifferenceCodeWindow2 or SupportsDiff is false).");
            return null;
        }
        if (IsDirty(session))
        {
            Summary.Add("The document has unsaved changes: save it (or use another scratch file) and run the command again.");
            return null;
        }
        var existing = api.TryGetState(session.FilePath);
        if (existing is not null)
        {
            Summary.Add("Copilot already holds a review of this file (its own agent is editing it?): use another scratch file.");
            return null;
        }
        session.RealText = session.View.TextSnapshot.GetText();
        var documentText = session.View.TextDataModel.DocumentBuffer.CurrentSnapshot.GetText();
        Say($"Document text: {session.RealText.Length} chars, {SplitLines(session.RealText).Count} lines, "
            + $"view text equals the document buffer's: {session.RealText == documentText}");
        if (SplitLines(session.RealText).Count < 2)
        {
            Summary.Add("The document has fewer than 2 lines: use a scratch file of 12 lines or more.");
            return null;
        }
        session.Wanted = SyntheticBaseline(session.RealText);
        Say($"Synthetic baseline: {session.Wanted.Length} chars, {SplitLines(session.Wanted).Count} lines "
            + "(the document without its first line; with 12 lines or more also a marked middle line: two hunks)");
        return session;
    }

    private static (IWpfTextView View, IVsDifferenceCodeWindow2 Window, string FilePath)? FindActiveDocument(IComponentModel components)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var monitor = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
        monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var frameObject);
        if (frameObject is not IVsWindowFrame frame) { return null; }
        frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var monikerObject);
        var path = monikerObject as string;
        var adapters = components.GetService<IVsEditorAdaptersFactoryService>();
        var vsView = VsShellUtilities.GetTextView(frame);
        var view = vsView is null ? null : adapters.GetWpfTextView(vsView);
        if (view is null || string.IsNullOrEmpty(path)) { return null; }

        view.Properties.TryGetProperty(typeof(IVsCodeWindow), out IVsCodeWindow fromProperties);
        var fromDocView = frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView) == VSConstants.S_OK
            ? docView as IVsCodeWindow
            : null;
        return (view, (fromProperties ?? fromDocView) as IVsDifferenceCodeWindow2, path);
    }

    // --- the steps ----------------------------------------------------------------------------------------

    /// <summary>Variant A. Copilot copies the document as the baseline when the state is created, so the
    /// state is created first, then its two copies of the baseline (the inert buffer that the hunk
    /// buttons edit and the temp file the code window shows) are rewritten, and only then is the view
    /// attached, with a diff state that already says "changes": the view model computes the first
    /// difference at attach time only in that case (decompiled BaselineMarginViewModel).</summary>
    private static async Task StartAAsync(Session s)
    {
        if (s.State is not null) { Say("a review is already on; ending it first"); await DismissAsync(s); }
        var api = s.Api;
        s.Id = api.NewInteractionId();
        s.DiffEvents = 0;
        var clock = Stopwatch.StartNew();
        s.State = await api.GetOrCreateAsync(s.FilePath, s.Id);
        Say($"state created in {clock.ElapsedMilliseconds} ms: {DescribeState(s.State)}");
        Subscribe(s);

        var snapshot = (ITextSnapshot)Call(s.State, "GetBaselineSnapshot");
        var inert = snapshot.TextBuffer;
        Say($"inert baseline buffer: {inert.GetType().Name}, content type {inert.ContentType.TypeName}, {snapshot.Length} chars, "
            + $"read-only: {Read(() => inert.IsReadOnly(new Span(0, snapshot.Length)))}, "
            + $"equals the document: {snapshot.GetText() == s.RealText}");
        var tempPath = (string)Get(s.State, "BaselineFilePath");
        Say($"temp file: {tempPath} (exists: {File.Exists(tempPath)})");

        Replace(inert, s.Wanted);
        // The same encoding Copilot writes the file with when it creates it: a byte-order mark, so the
        // code window reads it back as UTF-8 whatever it contains.
        File.WriteAllText(tempPath, s.Wanted, new UTF8Encoding(true));
        Say($"baseline rewritten: buffer {inert.CurrentSnapshot.Length} chars, file {new FileInfo(tempPath).Length} bytes");

        Call(s.State, "UpdateDiffState", api.NewDiffState(0, 0, 1));
        clock.Restart();
        await api.MarkAsync(s.View, s.Id);
        Say($"MarkForAiChangesAsync returned in {clock.ElapsedMilliseconds} ms");

        await ObserveAsync(s);
        api.MarkCompleted(s.View);
        Say($"after MarkAiChangesCompleted: {DescribeState(api.TryGetState(s.FilePath))}");
    }

    /// <summary>Variant B, Copilot's own recipe for a new file (MarkNewlyCreatedFileAsync): the document
    /// shows the wanted baseline while Mark copies it, then the real text is put back. One undo unit,
    /// then a save of the identical bytes, which clears the dirty flag.</summary>
    private static async Task StartBAsync(Session s)
    {
        if (s.State is not null) { Say("a review is already on; ending it first"); await DismissAsync(s); }
        var api = s.Api;
        var buffer = s.View.TextDataModel.DocumentBuffer;
        s.Id = api.NewInteractionId();
        s.DiffEvents = 0;
        var versionBefore = buffer.CurrentSnapshot.Version.VersionNumber;
        Say($"undo before: {Undo(s)}; buffer version {versionBefore}");

        var history = s.Undo.GetHistory(buffer);
        var clock = Stopwatch.StartNew();
        using (var transaction = history.CreateTransaction("Review with Claude"))
        {
            try
            {
                Replace(buffer, s.Wanted);
                await api.MarkAsync(s.View, s.Id);
                s.State = api.TryGetState(s.FilePath);
                Say($"Mark returned in {clock.ElapsedMilliseconds} ms, state found: {s.State is not null}");
                if (s.State is not null)
                {
                    Subscribe(s);
                    var baseline = ((ITextSnapshot)Call(s.State, "GetBaselineSnapshot")).GetText();
                    Say($"baseline equals the swapped text: {baseline == s.Wanted}; buffer version now {buffer.CurrentSnapshot.Version.VersionNumber}");
                }
                Replace(buffer, s.RealText);
                transaction.Complete();
            }
            catch
            {
                // Never leave the swapped text in the document.
                if (buffer.CurrentSnapshot.GetText() != s.RealText) { Replace(buffer, s.RealText); }
                throw;
            }
        }
        Say($"after the swap: buffer version {buffer.CurrentSnapshot.Version.VersionNumber} "
            + $"(+{buffer.CurrentSnapshot.Version.VersionNumber - versionBefore}), dirty {IsDirty(s)}, undo: {Undo(s)}");
        var saved = await IdeContextService.Instance.SaveDocumentAsync(s.FilePath);
        Say($"saved: {saved}, dirty now {IsDirty(s)}, disk equals the real text: {DiskText(s) == s.RealText}");

        if (s.State is null)
        {
            Result("Mark created no state for the view (see the log above).");
            return;
        }
        await ObserveAsync(s);
        api.MarkCompleted(s.View);
        Say($"after MarkAiChangesCompleted: {DescribeState(api.TryGetState(s.FilePath))}");
    }

    private static async Task DismissAsync(Session s)
    {
        if (s.State is null)
        {
            Say("no review is on");
            return;
        }
        var before = s.DismissEvents;
        Call(s.State, "RequestDismissal");
        var clock = Stopwatch.StartNew();
        await WaitForAsync(() => s.Api.TryGetState(s.FilePath) is null, 3000);
        Say($"RequestDismissal: DismissRequested raised {s.DismissEvents - before}x, state still active: "
            + $"{s.Api.TryGetState(s.FilePath) is not null} (waited {clock.ElapsedMilliseconds} ms)");
        await Task.Delay(400);
        Say($"after it: window Baseline is null: {Read(() => s.Window.Baseline is null)}, "
            + $"{EditorLine(s)}, temp file exists: {File.Exists((string)Get(s.State, "BaselineFilePath"))}, "
            + $"document unchanged: {s.View.TextSnapshot.GetText() == s.RealText}, dirty {IsDirty(s)}");
        Unsubscribe(s);
        s.State = null;
        Result("RequestDismissal ended the review: " + (s.Api.TryGetState(s.FilePath) is null ? "yes" : "NO, the state is still there"));
    }

    private static async Task KeepAllAsync(Session s)
    {
        if (s.State is null)
        {
            Say("no review is on (variant B did not start one?)");
            return;
        }
        var before = s.DismissEvents;
        var source = Enum.Parse(s.State.GetType().GetMethod("KeepAllAsync").GetParameters()[0].ParameterType, "WorkingSet");
        await (Task)Call(s.State, "KeepAllAsync", source, CancellationToken.None);
        await WaitForAsync(() => s.Api.TryGetState(s.FilePath) is null, 3000);
        await Task.Delay(400);
        Say($"KeepAllAsync: DismissRequested raised {s.DismissEvents - before}x, state still active: {s.Api.TryGetState(s.FilePath) is not null}, "
            + $"window Baseline is null: {Read(() => s.Window.Baseline is null)}, document unchanged: "
            + $"{s.View.TextSnapshot.GetText() == s.RealText}, dirty {IsDirty(s)}, disk equals the real text: {DiskText(s) == s.RealText}");
        Unsubscribe(s);
        s.State = null;
        Result("Keep all left the document and the disk alone and ended the review.");
    }

    private static async Task DiscardAllAsync(Session s)
    {
        if (s.State is null)
        {
            Say("no review is on");
            return;
        }
        var before = s.DismissEvents;
        var source = Enum.Parse(s.State.GetType().GetMethod("DiscardAllAsync").GetParameters()[0].ParameterType, "WorkingSet");
        await (Task)Call(s.State, "DiscardAllAsync", source, CancellationToken.None);
        await WaitForAsync(() => s.Api.TryGetState(s.FilePath) is null, 3000);
        await Task.Delay(400);
        var documentText = s.View.TextSnapshot.GetText();
        Say($"DiscardAllAsync: DismissRequested raised {s.DismissEvents - before}x, state still active: {s.Api.TryGetState(s.FilePath) is not null}, "
            + $"document equals the baseline: {documentText == s.Wanted}, dirty {IsDirty(s)}, disk equals the baseline: {DiskText(s) == s.Wanted}");
        Unsubscribe(s);
        s.State = null;
        Result($"Undo all through the API {(documentText == s.Wanted ? "rewrote the document" : "did NOT rewrite the document")} "
            + $"and {(DiskText(s) == s.Wanted ? "saved it" : "did NOT save it")}.");

        Replace(s.View.TextDataModel.DocumentBuffer, s.RealText);
        await IdeContextService.Instance.SaveDocumentAsync(s.FilePath);
        Say($"original text put back: document {s.View.TextSnapshot.GetText() == s.RealText}, disk {DiskText(s) == s.RealText}, dirty {IsDirty(s)}");
    }

    /// <summary>The review's tab was closed and the file opened again: what did Copilot do by itself, and
    /// what does Mark do for the new view?</summary>
    private static async Task ReopenAsync(Session s)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        Say("== Reopen probe: the tab of the review was closed");
        var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        var found = FindActiveDocument(components);
        if (found is null || !string.Equals(found.Value.FilePath, s.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            if (ShellHelpers.Confirm($"The tab of the review ({s.FilePath}) is closed.{Environment.NewLine}{Environment.NewLine}"
                + "Yes: start over (ends the review).  No: open the file again and run the command.", Title))
            {
                await ResetAsync();
                Summary.Add("The probe was reset. Run the command to start over.");
            }
            else
            {
                Summary.Add($"Open {s.FilePath} again, leave it as the active document and run the command.");
            }
            return;
        }
        s.View = found.Value.View;
        s.Window = found.Value.Window;
        Say($"new view: {s.View.GetType().Name}, roles {string.Join(", ", s.View.Roles)}");
        Say($"on its own: state active {s.Api.TryGetState(s.FilePath) is not null}, {EditorLine(s)}");
        await Task.Delay(1200);
        Say($"1.2 s later: state active {s.Api.TryGetState(s.FilePath) is not null}, window Baseline is null: {Read(() => s.Window.Baseline is null)}");
        var onItsOwn = Read(() => s.Window.Baseline is not null);

        if (s.Api.TryGetState(s.FilePath) is null)
        {
            Result("The state did not survive the closed tab; nothing to re-attach.");
            s.State = null;
            return;
        }
        s.State = s.Api.TryGetState(s.FilePath);
        Unsubscribe(s);
        Subscribe(s);
        s.DiffEvents = 0;
        await s.Api.MarkAsync(s.View, s.Id);
        await ObserveAsync(s);
        Result($"Reopened tab: the diff came back by itself: {onItsOwn}; after Mark on the new view: {Read(() => s.Window.Baseline is not null)}.");
    }

    // --- looking at the result ----------------------------------------------------------------------------

    private static async Task ObserveAsync(Session s)
    {
        var clock = Stopwatch.StartNew();
        bool baseline = false, left = false;
        while (clock.ElapsedMilliseconds < 4000 && !(baseline && left && s.DiffEvents >= 2))
        {
            await Task.Delay(50);
            if (!baseline && Read(() => s.Window.Baseline is not null) == "True")
            {
                baseline = true;
                Say($"+{clock.ElapsedMilliseconds} ms: the code window has a baseline");
            }
            if (baseline && !left && LeftText(s.Window) == s.Wanted)
            {
                left = true;
                Say($"+{clock.ElapsedMilliseconds} ms: its left side shows the wanted baseline text");
            }
        }
        await Task.Delay(300);
        Say($"observed for {clock.ElapsedMilliseconds} ms: baseline in the window {baseline}, left side is ours {left}, "
            + $"BaselineDiffChanged raised {s.DiffEvents}x");
        Say(EditorLine(s));
        Say($"state: {DescribeState(s.State)}");
        Say($"panel option on the viewer: {Read(() => s.Window.PrimaryDifferenceViewer?.Options.GetOptionValue<bool>(ActionPanelOption))}; "
            + $"view model wired to the view: {ViewModelKeys(s.View)}");
        Say($"adornments: {Adornments(s)}");
        Say($"document: dirty {IsDirty(s)}, undo: {Undo(s)}, buffer version {s.View.TextDataModel.DocumentBuffer.CurrentSnapshot.Version.VersionNumber}");
        Result(baseline && left
            ? "The code window shows OUR baseline through Copilot's state (see the adornment counts in the log, and the editor)."
            : $"The code window did not show our baseline (baseline {baseline}, left side {left}).");
    }

    private static string EditorLine(Session s)
    {
        // A window out of diff mode has no snapshot difference: that is an answer, not a throw (a debugger set to break on
        // NullReferenceException, the Visual Studio default, would stop the whole instance on it).
        return $"differences {Read(() => s.Window.DifferenceBuffer?.CurrentSnapshotDifference?.LineDifferences?.Differences.Count)}, "
            + $"view mode {Read(() => s.Window.PrimaryDifferenceViewer?.ViewMode)}, left side {Read(() => LeftText(s.Window)?.Length)} chars";
    }

    private static string Adornments(Session s)
    {
        var views = new List<(string Name, ITextView View)> { ("document view", s.View) };
        try
        {
            var viewer = s.Window.PrimaryDifferenceViewer;
            if (viewer is not null)
            {
                void Add(string name, Func<ITextView> get)
                {
                    try
                    {
                        var view = get();
                        if (view is not null && !views.Any(v => ReferenceEquals(v.View, view))) { views.Add((name, view)); }
                    }
                    catch (Exception) { /* that mode has no such view */ }
                }
                Add("inline", () => viewer.InlineView);
                Add("left", () => viewer.LeftView);
                Add("right", () => viewer.RightView);
            }
        }
        catch (Exception ex) { return $"no viewer ({ex.GetType().Name}: {ex.Message})"; }
        return string.Join("; ", views.Select(v =>
            $"{v.Name}: hunk buttons {Elements(v.View, HunkLayer)}, panel {Elements(v.View, PanelLayer)}"));
    }

    private static string Elements(ITextView view, string layer)
    {
        try { return ((IWpfTextView)view).GetAdornmentLayer(layer).Elements.Count.ToString(); }
        catch (Exception ex) { return $"n/a ({ex.GetType().Name})"; }
    }

    private static string ViewModelKeys(IWpfTextView view)
    {
        try
        {
            var keys = view.Properties.PropertyList
                .Select(p => p.Key as Type)
                .Where(t => t is not null && t.Name.IndexOf("Baseline", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(t => t.Name)
                .ToList();
            return keys.Count == 0 ? "none" : string.Join(", ", keys);
        }
        catch (Exception ex) { return $"threw {ex.GetType().Name}"; }
    }

    private static string DescribeState(object state)
    {
        if (state is null) { return "none"; }
        return $"{Read(() => Get(state, "DiffState"))}, AutoRemovalMode {Read(() => Get(state, "AutoRemovalMode"))}, "
            + $"baseline {Read(() => ((ITextSnapshot)Call(state, "GetBaselineSnapshot")).Length)} chars, "
            + $"metadata {Read(() => Get(state, "Metadata"))}";
    }

    private static string LeftText(IVsDifferenceCodeWindow2 window)
        => window?.DifferenceBuffer?.LeftBuffer?.CurrentSnapshot.GetText();

    private static string Undo(Session s)
    {
        return Read(() =>
        {
            var history = s.Undo.GetHistory(s.View.TextDataModel.DocumentBuffer);
            var top = history.UndoStack.FirstOrDefault();
            return $"{history.UndoStack.Count()} unit(s), top '{top?.Description ?? "none"}'";
        });
    }

    private static bool IsDirty(Session s)
    {
        return s.Documents.TryGetTextDocument(s.View.TextDataModel.DocumentBuffer, out var document) && document.IsDirty;
    }

    private static string DiskText(Session s) => File.Exists(s.FilePath) ? File.ReadAllText(s.FilePath) : null;

    // --- events from Copilot's state ----------------------------------------------------------------------

    private static void Subscribe(Session s)
    {
        Unsubscribe(s);
        s.Subscriptions.Add(Hook(s.State, "BaselineDiffChanged", args =>
        {
            Interlocked.Increment(ref s.DiffEvents);
            Say($"event BaselineDiffChanged ({args}) on {(ThreadHelper.CheckAccess() ? "the UI thread" : "a background thread")}");
        }));
        s.Subscriptions.Add(Hook(s.State, "BaselineChanged", args =>
        {
            Interlocked.Increment(ref s.ChangedEvents);
            Say($"event BaselineChanged on {(ThreadHelper.CheckAccess() ? "the UI thread" : "a background thread")}, "
                + $"baseline now {Read(() => ((ITextSnapshot)Call(s.State, "GetBaselineSnapshot")).Length)} chars");
        }));
        s.Subscriptions.Add(Hook(s.State, "DismissRequested", args =>
        {
            Interlocked.Increment(ref s.DismissEvents);
            Say($"event DismissRequested on {(ThreadHelper.CheckAccess() ? "the UI thread" : "a background thread")}");
        }));
    }

    private static void Unsubscribe(Session s)
    {
        foreach (var subscription in s.Subscriptions)
        {
            try { subscription.Dispose(); }
            catch (Exception ex) { OutputWindowLogger.Global.Warn($"{Tag} could not unsubscribe: {ex.Message}"); }
        }
        s.Subscriptions.Clear();
    }

    private sealed class Relay<T>
    {
        private readonly Action<object> _on;
        public Relay(Action<object> on) { _on = on; }
        public void Handle(object sender, T args) => _on(args);
    }

    private sealed class Unhook : IDisposable
    {
        private readonly EventInfo _event;
        private readonly object _target;
        private readonly Delegate _handler;
        public Unhook(EventInfo ev, object target, Delegate handler) { _event = ev; _target = target; _handler = handler; }
        public void Dispose() => _event.RemoveEventHandler(_target, _handler);
    }

    /// <summary>Subscribes to an event whose argument type belongs to Copilot, so no handler can be
    /// written against it: a generic relay with the argument type of the event's delegate.</summary>
    private static IDisposable Hook(object target, string eventName, Action<object> on)
    {
        var ev = target.GetType().GetEvent(eventName);
        var argumentType = ev.EventHandlerType.GetMethod("Invoke").GetParameters()[1].ParameterType;
        var relayType = typeof(Relay<>).MakeGenericType(argumentType);
        var relay = Activator.CreateInstance(relayType, on);
        var handler = Delegate.CreateDelegate(ev.EventHandlerType, relay, relayType.GetMethod("Handle"));
        ev.AddEventHandler(target, handler);
        return new Unhook(ev, target, handler);
    }

    // --- Copilot's parts, by reflection -------------------------------------------------------------------

    private sealed class Api
    {
        public object Marker;
        public object Registry;
        public MethodInfo Mark;
        public MethodInfo Completed;
        public MethodInfo TryGet;
        public MethodInfo GetOrCreate;
        public MethodInfo Release;
        public Type IdType;
        public Type StateType;

        public static Api Resolve(IComponentModel components)
        {
            var marker = ExportOf(components, MarkerContract);
            var registry = ExportOf(components, RegistryContract);
            if (marker is null || registry is null) { return null; }

            var markerType = marker.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == MarkerContract);
            var registryType = registry.GetType().GetInterfaces().FirstOrDefault(i => i.FullName == RegistryContract);
            if (markerType is null || registryType is null)
            {
                Say($"the instances do not implement their contract interface (marker {markerType is not null}, registry {registryType is not null})");
                return null;
            }
            var api = new Api
            {
                Marker = marker,
                Registry = registry,
                Mark = markerType.GetMethod("MarkForAiChangesAsync"),
                Completed = markerType.GetMethod("MarkAiChangesCompleted"),
                TryGet = registryType.GetMethod("TryGetActiveBaselineState"),
                GetOrCreate = registryType.GetMethod("GetOrCreateBaselineStateAsync"),
                Release = registryType.GetMethod("ReleaseBaselineStateAsync"),
            };
            var missing = new[]
            {
                ("MarkForAiChangesAsync", api.Mark), ("MarkAiChangesCompleted", api.Completed),
                ("TryGetActiveBaselineState", api.TryGet), ("GetOrCreateBaselineStateAsync", api.GetOrCreate),
                ("ReleaseBaselineStateAsync", api.Release),
            }.Where(m => m.Item2 is null).Select(m => m.Item1).ToList();
            if (missing.Count > 0)
            {
                Say("not found on the contracts: " + string.Join(", ", missing));
                return null;
            }
            api.IdType = api.Mark.GetParameters()[1].ParameterType;
            api.StateType = api.TryGet.GetParameters()[1].ParameterType.GetElementType();
            Say($"interaction id type: {api.IdType.FullName} in {api.IdType.Assembly.GetName().Name} {api.IdType.Assembly.GetName().Version}");
            Say($"state type: {api.StateType.FullName} in {api.StateType.Assembly.GetName().Name} {api.StateType.Assembly.GetName().Version}");
            return api;
        }

        private static object ExportOf(IComponentModel components, string contract)
        {
            var definition = new ContractBasedImportDefinition(
                contract, null, null, ImportCardinality.ZeroOrMore, false, false, CreationPolicy.Any);
            List<Export> exports;
            try { exports = components.DefaultExportProvider.GetExports(definition).ToList(); }
            catch (Exception ex)
            {
                Say($"{contract}: asking for the exports threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            Say($"{contract}: {exports.Count} export(s)");
            if (exports.Count == 0) { return null; }
            try
            {
                var value = exports[0].Value;
                var assembly = value.GetType().Assembly.GetName();
                Say($"  instance: {value.GetType().FullName}, {assembly.Name} {assembly.Version}");
                return value;
            }
            catch (Exception ex)
            {
                Say($"  creating it threw {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>A throwaway id for our own "conversation": a client id, a session and fresh
        /// correlation and message ids. Copilot only records it (telemetry and bookkeeping).</summary>
        public object NewInteractionId()
        {
            var sessionType = IdType.GetProperty("SessionId").PropertyType;
            var clientType = sessionType.GetProperty("ClientId").PropertyType;
            var client = Activator.CreateInstance(clientType, ClientName);
            var session = Activator.CreateInstance(sessionType, client);
            return Activator.CreateInstance(IdType, session);
        }

        public object NewDiffState(int additions, int deletions, int changes)
        {
            var type = StateType.GetProperty("DiffState").PropertyType;
            return Activator.CreateInstance(type, additions, deletions, changes);
        }

        public object TryGetState(string path)
        {
            var args = new object[] { path, null };
            return (bool)Invoke(TryGet, Registry, args) ? args[1] : null;
        }

        public async System.Threading.Tasks.Task<object> GetOrCreateAsync(string path, object id)
        {
            var task = (System.Threading.Tasks.Task)Invoke(GetOrCreate, Registry, path, id, CancellationToken.None);
            await task;
            return task.GetType().GetProperty("Result").GetValue(task);
        }

        public async Task ReleaseAsync(string path)
            => await (System.Threading.Tasks.Task)Invoke(Release, Registry, path, CancellationToken.None);

        public async Task MarkAsync(ITextView view, object id)
            => await (System.Threading.Tasks.Task)Invoke(Mark, Marker, view, id, CancellationToken.None);

        public void MarkCompleted(ITextView view) => Invoke(Completed, Marker, view, CancellationToken.None);
    }

    private static object Invoke(MethodInfo method, object target, params object[] args)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static object Get(object target, string name) => target.GetType().GetProperty(name).GetValue(target);

    private static object Call(object target, string name, params object[] args)
        => Invoke(target.GetType().GetMethod(name), target, args);

    // --- small helpers ------------------------------------------------------------------------------------

    private static void Replace(ITextBuffer buffer, string text)
    {
        using (var edit = buffer.CreateEdit())
        {
            edit.Replace(0, buffer.CurrentSnapshot.Length, text);
            edit.Apply();
            if (edit.Canceled) { throw new InvalidOperationException("the edit of the buffer was refused"); }
        }
    }

    /// <summary>The document without its first line (so one difference is an added line), and with 12 lines
    /// or more also a marked middle line (a second one, a changed line).</summary>
    private static string SyntheticBaseline(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count < 2) { return string.Empty; }
        lines.RemoveAt(0);
        if (lines.Count >= 12)
        {
            var middle = lines.Count / 2;
            lines[middle] = "CV4VS-BASELINE " + lines[middle];
        }
        return string.Concat(lines);
    }

    /// <summary>Lines with their own terminators, so that joining them gives the text back.</summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') { continue; }
            lines.Add(text.Substring(start, i - start + 1));
            start = i + 1;
        }
        if (start < text.Length) { lines.Add(text.Substring(start)); }
        return lines;
    }

    private static async Task WaitForAsync(Func<bool> condition, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < milliseconds) { await Task.Delay(50); }
    }

    /// <summary>One read that reports a throw as text: half of what is probed is whether the member can be
    /// touched at all.</summary>
    private static string Read<T>(Func<T> read)
    {
        try { return read()?.ToString() ?? "null"; }
        catch (Exception ex) { return $"threw {ex.GetType().Name}: {ex.Message}"; }
    }

    private static void Say(string line)
    {
        lock (Gate) { Log.AppendLine(line); }
        OutputWindowLogger.Global.Info($"{Tag} {line}");
    }

    private static void Result(string line)
    {
        Say("RESULT: " + line);
        Summary.Add(line);
    }

    private static void End()
    {
        if (_session is not null) { Unsubscribe(_session); }
        _session = null;
        _next = 0;
    }

    /// <summary>After a failure: end a review the probe left on, best effort, and start over. A state that
    /// never got a view model has nobody to react to the dismissal, so it is released through the registry
    /// when it is still there afterwards. The document is only touched when it still holds the swapped
    /// text the probe put there.</summary>
    private static async Task ResetAsync()
    {
        var session = _session;
        try
        {
            if (session is not null && session.Api.TryGetState(session.FilePath) is not null)
            {
                if (session.State is not null) { Call(session.State, "RequestDismissal"); }
                await WaitForAsync(() => session.Api.TryGetState(session.FilePath) is null, 1500);
                if (session.Api.TryGetState(session.FilePath) is not null) { await session.Api.ReleaseAsync(session.FilePath); }
            }
            if (session is not null && !session.View.IsClosed && session.Wanted is not null
                && session.View.TextDataModel.DocumentBuffer.CurrentSnapshot.GetText() == session.Wanted)
            {
                Replace(session.View.TextDataModel.DocumentBuffer, session.RealText);
                await IdeContextService.Instance.SaveDocumentAsync(session.FilePath);
                Say("the original text was put back");
            }
        }
        catch (Exception ex) { OutputWindowLogger.Global.Warn($"{Tag} could not end the review while resetting: {ex.Message}"); }
        End();
    }

    private static void Publish()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string report;
        lock (Gate) { report = Log.ToString(); }
        try
        {
            var folder = Path.Combine(AppPaths.BaselinesFolder, "spike");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "copilot-report.txt"), report, Encoding.UTF8);
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("CopilotSpike.Publish", ex); }

        try { System.Windows.Clipboard.SetText(report); }
        catch (Exception ex) { OutputWindowLogger.Global.Warn($"{Tag} clipboard unavailable: {ex.Message}"); }

        ShellHelpers.ShowMessage(
            string.Join(Environment.NewLine + Environment.NewLine, Summary) + Environment.NewLine + Environment.NewLine
            + "(the whole log is on the clipboard)",
            Title,
            warning: false);
    }
}

#endif
