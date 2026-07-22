/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The one place that talks to the host app. Wraps a CvToolRow (as ToolRowState)
// and turns renderer requests into bridge messages / dialog opens. Renderers
// depend on the ToolHost interface, never on this class or the bridge.

import { state as appState } from '../../core/state';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { openDiffDialog } from '../../core/dialog-host';
import type { ToolHost, ToolRowState } from './types';
import type {
    IdeFileNotification,
    IdeFileAtEditNotification,
    ExternalUrlNotification,
    DiffDialogNotification,
    ToolOutputNotification,
} from '../../core/types';

// eslint-disable-next-line no-control-regex
const ANSI_RE = /\x1b\[[0-9;]*[A-Za-z]/g;
const TOOL_USE_ERROR_RE = /<tool_use_error>([\s\S]*?)<\/tool_use_error>/i;
// CLI envelope for a large, persisted output: header line + "Preview (first NKB):"
// then the preview, then "</persisted-output>". Capture just the preview body.
const PERSISTED_OUTPUT_RE =
    /<persisted-output>[\s\S]*?Preview \(first[^)]*\):\n([\s\S]*?)<\/persisted-output>/i;

export class BridgeToolHost implements ToolHost {
    constructor(private row: ToolRowState) {}

    get name(): string {
        return this.row.data?.name ?? '';
    }
    get input(): Record<string, unknown> {
        return (this.row.data?.input ?? {}) as Record<string, unknown>;
    }
    get status() {
        return this.row.status;
    }
    get result(): string {
        return this.row.result;
    }
    get toolUseId(): string {
        return this.row.data?.id ?? '';
    }
    get fullLineCount(): number {
        return this.row.fullLineCount ?? 0;
    }
    get agentId(): string {
        return this.row.agentId ?? '';
    }
    get previewLines(): number {
        return appState.ui.previewLines;
    }
    get workingDirectory(): string {
        return appState.workingDirectory;
    }
    get expanded(): boolean {
        return this.row.expanded;
    }
    get elapsedSec(): number {
        return this.row.elapsedSec;
    }
    renderChildren() {
        return this.row.renderChildren();
    }
    renderHeaderActions() {
        return this.row.renderHeaderActions();
    }
    get showInlineToolErrors(): boolean {
        return appState.ui.showInlineToolErrors;
    }
    get showOpenDiffInVsButton(): boolean {
        return appState.ui.showOpenDiffInVsButton;
    }
    get showRelativePaths(): boolean {
        return appState.ui.showRelativePaths;
    }
    get compactOutputAskAnswers(): boolean {
        return appState.ui.compactOutputAskAnswers;
    }
    get clipsOutput(): boolean {
        return this.row.clipsOutput;
    }
    set clipsOutput(v: boolean) {
        this.row.clipsOutput = v;
    }

    toggleExpanded(): void {
        this.row.toggleExpanded();
    }

    openFile(filePath: string, startLine = 0, endLine = 0): void {
        if (!filePath) {
            return;
        }
        bridge.sendNotification<IdeFileNotification>(Msg.fromWebView.open.ideFile, {
            filePath,
            startLine,
            endLine,
        });
    }

    openFileAtEdit(filePath: string): void {
        if (!filePath) {
            return;
        }
        const inp = this.input;
        bridge.sendNotification<IdeFileAtEditNotification>(Msg.fromWebView.open.ideFileAtEdit, {
            filePath,
            oldString: String(inp.old_string ?? ''),
            newString: String(inp.new_string ?? inp.content ?? ''),
            startLine: 0,
            endLine: 0,
        });
    }

    openUrl(url: string): void {
        bridge.sendNotification<ExternalUrlNotification>(Msg.fromWebView.open.externalUrl, { url });
    }

    openDiffDialog(filePath: string, oldString: string, newString: string): void {
        openDiffDialog({ filePath, oldString, newString });
    }

    openDiffInVs(filePath: string, oldString: string, newString: string): void {
        bridge.sendNotification<DiffDialogNotification>(Msg.fromWebView.open.diffDialog, {
            filePath,
            oldString,
            newString,
        });
    }

    /** Open IN/OUT in a temp file in VS. The host holds only the preview-capped
     *  text; the app fetches the full content by toolUseId. */
    openOutput(which: 'in' | 'out'): void {
        const inp = this.input;
        const detail =
            String(inp.file_path ?? '') ||
            String(inp.command ?? '') ||
            String(inp.script ?? '') ||
            String(inp.code ?? '') ||
            String(inp.pattern ?? '') ||
            String(inp.query ?? '') ||
            String(inp.url ?? '') ||
            this.name;
        bridge.sendNotification<ToolOutputNotification>(Msg.fromWebView.open.toolOutput, {
            toolUseId: this.toolUseId,
            title: `[${this.name || 'Tool'}] ${detail.slice(0, 60)}`,
            which,
            agentId: this.agentId,
            // Lets the host project the IN to the tool's main field (Agent→prompt,
            // Bash→command) instead of dumping the whole input JSON.
            toolName: this.name,
        });
    }

    openError(): void {
        bridge.sendNotification<ToolOutputNotification>(Msg.fromWebView.open.toolOutput, {
            toolUseId: this.toolUseId,
            title: `[${this.name || 'Tool'}] error`,
            which: 'out',
            agentId: this.agentId,
        });
    }
}

/** Clean tool output: strip ANSI, unwrap <tool_use_error>, trim trailing ws. */
export function cleanResult(result: string, isError: boolean): string {
    let r = result ?? '';
    if (!r) {
        return '';
    }
    if (isError) {
        const m = r.match(TOOL_USE_ERROR_RE);
        if (m) {
            r = m[1].trim();
        }
    }
    // Large outputs arrive wrapped in the CLI's <persisted-output> envelope; show
    // only the preview body — the full output opens on click (persistedOutputPath).
    const persisted = r.match(PERSISTED_OUTPUT_RE);
    if (persisted) {
        r = persisted[1].replace(/\n\.\.\.\n$/, '\n');
    }
    return r.replace(ANSI_RE, '').replace(/\s+$/, '');
}

/** "12s" / "1m 5s" elapsed display. */
export function formatElapsed(sec: number): string {
    if (sec < 60) {
        return `${Math.round(sec)}s`;
    }
    const m = Math.floor(sec / 60);
    const s = Math.round(sec % 60);
    return `${m}m ${s}s`;
}

/** Clip text to `previewLines`. `clip` forces clipping even when expanded. */
export function previewText(
    text: string,
    previewLines: number,
    expanded: boolean,
    clip: boolean,
): string {
    if (!clip && (expanded || previewLines <= 0)) {
        return text;
    }
    if (previewLines <= 0) {
        return text;
    }
    const lines = text.split('\n');
    if (lines.length <= previewLines) {
        return text;
    }
    return lines.slice(0, previewLines).join('\n') + ' …';
}
