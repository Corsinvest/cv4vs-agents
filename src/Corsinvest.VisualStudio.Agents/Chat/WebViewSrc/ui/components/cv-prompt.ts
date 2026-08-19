/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css, nothing } from 'lit';
import { customElement, query, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { iconStyles, tooltipStyles } from '../styles/shared';
import Send16Filled from '@fluentui/svg-icons/icons/send_16_filled.svg';
import Stop16Filled from '@fluentui/svg-icons/icons/stop_16_filled.svg';
import { iconUrl } from '../../core/icon-url';
import { state as appState } from '../../core/state';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import type {
    Attachment,
    SubagentTask,
    AtItemDto,
    RateLimitNotification,
    NoticeNotification,
    NoticeDismissedDetail,
    PromptHistoryNotification,
    UserTextNotification,
    UserImageDto,
    UserFileDto,
    SendPromptNotification,
    SetPermissionModeNotification,
    SetModelNotification,
    ExternalUrlNotification,
    OpenOptionsNotification,
    PermissionMode,
} from '../../core/types';
import { GetSuggestionsReq } from '../../core/request-types';
import { permissionItems } from '../../core/permission-modes';
import type { ChatCommand, CommandHost } from '../../core/commands';
import './cv-notice-stack';
import type { CvNoticeStack } from './cv-notice-stack';
import './cv-at-menu';
import './cv-command-menu';
import './cv-attach-menu';
import './cv-attach-chip';
import './cv-context-gauge';
import './cv-ide-context-badge';
import './cv-subagent-chip';
import './cv-queue-row';
import './cv-effort-selector';
import './cv-thinking-toggle';
import './cv-remote-chip';
import './cv-model-list';
import './cv-model-selector';
import './cv-permission-list';
import './cv-permission-selector';
import './cv-mic-button';
import { openLightbox, openPluginManagerDialog } from '../../core/dialog-host';
import { openAttachment } from '../../core/lazy';

const TEXTAREA_MAX_HEIGHT_PX = 160;

/** The file's lowercased extension including the leading dot, or '' if none. */
function fileExt(name: string): string {
    const i = name.lastIndexOf('.');
    return i >= 0 ? name.slice(i).toLowerCase() : '';
}

/** True if the file's extension is in the user's upload allowlist. The host
 *  (BuildContentBlocks) decides how to wrap each accepted file by extension. */
function isAllowedUpload(file: File): boolean {
    const ext = fileExt(file.name);
    return !!ext && (appState.ui.allowedUploadExtensions ?? []).includes(ext);
}

/** Wording for files whose extension isn't in the upload allowlist. The Options path it used to
 *  spell out is now the action button's job. */
function unsupportedMessage(names: string[]): string {
    const list = names.join(', ');
    return (
        `<strong>Unsupported file types:</strong> ${list}. ` +
        'Add the extension to allow it, or reference the file by absolute path in your prompt ' +
        '(@ for paths inside your working directory).'
    );
}

// Chip thumbnail edge (px). Matches the host-side ThumbnailGenerator so a live paste and a
// re-opened history image look identical. Slightly above the 16px render size for high-DPI.
const THUMB_PX = 24;

// Builtins that end or reset the current turn's state rather than adding to it: queueing these
// behind a running turn would hold back the one command that gets the session out of it.
const RESET_COMMANDS = new Set(['/clear', '/compact']);

/** Downscale an image data-URL to a tiny PNG data-URL for the chip. Resolves to undefined on
 *  any failure (decode error, unsupported codec) — the chip then falls back to its file icon. */
function makeThumb(dataUrl: string): Promise<string | undefined> {
    return new Promise((resolve) => {
        const img = new Image();
        img.onload = () => {
            try {
                const scale = Math.min(1, THUMB_PX / Math.max(img.width, img.height));
                const w = Math.max(1, Math.round(img.width * scale));
                const h = Math.max(1, Math.round(img.height * scale));
                const canvas = document.createElement('canvas');
                canvas.width = w;
                canvas.height = h;
                const ctx = canvas.getContext('2d');
                if (!ctx) {
                    resolve(undefined);
                    return;
                }
                ctx.drawImage(img, 0, 0, w, h);
                resolve(canvas.toDataURL('image/png'));
            } catch {
                resolve(undefined);
            }
        };
        img.onerror = () => resolve(undefined);
        img.src = dataUrl;
    });
}

/** Read any allowed file as base64 (one code path, like VS Code). The host
 *  (BuildContentBlocks) decides by extension whether to send it as an image,
 *  a PDF, or decode the base64 back to text. `dataUrl` is kept so image chips
 *  can preview without a round-trip; `preview` is the small thumbnail that rides
 *  in the sent message (the full dataUrl would be too heavy to keep in the DOM). */
function readAsAttachment(file: File): Promise<Attachment> {
    const mediaType = file.type || 'application/octet-stream';
    const isImage = mediaType.startsWith('image/');
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onerror = () => reject(reader.error);
        reader.onload = async () => {
            const dataUrl = String(reader.result ?? '');
            const preview = isImage ? await makeThumb(dataUrl) : undefined;
            resolve({
                name: file.name,
                mediaType,
                isImage,
                base64: dataUrl.split(',')[1] ?? '',
                dataUrl,
                preview,
            });
        };
        reader.readAsDataURL(file);
    });
}

/**
 * Composer area: textarea, send button, and toolbar buttons.
 *   Enter submit (or stop when busy) · Shift+Enter newline · Esc stop while busy.
 */
