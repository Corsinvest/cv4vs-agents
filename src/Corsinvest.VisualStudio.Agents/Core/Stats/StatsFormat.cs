/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Core.Stats;

/// <summary>Shared number formatting for the Statistics UI, so tiles, the chart axis and the model
/// rows read the same way.</summary>
internal static class StatsFormat
{
    /// <summary>Compact token count: 1.2B / 12.3M / 4.5k / 812. Cache totals reach billions.</summary>
    public static string FormatTokens(double n)
    {
        if (n >= 1_000_000_000) { return (n / 1_000_000_000.0).ToString("0.#") + "B"; }
        if (n >= 1_000_000) { return (n / 1_000_000.0).ToString("0.#") + "M"; }
        if (n >= 1_000) { return (n / 1_000.0).ToString("0.#") + "k"; }
        return n.ToString("0");
    }
}
