/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Ide;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>What putting text back into an open document came to.</summary>
internal enum DocumentReplaceResult
{
    /// <summary>The file is not open: the caller writes it on disk.</summary>
    NotOpen,

    /// <summary>Replaced in the buffer and saved.</summary>
    Replaced,

    /// <summary>Replaced in the buffer, but the save did not happen: the editor and the disk disagree.</summary>
    ReplacedNotSaved,

    /// <summary>Open, but not as text that can be replaced (a designer, a read-only buffer).</summary>
    Unsupported,
}

/// <summary>The editor operations Keep, Undo and Open need, so <see cref="ChangeActions"/> can be
/// tested without a running Visual Studio.</summary>
internal interface IChangeEditor
{
    Task<DocumentReplaceResult> ReplaceDocumentTextAsync(string path, string text);

    /// <summary>Close the file's tabs without saving.</summary>
    Task CloseDocumentAsync(string path);

    /// <summary>The file is open and has changes in the editor that are not saved.</summary>
    Task<bool> IsDirtyAsync(string path);

    /// <summary>Show two texts side by side in VS's comparison window. Read-only.</summary>
    Task ShowDiffAsync(string key, string path, string before, string after, string leftLabel, string rightLabel);

    Task OpenFileAsync(string path);

    /// <summary>Open the file in its own editor with the changes drawn in it, against the baseline of its row in
    /// <paramref name="set"/>: GitHub Copilot's review, with its Keep and Undo on each hunk. False when that cannot be done
    /// here (the setting is off, Copilot is not in this Visual Studio, the file is not plain text, another chat or Copilot is
    /// reviewing it): the caller falls back to the comparison window.</summary>
    Task<bool> ShowInPlaceAsync(ChangeSet set, string path);

    /// <summary>End the review of the file in its editor, if there is one: the diff goes and Copilot lets go of its copy of the
    /// baseline, with nothing done to the document. Asked before Keep and Undo act on the file, which would otherwise be
    /// rewritten under a review that is still drawing against it.</summary>
    Task EndReviewAsync(string path);

    /// <summary>Show every file of <paramref name="set"/> that can be shown in text in the one window Visual Studio 18 has for
    /// all changes. False when it cannot be done.</summary>
    Task<bool> ShowAllAsync(ChangeSet set);
}

/// <summary>The real editor: Visual Studio, through <see cref="IdeContextService"/>.</summary>
internal sealed class IdeChangeEditor : IChangeEditor
{
    public static IdeChangeEditor Instance { get; } = new();

    public Task<DocumentReplaceResult> ReplaceDocumentTextAsync(string path, string text)
        => IdeContextService.Instance.ReplaceDocumentTextAsync(path, text);

    public async Task CloseDocumentAsync(string path)
        => await IdeContextService.Instance.CloseDocumentAsync(path);

    public async Task<bool> IsDirtyAsync(string path)
        => await IdeContextService.Instance.IsDocumentDirtyAsync(path) == true;

    public Task ShowDiffAsync(string key, string path, string before, string after, string leftLabel, string rightLabel)
        => IdeContextService.Instance.ShowDiffAsync(key, path, before, after, leftLabel, rightLabel);

    // In the preview tab: going through the list one file after another should not leave a tab behind for each.
    public async Task OpenFileAsync(string path)
        => await IdeContextService.Instance.OpenFileAsync(path, 0, 0, true, preview: true);

    public Task<bool> ShowInPlaceAsync(ChangeSet set, string path)
        => Ide.Changes.ReviewRegistry.Instance.ShowAsync(set, path);

    public Task EndReviewAsync(string path)
        => Ide.Changes.ReviewRegistry.Instance.EndAsync(path);

    public Task<bool> ShowAllAsync(ChangeSet set) => Ide.Changes.MultiDiffReviewer.ShowAsync(set);
}