@customElement('cv-prompt')
export class CvPrompt extends LitElement implements CommandHost {
    static override styles = [
        iconStyles,
        tooltipStyles,
        css`
            :host {
                display: contents;
            }
            /* Icon inside the notice bar / inline glyphs. */
            .ico {
                display: inline-flex;
                align-items: center;
                justify-content: center;
                flex-shrink: 0;
                line-height: 1;
            }
            /* Notice bar (error/warning) shown above the textarea. */
            .notice {
                margin-bottom: 6px;
            }
            /* A bare column: the border belongs to #field, not here. position:relative because the
               popovers (@, commands, model, permission) anchor to it. */
            #box {
                position: relative;
                display: flex;
                flex-direction: column;
            }
            /* display:flex above beats the UA [hidden] rule (id specificity), so hide explicitly
               while an ask/permission is pending — the composer stays mounted (draft preserved). */
            #box[hidden] {
                display: none;
            }
            /* The composer's one border, and it draws a line with a meaning: what travels with this
               message is inside (attachment chips, the text, send), what outlives it is not (model,
               effort, permission, the gauge). */
            #field {
                display: flex;
                flex-direction: column;
                background: var(--colorNeutralBackground3);
                border: 1px solid var(--colorNeutralStroke2);
                border-radius: var(--borderRadiusLarge);
                transition: border-color 0.15s;
            }
            /* While the textarea has focus, the border reflects the active permission
             * mode — quick visual feedback for Shift+Tab cycling. The mode colour IS the focus
             * state; a separate accent would only fight it for the same border. */
            #box[data-permission-mode='default'] #field:focus-within {
                /* Peach (a light warm orange) — lighter and clearly apart from the Red
                 * used by 'auto', which Pumpkin/DarkOrange sat too close to. */
                border-color: var(--colorPalettePeachBorderActive);
            }
            #box[data-permission-mode='acceptEdits'] #field:focus-within {
                border-color: var(--colorNeutralStrokeAccessible);
            }
            #box[data-permission-mode='plan'] #field:focus-within {
                border-color: var(--colorBrandStroke1);
            }
            #box[data-permission-mode='auto'] #field:focus-within {
                border-color: var(--colorPaletteRedBorderActive);
            }
            /* The drop target is the whole composer (the handlers sit on #box), but the highlight
               goes on the field: it is the thing the file ends up in. */
            #box.drag-over #field {
                border-color: var(--colorBrandStroke1);
                background: var(--colorBrandBackground2);
            }
            /* Text and send on one line, send pinned to the bottom so it stays put as the textarea
               grows. */
            #input-line {
                display: flex;
                align-items: flex-end;
                gap: 4px;
                padding-inline-end: 6px;
            }
            #input {
                width: 100%;
                /* base.css's global box-sizing:border-box doesn't cross the shadow
                 * boundary; set it here so the padding stays inside min-height (else
                 * the textarea renders taller than 36px). */
                box-sizing: border-box;
                min-height: 36px;
                max-height: 160px;
                padding: 8px 10px;
                background: transparent;
                border: none;
                color: var(--colorNeutralForeground1);
                font-family: var(--fontFamilyBase);
                font-size: var(--fontSizeBase300);
                line-height: var(--lineHeightBase300);
                resize: none;
                overflow-y: auto;
                outline: none;
            }
            #input::placeholder {
                color: var(--colorNeutralForeground3);
            }
            /* Outside the field and with no chrome of its own: these settings outlive the message,
               so nothing should tie them to it. */
            #toolbar {
                display: flex;
                align-items: center;
                justify-content: space-between;
                padding: 4px 2px 0;
            }
            #toolbar-left,
            #toolbar-right {
                display: flex;
                align-items: center;
                gap: 4px;
            }
            /* One row for everything, so the left side has to be able to shrink: min-width:0 lets
               it go below its content width, and the file chip — the only elastic thing in it —
               is what gives way. The right side keeps its buttons at full size. */
            #toolbar-left {
                min-width: 0;
            }
            /* Everything on the left keeps its size except the file chip — it is the one carrying
               text that can be shortened, so it absorbs whatever the row runs out of. */
            #toolbar-left > *:not(cv-ide-context-badge) {
                flex-shrink: 0;
            }
            #toolbar-right {
                flex: 0 0 auto;
            }
            /* The one filled button in the composer. appearance="primary" is what makes the three
               states tell themselves apart: grey while the field is empty (nothing to send), brand
               once there is something (the action is ready), red while a turn runs (stop). Bare
               would leave the first two a shade of grey apart. Fluent brings the fill, its hover
               and its disabled state, so only the metrics are ours. */
            #send {
                padding: 3px;
                min-width: 0;
                margin-block-end: 6px;
            }
            /* Busy = "Stop": red like the mic's recording state (same "click to stop the live
               action" pattern) — the one state Fluent has no appearance for. It changes in place:
               send and stop are one button, and it never moves between them. */
            #send.is-busy {
                background: var(--colorPaletteRedBackground3);
                border-color: var(--colorPaletteRedBackground3);
            }
            #send.is-busy:hover {
                background: var(--colorPaletteRedForeground1);
                border-color: var(--colorPaletteRedForeground1);
            }
            /* Inside the field, on its own band above the text: a long list pushes the textarea
               down instead of eating its first line. Fill and padding separate it — no rule, the
               field's border is meant to be the only line here. */
            #attachments {
                display: flex;
                flex-wrap: wrap;
                gap: 4px;
                padding: 6px 8px 0;
                position: relative;
            }
        `,
    ];

    @state() private _isBusy = appState.isBusy;
    @state() private _hasText = false;
    @state() private _attachments: Attachment[] = [];
    @state() private _dragOver = false;
    @state() private _queue: Array<{ text: string; attachments: Attachment[]; uuid: string }> = [];
    @state() private _atOpen = false;
    @state() private _atItems: AtItemDto[] = [];
    // Command palette (typing `/`, or the attach menu's "Slash command" item). `_cmdQuery` is the
    // text after `/` while typing; empty when opened from the menu.
    @state() private _cmdOpen = false;
    @state() private _cmdQuery = '';
    // True when the palette was opened from the menu item (owns its own
    // search box + focus); false when opened by typing `/` (textarea drives it).
    @state() private _cmdSearchable = false;
    // Non-null when the palette was opened on one specific row (the Effort trigger): the ids to
    // show, instead of the whole list.
    @state() private _cmdOnly: string[] | null = null;
    @state() private _modelListOpen = false;
    @state() private _permissionListOpen = false;
    @state() private _permissionMode = appState.permissionMode;
    // Hidden while a permission/question prompt is pending (answered in overlay).
    @state() private _pendingPermission = appState.pendingPermission != null;
    // Reusable notice shown above the textarea (e.g. unsupported upload, rate limit).
    // Notices (rate limit, CLI informational, upload errors) accumulate INSIDE cv-notice-stack — it
    // owns the queue, the dedup, the cap and the auto-dismiss; we just push into it.
    @query('cv-notice-stack') private _noticeStack!: CvNoticeStack | null;
    @state() private _subagentTasks: SubagentTask[] = appState.subagentTasks;
    // Dedup: don't re-show a rate-limit banner the user dismissed until its key
    // (status:type) changes. _rateKey = the live banner's key (null if not a rate limit).
    private _dismissedRateKey: string | null = null;
    private _rateKey: string | null = null;

    /** The user closed a notice by hand. Only the rate limit needs remembering: the CLI re-sends
     *  `rate_limit_info` on every turn with the same key, so without this the banner comes straight
     *  back on the next message. A changed key (warning → rejected) is a different condition and
     *  shows again, which is the point. */
    private _onNoticeDismissed = (e: CustomEvent<NoticeDismissedDetail>): void => {
        if (e.detail?.key && e.detail.key === this._rateKey) {
            this._dismissedRateKey = e.detail.key;
        }
    };

    @query('textarea') private _ta!: HTMLTextAreaElement;
    @query('input[data-cv-file-picker]') private _filePicker!: HTMLInputElement;
    @query('cv-at-menu') private _atMenu!: import('./cv-at-menu').CvAtMenu;
    @query('cv-command-menu') private _cmdMenu?: import('./cv-command-menu').CvCommandMenu;
    @query('cv-model-list') private _modelList?: import('./cv-model-list').CvModelList;
    @query('cv-permission-list')
    private _permissionList?: import('./cv-permission-list').CvPermissionList;

    private _offBusy?: () => void;
    private _offPerm?: () => void;
    private _offPending?: () => void;
    private _offHistory?: () => void;
    private _offCleared?: () => void;
    private _offRate?: () => void;
    private _offNotice?: () => void;
    private _offSubagentTasks?: () => void;

    // Input ↑/↓ prompt history (shell-style). Oldest first; the typed prompts of
    // the current conversation. Seeded from the loaded session, appended on send.
    private _promptHistory: string[] = [];
    // Navigation cursor: -1 = not navigating (editing live). 0..len-1 indexes
    // _promptHistory from newest (len-1) downward as the user presses ↑.
    private _historyIdx = -1;
    // The live draft saved when entering history, restored on ↓ past the newest.
    private _historyDraft = '';

