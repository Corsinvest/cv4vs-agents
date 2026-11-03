/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System.Collections.Generic;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>The set as the WebView receives it. Kept apart from the bridge so the mapping, which is
/// where a wrong word would reach the screen, can be tested.</summary>
internal static class ChangedFilesMapper
{
    /// <param name="commandsNote">Why shell commands are not followed, when they are not (see <see cref="SessionChanges.CommandsNote"/>).</param>
    /// <param name="canReviewAll">The "all changes" window of Visual Studio 18 is there to be opened.</param>
    public static Contracts.ChangedFilesNotification Build(ChangeSet set, string commandsNote = null, bool canReviewAll = false) => new()
    {
        SessionId = set.SessionId,
        Files = set.Snapshot().Select(ToDto).ToArray(),
        Overflow = set.Overflow,
        MaxFiles = ChangeSet.MaxFiles,
        Disabled = set.DisabledReason,
        CommandsNote = commandsNote,
        CanReviewAll = canReviewAll,
    };

    public static Contracts.ChangedFileDto ToDto(FileChange row) => new()
    {
        Path = row.Path,
        State = Word(row.State),
        Source = Word(row.Source),
        Added = row.Added,
        Deleted = row.Deleted,
        Counts = Word(row.Counts),
        IsBinary = row.IsBinary,
        HasBaseline = row.Baseline.IsKnown && !row.BaselineMissing,
        BaselineMissing = row.BaselineMissing,
        Incomplete = row.Incomplete,
        SharedWithPane = row.SharedWithPane,
    };

    public static IReadOnlyList<Contracts.ChangedFileFailureDto> ToDtos(IEnumerable<ChangeFailure> failed)
        => failed.Select(f => new Contracts.ChangedFileFailureDto { Path = f.Path, Reason = f.Reason }).ToArray();

    // The wire spells an enum in lower case: "modified", "approximate". The names are the contract
    // with the WebView, so they are written out here, not derived from whatever the enum is renamed to.
    private static string Word(ChangeState state) => state switch
    {
        ChangeState.Added => "added",
        ChangeState.Deleted => "deleted",
        _ => "modified",
    };

    private static string Word(ChangeSource source) => source == ChangeSource.Bash ? "bash" : "edit";

    private static string Word(CountsKind counts) => counts switch
    {
        CountsKind.Approximate => "approximate",
        CountsKind.Unavailable => "unavailable",
        _ => "exact",
    };
}
