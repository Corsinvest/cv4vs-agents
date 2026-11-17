/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

// IVsDifferenceCodeWindow2 is marked provisional ([Obsolete]) in the SDK. Whether it works on a
// given Visual Studio is exactly what this probe exists to find out.
#pragma warning disable CS0618

using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Primitives;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Task = System.Threading.Tasks.Task;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>
/// Debug-build probe for issue #247 (review what Claude changed). It answers one question before
/// anything is built on it: can the document's own editor window be switched into a difference
/// view against a baseline buffer on THIS Visual Studio?
/// <para>First run, on the active document: reports what the code window offers, hands it a
/// baseline (the document's text with one extra line in front, so exactly one difference is
/// expected), then reports whether the view switched and whether it can change mode. Second run:
/// takes the baseline down again.</para>
/// <para>The report goes to a file, the clipboard and a message box, not to the log: the log is
/// off by default and this has to be readable on a first run.</para>
/// </summary>
internal static class DiffSpike
{
    private const string Tag = "[changes:spike]";
    private const string ViewModeOption = "Diff/View/ViewMode";
    private const string ActionPanelOption = "ubidiff/isactionpanelactive";
    private const string MultiDiffContract = "Microsoft.VisualStudio.Text.Differencing.IMultiDiffService";

    // What the first run set up, kept for the second run to take down. One probe at a time is the
    // point, so it is not per document.
    private static Probe _live;

    private sealed class Probe
    {
        public IVsDifferenceCodeWindow2 Window;
        public IVsInvisibleEditor Editor;
        public string TempPath;
    }

    public static void Run()
        => ThreadHelper.JoinableTaskFactory.RunAsync(RunAsync).FileAndForget("DiffSpike.Run");

    private static async Task RunAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var report = new StringBuilder();
        void Say(string line)
        {
            report.AppendLine(line);
            OutputWindowLogger.Global.Info($"{Tag} {line}");
        }

