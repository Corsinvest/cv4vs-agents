/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Ide;

// Rename via the per-language IEditorInlineRenameService — the same service the editor's inline
// rename uses — but we only call its non-interactive compute methods (GetRenameInfoAsync →
// FindRenameLocationsAsync → GetReplacementsAsync) and apply the resulting Solution ourselves,
// so no UI is shown.
internal sealed partial class IdeNavigationService
{
    /// <summary>A file touched by a rename, with how many edits it received.</summary>
    public sealed class RenameChange
    {
        public string FilePath { get; set; }
        public int Count { get; set; }
    }

    public sealed class RenameResult
    {
        /// <summary>False when the language/VS can't be queried this way (degrade to grep+edit).</summary>
        public bool Supported { get; set; }
        /// <summary>True only if the rename was computed AND written to the solution.</summary>
        public bool Applied { get; set; }
        public string NewName { get; set; }
        public RenameChange[] ChangedFiles { get; set; } = [];
        /// <summary>Where the rename couldn't be applied cleanly (unresolved conflicts).
        /// Populated only when Applied is false because of conflicts.</summary>
        public NavLocation[] Conflicts { get; set; } = [];
        public string Reason { get; set; }
    }

    private bool _renameProbed;
    private bool _renameAvailable;
    private Type _inlineRenameServiceType;           // internal IEditorInlineRenameService
    private MethodInfo _getRenameInfoAsync;          // on the service
    private object _symbolRenameOptionsDefault;      // SymbolRenameOptions (default struct)

    /// <summary>Resolve the rename types/members. SymbolRenameOptions is a struct of flags
    /// (overloads/strings/comments/file) — we use its default (all off).</summary>
    private bool EnsureRenameProbed()
    {
        if (_renameProbed) { return _renameAvailable; }
        _renameProbed = true;
        if (!EnsureProbed()) { return false; }
        try
        {
            string step = "IEditorInlineRenameService";
            _inlineRenameServiceType = VsReflection.FindType("Microsoft.CodeAnalysis.Editor.IEditorInlineRenameService");
            if (_inlineRenameServiceType == null) { return ProbeFailed(step); }

            step = "GetRenameInfoAsync";
            _getRenameInfoAsync = _inlineRenameServiceType.GetMethods()
                .FirstOrDefault(m => m.Name == "GetRenameInfoAsync" && m.GetParameters().Length == 3);
            if (_getRenameInfoAsync == null) { return ProbeFailed(step); }

            step = "SymbolRenameOptions";
            var optionsType = VsReflection.FindType("Microsoft.CodeAnalysis.Rename.SymbolRenameOptions");
            if (optionsType == null) { return ProbeFailed(step); }
            _symbolRenameOptionsDefault = VsReflection.CreateInstance(optionsType); // struct, all flags false

            _renameAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.EnsureRenameProbed", ex);
            return false;
        }
    }

