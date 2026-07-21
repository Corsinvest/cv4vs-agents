/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import LockClosed16Regular from '@fluentui/svg-icons/icons/lock_closed_16_regular.svg';
import QuestionCircle16Regular from '@fluentui/svg-icons/icons/question_circle_16_regular.svg';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import { iconStyles } from '../styles/shared';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { state as appState } from '../../core/state';
import type {
    ToolResultNotification,
    ToolPermissionNotification,
    ToolPermissionCancelNotification,
    RespondPermissionNotification,
    AskQuestion,
} from '../../core/types';

interface ToolPermission {
    id: string;
    name: string;
    preview?: string;
    input?: { questions?: AskQuestion[] } & Record<string, unknown>;
    /** True only for a real can_use_tool request — when false this message
     *  is a tool_use for RENDERING (the tool row), not a permission prompt. */
    needsPermission?: boolean;
    /** CLI permission_suggestions (PermissionUpdate[]) — each yields an extra
     *  "allow … for this session/project" button. Echoed back as updatedPermissions. */
    permissionSuggestions?: PermissionSuggestion[];
}

/** A CLI permission_suggestion (PermissionUpdate). We render a button per entry
 *  and send the chosen one back verbatim as updatedPermissions. */
interface PermissionSuggestion {
    type: string;
    rules?: { toolName?: string; ruleContent?: string }[];
    behavior?: string;
    destination?: string;
    mode?: string;
}

const OTHER = 'Other';

/** Scope destinations the "Yes, allow …" choice can cycle through, in VS Code's
 *  order (`wK` in the bundle). Clicking the scope word advances this cycle. */
const SCOPE_ORDER = ['localSettings', 'userSettings', 'projectSettings', 'session'] as const;
type Scope = (typeof SCOPE_ORDER)[number];

/** Short human label for a scope, shown as the clickable suffix of the button.
 *  Matches VS Code's wording exactly. */
const SCOPE_LABEL: Record<Scope, string> = {
    localSettings: 'this project (just you)',
    userSettings: 'all projects',
    projectSettings: 'this project (shared)',
    session: 'this session',
};

/** Tooltip per scope — explains where the permission is persisted. Mirrors the
 *  VS Code `title` map (`JLt`), so the hint changes as the scope cycles. */
const SCOPE_TOOLTIP: Record<Scope, string> = {
    localSettings: 'Saves to .claude/settings.local.json (gitignored)',
    userSettings: 'Saves to ~/.claude/settings.json',
    projectSettings: 'Saves to .claude/settings.json (shared with team)',
    session: 'Only for this session (not saved)',
};

/**
 * Allow/Deny banner shown when the CLI requests tool-use permission.
 * Active only in `default` mode; other modes auto-resolve and never show it.
 */
