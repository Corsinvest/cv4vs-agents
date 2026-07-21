/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { html, css, nothing, type TemplateResult } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import Dismiss16Regular from '@fluentui/svg-icons/icons/dismiss_16_regular.svg';
import Delete16Regular from '@fluentui/svg-icons/icons/delete_16_regular.svg';
import ArrowSync16Regular from '@fluentui/svg-icons/icons/arrow_sync_16_regular.svg';
import ArrowDownload16Regular from '@fluentui/svg-icons/icons/arrow_download_16_regular.svg';
import Add16Regular from '@fluentui/svg-icons/icons/add_16_regular.svg';
import { dialogStyles, iconStyles } from '../styles/shared';
import { bridge } from '../../core/bridge';
import { Msg } from '../../core/bridge-messages';
import { fetchPlugins, fetchMarketplaces } from '../../core/lazy';
import { CvDialogBase } from './cv-dialog-base';
import type { PluginDto } from '../../core/generated/PluginDto';
import type { AvailablePluginDto } from '../../core/generated/AvailablePluginDto';
import type { MarketplaceDto } from '../../core/generated/MarketplaceDto';
import type { PluginOpResultNotification } from '../../core/generated/PluginOpResultNotification';

type Tab = 'installed' | 'available' | 'marketplaces';

// How many available cards to render without a search (255+ exist; the CLI can't filter, so we
// slice client-side). A search narrows the full list first, then the same cap applies.
const AVAILABLE_LIMIT = 30;

// Anthropic's official marketplace — flagged with a hippo, like VS Code.
const OFFICIAL_MARKETPLACE = 'claude-plugins-official';

// Strip the CLI's leading ✔/✘ (and stray whitespace) — the message-bar icon already conveys it.
function cleanOpMessage(msg: string): string {
    return msg.replace(/^[✔✘✓✗]\s*/u, '').trim();
}

// ISO timestamp → "26 Jun 2026"; empty string if unparseable.
function formatDate(iso: string): string {
    if (!iso) {
        return '';
    }
    const d = new Date(iso);
    return isNaN(d.getTime())
        ? ''
        : d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
}

// Human label for a marketplace source (mirrors the CLI's "Source: GitHub (owner/repo)").
function marketplaceSourceLabel(m: MarketplaceDto): string {
    switch (m.source) {
        case 'github':
            return `GitHub: ${m.repo ?? ''}`;
        case 'git':
            return `Git: ${m.url ?? ''}`;
        case 'url':
            return `URL: ${m.url ?? ''}`;
        case 'directory':
            return `Directory: ${m.path ?? ''}`;
        case 'file':
            return `File: ${m.path ?? ''}`;
        default:
            return m.source;
    }
}

// A browsable https URL for a marketplace, or null for local (directory/file) sources.
function marketplaceUrl(m: MarketplaceDto): string | null {
    if (m.source === 'github' && m.repo) {
        return `https://github.com/${m.repo}`;
    }
    if ((m.source === 'git' || m.source === 'url') && m.url?.startsWith('http')) {
        return m.url;
    }
    return null;
}

// Resolve an available plugin's source to a browsable URL. Url sources are already the final URL
// (git-subdir resolved host-side); a Relative "./path" is joined onto the owning marketplace's
// repo tree (mirrors VS Code).
function pluginUrl(p: AvailablePluginDto, marketplaces: MarketplaceDto[]): string | null {
    if (p.sourceKind === 'url') {
        return p.source;
    }
    if (p.sourceKind === 'relative') {
        const m = marketplaces.find((x) => x.name === p.marketplaceName);
        const rel = p.source.startsWith('./') ? p.source.slice(2) : p.source;
        if (m?.source === 'github' && m.repo) {
            return `https://github.com/${m.repo}/tree/main/${rel}`;
        }
        return m ? marketplaceUrl(m) : null;
    }
    return null;
}

