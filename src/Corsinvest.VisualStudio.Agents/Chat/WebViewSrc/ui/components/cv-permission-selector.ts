/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { state as appState } from '../../core/state';
import { tooltipStyles } from '../styles/shared';
import type { PermissionMode } from '../../core/types';
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
        `,
    ];

    @state() private _current: PermissionMode = appState.permissionMode;
    @state() private _models = appState.models;

    private _off?: () => void;
    private _offModels?: () => void;

    override connectedCallback(): void {
        super.connectedCallback();
        this._off = appState.on('permissionMode', (v) => {
            this._current = v;
        });
        // The label's item list is model-dependent (supportsAutoMode).
        this._offModels = appState.on('models', (v) => {
            this._models = v;
        });
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
        // _models is read so the label re-resolves when the catalogue (and thus the
        // available modes) changes.
        void this._models;
        const items = permissionItems();
        const item = items.find((it) => it.value === this._current) ?? items[0];
        return html`
            <fluent-button
                id="perm-trigger"
                class="trigger"
                appearance="subtle"
                size="small"
                @click=${this._onClick}
            >
                <span>${item.short}</span>
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
