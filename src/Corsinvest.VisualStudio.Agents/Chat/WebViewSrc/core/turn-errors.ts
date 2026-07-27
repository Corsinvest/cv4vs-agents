/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Human labels for a failed turn. The host sends `errorKind` from the result's
// `terminal_reason` when present, else its `subtype` — both vocabularies land here.

const LABELS: Record<string, string> = {
    // result subtypes
    error_max_turns: 'Turn limit reached',
    error_max_budget_usd: 'Budget limit reached',
    error_max_structured_output_retries: 'Structured output retry limit reached',
    error_during_execution: 'Execution error',
    // terminal_reason (finer, wins when the CLI sends it)
    max_turns: 'Turn limit reached',
    budget_exhausted: 'Budget limit reached',
    blocking_limit: 'Usage limit reached',
    prompt_too_long: 'Prompt too long',
    aborted_streaming: 'Response interrupted',
    aborted_tools: 'Tool run interrupted',
    stop_hook_prevented: 'Stopped by a hook',
    hook_stopped: 'Stopped by a hook',
    api_error: 'API error',
    model_error: 'Model error',
    image_error: 'Image error',
    malformed_tool_use_exhausted: 'Tool call kept coming back malformed',
    structured_output_retry_exhausted: 'Structured output retry limit reached',
    turn_setup_failed: "Turn couldn't be started",
};

/** Label for a failed turn; falls back to a generic one so a new/unknown wire value
 *  still produces a readable notice rather than a raw identifier. */
export function turnErrorLabel(kind: string): string {
    return LABELS[kind] || 'Turn failed';
}

/** Reasons that are the user's own doing. The CLI flags them is_error like any other failure, but
 *  pressing stop is not a failure — and the transcript already shows "[Request interrupted by
 *  user]", so a red notice on top of it reads as though something went wrong. */
const USER_ABORTED = new Set(['aborted_streaming', 'aborted_tools']);

export function isUserAbort(kind: string): boolean {
    return USER_ABORTED.has(kind);
}

/** The CLI's `result` text sometimes carries an internal diagnostic rather than a message
 *  ("[ede_diagnostic] result_type=user last_content_type=n/a stop_reason=null"). It means nothing
 *  to a reader, so keep the label alone rather than pasting a debug line into the chat. */
export function turnErrorDetail(detail: string): string {
    const t = detail.trim();
    return /^\[[a-z_]+_diagnostic\]/i.test(t) ? '' : t;
}