/**
 * "Manage Plugins" dialog — 3 tabs (Installed / Available / Marketplaces). Backed by
 * `claude plugin … --json` one-shot processes (via the host's PluginService), because the live
 * chat process rejects plugin ops. List uses request/response; ops send a notification and the
 * host re-broadcasts a "plugins changed" banner to all chats. Create-on-open via dialog-host.
 */
@customElement('cv-plugin-manager')
export class CvPluginManager extends CvDialogBase {
    static override styles = [
        dialogStyles,
        iconStyles,
        css`
            /* Fixed dialog height + top-aligned content. Switching tabs changes content length;
             * a variable height shrinks the box under the pointer mid-click, and the click then
             * lands on the modal backdrop (which light-dismisses fluent-dialog). A fixed height
             * (vh scales with the VS window) keeps the box stable. */
            /* Body is a fixed-height flex column: title/tabs pinned, the tab-panel fills the rest. */
            fluent-dialog-body::part(content) {
                height: 70vh;
                display: flex;
                flex-direction: column;
                min-height: 0;
                overflow: hidden;
            }
            .tabs {
                margin-bottom: 10px;
                flex: 0 0 auto;
            }
            /* Tab panel fills the remaining height; its own inner list is the only scroller. */
            .tab-panel {
                flex: 1;
                min-height: 0;
                display: flex;
                flex-direction: column;
            }
            .op-bar {
                margin-bottom: 8px;
            }
            .search {
                flex: 0 0 auto;
            }
            .add-row {
                flex: 0 0 auto;
            }
            /* Single indeterminate bar under the tabs; kept laid out but hidden when idle. */
            .loading {
                display: block;
                margin-bottom: 8px;
            }
            .loading.idle {
                visibility: hidden;
            }
            /* Full-width search: lift Fluent's host max-width:400px and stretch the inner parts. */
            .search {
                width: 100%;
                max-width: none;
                box-sizing: border-box;
                margin-bottom: 8px;
            }
            .search::part(root),
            .search::part(control) {
                width: 100%;
                box-sizing: border-box;
            }
            .add-row fluent-text-input {
                flex: 1;
                max-width: none;
            }
            .add-row fluent-text-input::part(root),
            .add-row fluent-text-input::part(control) {
                width: 100%;
                box-sizing: border-box;
            }
            /* Counter badge wrapped in the tab's end slot (span wrapper projects reliably). */
            .section-head {
                font-size: var(--fontSizeBase200);
                font-weight: var(--fontWeightSemibold);
                color: var(--colorNeutralForeground3);
                text-transform: uppercase;
                letter-spacing: 0.04em;
                margin: 4px 0;
            }
            /* Only the list scrolls (not the whole body → tabs/search/add-row stay pinned). */
            .list {
                display: flex;
                flex-direction: column;
                gap: 8px;
                flex: 1;
                min-height: 0;
                overflow-y: auto;
            }
            .card {
                display: flex;
                align-items: center;
                gap: 10px;
                padding: 10px 12px;
                border: 1px solid var(--colorNeutralStroke2);
                border-radius: 6px;
            }
            .card .body {
                flex: 1;
                min-width: 0;
            }
            .card .title {
                font-weight: var(--fontWeightSemibold);
            }
            /* Available card head: name left, install count right (like VS Code). */
            .av-head {
                display: flex;
                align-items: baseline;
                justify-content: space-between;
                gap: 8px;
            }
            .installs {
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                white-space: nowrap;
                flex: 0 0 auto;
            }
            .card .desc {
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
                margin-top: 2px;
                /* Full text — wrap freely, no clamp (VS Code shows the whole description). */
                overflow-wrap: anywhere;
            }
            .card .meta {
                font-size: var(--fontSizeBase100);
                color: var(--colorNeutralForeground4);
                margin-top: 2px;
                font-variant-numeric: tabular-nums;
                /* Long source URLs wrap instead of overflowing the card. */
                overflow-wrap: anywhere;
            }
            .card .meta fluent-link {
                font-size: inherit;
            }
            .card .actions {
                display: flex;
                align-items: center;
                gap: 6px;
                flex: 0 0 auto;
            }
            .dot {
                width: 8px;
                height: 8px;
                border-radius: 50%;
                flex: 0 0 auto;
                background: var(--colorPaletteGreenForeground1);
            }
            .dot.off {
                background: var(--colorPaletteRedForeground1);
            }
            /* Installed card: top-aligned so the right column (dates ↔ actions) lines up with the title. */
            .ins-card {
                align-items: flex-start;
            }
            .ins-card .dot {
                margin-top: 5px;
            }
            .title-row {
                display: flex;
                align-items: baseline;
                gap: 8px;
                flex-wrap: wrap;
            }
            .sub {
                font-size: var(--fontSizeBase200);
                color: var(--colorNeutralForeground3);
            }
            /* Right column: shows dates by default, swaps to switch+trash on card hover. */
            .ins-right {
                flex: 0 0 auto;
                display: flex;
                align-items: center;
            }
            .dates {
                display: flex;
                flex-direction: column;
                align-items: flex-end;
                gap: 2px;
                font-size: var(--fontSizeBase100);
                color: var(--colorNeutralForeground4);
            }
            .date-row {
                display: inline-flex;
                align-items: center;
                gap: 4px;
                white-space: nowrap;
            }
            .date-row svg {
                width: 12px;
                height: 12px;
            }
            .ins-actions {
                display: none;
                align-items: center;
                gap: 8px;
            }
            .ins-card:hover .dates {
                display: none;
            }
            .ins-card:hover .ins-actions {
                display: flex;
            }
            .danger-hover:hover {
                color: var(--colorPaletteRedForeground1);
            }
            /* Marketplace actions appear on hover (or while a refresh is spinning). */
            .mkt-actions {
                display: flex;
                align-items: center;
                gap: 6px;
                flex: 0 0 auto;
                opacity: 0;
            }
            .mkt-card:hover .mkt-actions,
            .mkt-actions:has(.spin) {
                opacity: 1;
            }
            /* Available install action appears on hover (icon + tooltip, cleaner than a button). */
            .av-actions {
                display: flex;
                align-items: center;
                flex: 0 0 auto;
                opacity: 0;
            }
            .av-card:hover .av-actions {
                opacity: 1;
            }
            .add-row {
                display: flex;
                gap: 8px;
                margin-bottom: 10px;
            }
            .empty {
                color: var(--colorNeutralForeground3);
                padding: 16px 0;
                text-align: center;
            }
            /* Spinning ↻ while a marketplace refreshes. */
            @keyframes cv-spin {
                to {
                    transform: rotate(360deg);
                }
            }
            .spin svg {
                animation: cv-spin 0.8s linear infinite;
                transform-origin: center;
            }
        `,
    ];