        try
        {
            if (_live is not null) { TakeDown(Say); }
            else { await ProbeAsync(Say); }
        }
        catch (Exception ex)
        {
            Say($"FAILED: {ex.GetType().Name}: {ex.Message}");
            OutputWindowLogger.Global.LogException("DiffSpike.Run", ex);
        }
        Publish(report.ToString());
    }

    private static async Task ProbeAsync(Action<string> say)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        say($"Visual Studio: {Process.GetCurrentProcess().MainModule?.FileVersionInfo.FileVersion}");

        var components = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
        ReportMultiDiff(components, say);

        var monitor = (IVsMonitorSelection)Package.GetGlobalService(typeof(SVsShellMonitorSelection));
        monitor.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out var frameObject);
        if (frameObject is not IVsWindowFrame frame)
        {
            say("No active document: open a code file and run the command again.");
            return;
        }
        frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out var monikerObject);
        var moniker = monikerObject as string;
        say($"Document: {moniker}");

        var adapters = components.GetService<IVsEditorAdaptersFactoryService>();
        var vsView = VsShellUtilities.GetTextView(frame);
        var view = vsView is null ? null : adapters.GetWpfTextView(vsView);
        if (view is null)
        {
            say("The active document has no text view: not a code editor.");
            return;
        }
        say($"View roles: {string.Join(", ", view.Roles)}");
        say($"TextViewModel: {view.TextViewModel?.GetType().FullName} (IDifferenceTextViewModel: {view.TextViewModel is IDifferenceTextViewModel})");

        // The two places a code window can be found. Copilot reads the view's property bag; the
        // frame's DocView is the older, documented route. Reporting both says whether they agree.
        view.Properties.TryGetProperty(typeof(IVsCodeWindow), out IVsCodeWindow fromProperties);
        var fromDocView = frame.GetProperty((int)__VSFPROPID.VSFPROPID_DocView, out var docView) == VSConstants.S_OK
            ? docView as IVsCodeWindow
            : null;
        say($"IVsCodeWindow from view.Properties: {fromProperties?.GetType().FullName ?? "none"}");
        say($"IVsCodeWindow from frame DocView: {fromDocView?.GetType().FullName ?? "none"}");
        say($"Same object: {ReferenceEquals(fromProperties, fromDocView)}");

        var window = (fromProperties ?? fromDocView) as IVsDifferenceCodeWindow2;
        say($"Implements IVsDifferenceCodeWindow2: {window is not null}");
        if (window is null)
        {
            say("VERDICT: no difference code window here. Stage 3 would only have the comparison window.");
            return;
        }

        say($"SupportsDiff: {Read(() => window.SupportsDiff)}");
        say($"Baseline is null: {Read(() => window.Baseline is null)}");
        say($"DifferenceBuffer present: {Read(() => window.DifferenceBuffer is not null)}");
        say($"PrimaryDifferenceViewer present: {Read(() => window.PrimaryDifferenceViewer is not null)}");
        if (!window.SupportsDiff)
        {
            say("VERDICT: SupportsDiff is false. Stage 3 would only have the comparison window.");
            return;
        }
        if (window.Baseline is not null)
        {
            say("VERDICT: a baseline is already set (Copilot or a Git diff owns this window). "
                + "Stage 3 must fall back to the comparison window for this file.");
            return;
        }

        var text = view.TextSnapshot.GetText();
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var folder = Path.Combine(AppPaths.BaselinesFolder, "spike");
        Directory.CreateDirectory(folder);
        // The tilde keeps the extension, so the editor picks the same language service and the
        // baseline side is coloured like the document.
        var temp = Path.Combine(folder, "~" + (string.IsNullOrEmpty(moniker) ? "document.txt" : Path.GetFileName(moniker)));
        File.WriteAllText(temp, "cv4vs spike baseline line" + eol + text, new UTF8Encoding(false));

        var manager = (IVsInvisibleEditorManager)Package.GetGlobalService(typeof(SVsInvisibleEditorManager));
        ErrorHandler.ThrowOnFailure(manager.RegisterInvisibleEditor(
            temp, null, (uint)_EDITORREGFLAGS.RIEF_ENABLECACHING, null, out var editor));
        try
        {
            var iid = typeof(IVsTextLines).GUID;
            ErrorHandler.ThrowOnFailure(editor.GetDocData(1, ref iid, out var pointer));
            IVsTextLines lines;
            try { lines = (IVsTextLines)Marshal.GetObjectForIUnknown(pointer); }
            finally { Marshal.Release(pointer); }

            window.Baseline = (IVsTextBuffer)lines;
        }
        catch
        {
            Marshal.ReleaseComObject(editor);
            TryDelete(temp);
            throw;
        }
        _live = new Probe { Window = window, Editor = editor, TempPath = temp };
        say("Baseline assigned. One difference expected (the extra first line).");

        await Task.Delay(500);
        ReportDifferences(window, say);

        var viewer = window.PrimaryDifferenceViewer;
        if (viewer is null)
        {
            say("PrimaryDifferenceViewer is null after the assignment: the view mode cannot be tested.");
        }
        else
        {
            say($"Viewer: {viewer.GetType().FullName}");
            say($"ViewMode option key (SDK constant): {Read(() => DifferenceViewerOptions.ViewModeId.Name)}");
            say($"View mode now: {Read(() => viewer.ViewMode)}");
            foreach (var mode in new[] { DifferenceViewMode.SideBySide, DifferenceViewMode.Inline })
            {
                try
                {
                    viewer.Options.SetOptionValue(ViewModeOption, mode);
                    await Task.Delay(500);
                    say($"After setting {mode}: ViewMode={Read(() => viewer.ViewMode)}, "
                        + $"left={Read(() => viewer.LeftView is not null)}, right={Read(() => viewer.RightView is not null)}, "
                        + $"inline={Read(() => viewer.InlineView is not null)}");
                }
                catch (Exception ex)
                {
                    say($"Setting {mode} threw {ex.GetType().Name}: {ex.Message}");
                }
            }
            say($"Action panel option defined: {Read(() => viewer.Options.IsOptionDefined(ActionPanelOption, false))} "
                + "(read only: it belongs to Copilot's single export, so it is never set here)");
        }

        say("Run the command again to take the baseline down.");
    }

    private static void ReportDifferences(IVsDifferenceCodeWindow2 window, Action<string> say)
    {
        say($"Baseline set: {Read(() => window.Baseline is not null)}");
        say($"Differences rendered: {Read(() => window.DifferenceBuffer.CurrentSnapshotDifference.LineDifferences.Differences.Count)}");
        say($"DisplayDifferences: {Read(() => (window.PrimaryDifferenceViewer as IDifferenceViewer3)?.DisplayDifferences)}");
    }

    private static void ReportMultiDiff(IComponentModel components, Action<string> say)
    {
        var type = Type.GetType(MultiDiffContract + ", Microsoft.VisualStudio.Text.UI.Wpf");
        say($"IMultiDiffService type: {type?.AssemblyQualifiedName ?? "absent"}");
        var definition = new ContractBasedImportDefinition(
            MultiDiffContract, null, null, ImportCardinality.ZeroOrMore, false, false, CreationPolicy.Any);
        say($"IMultiDiffService exports: {Read(() => components.DefaultExportProvider.GetExports(definition).Count())}");
    }

    private static void TakeDown(Action<string> say)
    {
        var live = _live;
        _live = null;
        try
        {
            live.Window.Baseline = null;
            say("Baseline cleared.");
        }
        catch (Exception ex)
        {
            say($"Clearing the baseline threw {ex.GetType().Name}: {ex.Message}");
        }
        try { Marshal.ReleaseComObject(live.Editor); }
        catch (Exception ex) { say($"Releasing the invisible editor threw {ex.GetType().Name}: {ex.Message}"); }
        TryDelete(live.TempPath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { OutputWindowLogger.Global.Warn($"{Tag} could not delete {path}: {ex.Message}"); }
    }

    /// <summary>One property read that reports a throw as text: half of what is being probed is
    /// whether the member can be touched at all.</summary>
    private static string Read<T>(Func<T> read)
    {
        try { return read()?.ToString() ?? "null"; }
        catch (Exception ex) { return $"threw {ex.GetType().Name}: {ex.Message}"; }
    }

    private static void Publish(string report)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var folder = Path.Combine(AppPaths.BaselinesFolder, "spike");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "report.txt"), report, Encoding.UTF8);
        }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("DiffSpike.Publish", ex); }

        try { System.Windows.Clipboard.SetText(report); }
        catch (Exception ex) { OutputWindowLogger.Global.Warn($"{Tag} clipboard unavailable: {ex.Message}"); }

        ShellHelpers.ShowMessage(report + Environment.NewLine + "(also copied to the clipboard)",
            "cv4vs Agents: diff spike", warning: false);
    }
}