    /// <summary>Rename the symbol named <paramref name="symbolName"/> on 1-based
    /// <paramref name="line"/> of <paramref name="filePath"/> to <paramref name="newName"/>,
    /// across the whole solution. Atomic: if there are unresolved conflicts nothing is
    /// written. Multi-language; never throws (degrades to Supported=false). The file must be
    /// in the open solution.</summary>
    public async Task<RenameResult> RenameSymbolAsync(
        string filePath, int line, string symbolName, string newName, CancellationToken ct)
    {
        if (!EnsureRenameProbed())
        {
            return new RenameResult { Supported = false, Reason = "Rename not available in this Visual Studio." };
        }
        if (string.IsNullOrWhiteSpace(newName))
        {
            return new RenameResult { Supported = true, Applied = false, Reason = "New name is empty." };
        }

        try
        {
            // Open the starting file FIRST, before anything reads the solution. Opening moves
            // CurrentSolution on, and every object below — the document, the rename info, the
            // location set — is bound to the solution it was made from: an open half way through
            // leaves them all pointing at the previous one, and TryApplyChanges then refuses the
            // whole thing. Two earlier attempts moved the open between the compute steps and got
            // "workspace rejected the changes" both times; the position that works is before them.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            OpenFileForEdit(filePath);

            var solution = VsReflection.GetProp(_workspace, "CurrentSolution");
            var document = ResolveDocument(filePath);
            if (document == null) { return new RenameResult { Supported = false, Reason = "No language document for this file (language not supported)." }; }

            var offset = await ResolveOffsetAsync(document, line, symbolName, ct).ConfigureAwait(false);
            if (offset < 0) { return new RenameResult { Supported = true, Applied = false, Reason = "Symbol not found on that line." }; }

            var service = GetLanguageService(document, _inlineRenameServiceType);
            if (service == null) { return new RenameResult { Supported = false, Reason = "This language has no rename service." }; }

            // 1) GetRenameInfoAsync(document, position, ct) → IInlineRenameInfo.
            var infoTask = (Task)_getRenameInfoAsync.Invoke(service, [document, offset, ct]);
            await infoTask.ConfigureAwait(false);
            var info = VsReflection.GetProp(infoTask, "Result");
            if (info == null) { return new RenameResult { Supported = true, Applied = false, Reason = "No rename info at that position." }; }

            var canRename = VsReflection.GetProp<bool>(info, "CanRename");
            if (!canRename)
            {
                var msg = VsReflection.GetPropOrNull(info, "LocalizedErrorMessage") as string;
                return new RenameResult { Supported = true, Applied = false, Reason = msg ?? "This symbol can't be renamed." };
            }

            // 2) FindRenameLocationsAsync(options, ct) → IInlineRenameLocationSet
            var locSet = await VsReflection.InvokeAsync(info, "FindRenameLocationsAsync", _symbolRenameOptionsDefault, ct);

            // 3) GetReplacementsAsync(newName, options, ct) → IInlineRenameReplacementInfo
            var repl = await VsReflection.InvokeAsync(locSet, "GetReplacementsAsync", newName, _symbolRenameOptionsDefault, ct);

            var valid = VsReflection.GetProp<bool>(repl, "ReplacementTextValid");
            if (!valid) { return new RenameResult { Supported = true, Applied = false, Reason = $"'{newName}' is not a valid name here." }; }

            // Atomic guard: collect any unresolved conflicts; if there are any, apply nothing.
            var docIdsToChange = ((IEnumerable)VsReflection.GetProp(repl, "DocumentIds")).Cast<object>().ToList();
            var changes = new System.Collections.Generic.List<RenameChange>();
            var conflicts = new System.Collections.Generic.List<NavLocation>();
            foreach (var changedDocId in docIdsToChange)
            {
                var replacements = ((IEnumerable)VsReflection.Invoke(repl, "GetReplacements", changedDocId)).Cast<object>().ToList();
                var changedDoc = VsReflection.Invoke(solution, "GetDocument", [changedDocId.GetType()], [changedDocId]);
                var path = VsReflection.GetPropOrNull(changedDoc, "FilePath") as string;

                object docText = null; // loaded lazily, only if this doc has a conflict
                foreach (var r in replacements)
                {
                    var kind = VsReflection.GetProp(r, "Kind").ToString();
                    if (kind != "UnresolvedConflict") { continue; }
                    if (docText == null && changedDoc != null) { docText = await GetTextAsync(changedDoc, ct).ConfigureAwait(false); }
                    // OriginalSpan is in the OLD document — turn its start into a 1-based line.
                    var origSpan = VsReflection.GetProp(r, "OriginalSpan");
                    var start = VsReflection.GetProp<int>(origSpan, "Start");
                    conflicts.Add(new NavLocation { FilePath = path, Line = docText != null ? OffsetToLine(docText, start) : 0 });
                }
                changes.Add(new RenameChange { FilePath = path, Count = replacements.Count });
            }

            if (conflicts.Count > 0)
            {
                return new RenameResult
                {
                    Supported = true,
                    Applied = false,
                    Conflicts = [.. conflicts],
                    Reason = $"Rename would cause {conflicts.Count} unresolved conflict(s) — not applied.",
                };
            }

            // 4) Apply the new Solution to the workspace (writes the edits).
            // TryApplyChanges must run on the VS UI thread; the rest of the compute is fine on a
            // background thread, so we switch only for the apply.
            var newSolution = VsReflection.GetProp(repl, "NewSolution");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            var applied = (bool)VsReflection.Invoke(_workspace, "TryApplyChanges",
                [newSolution.GetType()], [newSolution]);
            return !applied
                ? new RenameResult { Supported = true, Applied = false, Reason = "Workspace rejected the changes (files modified meanwhile?)." }
                : new RenameResult { Supported = true, Applied = true, NewName = newName, ChangedFiles = [.. changes] };
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IdeNavigationService.RenameSymbolAsync", ex);
            return new RenameResult { Supported = false, Reason = "Rename failed (internal API changed?)." };
        }
    }

    /// <summary>Open the file the rename starts from, so TryApplyChanges edits a buffer rather than
    /// the file.
    /// <para>The workspace applies a change to a CLOSED document by writing it straight to disk —
    /// no dirty buffer, and nothing for Ctrl+Z to undo. Measured: a rename of a symbol in a file
    /// nobody had open reported applied=true with the file already rewritten, while the tool
    /// promised the edit was pending until document_save. Renaming what is open behaved as
    /// described, which is why it went unnoticed: the same command did two different things
    /// depending on which tabs happened to be there.</para>
    /// <para>Opening first makes it one thing. Failing to open is not worth abandoning a rename
    /// that has not been computed yet — the file takes the old behaviour and is written directly,
    /// which is what happened to every file before this. Files the rename reaches BEYOND this one
    /// are not opened: their ids are only known once the solution has been read, and opening then
    /// is what the ordering above exists to avoid. A cross-file rename still writes those to
    /// disk.</para>
    /// <para>The file is activated, which is visible and not wanted. A document that is open but
    /// never activated is not synchronised into the RDT and TryApplyChanges refuses the solution
    /// over it — dotnet/roslyn#25425, closed as not planned, so the workaround stays ours.</para>
    /// <para>By path rather than by DocumentId: this runs before the solution has been read, so
    /// there is no document to have an id yet, and that is the whole point of the ordering.</para></summary>
    private static void OpenFileForEdit(string filePath)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var path = PathHelpers.FromFileUri(filePath);
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) { return; }

            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path,
                Microsoft.VisualStudio.VSConstants.LOGVIEWID.TextView_guid, out _, out _, out var frame);
            frame?.Show();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.Warn(
                $"[nav] rename: could not open '{filePath}' before computing the rename — " +
                $"the change may be written straight to disk. {ex.Message}");
        }
    }
}