    @state() private _tab: Tab = 'installed';
    @state() private _installed: PluginDto[] = [];
    @state() private _available: AvailablePluginDto[] = [];
    @state() private _marketplaces: MarketplaceDto[] = [];
    @state() private _query = '';
    @state() private _busy = false;
    @state() private _opMessage = '';
    @state() private _opError = false;
    // Name of the marketplace currently refreshing (its ↻ icon spins until the op completes).
    @state() private _refreshing: string | null = null;
    // Live value of the "add marketplace" input, so the + button enables only when non-empty.
    @state() private _addSource = '';

    private _offOpResult?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        // Re-list after any op the host reports done (updates the affected tab).
        this._offOpResult = bridge.onNotification<PluginOpResultNotification>(
            Msg.toWebView.plugins.opResult,
            (r) => this._onOpResult(r),
        );
        void this._loadAll();
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._offOpResult?.();
        this._offOpResult = undefined;
    }

    private async _loadAll(): Promise<void> {
        this._busy = true;
        try {
            const [plugins, markets] = await Promise.all([fetchPlugins(), fetchMarketplaces()]);
            this._installed = plugins.installed ?? [];
            this._available = plugins.available ?? [];
            this._marketplaces = markets.marketplaces ?? [];
        } catch {
            /* leave lists as-is; the op message area shows errors */
        } finally {
            this._busy = false;
        }
    }

    private _onOpResult(r: PluginOpResultNotification): void {
        this._opError = !r.ok;
        this._opMessage = r.message ?? '';
        this._refreshing = null; // stop the spinner (any op completed)
        // Refresh regardless of which tab acted: an install changes both Installed and Available.
        void this._loadAll();
    }

    private _onRefresh(name: string): void {
        this._refreshing = name;
        this._send(Msg.fromWebView.plugins.marketplaceRefresh, { name });
    }

    private _send(channel: string, payload: Record<string, unknown>): void {
        this._opMessage = '';
        this._opError = false;
        bridge.sendNotification(channel, payload);
    }

    private _onTabChange = (e: Event): void => {
        const active = (e.target as HTMLElement & { activetab?: HTMLElement }).activetab;
        const id = active?.id as Tab | undefined;
        if (id && id !== this._tab) {
            this._tab = id;
        }
    };

    // Compact install counts like VS Code: 1636 → "1.6k", 404300 → "404.3k", 1_000_000 → "1m".
    private _formatInstalls(n: number): string {
        if (n >= 1_000_000) {
            return `${(n / 1_000_000).toFixed(n % 1_000_000 ? 1 : 0)}m`;
        }
        if (n >= 1_000) {
            return `${(n / 1_000).toFixed(n % 1_000 ? 1 : 0)}k`;
        }
        return `${n}`;
    }

    /** Override the base toggle handler with a target guard: inner elements (tabs, badges) bubble
     *  their own toggle events, so only act on the dialog's own. */
    protected override _onDialogToggle = (e: Event): void => {
        if (e.target !== this._dlg) {
            return;
        }
        const detail = (e as CustomEvent<{ newState?: string }>).detail;
        if (detail?.newState === 'closed' && this.open) {
            this._close();
        }
    };

    override render() {
        if (!this.open) {
            return nothing;
        }
        return html`
            <fluent-dialog type="modal" aria-label="Manage Plugins" @toggle=${this._onDialogToggle}>
                <fluent-dialog-body>
                    <h2 slot="title">Manage Plugins</h2>
                    <fluent-button
                        slot="close"
                        appearance="transparent"
                        icon-only
                        aria-label="Close"
                        >${unsafeHTML(Dismiss16Regular)}</fluent-button
                    >
                    <fluent-progress-bar
                        class="loading ${this._busy ? '' : 'idle'}"
                        aria-label="Loading"
                    ></fluent-progress-bar>
                    ${
                        this._opMessage
                            ? html`<fluent-message-bar
                                  class="op-bar"
                                  intent=${this._opError ? 'error' : 'success'}
                              >
                                  <span>${cleanOpMessage(this._opMessage)}</span>
                                  <fluent-button
                                      slot="dismiss"
                                      appearance="transparent"
                                      icon-only
                                      title="Dismiss"
                                      @click=${() => {
                                          this._opMessage = '';
                                      }}
                                      >${unsafeHTML(Dismiss16Regular)}</fluent-button
                                  >
                              </fluent-message-bar>`
                            : nothing
                    }
                    <fluent-tablist
                        class="tabs"
                        size="small"
                        activeid=${this._tab}
                        @change=${this._onTabChange}
                    >
                        <fluent-tab id="installed">Installed</fluent-tab>
                        <fluent-tab id="available">Available</fluent-tab>
                        <fluent-tab id="marketplaces">Marketplaces</fluent-tab>
                    </fluent-tablist>
                    <div class="tab-panel">${this._renderTab()}</div>
                </fluent-dialog-body>
            </fluent-dialog>
        `;
    }

    private _renderTab(): TemplateResult {
        if (this._tab === 'installed') {
            return this._renderInstalled();
        }
        if (this._tab === 'available') {
            return this._renderAvailable();
        }
        return this._renderMarketplaces();
    }

    private _renderInstalled(): TemplateResult {
        if (this._installed.length === 0) {
            return html`<div class="empty">No plugins installed.</div>`;
        }
        // Active plugins first, disabled ones at the bottom; stable name order within each group.
        const sorted = this._installed
            .slice()
            .sort((a, b) =>
                a.enabled === b.enabled ? a.id.localeCompare(b.id) : a.enabled ? -1 : 1,
            );
        return html`<div class="list">
            ${sorted.map((p) => {
                const mktUrl = marketplaceUrl(
                    this._marketplaces.find((m) => m.name === p.marketplace) ??
                        ({} as MarketplaceDto),
                );
                const installed = formatDate(p.installedAt);
                const updated = p.lastUpdated !== p.installedAt ? formatDate(p.lastUpdated) : '';
                return html` <div class="card ins-card">
                    <span class="dot ${p.enabled ? '' : 'off'}"></span>
                    <div class="body">
                        <div class="title-row">
                            <span class="title">${p.name}</span>
                            <span class="sub">v${p.version} · ${p.scope}</span>
                        </div>
                        <div class="meta">
                            ${
                                mktUrl
                                    ? html`<fluent-link
                                          href=${mktUrl}
                                          target="_blank"
                                          rel="noopener noreferrer"
                                          >${p.marketplace}</fluent-link
                                      >`
                                    : `from ${p.marketplace}`
                            }
                        </div>
                    </div>
                    <div class="ins-right">
                        <div class="dates">
                            <span class="date-row" title="Installed"
                                >${unsafeHTML(ArrowDownload16Regular)}${installed}</span
                            >
                            ${updated ? html`<span class="date-row" title="Updated">${unsafeHTML(ArrowSync16Regular)}${updated}</span>` : nothing}
                        </div>
                        <div class="ins-actions">
                            <fluent-switch
                                ?checked=${p.enabled}
                                title=${p.enabled ? 'Disable' : 'Enable'}
                                @change=${(e: Event) =>
                                    this._send(Msg.fromWebView.plugins.setEnabled, {
                                        pluginId: p.id,
                                        enabled: (e.target as HTMLInputElement).checked,
                                    })}
                            ></fluent-switch>
                            <fluent-button
                                appearance="transparent"
                                icon-only
                                class="danger-hover"
                                title="Uninstall"
                                aria-label="Uninstall"
                                @click=${() => this._send(Msg.fromWebView.plugins.uninstall, { pluginId: p.id })}
                                >${unsafeHTML(Delete16Regular)}</fluent-button
                            >
                        </div>
                    </div>
                </div>`;
            })}
        </div>`;
    }

    private _renderAvailable(): TemplateResult {
        const q = this._query.trim().toLowerCase();
        const filtered = (
            q
                ? this._available.filter(
                      (p) =>
                          p.name.toLowerCase().includes(q) ||
                          (p.description ?? '').toLowerCase().includes(q),
                  )
                : this._available
        )
            .slice() // don't mutate state
            .sort((a, b) => (b.installCount ?? 0) - (a.installCount ?? 0))
            .slice(0, AVAILABLE_LIMIT);
        return html`
            <fluent-text-input
                class="search"
                placeholder="Search plugins…"
                .value=${this._query}
                @input=${(e: Event) => {
                    this._query = (e.target as HTMLInputElement).value;
                }}
            ></fluent-text-input>
            ${
                filtered.length === 0
                    ? html`<div class="empty">No matching plugins.</div>`
                    : html`<div class="list">
                          ${filtered.map(
                              (p) =>
                                  html` <div class="card av-card">
                                      <div class="body">
                                          <div class="av-head">
                                              <span class="title"
                                                  >${p.name}${p.version ? html` <span class="sub">v${p.version}</span>` : nothing}</span
                                              >
                                              ${
                                                  p.installCount
                                                      ? html`<span class="installs"
                                                            >${this._formatInstalls(p.installCount)}
                                                            installs</span
                                                        >`
                                                      : nothing
                                              }
                                          </div>
                                          <div class="desc">${p.description}</div>
                                          <div class="meta">
                                              from
                                              ${p.marketplaceName}${OFFICIAL_MARKETPLACE === p.marketplaceName ? ' 🦛' : ''}
                                          </div>
                                          ${(() => {
                                              const url = pluginUrl(p, this._marketplaces);
                                              return url
                                                  ? html`<div class="meta">
                                                        Source:
                                                        <fluent-link
                                                            href=${url}
                                                            target="_blank"
                                                            rel="noopener noreferrer"
                                                            >${url}</fluent-link
                                                        >
                                                    </div>`
                                                  : nothing;
                                          })()}
                                      </div>
                                      <div class="av-actions">
                                          <fluent-button
                                              appearance="transparent"
                                              icon-only
                                              title="Install"
                                              aria-label="Install"
                                              @click=${() => this._send(Msg.fromWebView.plugins.install, { pluginId: p.pluginId })}
                                              >${unsafeHTML(ArrowDownload16Regular)}</fluent-button
                                          >
                                      </div>
                                  </div>`,
                          )}
                      </div>`
            }
        `;
    }

    private _renderMarketplaces(): TemplateResult {
        return html`
            <div class="add-row">
                <fluent-text-input
                    placeholder="GitHub repo, URL, or path…"
                    id="mkt-src"
                    .value=${this._addSource}
                    @input=${(e: Event) => {
                        this._addSource = (e.target as HTMLInputElement).value;
                    }}
                    @keydown=${(e: KeyboardEvent) => {
                        if (e.key === 'Enter') {
                            this._onAddMarketplace();
                        }
                    }}
                ></fluent-text-input>
                <fluent-button
                    appearance="primary"
                    icon-only
                    title="Add marketplace"
                    aria-label="Add marketplace"
                    ?disabled=${!this._addSource.trim()}
                    @click=${this._onAddMarketplace}
                    >${unsafeHTML(Add16Regular)}</fluent-button
                >
            </div>
            ${
                this._marketplaces.length === 0
                    ? html`<div class="empty">No marketplaces configured.</div>`
                    : html`<div class="list">
                          ${this._marketplaces.map((m) => {
                              const url = marketplaceUrl(m);
                              const refreshing = this._refreshing === m.name;
                              return html` <div class="card mkt-card">
                                  <div class="body">
                                      <div class="title-row">
                                          <span class="title">${m.name}</span>
                                          ${OFFICIAL_MARKETPLACE === m.name ? html`<span>🦛</span>` : nothing}
                                      </div>
                                      <div class="meta">
                                          ${
                                              url
                                                  ? html`<fluent-link
                                                        href=${url}
                                                        target="_blank"
                                                        rel="noopener noreferrer"
                                                        >${marketplaceSourceLabel(m)}</fluent-link
                                                    >`
                                                  : marketplaceSourceLabel(m)
                                          }
                                      </div>
                                  </div>
                                  <div class="mkt-actions">
                                      <fluent-button
                                          appearance="transparent"
                                          icon-only
                                          title="Refresh"
                                          aria-label="Refresh"
                                          class=${refreshing ? 'spin' : ''}
                                          ?disabled=${refreshing}
                                          @click=${() => this._onRefresh(m.name)}
                                          >${unsafeHTML(ArrowSync16Regular)}</fluent-button
                                      >
                                      <fluent-button
                                          appearance="transparent"
                                          icon-only
                                          class="danger-hover"
                                          title="Remove"
                                          aria-label="Remove"
                                          @click=${() => this._send(Msg.fromWebView.plugins.marketplaceRemove, { name: m.name })}
                                          >${unsafeHTML(Delete16Regular)}</fluent-button
                                      >
                                  </div>
                              </div>`;
                          })}
                      </div>`
            }
        `;
    }

    private _onAddMarketplace = (): void => {
        const source = this._addSource.trim();
        if (!source) {
            return;
        }
        // No client-side URL validation — the CLI validates (clones the repo, checks marketplace.json)
        // and reports failure via the op result, exactly like VS Code.
        this._send(Msg.fromWebView.plugins.marketplaceAdd, { source });
        this._addSource = '';
    };
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-plugin-manager': CvPluginManager;
    }
}
