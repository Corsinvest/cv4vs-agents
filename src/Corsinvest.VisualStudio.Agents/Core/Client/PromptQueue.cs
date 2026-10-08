/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace Corsinvest.VisualStudio.Agents.Core.Client;

/// <summary>What survived an interrupt and what it cancelled, as the CLI reports it.
/// <para><see cref="Known"/> is false on a CLI that answers with an empty success: the lists are
/// then empty because nothing was said, not because nothing is queued.</para></summary>
public sealed class InterruptReceipt
{
    public bool Known { get; set; }
    public string[] StillQueued { get; set; } = [];
    public string[] Cancelled { get; set; } = [];
}

/// <summary>Reads what the CLI says about prompts written on stdin while a turn runs. The queue is
/// the CLI's: this only parses its answers.</summary>
internal static class PromptQueue
{
    /// <summary>A `command_lifecycle` line: which prompt, and the state it moved to.</summary>
    public static bool TryParseLifecycle(JObject line, out string uuid, out string state)
    {
        uuid = line?.Val("command_uuid");
        state = line?.Val("state");
        return !string.IsNullOrEmpty(uuid) && !string.IsNullOrEmpty(state);
    }

    /// <summary>States after which the prompt will never run. Anything else, including a state
    /// a later CLI adds, leaves the prompt alone: taking a bubble off screen for a state we do not
    /// know would hide a message the model may still get.</summary>
    public static bool IsGone(string state) => state is "cancelled" or "discarded" or "refused";

    public static InterruptReceipt ParseInterruptReceipt(JObject response)
        => response?["still_queued"] is not JArray still
            ? new InterruptReceipt()
            : new InterruptReceipt
            {
                Known = true,
                StillQueued = Strings(still),
                Cancelled = Strings(response["cancelled"] as JArray),
            };

    /// <summary>The value to put on a user message, or null for none.</summary>
    public static string NormalizePriority(string priority)
        => priority is "now" or "next" or "later" ? priority : null;

    private static string[] Strings(JArray array)
        => array == null
            ? []
            : [.. array.Where(t => t.Type == JTokenType.String)
                       .Select(t => (string)t)
                       .Where(s => !string.IsNullOrEmpty(s))];
}
