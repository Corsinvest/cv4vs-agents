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

    public async Task OpenFileAsync(string path)
        => await IdeContextService.Instance.OpenFileAsync(path, 0, 0, true);
}
