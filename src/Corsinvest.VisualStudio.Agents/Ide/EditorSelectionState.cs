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
    private readonly Action _onChanged;
    private bool _detached;

    internal IWpfTextView View { get; }
    internal ITextDocument Document { get; }

    private EditorSelectionState(IWpfTextView view, ITextDocument document, Action onChanged)
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
                                                     Action onChanged)
    {
        if (view == null || docFactory == null) { return null; }
        return view.Properties.GetOrCreateSingletonProperty(typeof(Key), () =>
        {
            // DocumentBuffer, not TextBuffer: in a projected view (Razor, .vue) TextBuffer is the
            // projection and carries no ITextDocument.
            return docFactory.TryGetTextDocument(view.TextDataModel.DocumentBuffer, out var doc)
                ? new EditorSelectionState(view, doc, onChanged)
                : null;
        });
    }

    /// <summary>The primary selection's span and the real caret, on the view's current snapshot.
    /// False when the broker is unavailable or the view is gone.</summary>
    internal bool TryGetSpan(out SnapshotSpan span, out SnapshotPoint caret)
    {
        span = default;
        caret = default;
        if (_detached || _broker == null || View.IsClosed) { return false; }

        var primary = _broker.PrimarySelection;
        span = new SnapshotSpan(primary.Start.Position, primary.End.Position);
        caret = primary.InsertionPoint.Position;
        return true;
    }

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
        catch { /* view already torn down */ }
    }

    private void OnSelectionChanged(object sender, EventArgs e) => _onChanged?.Invoke();
    private void OnGotFocus(object sender, EventArgs e) => _onChanged?.Invoke();

    private void OnClosed(object sender, EventArgs e)
    {
        Detach();
        _onChanged?.Invoke();
    }
}