    override connectedCallback(): void {
        super.connectedCallback();
        this._offBusy = appState.on('isBusy', (v) => {
            this._isBusy = v;
            if (!v) {
                queueMicrotask(() => this._flushQueue());
            }
        });
        this._offPerm = appState.on('permissionMode', (v) => {
            this._permissionMode = v;
        });
        this._offPending = appState.on('pendingPermission', (v) => {
            this._pendingPermission = v != null;
        });

        // Seed the ↑/↓ prompt history. The host loads it in the background after
        // the initial render and pushes it via chat_prompt_history. Replace, not
        // append — the history is per-session.
        this._offHistory = bridge.onNotification<PromptHistoryNotification>(
            Msg.toWebView.chat.promptHistory,
            (data) => {
                if (Array.isArray(data?.prompts)) {
                    this._promptHistory = data.prompts.slice();
                    this._resetHistoryNav();
                }
            },
        );
        // New/cleared chat → empty history.
        this._offCleared = bridge.onNotification(Msg.toWebView.chat.cleared, () => {
            this._promptHistory = [];
            this._resetHistoryNav();
            // Close any composer overlay left open (model list / @-mention / command menu) so a
            // session switch starts clean — the assignments are no-ops when already closed.
            this._modelListOpen = false;
            this._permissionListOpen = false;
            this._atOpen = false;
            if (this._cmdOpen) {
                this._closeCommandMenu();
            }
            // Composer notices are turn-scoped (a rate limit hit, a rejected upload): none of them
            // still applies to the session being switched to.
            this._noticeStack?.clear();
            this._rateKey = null;
        });

        // Turn-scoped notices: this stack keeps the `composer` ones (session-scoped `top` ones go to
        // cv-app's stack, which listens on the same channel).
        this._offNotice = bridge.onNotification<NoticeNotification>(
            Msg.toWebView.chat.notice,
            (d) => {
                if (!d?.message || d.position !== 'composer') {
                    return;
                }
                this._noticeStack?.push({
                    severity: d.severity ?? 'info',
                    message: d.message,
                    key: d.key ?? undefined,
                    actionLabel: d.actionLabel ?? undefined,
                    actionMessage: d.actionMessage ?? undefined,
                    sticky: !!d.sticky,
                });
            },
        );

        // Null message clears; else show unless this key was already dismissed.
        this._offRate = bridge.onNotification<RateLimitNotification>(
            Msg.toWebView.chat.rateLimit,
            (d) => {
                if (!d?.message) {
                    // Cleared upstream: drop the rate-limit row (other notices stay).
                    if (this._rateKey) {
                        this._noticeStack?.dismissByKey(this._rateKey);
                    }
                    this._rateKey = null;
                    this._dismissedRateKey = null;
                    return;
                }
                if (d.key === this._dismissedRateKey) {
                    return;
                }
                this._rateKey = d.key ?? 'rate-limit';
                this._noticeStack?.push({
                    severity: d.severity ?? 'warning',
                    message: d.message,
                    key: this._rateKey,
                });
            },
        );

        this._offSubagentTasks = appState.on('subagentTasks', (v) => {
            this._subagentTasks = v;
        });

        // Close a menu-opened palette on an outside click. (The `/`-opened
        // one closes on textarea blur; this covers the searchable case, whose
        // focus lives in the menu's own search box.)
        document.addEventListener('pointerdown', this._onDocPointerDown, true);
        // Esc closes any open composer menu (model / command / @). At document level because the
        // menu's own search box holds focus (not the textarea), and VS's Esc arrives as a synthetic
        // document keydown (ui_escape) — neither reaches the textarea's @keydown across the shadow.
        document.addEventListener('keydown', this._onDocKeyDown, true);
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offBusy?.();
        this._offPerm?.();
        this._offPending?.();
        this._offHistory?.();
        this._offCleared?.();
        this._offRate?.();
        this._offNotice?.();
        this._offSubagentTasks?.();
        document.removeEventListener('pointerdown', this._onDocPointerDown, true);
        document.removeEventListener('keydown', this._onDocKeyDown, true);
    }

    /** Esc closes an open composer menu from anywhere (menu search box or VS's synthetic Esc),
     *  which the textarea's own @keydown can't see across the shadow boundary. */
    private _onDocKeyDown = (e: KeyboardEvent): void => {
        if (e.key !== 'Escape') {
            return;
        }
        if (this._modelListOpen) {
            this._modelListOpen = false;
            this._ta?.focus({ preventScroll: true });
        } else if (this._permissionListOpen) {
            this._permissionListOpen = false;
            this._ta?.focus({ preventScroll: true });
        } else if (this._cmdOpen) {
            this._closeCommandMenu();
        } else if (this._atOpen) {
            this._atOpen = false;
        } else {
            return;
        }
        e.preventDefault();
        e.stopPropagation();
    };

    /** Outside-click closes the searchable (menu-opened) palette. The attach menu's own click
     *  toggles, so ignore clicks on it (it handles itself). */
    private _onDocPointerDown = (e: PointerEvent): void => {
        // Outside-click closes the model list (clicks on it are handled by its rows). As with the
        // mode list, the toolbar trigger toggles itself — closing here too would reopen-then-close.
        if (this._modelListOpen) {
            const onListOrTrigger = e
                .composedPath()
                .some(
                    (n) =>
                        n instanceof Element &&
                        (n.tagName === 'CV-MODEL-LIST' || n.tagName === 'CV-MODEL-SELECTOR'),
                );
            if (!onListOrTrigger) {
                this._modelListOpen = false;
            }
        }
        // Same for the mode list — but the trigger toggles itself, so a click on it
        // must not also close here (that would reopen-then-close on every click).
        if (this._permissionListOpen) {
            const onListOrTrigger = e
                .composedPath()
                .some(
                    (n) =>
                        n instanceof Element &&
                        (n.tagName === 'CV-PERMISSION-LIST' ||
                            n.tagName === 'CV-PERMISSION-SELECTOR'),
                );
            if (!onListOrTrigger) {
                this._permissionListOpen = false;
            }
        }
        // Only the palettes a click opened: the `/` one is driven by the textarea and closes when
        // the token goes away, so an outside click must not touch it.
        if (!this._cmdOpen || !(this._cmdSearchable || this._cmdOnly)) {
            return;
        }
        const path = e.composedPath();
        if (!path.some((n) => n instanceof Element && n.tagName === 'CV-COMMAND-MENU')) {
            // Both triggers toggle themselves; closing here too would reopen-then-close. The
            // attach menu is one of them: its "Slash command" item is what opens this palette.
            const onTrigger = path.some(
                (n) =>
                    n instanceof Element &&
                    (n.tagName === 'CV-ATTACH-MENU' || n.tagName === 'CV-EFFORT-SELECTOR'),
            );
            if (!onTrigger) {
                this._closeCommandMenu();
            }
        }
    };

    /** Public getter so other components / debug helpers can peek. */
    get value(): string {
        return this._ta?.value ?? '';
    }

    /** Move keyboard focus to the prompt textarea (host ui_focus_input).
     *  `preventScroll` because the host calls this while VS is still relaying the pane out — after
     *  an InfoBar closes, say. The browser would measure a textarea that is momentarily outside the
     *  viewport and scroll to it, and a frame later the layout settles: the scroll was pointless but
     *  visible as a jump. The composer is pinned to the bottom and always in view anyway. */
    focusInput(): void {
        this._ta?.focus({ preventScroll: true });
    }

    /** Pre-fill the composer with text and focus it, caret at the end (a forked
     *  pane drops the forked-at message here so the user can edit/resend it). */
    setComposerText(text: string): void {
        const ta = this._ta;
        if (!ta) {
            return;
        }
        ta.value = text;
        this._hasText = text.trim().length > 0;
        this._autoResize();
        ta.focus({ preventScroll: true });
        ta.setSelectionRange(text.length, text.length);
    }

    /** Clear the textarea and reset its height. */
    clear(): void {
        if (!this._ta) {
            return;
        }
        this._ta.value = '';
        this._hasText = false;
        this._autoResize();
    }

