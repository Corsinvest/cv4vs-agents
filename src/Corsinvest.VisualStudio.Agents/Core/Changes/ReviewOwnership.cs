/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;

namespace Corsinvest.VisualStudio.Agents.Core.Changes;

/// <summary>Which chat pane gets the review of a file that more than one lists, and what each of them says about it. A file has
/// one editor window, so one pane at a time can show its changes there; the rule is the one a person would guess, and it is
/// kept apart from the editor so it can be tested.</summary>
internal static class ReviewOwnership
{
    /// <summary>One pane's row of the file.</summary>
    internal readonly struct Candidate<T> where T : class
    {
        public Candidate(T owner, DateTime changedUtc, int number)
        {
            Owner = owner;
            ChangedUtc = changedUtc;
            Number = number;
        }

        public T Owner { get; }

        /// <summary>When the row was last listed or changed by a tool call.</summary>
        public DateTime ChangedUtc { get; }

        /// <summary>The pane's number, the N of its tab, "Chat N".</summary>
        public int Number { get; }
    }

    /// <summary>The index of the candidate that gets the review: the one whose row changed last, where being asked for counts as a
    /// change (the person who presses Open on a file in one chat wants it there, and a later change in the other takes it back).
    /// A tie goes to whoever has it already, so that two panes do not take it from each other.</summary>
    /// <param name="current">The owner of the review that is showing now, if any.</param>
    /// <param name="claimed">The owner that was asked to show the file, if any.</param>
    /// <param name="claimedUtc">When it was asked.</param>
    public static int Winner<T>(IReadOnlyList<Candidate<T>> candidates, T current, T claimed, DateTime claimedUtc) where T : class
    {
        var best = -1;
        var bestTime = DateTime.MinValue;
        var bestIsCurrent = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var time = candidate.ChangedUtc;
            if (claimed is not null && ReferenceEquals(candidate.Owner, claimed) && claimedUtc > time) { time = claimedUtc; }
            var isCurrent = current is not null && ReferenceEquals(candidate.Owner, current);
            if (best < 0 || time > bestTime || (time == bestTime && isCurrent && !bestIsCurrent))
            {
                best = i;
                bestTime = time;
                bestIsCurrent = isCurrent;
            }
        }
        return best;
    }

    /// <summary>What each candidate's row says about the others: the number of another pane that lists the file, or null when it is
    /// alone. The winner names the first of the others; every other names the winner, the pane whose editor is showing it.</summary>
    public static int?[] SharedWith<T>(IReadOnlyList<Candidate<T>> candidates, int winner) where T : class
    {
        var marks = new int?[candidates.Count];
        if (candidates.Count < 2 || winner < 0) { return marks; }
        var firstOther = winner == 0 ? 1 : 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            marks[i] = i == winner ? candidates[firstOther].Number : candidates[winner].Number;
        }
        return marks;
    }
}
