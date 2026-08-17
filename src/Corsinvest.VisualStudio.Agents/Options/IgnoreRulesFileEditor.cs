/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Chat.Host;
using Microsoft.VisualStudio.Shell;
using System;
using System.ComponentModel;
using System.Drawing.Design;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>
/// The `…` button next to the picker's ignore rules: opens the rules FILE in the IDE rather than
/// showing a dialog. The content is a `.gitignore`, so the editor that already colours that format
/// and offers find/undo beats a modal text box — and the file is the storage, so there is no value
/// to hand back.
/// <para>The file is created from the shipped defaults on first open, so the user lands on a
/// commented starting point instead of an empty buffer.</para>
/// </summary>
internal sealed class IgnoreRulesFileEditor : UITypeEditor
{
    // Modal: the button opens a document and returns immediately, but the grid still needs to be
    // told this is a "click to act" cell rather than a drop-down.
    public override UITypeEditorEditStyle GetEditStyle(ITypeDescriptorContext context)
        => UITypeEditorEditStyle.Modal;

    public override object EditValue(ITypeDescriptorContext context, IServiceProvider provider, object value)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var path = IgnoreRulesStore.EnsureFile();
            if (path != null)
            {
                VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, path);
            }
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Global.LogException("IgnoreRulesFileEditor.EditValue", ex);
        }

        // The value is unchanged by design — the file is what holds the rules. Returning the
        // incoming value leaves the grid's cell (the file path) exactly as it was.
        return value;
    }
}