    private _autoResize = (): void => {
        const ta = this._ta;
        if (!ta) {
            return;
        }
        ta.style.height = 'auto';
        ta.style.height = Math.min(ta.scrollHeight, TEXTAREA_MAX_HEIGHT_PX) + 'px';
    };

    private _atDebounce?: ReturnType<typeof setTimeout>;

    private _onInput = (e: InputEvent): void => {
        this._hasText = this._ta.value.trim().length > 0;
        this._autoResize();
        // Any real edit exits history navigation (shell-style): the recalled text
        // is now the user's working draft.
        if (this._historyIdx !== -1) {
            this._resetHistoryNav();
        }
        // Live `/` trigger: command palette open while the caret sits inside a
        // leading `/command` token (slash as the first char of the prompt).
        const slashQuery = this._getSlashQuery();
        if (slashQuery !== null) {
            this._cmdOpen = true;
            this._cmdSearchable = false; // textarea drives the filter
            this._cmdQuery = slashQuery;
            this._cmdOnly = null; // typing `/` searches everything, whatever opened it last
            this._atOpen = false;
            return;
        }
        if (this._cmdOpen) {
            this._cmdOpen = false;
            this._cmdQuery = '';
        }
        // Live `@` trigger: popover open while caret sits inside an `@token`.
        if (e.data === '@') {
            this._atOpen = true;
        }
        const query = this._getAtQuery();
        if (query === null) {
            if (this._atOpen) {
                this._atOpen = false;
                this._atItems = [];
            }
            if (this._atDebounce) {
                clearTimeout(this._atDebounce);
                this._atDebounce = undefined;
            }
            return;
        }
        if (!this._atOpen) {
            this._atOpen = true;
        }
        // Debounce file-suggestions: 150ms pause avoids a request per keystroke.
        if (this._atDebounce) {
            clearTimeout(this._atDebounce);
        }
        this._atDebounce = setTimeout(() => {
            this._atDebounce = undefined;
            this._fetchSuggestions(query);
        }, 150);
    };

    /** Correlated @-mention fetch: the response updates the picker only if it's still open
     *  (a stale response after the picker closed is ignored). Rejection (timeout) is swallowed. */
    private _fetchSuggestions(query: string): void {
        bridge
            .sendRequest(GetSuggestionsReq, { query })
            .then((data) => {
                if (this._atOpen) {
                    this._atItems = data?.items ?? [];
                }
            })
            .catch(() => {
                /* timeout — leave current items */
            });
    }

    /** Returns the chars typed after the last `/` token in the current text, or
     *  null. The `/` must start a token (preceded by start-of-text or
     *  whitespace, like `@`), so a path's inner slash doesn't trigger. Stops at
     *  the first space (args end the command token). Works anywhere in the
     *  prompt, not just at the start. */
    private _getSlashQuery(): string | null {
        const ta = this._ta;
        if (!ta) {
            return null;
        }
        const before = ta.value.slice(0, ta.selectionStart ?? 0);
        const m = before.match(/(?:^|\s)\/([^\s/]*)$/);
        return m ? m[1] : null;
    }

    /** Returns chars typed after the last `@` in the current line, or null. */
    private _getAtQuery(): string | null {
        const ta = this._ta;
        if (!ta) {
            return null;
        }
        const before = ta.value.slice(0, ta.selectionStart ?? 0);
        const m = before.match(/@([^\s]*)$/);
        return m ? m[1] : null;
    }

    /** Close the @ menu on blur. Menu items use mousedown.preventDefault, so
     *  suggestion clicks don't fire this — only true outside-clicks. */
    private _onTextareaBlur = (): void => {
        if (this._atOpen) {
            this._atOpen = false;
        }
        // Don't close a menu-opened palette: focus moves to its own search
        // box (blurring the textarea). That menu closes on its own Esc/select or
        // an outside click, not on this blur.
        if (!this._cmdSearchable) {
            this._closeCommandMenu();
        }
    };

