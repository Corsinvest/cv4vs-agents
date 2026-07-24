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
    /** How many nested children this row holds (Agent tool today). 0 for a normal tool. */
    readonly childCount: number;
    clipsOutput: boolean;
    toggleExpanded(): void;
    /** Nested child rows/messages (Agent tool's transcript), or nothing. Generic:
     *  any tool with nested children can return them here. */
    renderChildren(): TemplateResult | typeof nothing;
    /** Actions that depend on the component's Lit state (sub-agent copy + show-all).
     *  Default nothing; the Agent uses it. Composed by the renderer's renderHeaderActions. */
    componentHeaderActions(): TemplateResult | typeof nothing;
}

// What a renderer sees: everything the component provides (ToolRowState) plus the tool-scoped data
// derived from it and the app actions that touch the bridge/VS. NO global user settings — those live
// in appState.ui, read directly by the renderers.
export interface ToolHost extends ToolRowState {
    readonly name: string;
    readonly input: Record<string, unknown>;
    readonly toolUseId: string;
    readonly previewLines: number;
    readonly workingDirectory: string;

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
}