@customElement('cv-permission-banner')
export class CvPermissionBanner extends LitElement {
    static override styles = [
        iconStyles,
        css`
            /* In-flow, sitting where the input box is (the input hides while a
             * prompt is pending) — like VS Code, which swaps the composer for the
             * permission request rather than overlaying the chat. */
            :host {
                display: contents;
            }
            #permission-area {
                background: var(--colorNeutralBackground2);
                border: 1px solid var(--colorNeutralStroke2);
                border-radius: var(--borderRadiusMedium);
                padding: 10px 12px;
                max-height: 60vh;
                overflow-y: auto;
            }
            #permission-area.hidden {
                display: none;
            }
            #permission-header {
                display: flex;
                align-items: center;
                gap: 6px;
                font-weight: var(--fontWeightSemibold);
                margin-bottom: 6px;
            }
            /* Collapsible "Details" expander (VS Code style): the command/input is
             * hidden by default behind a small "Details" toggle. */
            #permission-details {
                margin-bottom: 8px;
            }
            #permission-details > summary {
                cursor: pointer;
                width: fit-content;
                list-style: none;
                user-select: none;
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                padding: 2px 4px;
                border-radius: var(--borderRadiusSmall);
            }
            #permission-details > summary::-webkit-details-marker {
                display: none;
            }
            #permission-details > summary::after {
                content: ' ▾';
                color: var(--colorNeutralForeground4);
            }
            #permission-details[open] > summary::after {
                content: ' ▴';
            }
            #permission-details > summary:hover {
                color: var(--colorNeutralForeground2);
            }
            #permission-details-body {
                font-family: var(--fontFamilyMonospace);
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                background: var(--colorNeutralBackground3);
                border-radius: var(--borderRadiusMedium);
                padding: 6px 10px;
                margin-top: 6px;
                word-break: break-all;
                white-space: pre-wrap;
                max-height: 160px;
                overflow-y: auto;
            }
            /* Short single-line command shown inline (no expander), like VS Code. */
            #permission-detail-inline {
                font-family: var(--fontFamilyMonospace);
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                margin: 4px 0 0;
                white-space: pre-wrap;
                word-break: break-all;
            }
            /* auto-resize grows to content; cap at 6 rows then scroll. */
            #permission-command {
                display: block;
                width: 100%;
                /* Lift Fluent's host max-width:400px so the command spans the composer. */
                max-width: none;
                box-sizing: border-box;
                margin: 4px 0;
            }
            #permission-command::part(root),
            #permission-command::part(control) {
                width: 100%;
                box-sizing: border-box;
            }
            /* Fluent sizes the root for a 2-row textarea (min-height 52px); release it so a
               one-line command doesn't sit in a box twice its height. */
            #permission-command::part(root) {
                min-height: 0;
            }
            #permission-command::part(control) {
                /* The native textarea defaults to two rows and Fluent v3 exposes no rows
                   attribute, so height comes from CSS: auto-resize then sets the real height. */
                height: auto;
                max-height: calc(6lh + 12px + 2px);
                font-family: var(--fontFamilyMonospace);
                font-size: var(--fontSizeBase200);
                line-height: var(--lineHeightBase300);
                white-space: pre-wrap;
                word-break: break-all;
            }
            /* Tool description under the command (the CLI description field). */
            #permission-description {
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                margin: 0 0 8px;
            }
            #permission-buttons {
                display: flex;
                /* Stacked vertically (one choice per row), like the VS Code prompt. */
                flex-direction: column;
                align-items: stretch;
                gap: 4px;
            }
            #permission-buttons fluent-button {
                justify-content: flex-start;
            }
            /* Question Submit/Cancel: centred (no number badge like permission choices). */
            #permission-buttons.question-actions fluent-button {
                justify-content: center;
            }

            /* Free-text alternative to the choices — denies with this as the message.
             * Stretches to the full width of the choice buttons above it. */
            #permission-deny-input {
                display: block;
                width: 100%;
                max-width: none;
                box-sizing: border-box;
                margin-top: 4px;
            }
            #permission-deny-input::part(root),
            #permission-deny-input::part(control) {
                width: 100%;
                box-sizing: border-box;
            }
            #permission-deny-input::part(root) {
                display: flex;
                /* Same as the command box: don't reserve two rows for a one-line field. */
                min-height: 0;
            }
            #permission-deny-input::part(control) {
                flex: 1 1 auto;
                min-width: 0;
                resize: none;
                /* Starts at one line and grows with the text (auto-resize), capped like the
                   command box so a long instruction scrolls instead of pushing the choices away. */
                height: auto;
                max-height: calc(4lh + 12px + 2px);
                white-space: pre-wrap;
                overflow-y: auto;
            }
            /* Numbered shortcut badge on each choice (1/2/3), like the VS Code prompt. */
            .num {
                display: inline-block;
                min-width: 1.1em;
                margin-right: 4px;
                color: var(--colorNeutralForeground3);
                font-variant-numeric: tabular-nums;
            }
            /* Wraps the suggestion label so "Yes, allow …", "for" and the clickable
             * scope flow as normal inline text (the fluent-button slot would otherwise
             * flex-gap the spans and swallow the spaces between words). */
            .label {
                white-space: normal;
            }
            /* Clickable scope word inside "Yes, allow … for {scope}". Clicking it
             * cycles the scope (session/project/…) without confirming the choice. */
            .scope {
                text-decoration: underline;
                text-underline-offset: 2px;
                cursor: pointer;
                white-space: nowrap;
                font-weight: var(--fontWeightSemibold);
            }
            .scope:hover {
                color: var(--colorBrandForegroundLinkHover);
            }
            #permission-hint {
                margin-top: 4px;
                font-size: var(--fontSizeBase100);
                color: var(--colorNeutralForeground4);
            }

            /* AskUserQuestion interactive UI. Reuses #permission-area chrome; the
             * pieces below are the question flow. */
            .area .tabs {
                display: flex;
                align-items: center;
                gap: 6px;
                flex-wrap: wrap;
                margin-bottom: 8px;
            }
            /* Leading "?" icon, matching #permission-header's icon on the Yes/No banner. */
            .icon {
                display: inline-flex;
                align-items: center;
                flex: 0 0 auto;
            }
            .tablist {
                display: flex;
                align-items: stretch;
                gap: 2px;
                flex: 1 1 auto;
                min-width: 0;
                flex-wrap: wrap;
            }
            .tabs-spacer {
                flex: 1 1 auto;
            }
            .close {
                flex: 0 0 auto;
            }
            /* Question tabs: fluent-tablist stays pure (active underline is Fluent's).
             * We colour our own slotted label span, mirroring VS Code's navTab: an
             * unanswered question stays highlighted (full foreground + medium weight),
             * an answered one greys out and stays grey even while active. */
            .tab-label {
                color: var(--colorNeutralForeground1);
                font-weight: var(--fontWeightMedium);
            }
            .tab-label.answered {
                color: var(--colorNeutralForeground3);
                font-weight: var(--fontWeightRegular);
            }
            .header-title {
                font-weight: var(--fontWeightSemibold);
            }
            .question {
                font-weight: var(--fontWeightSemibold);
                margin-bottom: 8px;
            }
            .options {
                display: flex;
                flex-direction: column;
                gap: 2px;
                margin-bottom: 10px;
            }
            .option {
                display: flex;
                align-items: flex-start;
                gap: 8px;
                padding: 6px 8px;
                border-radius: var(--borderRadiusMedium);
                cursor: pointer;
                transition: background 0.1s;
                outline: none;
            }
            .option:hover,
            .option:focus-visible {
                background: var(--colorNeutralBackground1Hover);
            }
            .option.selected {
                background: var(--colorBrandBackground2);
            }
            /* Fluent radio/checkbox used as the marker only; the row handles the click. */
            .mark {
                flex-shrink: 0;
                margin-top: 2px;
                pointer-events: none;
            }
            .option-text {
                display: flex;
                flex-direction: column;
                min-width: 0;
            }
            .option-label {
                font-size: var(--fontSizeBase300);
            }
            .option-desc {
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                line-height: var(--lineHeightBase200);
            }
            .other-wrap {
                padding: 2px 8px 4px 30px;
            }
            .other-wrap fluent-textarea {
                display: block;
                width: 100%;
                max-width: none;
            }
            .other-input::part(root),
            .other-input::part(control) {
                width: 100%;
                box-sizing: border-box;
            }
            .other-input::part(root) {
                display: flex;
            }
            .other-input::part(control) {
                flex: 1 1 auto;
                min-width: 0;
                resize: none;
            }
        `,
    ];

