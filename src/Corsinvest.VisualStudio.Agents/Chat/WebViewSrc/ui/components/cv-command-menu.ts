/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import Fuse from 'fuse.js';
import { LitElement, html, nothing } from 'lit';
import { customElement, property, query } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import {
    ChatCommand,
    type CommandHost,
    allCommands,
    groupCommands,
    SlashCommand,
} from '../../core/commands';
import { state as appState } from '../../core/state';
import './cv-segmented-slider';
import type { SliderStop } from './cv-segmented-slider';
import './cv-popover-list';
import type { CvPopoverList, ListSection } from './cv-popover-list';

/**
 * Unified command palette for the input toolbar — the `/` menu and the lightning button open this.
 * A wrapper over cv-popover-list: it owns the DOMAIN logic (fuzzy filter with Fuse, per-section
 * grouping, the heterogeneous trailing controls) and passes the grouped commands + a renderRow to
 * the generic list, which owns the popover/navigation/scroll. Parent-controlled like cv-at-menu:
 * cv-prompt drives `open`/`query` and forwards keys via moveSelection()/pickActive().
 */
@customElement('cv-command-menu')
export class CvCommandMenu extends LitElement {
    @property({ type: Boolean, reflect: true }) open = false;
    /** Filter text. Driven by the parent (textarea) when opened from `/`; owned by the list's
     *  own search box when opened from the lightning button (`searchable`). */
    @property({ attribute: false }) query = '';
    /** True when opened from the lightning button: show the search box in the list. */
    @property({ type: Boolean }) searchable = false;
    /** The command host (cv-prompt), needed by inline controls (slider/toggle). */
    @property({ attribute: false }) host!: CommandHost;

    @query('cv-popover-list') private _list?: CvPopoverList;

    override createRenderRoot() {
        // Light DOM: pass-through wrapper, no styles of its own; cv-popover-list owns shadow + CSS.
        return this;
    }

    // Parent (cv-prompt) drives navigation through these.
    moveSelection(delta: number): void {
        this._list?.moveSelection(delta);
    }
    pickActive(): void {
        this._list?.pickActive();
    }

    /** Display groups: always the per-section grouping, like VS Code. With a query the order
     *  follows Fuse relevance; with none each section is its natural order. */
    private _displayGroups(): ListSection<ChatCommand>[] {
        const hasQuery = this.query.trim().length > 0;
        return groupCommands(this._filtered(), hasQuery).map((g) => ({
            label: g.label,
            items: g.commands,
        }));
    }

