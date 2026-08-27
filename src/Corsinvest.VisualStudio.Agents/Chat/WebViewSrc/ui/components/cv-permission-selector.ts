/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { tooltipStyles } from '../styles/shared';
import type { PermissionMode } from '../../core/types';
import { PERMISSION_MODE } from '../../core/types';
import { permissionItems } from '../../core/permission-modes';

/**
 * Permission-mode trigger in the input toolbar: shows the active mode and asks cv-prompt to
 * open the picker (cv-permission-list, above the textarea — same place as the model picker
 * and the `/` palette). The list, not this button, owns the menu.
 *
 * The title carries the mode's full description as well as its name: which mode is active decides
 * whether a command runs unattended, so "Edit automatically" alone is not enough to choose by.
 */
@customElement('cv-permission-selector')
export class CvPermissionSelector extends LitElement {
    static override styles = [
        tooltipStyles,
        css`
            :host {
                display: contents;
            }
            /* See cv-model-selector: Fluent's own subtle button, ours only the metrics. */
            .trigger {
                font-size: var(--fontSizeBase200);
                padding-inline: 8px;
                min-width: 0;
            }
            /* The one mode that asks nothing before running anything, said where the modes are
             * named — the composer border only shows it while the composer has focus. On the span:
             * the button is Fluent's, which takes layout from us and nothing else. */
            .trigger .danger {
                color: var(--colorPaletteRedForeground1);
                font-weight: var(--fontWeightSemibold);
            }
        `,
    ];

    @state() private _current: PermissionMode = appState.permissionMode;

    private _off?: () => void;
    private _offModels?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        this._off = appState.on('permissionMode', (v) => {
            this._current = v;
        });
        this._offModels = appState.on('models', () => this.requestUpdate());
    }

    override disconnectedCallback(): void {
        super.disconnectedCallback();
        this._off?.();
        this._offModels?.();
    }

    private _onClick = (): void => {
        this.dispatchEvent(new CustomEvent('open-permissions', { bubbles: true, composed: true }));
    };

    override render() {
        // No fallback to the first item: the CLI can sit in a mode the picker doesn't list
        // (`dontAsk`, or `auto`/`bypassPermissions` once filtered out), and labelling that
        // "Manual" would state the opposite of what is in force. The raw value is ugly and true.
        const item = permissionItems().find((it) => it.value === this._current);
        return html`
            <fluent-button
                id="perm-trigger"
                class="trigger"
                aria-label="Permission mode"
                appearance="subtle"
                size="small"
                @click=${this._onClick}
            >
                <span class=${this._current === PERMISSION_MODE.bypassPermissions ? 'danger' : ''}
                    >${item?.short ?? this._current}</span
                >
            </fluent-button>
            <!-- The name of the control, not of the mode: three of the five modes are shown in full
                 on the button already (Manual, Plan, Auto), so echoing the label would be the same
                 word twice. What a mode allows, and the Shift+Tab hint, are in cv-permission-list —
                 a click answers that better than a tooltip repeating it. -->
            <fluent-tooltip anchor="perm-trigger" positioning="above-end"
                >Permission mode</fluent-tooltip
            >
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-permission-selector': CvPermissionSelector;
    }
}