    @state() private _pending: ToolPermission | null = null;

    // Editable command text for tools that carry a `command` (Bash/PowerShell):
    // shown in a textarea, sent back as updatedInput on allow. null = not editing.
    @state() private _editedCommand: string | null = null;

    // AskUserQuestion interactive state
    @state() private _qIndex = 0;
    // question -> set of chosen option labels (may include OTHER)
    @state() private _picked: Map<string, Set<string>> = new Map();
    // question -> free text typed for the "Other" option
    @state() private _other: Map<string, string> = new Map();

    // Scope cycle for the "Yes, allow … for {scope}" choice. Clicking the
    // scope word cycles through SCOPE_ORDER (VS Code behaviour); the button
    // itself confirms with the current scope. Index into SCOPE_ORDER.
    @state() private _scopeIdx = 0;
    // True once the user has touched the scope cycle: the suggestion choice
    // becomes the primary (blue) button and "Yes" drops to outline, mirroring
    // VS Code where the focused choice is the primary one.
    @state() private _scopeActive = false;

    private _offs: Array<() => void> = [];

    // Set once the first button has been focused for the current prompt, so the
    // auto-focus runs only when the prompt opens — not on every re-render.
    private _focusedOnOpen = false;

    /** Focus the first choice — the primary button (Yes) for a tool-permission ask, the first
     *  option for an AskUserQuestion. Public so "Go to pane" can land the user on the ask, not the
     *  (hidden) textarea. Returns true if something was focused. */
    focusFirst(): boolean {
        const first =
            this._pending?.name === 'AskUserQuestion'
                ? this.renderRoot.querySelector<HTMLElement>('.options .option')
                : this._focusables()[0];
        first?.focus();
        return !!first;
    }

    override updated(): void {
        // Focus the first choice when the prompt opens, like VS Code, so Enter and the arrow keys
        // act on it straight away.
        if (this._pending && !this._focusedOnOpen) {
            if (this.focusFirst()) {
                this._focusedOnOpen = true;
            }
        }
    }