    private _onKeyDown = (e: KeyboardEvent): void => {
        // While the model list is open, ↑/↓/Enter/Tab/Esc drive it.
        if (this._modelListOpen) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                this._modelList?.moveSelection(1);
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                this._modelList?.moveSelection(-1);
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                e.stopPropagation();
                this._modelList?.pickActive();
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                this._modelListOpen = false;
                this._ta?.focus({ preventScroll: true });
                return;
            }
        }
        // Same keys drive the mode list while it's open.
        if (this._permissionListOpen) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                this._permissionList?.moveSelection(1);
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                this._permissionList?.moveSelection(-1);
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                e.stopPropagation();
                this._permissionList?.pickActive();
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                this._permissionListOpen = false;
                this._ta?.focus({ preventScroll: true });
                return;
            }
        }
        // While the command palette is open, ↑/↓/Enter/Tab/Esc drive it.
        if (this._cmdOpen) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                this._cmdMenu?.moveSelection(1);
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                this._cmdMenu?.moveSelection(-1);
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                e.stopPropagation();
                this._cmdMenu?.pickActive();
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                this._closeCommandMenu();
                return;
            }
        }
        // While the @ menu is open, ↑/↓/Enter/Tab/Esc drive it, not the textarea.
        if (this._atOpen) {
            if (e.key === 'ArrowDown') {
                e.preventDefault();
                this._atMenu?.moveSelection(1);
                return;
            }
            if (e.key === 'ArrowUp') {
                e.preventDefault();
                this._atMenu?.moveSelection(-1);
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                e.stopPropagation();
                this._atMenu?.pickActive();
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                this._atOpen = false;
                this._atItems = [];
                return;
            }
        }
        // Prompt history (shell-style ↑/↓): ↑ recalls an older prompt only from the
        // first visual line, ↓ goes forward only from the last one — so navigating a
        // multi-line draft with the arrows still works. No modifiers. The key is NOT
        // swallowed here: _historyOnArrow needs the caret to move first to tell the
        // two cases apart, and decides once the default has run.
        if (
            (e.key === 'ArrowUp' || e.key === 'ArrowDown') &&
            !e.shiftKey &&
            !e.ctrlKey &&
            !e.altKey &&
            !e.metaKey
        ) {
            this._historyOnArrow(e.key === 'ArrowUp' ? -1 : 1);
        }
        // Shift+Tab cycles through permission modes (Anthropic CLI parity).
        if (e.key === 'Tab' && e.shiftKey && !e.ctrlKey && !e.altKey && !e.metaKey) {
            e.preventDefault();
            e.stopPropagation();
            this._cyclePermissionMode();
            return;
        }
        // Submit on Enter (or Ctrl/Cmd+Enter when useCtrlEnterToSend).
        if (e.key === 'Enter' && !e.altKey) {
            const ctrlOrMeta = e.ctrlKey || e.metaKey;
            const send = appState.ui.useCtrlEnterToSend ? ctrlOrMeta : !e.shiftKey && !ctrlOrMeta;
            if (send) {
                e.preventDefault();
                e.stopPropagation();
                this._submit();
            }
            return;
        }
        // Esc-to-stop is handled globally in cv-app (works regardless of focus,
        // like VS Code), so the textarea no longer handles it here.
    };

    /** Cycle through the modes the selector actually offers — same source, so the gates hold
     *  here too (model support for `auto`, VS option + CLI policy for `bypassPermissions`).
     *  A hardcoded list ignored them, and stranded whoever was in a mode it left out. */
    private _cyclePermissionMode(): void {
        const order = permissionItems().map((it) => it.value);
        if (!order.length) {
            return;
        }
        // Current mode absent from the list (a policy can withdraw one mid-session):
        // restart from the first rather than leaving it to the modulo.
        const i = order.indexOf(appState.permissionMode);
        const next = i < 0 ? order[0] : order[(i + 1) % order.length];
        appState.permissionMode = next;
        bridge.sendNotification<SetPermissionModeNotification>(
            Msg.fromWebView.cli.setPermissionMode,
            { mode: next },
        );
    }

    private _onSelectAtLive = (e: CustomEvent<{ token: string; isDir: boolean }>): void => {
        const ta = this._ta;
        if (!ta) {
            return;
        }
        // Replace the current `@xxx` token (last `@` to caret) with the pick.
        const caret = ta.selectionStart ?? ta.value.length;
        const before = ta.value.slice(0, caret);
        const start = before.lastIndexOf('@');
        if (start < 0) {
            return;
        }
        ta.value = ta.value.slice(0, start) + e.detail.token + ta.value.slice(caret);
        const newCaret = start + e.detail.token.length;
        ta.setSelectionRange(newCaret, newCaret);
        this._hasText = ta.value.trim().length > 0;
        this._autoResize();
        // Folders used to keep the menu open so it could be re-queried one level down; the
        // recursive search made that walk pointless, so every pick now ends the token.
        this._atOpen = false;
        this._atItems = [];
        ta.focus({ preventScroll: true });
    };

    private _micPrefix = '';
    private _micSuffix = '';

    private _onMicTranscript = (e: CustomEvent<{ text: string; isFinal: boolean }>): void => {
        const ta = this._ta;
        ta.value = this._micPrefix + e.detail.text + (e.detail.isFinal ? this._micSuffix : '');
        if (e.detail.isFinal) {
            this._micPrefix = this._micPrefix + e.detail.text;
        }
        ta.dispatchEvent(new Event('input'));
        this._autoResize();
    };

    private _onMicStart = (): void => {
        const ta = this._ta;
        const pos = ta.selectionStart ?? ta.value.length;
        const prefix = ta.value.slice(0, pos);
        const sep = prefix && !prefix.endsWith(' ') ? ' ' : '';
        this._micPrefix = prefix + sep;
        this._micSuffix = ta.value.slice(pos);
        ta.value = this._micPrefix;
    };

    private _onMicEnd = (): void => {
        const ta = this._ta;
        ta.value = this._micPrefix + this._micSuffix;
        ta.dispatchEvent(new Event('input'));
        this._autoResize();
    };

    private _onSendClick = (): void => {
        if (this._isBusy) {
            this.stop();
        } else {
            this._submit();
        }
    };

    private _submit(): void {
        const text = this._ta.value.trim();
        if (!text && this._attachments.length === 0) {
            return;
        }
        // Client-minted uuid: the CLI reuses it for the JSONL entry, so
        // fork_session works on live messages too (not just replayed ones).
        const uuid = crypto.randomUUID();
        // Build the final prompt once: prepend the active editor's <ide_*> tag
        // when the IDE-context toggle is on. Doing it here (not in _dispatch)
        // means the echoed bubble and the CLI get the identical text — and
        // cv-message parses the tag back into a file/selection chip.
        // A slash command never carries IDE context (matches the VS Code webview:
        // ct = enabled && !startsWith('/')) — the file/selection chip would be noise on /model etc.
        const ctx =
            appState.ideContextEnabled && !text.startsWith('/') ? appState.ideContext : null;
        const ideBlock = !ctx?.filePath
            ? ''
            : ctx.hasSelection
              ? `<ide_selection>The user selected the lines ${ctx.startLine} to ${ctx.endLine} from ${ctx.filePath}:\n\nThis may or may not be related to the current task.</ide_selection>\n`
              : `<ide_opened_file>The user opened the file ${ctx.filePath} in the IDE. This may or may not be related to the current task.</ide_opened_file>\n`;
        const payload = { text: ideBlock + text, attachments: this._attachments, uuid };
        // Echo the user bubble locally now (stream-json doesn't reflect the
        // submitted message back). Same path for live and queued messages, so
        // a queued one shows up immediately instead of waiting for the flush.
        this._echoUserMessage(payload);
        if (this._isBusy) {
            // Already running → enqueue. Drained when isBusy flips to false.
            this._setQueue([...this._queue, payload]);
        } else {
            this._dispatch(payload);
        }
        // Append to the ↑/↓ history (skip a consecutive duplicate, shell-style).
        if (text && this._promptHistory[this._promptHistory.length - 1] !== text) {
            this._promptHistory.push(text);
        }
        this._resetHistoryNav();
        this._attachments = [];
        this.clear();
        this._ta.focus({ preventScroll: true });
    }

    private _resetHistoryNav(): void {
        this._historyIdx = -1;
        this._historyDraft = '';
    }

    /** ↑/↓ drive the prompt history, but only from the first/last **visual** line —
     *  anywhere else they must still move the caret, recalled prompt included: a long
     *  one is there to be edited, and losing it to the next history entry on the first
     *  ↑ is the same bug in the other direction. Soft wrap has no `\n` to look for, and
     *  only the engine that laid the text out knows where the visual lines fall, so let
     *  the default run and read the outcome: a caret that did not move had no line to
     *  move to. */
    private _historyOnArrow(dir: -1 | 1): void {
        const ta = this._ta;
        if (ta.selectionStart !== ta.selectionEnd) {
            return; // a selection collapses instead of moving — not a line change
        }
        const before = ta.selectionStart ?? 0;
        // Read the caret only once the default action has moved it — keydown fires
        // before that. A macrotask is the way to land after it: a microtask drains at
        // the end of THIS task and would always see the caret unmoved, and rAF ties
        // the read to the paint cycle, which has nothing to do with the caret.
        setTimeout(() => {
            // Moved at all → there was a line to move to, so the caret was not on the
            // edge line and the history stays out of it. Landing on 0/value.length is
            // NOT a reliable second signal: ↓ from a long line onto a short last one
            // ends at value.length having genuinely changed line, and a recalled prompt
            // starts there to begin with. So the caret sitting mid-line on the edge row
            // costs one extra keypress to reach the history — the way a shell behaves,
            // and the harmless way to be wrong: the draft is never lost.
            if ((ta.selectionStart ?? 0) !== before) {
                return;
            }
            if (dir === -1) {
                this._historyPrev();
            } else {
                this._historyNext();
            }
        }, 0);
    }

    /** ↑: recall an older prompt. Returns true if it handled the key. */
    private _historyPrev(): boolean {
        if (this._promptHistory.length === 0) {
            return false;
        }
        if (this._historyIdx === -1) {
            // Entering history: stash the live draft, start at the newest prompt.
            this._historyDraft = this._ta.value;
            this._historyIdx = this._promptHistory.length - 1;
        } else if (this._historyIdx > 0) {
            this._historyIdx--;
        } else {
            return true; // at the oldest — swallow the key, stay put
        }
        this._applyHistoryEntry(this._promptHistory[this._historyIdx], -1);
        return true;
    }

    /** ↓: go forward toward the live draft. Returns true if it handled the key. */
    private _historyNext(): boolean {
        if (this._historyIdx === -1) {
            return false; // not navigating — let ↓ move the cursor.
        }
        if (this._historyIdx < this._promptHistory.length - 1) {
            this._historyIdx++;
            this._applyHistoryEntry(this._promptHistory[this._historyIdx]);
        } else {
            // Past the newest → restore the draft and exit history.
            this._historyIdx = -1;
            this._applyHistoryEntry(this._historyDraft);
            this._historyDraft = '';
        }
        return true;
    }

    /** Apply a recalled entry, caret parked on the edge line the arrow came from: ↑
     *  leaves it at the start, ↓ at the end (VS Code and zsh both keep the caret on
     *  that line). Parking it at the far end instead would make the next press climb
     *  the whole recalled prompt before it could reach the entry beyond it. */
    private _applyHistoryEntry(text: string, dir: -1 | 1 = 1): void {
        const ta = this._ta;
        ta.value = text;
        const caret = dir === -1 ? 0 : text.length;
        ta.setSelectionRange(caret, caret);
        this._hasText = text.trim().length > 0;
        this._autoResize();
    }

    /** Echo the submitted message into the chat as a user bubble, mirroring what
     *  the host used to do server-side. Attachment chips are lazy (uuid+blockIdx):
     *  they resolve from the JSONL on click, matching the history-replay format so
     *  there's a single rendering path. blockIdx is the send-order index, matching
     *  the content[] block the host writes. */
    private _echoUserMessage(payload: {
        text: string;
        attachments: Attachment[];
        uuid: string;
    }): void {
        if (!payload.text && payload.attachments.length === 0) {
            return;
        }
        const images: UserImageDto[] = [];
        const files: UserFileDto[] = [];
        payload.attachments.forEach((att, i) => {
            if (att.isImage) {
                images.push({
                    uuid: payload.uuid,
                    blockIdx: i,
                    mediaType: att.mediaType,
                    preview: att.preview ?? null,
                });
            } else {
                files.push({ name: att.name, uuid: payload.uuid, blockIdx: i });
            }
        });
        const msg: UserTextNotification = {
            text: payload.text,
            uuid: payload.uuid,
            images: images.length > 0 ? images : null,
            files: files.length > 0 ? files : null,
            parentToolUseId: null,
        };
        bridge.emit(Msg.toWebView.chat.userText, msg);
    }

    private _dispatch(payload: { text: string; attachments: Attachment[]; uuid: string }): void {
        appState.isBusy = true;
        bridge.sendNotification<SendPromptNotification>(Msg.fromWebView.cli.sendPrompt, payload);
    }

    /**
     * Replace the queue and mirror its uuids into the shared state — how cv-app knows which
     * bubbles are on screen without having been sent.
     *
     * Every write goes through here, so the two cannot drift. The payloads stay with the component
     * that sends them (text and attachments are of no use to anyone else); only the uuids, which
     * another component does need, are published.
     */
    private _setQueue(queue: typeof this._queue): void {
        this._queue = queue;
        appState.queuedUuids = queue.map((q) => q.uuid);
    }

    /**
     * Drop what is still queued and take its bubbles with it — on Stop, and when the session is
     * swapped out from under it. They were echoed on submit but never reached the CLI, so leaving
     * them on screen would show the model messages it has never been told about: the same
     * divergence a retraction causes, from our own side.
     */
    dropQueue(): void {
        if (this._queue.length === 0) {
            return;
        }
        const uuids = this._queue.map((q) => q.uuid);
        this._setQueue([]);
        this.dispatchEvent(
            new CustomEvent('queued-dropped', { detail: { uuids }, bubbles: true, composed: true }),
        );
    }

    /** One queued message, taken out from the row above the composer. Same event as dropQueue with
     *  a list of one, so the transcript side stays the single path it already is. */
    private _onDropQueued = (e: CustomEvent<{ uuid: string }>): void => {
        const { uuid } = e.detail;
        if (!this._queue.some((q) => q.uuid === uuid)) {
            return;
        }
        this._setQueue(this._queue.filter((q) => q.uuid !== uuid));
        this.dispatchEvent(
            new CustomEvent('queued-dropped', {
                detail: { uuids: [uuid] },
                bubbles: true,
                composed: true,
            }),
        );
    };

    private _onClearQueue = (): void => {
        this.dropQueue();
    };

    private _flushQueue(): void {
        if (this._isBusy || this._queue.length === 0) {
            return;
        }
        const [next, ...rest] = this._queue;
        this._setQueue(rest);
        this._dispatch(next);
        // The state above unfades the bubble; this says WHICH one left, so cv-app can move it below
        // the reply it had been sitting above.
        this.dispatchEvent(
            new CustomEvent('queued-sent', {
                detail: { uuid: next.uuid },
                bubbles: true,
                composed: true,
            }),
        );
    }

    private _addAttachment(att: Attachment): void {
        this._attachments = [...this._attachments, att];
    }

    private _removeAttachment(idx: number): void {
        this._attachments = this._attachments.filter((_, i) => i !== idx);
    }

    private async _readFiles(files: FileList | File[]): Promise<void> {
        const rejected: string[] = [];
        for (const f of Array.from(files)) {
            if (!isAllowedUpload(f)) {
                rejected.push(f.name);
                continue;
            }
            try {
                this._addAttachment(await readAsAttachment(f));
            } catch (e) {
                console.error('[cv-prompt] read failed', f.name, e);
            }
        }
        if (rejected.length > 0) {
            this._noticeStack?.push({
                severity: 'error',
                message: unsupportedMessage(rejected),
                key: 'upload-rejected',
                actionLabel: 'Open settings',
                actionMessage: Msg.fromWebView.open.options,
                actionPayload: { page: 'chat' },
            });
        }
    }

    /** Public because the drop is caught in WPF, not here (ui_files_dropped). */
    async addDroppedFiles(files: File[]): Promise<void> {
        await this._readFiles(files);
    }

    private _onPickFile = (): void => {
        this._filePicker?.click();
    };

    /** "Add content" menu item: insert "@" at the caret and open the file
     *  picker menu, as if the user had typed "@". */
    private _onAddMention = (): void => {
        this.insertAtCaret('@');
    };

    /** Send a prompt straight to the CLI (used by builtins like /clear, /compact
     *  and dynamic prompt-commands). Mirrors a user submit of that text. With `echo`,
     *  the text is also shown as a user bubble (slash commands picked from the menu) —
     *  same uuid as the dispatch so fork works on it. */
    sendPrompt(text: string, echo = false): void {
        const uuid = crypto.randomUUID();
        const payload = { text, attachments: [] as Attachment[], uuid };
        if (echo) {
            this._echoUserMessage(payload);
        }
        // A builtin that resets the session is also the way out of a stuck one, so it must not
        // queue behind the very turn it is meant to clear — the CLI takes these mid-turn.
        if (this._isBusy && !RESET_COMMANDS.has(text.trim().split(/\s+/, 1)[0])) {
            this._setQueue([...this._queue, payload]);
        } else {
            this._dispatch(payload);
        }
    }

    /** Insert text at the caret in the prompt box. For "@" this also opens the
     *  file-mention menu (so the "Reference a workspace file" item behaves like a
     *  typed "@"). A leading space is added when needed so it parses fresh. */
    insertAtCaret(text: string): void {
        const ta = this._ta;
        if (!ta) {
            return;
        }
        ta.focus({ preventScroll: true });
        const start = ta.selectionStart ?? ta.value.length;
        const end = ta.selectionEnd ?? ta.value.length;
        const before = ta.value.slice(0, start);
        const needsSpace = before.length > 0 && !/\s$/.test(before);
        const insert = (needsSpace ? ' ' : '') + text;
        ta.value = before + insert + ta.value.slice(end);
        const caret = start + insert.length;
        ta.setSelectionRange(caret, caret);
        this._hasText = ta.value.trim().length > 0;
        this._autoResize();
        if (text === '@') {
            // Open the @ menu and request the (empty-query) file list.
            this._atOpen = true;
            this._fetchSuggestions('');
        }
    }

    /** Open the upload-from-computer file picker. */
    pickFile(): void {
        this._onPickFile();
    }

    /** Open the model list above the textarea (like the `/` menu). */
    openModelPicker(): void {
        this._permissionListOpen = false;
        this._modelListOpen = true;
    }

    /** Open the permission-mode list above the textarea. */
    openPermissionPicker(): void {
        this._modelListOpen = false;
        this._permissionListOpen = true;
    }

    /** Open a URL in the system browser. */
    openExternalUrl(url: string): void {
        bridge.sendNotification<ExternalUrlNotification>(Msg.fromWebView.open.externalUrl, { url });
    }

    /** Open the extension's Tools → Options page. */
    openOptions(): void {
        bridge.sendNotification<OpenOptionsNotification>(Msg.fromWebView.open.options, {});
    }

    /** Open a fresh interactive CLI pane (host runs PaneLauncher.OpenNew(Cli)). */
    openCliTerminal(): void {
        bridge.sendNotification(Msg.fromWebView.open.cliTerminal, {});
    }

    /** Open the session picker (host opens the toolbar's History popup). */
    openSessionHistory(): void {
        bridge.sendNotification(Msg.fromWebView.open.sessionHistory, {});
    }

    /** Open a fresh chat pane (host runs PaneLauncher.OpenNew(Chat)). */
    openChatPane(): void {
        bridge.sendNotification(Msg.fromWebView.open.chatPane, {});
    }

    openPluginManager(): void {
        openPluginManagerDialog();
    }

    /** Merge keys into the CLI flag-settings layer (Model menu controls). */
    applyFlagSettings(settings: Record<string, unknown>): void {
        bridge.sendNotification(Msg.fromWebView.cli.applyFlagSettings, { settings });
    }

    /** Hot-swap the runtime thinking budget (Thinking toggle). */
    setMaxThinkingTokens(maxThinkingTokens: number, display: string | null): void {
        bridge.sendNotification(Msg.fromWebView.cli.setMaxThinkingTokens, {
            maxThinkingTokens,
            display,
        });
    }

    setRemoteControl(enabled: boolean): void {
        bridge.sendNotification(Msg.fromWebView.cli.setRemoteControl, { enabled });
    }

    private _onFilePickerChange = (e: Event): void => {
        const input = e.target as HTMLInputElement;
        if (input.files) {
            void this._readFiles(input.files);
        }
        input.value = '';
    };

    private _onDragOver = (e: DragEvent): void => {
        e.preventDefault();
        this._dragOver = true;
    };

    private _onDragLeave = (e: DragEvent): void => {
        const box = this.renderRoot.querySelector('#box');
        if (box && !box.contains(e.relatedTarget as Node | null)) {
            this._dragOver = false;
        }
    };

    private _onDrop = (e: DragEvent): void => {
        e.preventDefault();
        this._dragOver = false;
        if (e.dataTransfer?.files?.length) {
            void this._readFiles(e.dataTransfer.files);
        }
    };

    /** kind, not an image/ mime: a pasted PDF then meets the same allow-list as drop and the
     *  picker, which either takes it or says why — instead of vanishing. */
    private _onPaste = (e: ClipboardEvent): void => {
        const files = Array.from(e.clipboardData?.items ?? [])
            .filter((i) => i.kind === 'file')
            .map((i) => i.getAsFile())
            .filter((f): f is File => f !== null);
        if (files.length === 0) {
            return; // plain text: leave the paste to the browser
        }
        e.preventDefault();
        void this._readFiles(files);
    };

    /** Stop the turn: interrupt the CLI, drop anything queued behind it, free the UI.
     *  Public because Esc (handled globally in cv-app) is the same gesture as the Stop
     *  button — it must not stop halfway and leave the queue to fire on the next flush. */
    stop(): void {
        bridge.sendNotification(Msg.fromWebView.cli.stop, {});
        // Queued prompts were meant to follow the turn being stopped; flushing them after an
        // interrupt would send messages the user never confirmed at a CLI they just halted.
        this.dropQueue();
        // Optimistic reset: free the UI now in case the CLI is wedged and
        // never sends `result`.
        appState.isBusy = false;
    }

    /** The attach menu's "Slash command" item: toggle the full palette with its own search box
     *  focused (all sections, the menu owns filtering + keyboard nav). */
    private _onOpenCommands = (): void => {
        // Re-opening it while its own list is open closes it (toggle). Not when the
        // palette is open on a single row: that came from another trigger, so this click means
        // "show me all of them", not "close".
        if (this._cmdOpen && this._cmdSearchable && !this._cmdOnly) {
            this._closeCommandMenu();
            return;
        }
        this._cmdQuery = '';
        this._cmdSearchable = true;
        this._cmdOnly = null; // the full list, even if the Effort trigger narrowed it last
        this._cmdOpen = true;
        this._atOpen = false;
    };

    /** The Effort trigger: the same palette, opened on its Effort row alone. The row already
     *  carries the slider, so there is nothing here the menu doesn't already do. */
    private _onOpenEffort = (): void => {
        if (this._cmdOpen && this._cmdOnly) {
            this._closeCommandMenu();
            return;
        }
        this._modelListOpen = false;
        this._permissionListOpen = false;
        this._atOpen = false;
        this._cmdQuery = '';
        this._cmdSearchable = false;
        this._cmdOnly = ['effort'];
        this._cmdOpen = true;
    };

    private _closeCommandMenu = (): void => {
        if (this._cmdOpen) {
            this._cmdOpen = false;
            this._cmdQuery = '';
            this._cmdSearchable = false;
            this._cmdOnly = null;
        }
    };

    /** A command was chosen: strip the leading `/token` (if the prompt is just
     *  that token), close the menu, then run the command through the host. */
    private _onSelectCommand = (e: CustomEvent<{ command: ChatCommand }>): void => {
        const ta = this._ta;
        if (ta) {
            // Strip the `/token` the menu was filtering on (wherever it is), so
            // e.g. "/att" doesn't linger before the command acts. Leaves the
            // rest of the prompt intact.
            const caret = ta.selectionStart ?? ta.value.length;
            const before = ta.value.slice(0, caret);
            const stripped = before.replace(/(^|\s)\/[^\s/]*$/, '$1');
            if (stripped !== before) {
                ta.value = stripped + ta.value.slice(caret);
                const c = stripped.length;
                ta.setSelectionRange(c, c);
                this._hasText = ta.value.trim().length > 0;
                this._autoResize();
            }
        }
        this._closeCommandMenu();
        e.detail.command.run(this);
    };

    private _onSelectModel = (e: CustomEvent<{ value: string }>): void => {
        // Re-picking the current model is a no-op: skip the set_model round-trip and
        // the "Switched to X" notice (else the same model reads as a switch to itself).
        if (e.detail.value === appState.currentModel) {
            return;
        }
        appState.currentModel = e.detail.value;
        bridge.sendNotification<SetModelNotification>(Msg.fromWebView.cli.setModel, {
            model: e.detail.value,
        });
        // Bubble up to cv-app so it can render the "Switched to X" notice — the
        // notice only fires for a user-driven menu pick, never for the ui_init
        // seed or a runtime cli_model_changed.
        this.dispatchEvent(
            new CustomEvent('model-switched', {
                detail: { value: e.detail.value },
                bubbles: true,
                composed: true,
            }),
        );
        this._modelListOpen = false;
        this._ta?.focus({ preventScroll: true });
    };

    /** Toolbar trigger asked for the model picker. Toggles (unlike openModelPicker(), which the
     *  menu's "Switch model…" row calls to always open) and closes the menus it is exclusive with. */
    private _onOpenModels = (): void => {
        this._permissionListOpen = false;
        this._closeCommandMenu();
        this._atOpen = false;
        this._modelListOpen = !this._modelListOpen;
        // Same reason as the permission trigger: navigation is driven from the textarea's
        // keydown, so give focus back after the click for the list to be keyboard-navigable.
        if (this._modelListOpen) {
            this._ta?.focus({ preventScroll: true });
        }
    };

    /** Trigger asked for the mode picker; the other above-textarea menus are exclusive with it. */
    private _onOpenPermissions = (): void => {
        this._modelListOpen = false;
        this._closeCommandMenu();
        this._atOpen = false;
        this._permissionListOpen = !this._permissionListOpen;
        // Arrow/Enter navigation lives on the textarea's keydown (that's where the other
        // menus are driven from), but clicking the toolbar trigger leaves focus on the
        // button — put it back so the list is keyboard-navigable straight away.
        if (this._permissionListOpen) {
            this._ta?.focus({ preventScroll: true });
        }
    };

    private _onSelectPermission = (e: CustomEvent<{ value: PermissionMode }>): void => {
        appState.permissionMode = e.detail.value;
        bridge.sendNotification<SetPermissionModeNotification>(
            Msg.fromWebView.cli.setPermissionMode,
            { mode: e.detail.value },
        );
        this._permissionListOpen = false;
        this._ta?.focus({ preventScroll: true });
    };

    private _renderChips() {
        if (this._attachments.length === 0) {
            return nothing;
        }
        return html`
            <div id="attachments">
                ${this._attachments.map(
                    (a, i) =>
                        html`<cv-attach-chip
                            .src=${a.isImage ? a.dataUrl : iconUrl(a.name)}
                            .label=${a.name}
                            removable
                            title=${a.name}
                            @click=${
                                a.isImage
                                    ? () => openLightbox({ src: a.dataUrl, name: a.name })
                                    : () => openAttachment(a.name, a.base64, a.mediaType)
                            }
                            @remove=${() => this._removeAttachment(i)}
                        ></cv-attach-chip>`,
                )}
            </div>
        `;
    }

    override render() {
        // While a permission/question prompt is pending, hide the composer — the user answers it
        // in the overlay above; typing the next message isn't allowed until then. Hide via CSS
        // (?hidden), NOT by unmounting: the textarea is uncontrolled, so unmounting would drop any
        // in-progress draft the user had typed while the turn was running.
        return html`
            <div
                id="box"
                ?hidden=${this._pendingPermission}
                class=${this._dragOver ? 'drag-over' : ''}
                data-permission-mode=${this._permissionMode}
                @dragover=${this._onDragOver}
                @dragleave=${this._onDragLeave}
                @drop=${this._onDrop}
            >
                <cv-notice-stack @notice-dismissed=${this._onNoticeDismissed}></cv-notice-stack>
                <cv-queue-row
                    .messages=${this._queue}
                    @drop-queued=${this._onDropQueued}
                    @clear-queue=${this._onClearQueue}
                ></cv-queue-row>
                <!-- Everything that travels with this message, and the one border that says so. -->
                <div id="field">
                    ${this._renderChips()}
                    <div id="input-line">
                        <textarea
                            id="input"
                            placeholder=${
                                this._isBusy
                                    ? 'Queue another message… (Esc to stop)'
                                    : // @ and / are the two things nobody discovers on their own, so they
                                      // lead; the send key follows because it's configurable (Ctrl+Enter).
                                      `Send a message…  @ for files, / for commands  ·  ${
                                          appState.ui.useCtrlEnterToSend ? 'Ctrl+Enter' : 'Enter'
                                      } to send`
                            }
                            rows="1"
                            aria-label="Prompt"
                            @input=${this._onInput}
                            @keydown=${this._onKeyDown}
                            @paste=${this._onPaste}
                            @blur=${this._onTextareaBlur}
                        ></textarea>
                        <fluent-button
                            id="send"
                            class=${this._isBusy ? 'is-busy' : ''}
                            appearance="primary"
                            shape="rounded"
                            size="small"
                            icon-only
                            ?disabled=${!this._isBusy && !this._hasText && this._attachments.length === 0}
                            @click=${this._onSendClick}
                        >
                            ${unsafeHTML(this._isBusy ? Stop16Filled : Send16Filled)}
                        </fluent-button>
                        <!-- The key is named because it is configurable: this is the one place
                             that says which of Enter / Ctrl+Enter is in force. -->
                        <fluent-tooltip anchor="send" positioning="above-end"
                            >${
                                this._isBusy
                                    ? 'Stop — Esc'
                                    : `Send — ${appState.ui.useCtrlEnterToSend ? 'Ctrl+Enter' : 'Enter'}`
                            }</fluent-tooltip
                        >
                    </div>
                </div>
                <cv-at-menu
                    .anchor=${this._ta ?? null}
                    .items=${this._atItems}
                    ?open=${this._atOpen}
                    @select-at=${this._onSelectAtLive}
                ></cv-at-menu>
                <cv-command-menu
                    .query=${this._cmdQuery}
                    ?open=${this._cmdOpen}
                    .searchable=${this._cmdSearchable}
                    .only=${this._cmdOnly}
                    .host=${this}
                    @select-command=${this._onSelectCommand}
                    @close-commands=${this._closeCommandMenu}
                ></cv-command-menu>
                <cv-model-list
                    ?open=${this._modelListOpen}
                    @select-model=${this._onSelectModel}
                ></cv-model-list>
                <cv-permission-list
                    ?open=${this._permissionListOpen}
                    @select-permission=${this._onSelectPermission}
                ></cv-permission-list>
                <div id="toolbar">
                    <div
                        id="toolbar-left"
                        @open-commands=${this._onOpenCommands}
                        @pick-file=${this._onPickFile}
                        @add-mention=${this._onAddMention}
                    >
                        <cv-attach-menu></cv-attach-menu>
                        <!-- Dictation produces text, the way attach produces content: it belongs
                             with what fills the message, not with the settings that describe it. -->
                        <cv-mic-button
                            @transcript=${this._onMicTranscript}
                            @recording-start=${this._onMicStart}
                            @recording-end=${this._onMicEnd}
                        ></cv-mic-button>
                        <cv-subagent-chip .tasks=${this._subagentTasks}></cv-subagent-chip>
                        <!-- Before the file chip, not after: that one is the only item here that
                             resizes (it absorbs the row's spare width), so anything past it moves
                             on every editor change. -->
                        <cv-remote-chip></cv-remote-chip>
                        <cv-ide-context-badge></cv-ide-context-badge>
                    </div>
                    <div
                        id="toolbar-right"
                        @open-models=${this._onOpenModels}
                        @open-permissions=${this._onOpenPermissions}
                        @open-effort=${this._onOpenEffort}
                    >
                        <!-- The settings that outlive the message: the left of the row is what goes
                             into it, the right is how it will be answered. The gap between the two
                             groups is the separation. -->
                        <cv-thinking-toggle .host=${this}></cv-thinking-toggle>
                        <cv-effort-selector></cv-effort-selector>
                        <cv-model-selector></cv-model-selector>
                        <cv-permission-selector></cv-permission-selector>
                        <!-- Out here with the settings, not in the field with send: how much
                             context is left is about the conversation, not about the message you
                             are writing. -->
                        <cv-context-gauge></cv-context-gauge>
                    </div>
                </div>
                <input
                    data-cv-file-picker
                    type="file"
                    multiple
                    hidden
                    @change=${this._onFilePickerChange}
                />
            </div>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-prompt': CvPrompt;
    }
}