    /** Fuse config mirrors the VS Code extension: label/aliases/id weigh more than description. */
    private _fuseSearch(commands: ChatCommand[], query: string): ChatCommand[] {
        const docs = commands.map((c) => ({
            cmd: c,
            name: c.label.replace(/^\//, ''),
            aliases: c.aliases.join(' '),
            id: c.id,
            description: c.description ?? '',
        }));
        const fuse = new Fuse(docs, {
            includeScore: true,
            threshold: 0.3,
            location: 0,
            distance: 100,
            keys: [
                { name: 'name', weight: 3 },
                { name: 'aliases', weight: 2 },
                { name: 'id', weight: 2 },
                { name: 'description', weight: 0.5 },
            ],
        });
        // Re-rank like VS Code: exact name/alias match first, then prefix, then Fuse score.
        const q = query.toLowerCase();
        const name = (c: ChatCommand): string => c.label.replace(/^\//, '').toLowerCase();
        const aliasHits = (c: ChatCommand, pred: (a: string) => boolean): boolean =>
            c.aliases.some((a) => pred(a.toLowerCase()));
        return fuse
            .search(query)
            .sort((a, b) => {
                const exactA = name(a.item.cmd) === q || aliasHits(a.item.cmd, (x) => x === q);
                const exactB = name(b.item.cmd) === q || aliasHits(b.item.cmd, (x) => x === q);
                if (exactA !== exactB) {
                    return exactA ? -1 : 1;
                }
                const preA =
                    name(a.item.cmd).startsWith(q) || aliasHits(a.item.cmd, (x) => x.startsWith(q));
                const preB =
                    name(b.item.cmd).startsWith(q) || aliasHits(b.item.cmd, (x) => x.startsWith(q));
                if (preA !== preB) {
                    return preA ? -1 : 1;
                }
                return (a.score ?? 0) - (b.score ?? 0);
            })
            .map((r) => r.item.cmd);
    }

    /** Commands matching the current query (Fuse), or all commands unchanged when empty. */
    private _filtered(): ChatCommand[] {
        const q = this.query.trim();
        const all = allCommands();
        return q ? this._fuseSearch(all, q) : all;
    }

    /** Flat list of matching commands (the navigable set), in display order. */
    private get _flat(): ChatCommand[] {
        return this._displayGroups().flatMap((g) => g.items);
    }

    /** Hover tooltip: the command description + aliases. */
    private _tooltip(cmd: ChatCommand): string {
        const parts = cmd.description ? [cmd.description] : [];
        if (cmd.aliases.length > 0) {
            parts.push(`Aliases: ${cmd.aliases.join(', ')}`);
        }
        return parts.join('\n');
    }

    private _pick(cmd: ChatCommand): void {
        // keepMenuOpen commands (toggles/slider) act in place; the rest go to the parent.
        if (cmd.keepMenuOpen) {
            cmd.run(this.host);
            this.requestUpdate();
            return;
        }
        this.dispatchEvent(
            new CustomEvent<{ command: ChatCommand }>('select-command', {
                detail: { command: cmd },
                bubbles: true,
                composed: true,
            }),
        );
    }

    /** Right-side control for a row: toggle / effort slider / value label / argument hint. */
    private _renderTrailing(cmd: ChatCommand) {
        const ctrl = cmd.trailingControl;
        if (ctrl?.kind === 'toggle') {
            return html`<fluent-switch
                class="toggle"
                ?checked=${ctrl.on}
                @click=${(e: Event) => e.stopPropagation()}
                @change=${(e: Event) => {
                    e.stopPropagation();
                    this._pick(cmd);
                }}
            ></fluent-switch>`;
        }
        if (ctrl?.kind === 'slider') {
            return html`<span class="dots-wrap" @click=${(e: Event) => e.stopPropagation()}>
                <cv-segmented-slider
                    .stops=${ctrl.stops}
                    .activeValue=${ctrl.value}
                    @change=${(e: CustomEvent<SliderStop<number>>) => {
                        ctrl.onSet(this.host, e.detail.value);
                        this.requestUpdate();
                    }}
                ></cv-segmented-slider>
                <span class="dots-val">${ctrl.label}</span>
            </span>`;
        }
        if (ctrl?.kind === 'value') {
            return html`<span class="row-trailing">
                ${ctrl.icon ? html`<span class="trailing-icon">${unsafeHTML(ctrl.icon)}</span>` : nothing}
                ${ctrl.label}
            </span>`;
        }
        if (cmd instanceof SlashCommand && cmd.argumentHint) {
            return html`<span class="row-trailing">${cmd.argumentHint}</span>`;
        }
        if (cmd.id === 'report-problem' && appState.ui.appVersion) {
            return html`<span class="row-trailing">v${appState.ui.appVersion}</span>`;
        }
        return nothing;
    }

    override render() {
        if (!this.open) {
            return html``;
        }
        return html`
            <cv-popover-list
                .items=${this._flat}
                .sections=${this._displayGroups()}
                ?searchable=${this.searchable}
                .query=${this.query}
                emptyText="No matching commands"
                .renderRow=${(cmd: ChatCommand) => html`
                    ${
                        cmd.icon
                            ? html`<span class="row-icon">${unsafeHTML(cmd.icon)}</span>`
                            : html`<span class="row-icon"></span>`
                    }
                    <span class="row-label" title=${this._tooltip(cmd)}>${cmd.label}</span>
                    ${this._renderTrailing(cmd)}
                `}
                @search-input=${(e: CustomEvent<{ query: string }>) => (this.query = e.detail.query)}
                @select=${(e: CustomEvent<{ item: ChatCommand }>) => this._pick(e.detail.item)}
            ></cv-popover-list>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-command-menu': CvCommandMenu;
    }
}