    override connectedCallback(): void {
        super.connectedCallback();
        this._offs.push(
            bridge.onNotification<ToolPermissionNotification>(
                Msg.toWebView.chat.toolPermission,
                (dto) => {
                    if (!dto?.id) {
                        return;
                    }
                    // Only a real can_use_tool request raises the banner; other
                    // tool_use blocks are for rendering only (else it flashes).
                    if (!dto.needsPermission) {
                        return;
                    }
                    // Narrow the DTO's opaque input/permissionSuggestions (tool args /
                    // CLI PermissionUpdate) to the shapes the banner reads best-effort.
                    const data: ToolPermission = {
                        id: dto.id,
                        name: dto.name,
                        preview: dto.preview,
                        input: dto.input as ToolPermission['input'],
                        needsPermission: dto.needsPermission,
                        permissionSuggestions:
                            dto.permissionSuggestions as unknown as PermissionSuggestion[],
                    };
                    // AskUserQuestion is interactive: the user answers it directly.
                    if (data.name === 'AskUserQuestion') {
                        this._startQuestions(data);
                        return;
                    }
                    this._pending = data;
                    appState.pendingPermission = { id: data.id, name: data.name };
                },
            ),
        );
        this._offs.push(
            bridge.onNotification<ToolResultNotification>(Msg.toWebView.chat.toolResult, (data) => {
                if (this._pending && data?.toolUseId === this._pending.id) {
                    this._dismiss();
                }
            }),
        );
        // The CLI aborted this permission (interrupt / superseded turn) — no answer expected.
        this._offs.push(
            bridge.onNotification<ToolPermissionCancelNotification>(
                Msg.toWebView.chat.toolPermissionCancel,
                (data) => {
                    if (this._pending && data?.toolUseId === this._pending.id) {
                        this._dismiss();
                    }
                },
            ),
        );
        this._offs.push(bridge.onNotification(Msg.toWebView.chat.cleared, () => this._dismiss()));
        // Keyboard shortcuts while a prompt is open (like VS Code): Esc cancels,
        // 1/2/3 pick a choice, Enter confirms the default. Disabled while typing
        // in the "tell Claude what to do instead" field.
        document.addEventListener('keydown', this._onKeydown);
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        document.removeEventListener('keydown', this._onKeydown);
        for (const off of this._offs) {
            off();
        }
        this._offs.length = 0;
    }

