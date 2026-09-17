/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>Per-view selection tracking, stored on the view itself so its lifetime is the
/// view's. One instance per <see cref="IWpfTextView"/>; the view's own events drive it, so
/// nothing has to re-attach when focus moves between documents.</summary>
internal sealed class EditorSelectionState
{
    // Key type for the view's property bag: a private type cannot collide with any other
    // extension's entry.
    private sealed class Key { }

    private readonly IMultiSelectionBroker _broker;
    private readonly Action<EditorSelectionState> _onChanged;
    private bool _detached;

    internal IWpfTextView View { get; }
    internal ITextDocument Document { get; }

    private EditorSelectionState(IWpfTextView view, ITextDocument document, Action<EditorSelectionState> onChanged)
    {
        View = view;
        Document = document;
        _onChanged = onChanged;
        _broker = view.GetMultiSelectionBroker();

        // MultiSelectionSessionChanged is the multi-caret-aware signal; ITextSelection's
        // SelectionChanged only reports the legacy single selection.
        if (_broker != null) { _broker.MultiSelectionSessionChanged += OnSelectionChanged; }
        view.GotAggregateFocus += OnGotFocus;
        view.Closed += OnClosed;
    }

    /// <summary>Attach to the view, or return the instance already attached. Null when the view
    /// has no text document (a projection without a backing file, a non-document view).</summary>
    internal static EditorSelectionState GetOrCreate(IWpfTextView view,
                                                     ITextDocumentFactoryService docFactory,
                                                     Action<EditorSelectionState> onChanged)
    {
        if (view == null || docFactory == null) { return null; }

        // DocumentBuffer, not TextBuffer: in a projected view (Razor, .vue) TextBuffer is the
        // projection and carries no ITextDocument.
        // The lookup happens before GetOrCreateSingletonProperty because that one caches on key
        // presence, not on value: a null from the factory would bind the key forever and the view
        // could never be tracked, not even once its document resolves.
        if (!docFactory.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var doc)) { return null; }

        return view.Properties.GetOrCreateSingletonProperty(
            typeof(Key), () => new EditorSelectionState(view, doc, onChanged));
    }

    /// <summary>The primary selection's span on the view's current snapshot — the primary one, so
    /// that a second caret elsewhere in the file does not stretch it across everything in between.
    /// False when the broker is unavailable or the view is gone.</summary>
    internal bool TryGetSpan(out SnapshotSpan span)
    {
        span = default;
        if (_detached || _broker == null || View.IsClosed) { return false; }

        var primary = _broker.PrimarySelection;
        span = new SnapshotSpan(primary.Start.Position, primary.End.Position);
        return true;
    }

    /// <summary>Stop listening. Idempotent, and the flag outlives the unsubscribe: the instance
    /// stays in the view's property bag, so anything that meets this view again gets it back and
    /// must find it inert rather than half-attached.</summary>
    internal void Detach()
    {
        if (_detached) { return; }
        _detached = true;
        try
        {
            if (_broker != null) { _broker.MultiSelectionSessionChanged -= OnSelectionChanged; }
            View.GotAggregateFocus -= OnGotFocus;
            View.Closed -= OnClosed;
        }
        catch (Exception ex)
        {
            // Unsubscribing from a view being torn down: the events are gone either way, so
            // tracking is unaffected — but a throw here would mean the teardown is not what we
            // think it is.
            OutputWindowLogger.Global.Warn($"[ide-context] detach from a torn-down view: {ex.Message}");
        }
    }

    // The firing state goes with the event: the listener owns several views and must know which
    // one spoke, or it answers for the wrong file.
    private void OnSelectionChanged(object sender, EventArgs e) => _onChanged?.Invoke(this);
    private void OnGotFocus(object sender, EventArgs e) => _onChanged?.Invoke(this);

    private void OnClosed(object sender, EventArgs e)
    {
        Detach();
        _onChanged?.Invoke(this);
    }
}
