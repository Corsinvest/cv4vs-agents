/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;

namespace Corsinvest.VisualStudio.Agents.Ide;

/// <summary>Per-view selection tracking, stored on the view itself so its lifetime is the
/// view's and nothing has to re-attach when focus moves.</summary>
internal sealed class EditorSelectionState
{
    // Property-bag key: a private type cannot collide with another extension's entry.
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

    /// <summary>Null when the view has no text document: a projection without a backing file, or
    /// a non-document view.</summary>
    internal static EditorSelectionState GetOrCreate(IWpfTextView view,
                                                     ITextDocumentFactoryService docFactory,
                                                     Action<EditorSelectionState> onChanged)
    {
        if (view == null || docFactory == null) { return null; }

        // DocumentBuffer, not TextBuffer: in a projected view (Razor, .vue) TextBuffer is the
        // projection and carries no ITextDocument.
        // Before GetOrCreateSingletonProperty, which caches on key presence, not value: a null
        // would bind the key forever and the view could never be tracked.
        if (!docFactory.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var doc)) { return null; }

        return view.Properties.GetOrCreateSingletonProperty(
            typeof(Key), () => new EditorSelectionState(view, doc, onChanged));
    }

    /// <summary>The primary selection's span, so a second caret elsewhere in the file does not
    /// stretch it across everything in between.</summary>
    internal bool TryGetSpan(out SnapshotSpan span)
    {
        span = default;
        if (_detached || _broker == null || View.IsClosed) { return false; }

        var primary = _broker.PrimarySelection;
        span = new SnapshotSpan(primary.Start.Position, primary.End.Position);
        return true;
    }

    /// <summary>Stop listening. The flag outlives the unsubscribe because the instance stays in
    /// the view's property bag: whoever meets this view again must find it inert.</summary>
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
            // Nothing to recover — the events are gone either way — but a throw here means the
            // teardown is not what we think it is.
            OutputWindowLogger.Global.Warn($"[ide-context] detach from a torn-down view: {ex.Message}");
        }
    }

    private void OnSelectionChanged(object sender, EventArgs e) => _onChanged?.Invoke(this);
    private void OnGotFocus(object sender, EventArgs e) => _onChanged?.Invoke(this);

    private void OnClosed(object sender, EventArgs e)
    {
        Detach();
        _onChanged?.Invoke(this);
    }
}