    private _onKeydown = (e: KeyboardEvent): void => {
        const p = this._pending;
        if (!p) {
            return;
        }
        // Esc always cancels (even while typing the deny message).
        if (e.key === 'Escape') {
            e.preventDefault();
            e.stopPropagation();
            if (p.name === 'AskUserQuestion') {
                this._cancelQuestions();
            } else {
                this._respond(false);
            }
            return;
        }
        // AskUserQuestion has its own UI; only "1" submits (the answers must
        // already be picked). Digits are ignored while typing in the Other field.
        if (p.name === 'AskUserQuestion') {
            if (e.key === '1' && !this._inOtherInput()) {
                e.preventDefault();
                this._submitAnswers();
            }
            return;
        }
        const inCommand = this._inCommandInput();
        const inDeny = this._inDenyInput() && !inCommand;
        // Up/Down cycle focus across the choices and the deny field. While editing
        // the command textarea the arrows move the caret; in a multi-line deny
        // message they do too (VS Code).
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            if (inCommand || (inDeny && this._denyValue().includes('\n'))) {
                return;
            }
            e.preventDefault();
            this._navFocus(e.key === 'ArrowDown' ? 1 : -1);
            return;
        }
        // While typing in the command or deny field, let other keys (digits etc.) pass.
        if (inCommand || inDeny) {
            return;
        }
        // Numbered actions in VS Code order: Yes, [suggestion], No, focus the
        // deny field. The last number doesn't confirm — it jumps to the textarea.
        const actions = this._numberedActions();
        // Enter confirms the focused choice, defaulting to the first (Yes).
        if (e.key === 'Enter') {
            e.preventDefault();
            const i = this._focusedIndex();
            (actions[i] ?? actions[0])?.();
            return;
        }
        const idx = Number(e.key) - 1;
        if (Number.isInteger(idx) && idx >= 0 && idx < actions.length) {
            e.preventDefault();
            actions[idx]();
        }
    };

    /** Ordered keyboard actions (1..n), mirroring VS Code: Yes, the suggestion
     *  (if any), No, then focus the deny field as the final number. */
    private _numberedActions(): Array<() => void> {
        const p = this._pending;
        const suggestion = (p?.permissionSuggestions ?? [])[0];
        const actions: Array<() => void> = [() => this._onAllow()];
        if (suggestion) {
            actions.push(() => this._onAllowWith(suggestion));
        }
        actions.push(() => this._onDeny());
        actions.push(() => this._focusDenyInput());
        return actions;
    }

    /** Focusable elements in choice order: the choice buttons then the deny field. */
    private _focusables(): HTMLElement[] {
        const buttons = Array.from(
            this.renderRoot.querySelectorAll<HTMLElement>('#permission-buttons fluent-button'),
        );
        const deny = this.renderRoot.querySelector<HTMLElement>('#permission-deny-input');
        return deny ? [...buttons, deny] : buttons;
    }

    /** The element focused inside our shadow root. document.activeElement would
     *  return the host (focus is retargeted at the shadow boundary), so read the
     *  shadow's own activeElement instead. */
    private _activeInShadow(): Element | null {
        return (this.renderRoot as ShadowRoot).activeElement;
    }

    /** Index of the currently-focused choice, or -1 if none (defaults to Yes). */
    private _focusedIndex(): number {
        const active = this._activeInShadow();
        return this._focusables().findIndex((el) => el === active);
    }

    /** Move focus by `delta` across the choices, wrapping around (VS Code `ne`). */
    private _navFocus(delta: number): void {
        const items = this._focusables();
        if (!items.length) {
            return;
        }
        const cur = this._focusedIndex();
        const next = ((cur === -1 ? 0 : cur + delta) + items.length) % items.length;
        items[next]?.focus();
    }

    private _denyValue(): string {
        return (
            this.renderRoot.querySelector<HTMLTextAreaElement>('#permission-deny-input')?.value ??
            ''
        );
    }

    private _focusDenyInput(): void {
        this.renderRoot.querySelector<HTMLElement>('#permission-deny-input')?.focus();
    }

    // These read the shadow's focused element, not the keydown's e.target: the
    // listener is on `document`, so at the shadow boundary e.target is retargeted
    // to the host and loses the inner id/class. _activeInShadow() is accurate.
    private _inDenyInput(): boolean {
        const el = this._activeInShadow();
        return !!el && (el.id === 'permission-deny-input' || el.id === 'permission-command');
    }

    private _inOtherInput(): boolean {
        return !!this._activeInShadow()?.closest?.('.other-input');
    }

    private _inCommandInput(): boolean {
        return this._activeInShadow()?.id === 'permission-command';
    }

    private _dismiss(): void {
        this._pending = null;
        appState.pendingPermission = null;
        this._editedCommand = null;
        this._qIndex = 0;
        this._picked = new Map();
        this._other = new Map();
        this._scopeIdx = 0;
        this._scopeActive = false;
        this._focusedOnOpen = false;
        // Return focus to the composer: clearing pendingPermission re-shows the prompt,
        // but only on the next render, so defer the focus a frame (like dialog-focus).
        requestAnimationFrame(() =>
            (
                document.querySelector('cv-prompt') as { focusInput?: () => void } | null
            )?.focusInput?.(),
        );
    }

    private _startQuestions(data: ToolPermission): void {
        const qs = data.input?.questions ?? [];
        const picked = new Map<string, Set<string>>();
        for (const q of qs) {
            picked.set(q.question, new Set());
        }
        this._picked = picked;
        this._other = new Map();
        this._qIndex = 0;
        this._pending = data;
        // Mark a pending interaction so the input box hides while the user
        // answers (no diff/preview fields needed for questions).
        appState.pendingPermission = { id: data.id, name: data.name };
    }

    private _questions(): AskQuestion[] {
        return this._pending?.input?.questions ?? [];
    }

    // fluent-tablist drives selection via activetab; map its id ("q<index>") back to _qIndex.
    private _onQTabChange = (e: Event): void => {
        const active = (e.target as HTMLElement & { activetab?: HTMLElement }).activetab;
        const i = Number(active?.id?.slice(1));
        if (Number.isInteger(i) && i !== this._qIndex) {
            this._qIndex = i;
        }
    };

    private _isPicked(question: string, label: string): boolean {
        return this._picked.get(question)?.has(label) ?? false;
    }

    /** Toggle an option. Single-select clears the others and auto-advances to
     *  the next question; multi-select just flips. Picking "Other" focuses its
     *  text field. Mirrors the VS Code AskUserQuestion component. */
    private _toggle(q: AskQuestion, label: string): void {
        const set = new Set(this._picked.get(q.question) ?? []);
        if (q.multiSelect) {
            if (set.has(label)) {
                set.delete(label);
            } else {
                set.add(label);
            }
        } else {
            const wasOnly = set.has(label) && set.size === 1;
            set.clear();
            if (!wasOnly) {
                set.add(label);
            }
        }
        const next = new Map(this._picked);
        next.set(q.question, set);
        this._picked = next;

        if (label === OTHER && set.has(OTHER)) {
            // Let the text field render, then focus it.
            setTimeout(() => {
                this.renderRoot.querySelector<HTMLInputElement>('.other-input')?.focus();
            }, 0);
            return;
        }
        // Single-select non-Other: auto-advance if there's a next question.
        if (!q.multiSelect && set.has(label)) {
            const qs = this._questions();
            if (this._qIndex < qs.length - 1) {
                setTimeout(() => {
                    this._qIndex = this._qIndex + 1;
                }, 250);
            }
        }
    }

    private _setOther(question: string, value: string): void {
        const next = new Map(this._other);
        next.set(question, value);
        this._other = next;
    }

    /** Enter in the Other field advances to the next question; Shift+Enter adds
     *  a newline. Mirrors VS Code's Other input. */
    private _onOtherKey = (e: KeyboardEvent): void => {
        e.stopPropagation();
        if (e.key !== 'Enter' || e.shiftKey) {
            return;
        }
        e.preventDefault();
        const qs = this._questions();
        if (this._qIndex < qs.length - 1) {
            this._qIndex = this._qIndex + 1;
        }
    };

    /** Up/Down move focus between the options (radio or checkbox), wrapping
     *  around — selection stays on Enter/Space. Mirrors VS Code; also stops the
     *  arrows from scrolling the panel. */
    private _onOptionsKey = (e: KeyboardEvent): void => {
        if (e.key !== 'ArrowUp' && e.key !== 'ArrowDown') {
            return;
        }
        const opts = Array.from(
            this.renderRoot.querySelectorAll<HTMLElement>(
                '.options [role="radio"], .options [role="checkbox"]',
            ),
        );
        if (!opts.length) {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
        const cur = opts.indexOf(this._activeInShadow() as HTMLElement);
        const next =
            e.key === 'ArrowUp'
                ? cur <= 0
                    ? opts.length - 1
                    : cur - 1
                : cur >= opts.length - 1
                  ? 0
                  : cur + 1;
        opts[next]?.focus();
    };

    /** All questions have at least one selection (and Other, if chosen, has text). */
    private _allAnswered(): boolean {
        const qs = this._questions();
        if (!qs.length) {
            return false;
        }
        return qs.every((q) => {
            const set = this._picked.get(q.question);
            if (!set || set.size === 0) {
                return false;
            }
            if (set.has(OTHER) && !(this._other.get(q.question) ?? '').trim()) {
                return false;
            }
            return true;
        });
    }

    /** Build the answers map {question: "a, b"} replacing OTHER with its text. */
    private _buildAnswers(): Record<string, string> {
        const answers: Record<string, string> = {};
        for (const q of this._questions()) {
            const set = this._picked.get(q.question);
            if (!set || set.size === 0) {
                continue;
            }
            const labels = Array.from(set).map((l) =>
                l === OTHER ? (this._other.get(q.question) ?? '').trim() : l,
            );
            answers[q.question] = labels.filter(Boolean).join(', ');
        }
        return answers;
    }

    private _submitAnswers = (): void => {
        const p = this._pending;
        if (!p || !this._allAnswered()) {
            return;
        }
        bridge.sendNotification<RespondPermissionNotification>(
            Msg.fromWebView.cli.respondPermission,
            {
                allowed: true,
                toolUseId: p.id,
                updatedInput: { questions: this._questions(), answers: this._buildAnswers() },
            },
        );
        this._dismiss();
    };

    private _cancelQuestions = (): void => {
        const p = this._pending;
        if (p) {
            bridge.sendNotification<RespondPermissionNotification>(
                Msg.fromWebView.cli.respondPermission,
                { allowed: false, toolUseId: p.id },
            );
        }
        this._dismiss();
    };

    private _respond(allowed: boolean): void {
        if (!this._pending) {
            return;
        }
        const msg: RespondPermissionNotification = { allowed, toolUseId: this._pending.id };
        // If the user edited the command, allow with the patched input (like VS Code).
        if (allowed && this._editedCommand !== null && this._pending.input) {
            msg.updatedInput = { ...(this._pending.input as object), command: this._editedCommand };
        }
        bridge.sendNotification<RespondPermissionNotification>(
            Msg.fromWebView.cli.respondPermission,
            msg,
        );
        this._dismiss();
    }

    private _onAllow = () => this._respond(true);
    private _onDeny = () => this._respond(false);

    /** Allow once AND apply a permission_suggestion, overriding its destination
     *  with the currently-cycled scope (VS Code behaviour). Echoed back verbatim
     *  as updatedPermissions. `setMode` suggestions carry no rule scope, so they
     *  go back unchanged. */
    private _onAllowWith(suggestion: PermissionSuggestion): void {
        if (!this._pending) {
            return;
        }
        const out: PermissionSuggestion =
            suggestion.type === 'setMode'
                ? suggestion
                : { ...suggestion, destination: SCOPE_ORDER[this._scopeIdx] };
        bridge.sendNotification<RespondPermissionNotification>(
            Msg.fromWebView.cli.respondPermission,
            {
                allowed: true,
                toolUseId: this._pending.id,
                updatedPermissions: [out],
            },
        );
        this._dismiss();
    }

    /** Cycle the scope word forward (localSettings → userSettings → … → session
     *  → wrap), without confirming. Matches VS Code's clickable scope toggle. */
    private _cycleScope = (e: Event): void => {
        e.stopPropagation();
        this._scopeIdx = (this._scopeIdx + 1) % SCOPE_ORDER.length;
        this._scopeActive = true;
    };

    /** Fixed part of the suggestion button label, e.g. "Yes, allow Bash(npm:*)".
     *  For setMode suggestions this is the whole label (no scope cycle). */
    private _suggestionLabel(s: PermissionSuggestion): string {
        if (s.type === 'setMode') {
            return s.mode === 'acceptEdits'
                ? 'Yes, allow all edits this session'
                : 'Yes, and don’t ask again';
        }
        const rule = s.rules?.[0];
        // Prefer the specific rule pattern; fall back to the tool name.
        let what = rule?.toolName ?? this._pending?.name ?? 'this';
        const rc = rule?.ruleContent;
        if (rc) {
            const short = rc.length > 20 ? rc.slice(0, 17) + '…' : rc;
            what = rule?.toolName ? `${rule.toolName}(${short})` : short;
        }
        return `Yes, allow ${what}`;
    }

    /** Tool detail under the title. Like VS Code: a short single-line detail
     *  (≤250 chars, the command/path) is shown inline; a long or multi-line one
     *  collapses into a "Details" expander so the banner stays compact. */
    private _renderDetails(p: ToolPermission) {
        const input = p.input as Record<string, unknown> | undefined;
        const hasInput = input && typeof input === 'object' && Object.keys(input).length > 0;
        const cmd = typeof input?.command === 'string' ? (input.command as string) : null;
        if (cmd !== null) {
            const description =
                typeof input?.description === 'string' ? (input.description as string) : '';
            return html`<fluent-textarea
                    id="permission-command"
                    appearance="outline"
                    auto-resize
                    resize="none"
                    spellcheck="false"
                    .value=${this._editedCommand ?? cmd}
                    @input=${(e: Event) => {
                        this._editedCommand = (e.target as HTMLTextAreaElement).value;
                    }}
                ></fluent-textarea>
                ${
                    description
                        ? html`<div id="permission-description">${description}</div>`
                        : nothing
                }`;
        }
        if (!hasInput && !p.preview) {
            return nothing;
        }
        // Non-command tools: short single-line preview inline, else collapse to Details.
        const inline = p.preview && p.preview.length <= 250 && !p.preview.includes('\n');
        if (inline) {
            return html`<pre id="permission-detail-inline">${p.preview}</pre>`;
        }
        const detail = hasInput ? JSON.stringify(input, null, 2) : (p.preview ?? '');
        return html`<details id="permission-details">
            <summary>Details</summary>
            <pre id="permission-details-body">${detail}</pre>
        </details>`;
    }

    override render() {
        const p = this._pending;
        if (!p) {
            return nothing;
        }
        if (p.name === 'AskUserQuestion') {
            return this._renderQuestions();
        }
        // Like VS Code: at most THREE numbered choices — 1 Yes, 2 "allow … for
        // this project" (the suggestions collapsed into one), 3 No. Numbers are
        // keyboard shortcuts (handled in _onKeydown), shown as a badge per row.
        const suggestion = (p.permissionSuggestions ?? [])[0];
        let n = 1;
        return html`
            <div id="permission-area">
                <div id="permission-header">
                    ${unsafeHTML(LockClosed16Regular)}
                    <span id="permission-title">Allow this ${p.name} command?</span>
                </div>
                ${this._renderDetails(p)}
                <div id="permission-buttons">
                    <fluent-button
                        appearance=${suggestion && this._scopeActive ? 'outline' : 'primary'}
                        @click=${this._onAllow}
                    >
                        <span class="num">${n++}</span> Yes
                    </fluent-button>
                    ${
                        suggestion
                            ? html`<fluent-button
                                  appearance=${this._scopeActive ? 'primary' : 'outline'}
                                  @click=${() => this._onAllowWith(suggestion)}
                              >
                                  <span class="label">
                                      <span class="num">${n++}</span>
                                      ${this._suggestionLabel(suggestion)}${
                                          suggestion.type !== 'setMode'
                                              ? html` for
                                                    <span
                                                        class="scope"
                                                        role="button"
                                                        title=${SCOPE_TOOLTIP[SCOPE_ORDER[this._scopeIdx]]}
                                                        @click=${this._cycleScope}
                                                        >${SCOPE_LABEL[SCOPE_ORDER[this._scopeIdx]]}
                                                        ⇄</span
                                                    >`
                                              : nothing
                                      }
                                  </span>
                              </fluent-button>`
                            : nothing
                    }
                    <fluent-button appearance="outline" @click=${this._onDeny}>
                        <span class="num">${n++}</span> No
                    </fluent-button>
                </div>
                <fluent-textarea
                    id="permission-deny-input"
                    appearance="outline"
                    auto-resize
                    resize="none"
                    placeholder="Or type what to do instead…"
                    @keydown=${this._onDenyInputKey}
                ></fluent-textarea>
                <div id="permission-hint">Esc to cancel</div>
            </div>
        `;
    }

    /** Free-text deny: Enter denies the tool with the typed text as the message,
     *  so Claude reads the user's instruction instead of just being blocked. */
    private _onDenyInputKey = (e: KeyboardEvent): void => {
        // Enter sends; Shift+Enter inserts a newline.
        if (e.key !== 'Enter' || e.shiftKey) {
            return;
        }
        e.preventDefault();
        const text = (e.target as HTMLTextAreaElement).value?.trim();
        if (!text || !this._pending) {
            return;
        }
        bridge.sendNotification<RespondPermissionNotification>(
            Msg.fromWebView.cli.respondPermission,
            {
                allowed: false,
                toolUseId: this._pending.id,
                denyMessage: text,
            },
        );
        this._dismiss();
    };

    /** Interactive AskUserQuestion UI: per-question tabs, radio/checkbox
     *  options, an "Other" free-text option, and a Submit footer. Layout and
     *  behaviour mirror the VS Code extension. */
    private _renderQuestions() {
        const qs = this._questions();
        if (!qs.length) {
            return nothing;
        }
        const idx = Math.min(this._qIndex, qs.length - 1);
        const q = qs[idx];
        const multi = !!q.multiSelect;
        const renderOption = (label: string, description?: string) => {
            const on = this._isPicked(q.question, label);
            return html`<div
                class="option ${on ? 'selected' : ''}"
                role=${multi ? 'checkbox' : 'radio'}
                aria-checked=${on}
                tabindex="0"
                @click=${() => this._toggle(q, label)}
                @keydown=${(e: KeyboardEvent) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        this._toggle(q, label);
                    }
                }}
            >
                ${
                    multi
                        ? html`<fluent-checkbox
                              class="mark"
                              tabindex="-1"
                              ?checked=${on}
                          ></fluent-checkbox>`
                        : html`<fluent-radio
                              class="mark"
                              tabindex="-1"
                              ?checked=${on}
                          ></fluent-radio>`
                }
                <span class="option-text">
                    <span class="option-label">${label}</span>
                    ${description ? html`<span class="option-desc">${description}</span>` : nothing}
                </span>
            </div>`;
        };
        return html`
            <div id="permission-area" class="area">
                <div class="tabs">
                    <span class="icon">${unsafeHTML(QuestionCircle16Regular)}</span>
                    ${
                        qs.length > 1
                            ? html`<fluent-tablist
                                  class="tablist"
                                  size="small"
                                  activeid=${`q${idx}`}
                                  @change=${this._onQTabChange}
                              >
                                  ${qs.map((qq, i) => {
                                      const answered =
                                          (this._picked.get(qq.question)?.size ?? 0) > 0;
                                      return html`<fluent-tab id=${`q${i}`}>
                                          <span class="tab-label ${answered ? 'answered' : ''}"
                                              >${qq.header || `Q${i + 1}`}</span
                                          >
                                      </fluent-tab>`;
                                  })}
                              </fluent-tablist>`
                            : html`<span class="header-title">${q.question}</span>`
                    }
                    <span class="tabs-spacer"></span>
                    <fluent-button
                        class="close"
                        appearance="transparent"
                        icon-only
                        aria-label="Cancel"
                        @click=${this._cancelQuestions}
                    >
                        ${unsafeHTML(Dismiss16Regular)}
                    </fluent-button>
                </div>
                ${qs.length > 1 ? html`<div class="question">${q.question}</div>` : nothing}
                <div class="options" @keydown=${this._onOptionsKey}>
                    ${q.options.map((o) => renderOption(o.label, o.description))}
                    ${renderOption(OTHER, 'Provide a custom answer')}
                    ${
                        this._isPicked(q.question, OTHER)
                            ? html`<div class="other-wrap">
                                  <fluent-textarea
                                      class="other-input"
                                      rows="1"
                                      placeholder="Type your answer…"
                                      .value=${this._other.get(q.question) ?? ''}
                                      @input=${(e: Event) =>
                                          this._setOther(
                                              q.question,
                                              (e.target as HTMLTextAreaElement).value,
                                          )}
                                      @keydown=${this._onOtherKey}
                                  ></fluent-textarea>
                              </div>`
                            : nothing
                    }
                </div>
                <div id="permission-buttons" class="question-actions">
                    <fluent-button
                        appearance="primary"
                        ?disabled=${!this._allAnswered()}
                        @click=${this._submitAnswers}
                    >
                        <span class="num">1</span> Submit answers
                    </fluent-button>
                </div>
                <div id="permission-hint">Esc to cancel</div>
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-permission-banner': CvPermissionBanner;
    }
}
