/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>What became of a file since its baseline.</summary>
internal enum ChangeState { Modified, Added, Deleted }

/// <summary>How the extension learned of the change. Edit = an Edit/Write/NotebookEdit tool call
/// that reported itself through a hook; Bash = a shell command, found by watching the disk.</summary>
internal enum ChangeSource { Edit, Bash }

/// <summary>Exact = a line diff; Approximate = a bound, because the texts were too large or too
/// different for the shortest-edit search; Unavailable = no numbers (binary, or too large to read).</summary>
internal enum CountsKind { Exact, Approximate, Unavailable }

/// <summary>The content a file is compared against, and who may delete it.
/// <para>A copy of the CLI's own backup is read-only for us (the CLI prunes and rewinds from it);
/// a copy the extension took (a sub-agent's first touch, a file after Keep) is ours to delete when
/// the session ends.</para></summary>
internal readonly struct BaselineRef
{
    private BaselineRef(string path, bool isEmpty, bool owned)
    {
        Path = path;
        IsEmpty = isEmpty;
        Owned = owned;
    }

    /// <summary>Nothing is known about the earlier content: the change can be listed, but not
    /// compared or restored.</summary>
    public static BaselineRef None => default;

    /// <summary>The file did not exist: the baseline is an empty file.</summary>
    public static BaselineRef Empty => new(null, true, false);

    /// <summary>One of the CLI's backups under <c>file-history</c>. Never written or deleted here.</summary>
    public static BaselineRef Cli(string path) => new(path, false, false);

    /// <summary>A copy this extension took, under <see cref="AppPaths.BaselinesFolder"/>.</summary>
    public static BaselineRef Own(string path) => new(path, false, true);

    public string Path { get; }
    public bool IsEmpty { get; }
    public bool Owned { get; }

    /// <summary>True when there is something to compare against, an empty file included.</summary>
    public bool IsKnown => IsEmpty || Path is not null;
}

/// <summary>One row of the list: a file Claude changed in this session. Mutable under the owning
/// <see cref="ChangeSet"/>'s lock; everything that leaves the set is a <see cref="Clone"/>.</summary>
internal sealed class FileChange
{
    /// <summary>Absolute, as <see cref="ChangePaths.Normalize"/> spells it. The set's key.</summary>
    public string Path { get; set; }

    public ChangeState State { get; set; }
    public ChangeSource Source { get; set; }

    public BaselineRef Baseline { get; set; }

    /// <summary>The CLI named a backup and it is no longer on disk (it prunes its own history).
    /// Treated as no baseline, but worth saying so rather than showing a bare "not available".</summary>
    public bool BaselineMissing { get; set; }

    public int Added { get; set; }
    public int Deleted { get; set; }

    /// <summary>How far <see cref="Added"/> and <see cref="Deleted"/> can be trusted.</summary>
    public CountsKind Counts { get; set; }

    /// <summary>The content is not text, which is why there are no counts.</summary>
    public bool IsBinary { get; set; }

    /// <summary>A shell command changed more files than the watcher could keep track of.</summary>
    public bool Incomplete { get; set; }

    public string FirstToolUseId { get; set; }
    public string LastToolUseId { get; set; }
    public DateTime LastChangedUtc { get; set; }

    // The file's stamps when the counts were last computed: a refresh skips a row whose file has
    // not moved, so reopening the panel costs one stat per row rather than one diff.
    public long StampLength { get; set; }
    public DateTime StampWriteUtc { get; set; }

    public FileChange Clone() => (FileChange)MemberwiseClone();
}
