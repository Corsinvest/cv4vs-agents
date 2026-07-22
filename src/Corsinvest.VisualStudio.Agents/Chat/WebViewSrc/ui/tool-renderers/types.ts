/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The host a ToolRenderer is bound to (implemented by CvToolRow). It exposes
// the row's raw data plus the few actions that touch the bridge / VS — the
// only seam to the app. Renderers read data and call actions through `host`;
// everything else (markup, row layout, string cleanup) lives in ToolRenderer.
// No renderer imports bridge/state — only CvToolRow does.

import type { TemplateResult, nothing } from 'lit';
import type { ToolStatus, ToolUseData } from '../../core/types';

/** The slice of CvToolRow's state the host reads/writes. The component
 *  satisfies this; BridgeToolHost wraps it and adds the bridge actions. */
export interface ToolRowState {
    readonly data: ToolUseData | undefined;
    readonly status: ToolStatus;
    readonly result: string;
    readonly fullLineCount: number;
    readonly elapsedSec: number;
    readonly expanded: boolean;
    /** Sub-agent id when this row is a child of an Agent — routes open-output to the
     *  sub-agent transcript. Empty for top-level tools. */
    readonly agentId: string;
    clipsOutput: boolean;
    toggleExpanded(): void;
    /** Nested child rows/messages (Agent tool's transcript), or nothing. Generic:
     *  any tool with nested children can return them here. */
    renderChildren(): TemplateResult | typeof nothing;
    /** Actions rendered on the tool's header row (right side, before the chevron).
     *  Default nothing; the Agent uses it for copy + show-all. */
    renderHeaderActions(): TemplateResult | typeof nothing;
}

export interface ToolHost {
    readonly name: string;
    readonly input: Record<string, unknown>;
    readonly status: ToolStatus;
    readonly result: string; // raw tool output (preview-clipped)
    /** Full output line count (before clipping), 0 when empty; count-only renderers show it. */
    readonly fullLineCount: number;
    readonly toolUseId: string;
    /** Sub-agent id when this tool is a sub-agent child; empty for top-level tools. */
    readonly agentId: string;
    readonly previewLines: number;
    readonly workingDirectory: string;
    readonly elapsedSec: number;
    /** Whether this row is expanded (chevron toggled open). */
    readonly expanded: boolean;
    /** Always clip output to previewLines, even when expanded (shell tools). */
    clipsOutput: boolean;
    /** Nested child rows/messages (Agent tool's transcript), or nothing. Generic:
     *  any tool with nested children can return them here. */
    renderChildren(): TemplateResult | typeof nothing;
    /** Actions rendered on the tool's header row (right side, before the chevron).
     *  Default nothing; the Agent uses it for copy + show-all. */
    renderHeaderActions(): TemplateResult | typeof nothing;

    /** Open a file in VS, optionally selecting a line range. */
    openFile(filePath: string, startLine?: number, endLine?: number): void;
    /** Open a file in VS and select the edited region (Edit/Write/MultiEdit). */
    openFileAtEdit(filePath: string): void;
    /** Open an external URL via the host. */
    openUrl(url: string): void;
    /** Open the side-by-side diff dialog inside the webview (click on the row). */
    openDiffDialog(filePath: string, oldString: string, newString: string): void;
    /** Send the diff to Visual Studio's native diff viewer (VS icon button). */
    openDiffInVs(filePath: string, oldString: string, newString: string): void;
    /** Open this tool's IN or OUT content in a temp file in VS. */
    openOutput(which: 'in' | 'out'): void;
    /** Open this tool's full error output in VS (resolved by toolUseId). */
    openError(): void;
    /** Toggle the row's expanded state. */
    toggleExpanded(): void;
    /** Whether inline tool errors are shown in the body (user setting). */
    readonly showInlineToolErrors: boolean;
    /** Whether the "Open diff in Visual Studio" button is shown (user setting). */
    readonly showOpenDiffInVsButton: boolean;
    /** Whether tool-row paths are shown relative to the workdir (user setting). */
    readonly showRelativePaths: boolean;
    /** Compact AskUserQuestion output: only the chosen option per question. */
    readonly compactOutputAskAnswers: boolean;
}
