/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Changes;
using System;
using Xunit;

namespace Corsinvest.VisualStudio.Agents.Tests;

public class ReviewOwnershipTests
{
    private sealed class Pane { }

    private static readonly DateTime Noon = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    private static ReviewOwnership.Candidate<Pane> Row(Pane pane, int minutes, int number)
        => new(pane, Noon.AddMinutes(minutes), number);

    [Fact]
    public void The_pane_whose_row_changed_last_gets_the_review()
    {
        var a = new Pane();
        var b = new Pane();

        var winner = ReviewOwnership.Winner(new[] { Row(a, 0, 1), Row(b, 5, 2) }, null, null, default);

        Assert.Equal(1, winner);
    }

    [Fact]
    public void A_tie_goes_to_the_pane_that_has_the_review_already()
    {
        var a = new Pane();
        var b = new Pane();
        var rows = new[] { Row(a, 3, 1), Row(b, 3, 2) };

        Assert.Equal(1, ReviewOwnership.Winner(rows, b, null, default));
        Assert.Equal(0, ReviewOwnership.Winner(rows, a, null, default));
        // And with nobody showing it, the first one listed.
        Assert.Equal(0, ReviewOwnership.Winner(rows, null, null, default));
    }

    [Fact]
    public void Being_asked_to_show_the_file_counts_as_a_change_until_the_other_pane_changes_it_later()
    {
        var a = new Pane();
        var b = new Pane();

        // The person pressed Open in b, whose row is the older one, at minute 10.
        var asked = Noon.AddMinutes(10);
        Assert.Equal(1, ReviewOwnership.Winner(new[] { Row(a, 5, 1), Row(b, 0, 2) }, a, b, asked));

        // Then a's file changes again, at minute 12: it is a's again.
        Assert.Equal(0, ReviewOwnership.Winner(new[] { Row(a, 12, 1), Row(b, 0, 2) }, b, b, asked));
    }

    [Fact]
    public void Being_asked_never_makes_a_row_older_than_it_is()
    {
        var a = new Pane();
        var b = new Pane();

        // b was asked at minute 2 but its row changed at minute 8: the later time counts.
        var winner = ReviewOwnership.Winner(new[] { Row(a, 5, 1), Row(b, 8, 2) }, null, b, Noon.AddMinutes(2));

        Assert.Equal(1, winner);
    }

    [Fact]
    public void A_pane_that_was_asked_and_does_not_list_the_file_is_ignored()
    {
        var a = new Pane();
        var gone = new Pane();

        var winner = ReviewOwnership.Winner(new[] { Row(a, 0, 1) }, null, gone, Noon.AddMinutes(30));

        Assert.Equal(0, winner);
    }

    [Fact]
    public void No_rows_means_no_winner()
    {
        Assert.Equal(-1, ReviewOwnership.Winner(Array.Empty<ReviewOwnership.Candidate<Pane>>(), null, null, default));
        Assert.Empty(ReviewOwnership.SharedWith(Array.Empty<ReviewOwnership.Candidate<Pane>>(), -1));
    }

    [Fact]
    public void A_file_one_pane_lists_names_nobody()
    {
        var marks = ReviewOwnership.SharedWith(new[] { Row(new Pane(), 0, 4) }, 0);

        Assert.Equal(new int?[] { null }, marks);
    }

    [Fact]
    public void With_two_panes_each_row_names_the_other()
    {
        var a = new Pane();
        var b = new Pane();
        var rows = new[] { Row(a, 0, 1), Row(b, 5, 2) };

        // b is showing it: a's row names b, and b's names a.
        Assert.Equal(new int?[] { 2, 1 }, ReviewOwnership.SharedWith(rows, 1));
        Assert.Equal(new int?[] { 2, 1 }, ReviewOwnership.SharedWith(rows, 0));
    }

    [Fact]
    public void With_three_panes_the_pane_showing_it_names_the_first_of_the_others_and_every_other_names_it()
    {
        var rows = new[] { Row(new Pane(), 0, 1), Row(new Pane(), 1, 2), Row(new Pane(), 2, 3) };

        Assert.Equal(new int?[] { 3, 3, 1 }, ReviewOwnership.SharedWith(rows, 2));
        Assert.Equal(new int?[] { 2, 1, 1 }, ReviewOwnership.SharedWith(rows, 0));
    }
}
