/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
import { LitElement, html, css } from 'lit';
import { customElement, state } from 'lit/decorators.js';
import { unsafeHTML } from 'lit/directives/unsafe-html.js';
import { state as appState } from '../../core/state';
import { iconStyles } from '../styles/shared';
import type { PermissionMode } from '../../core/types';
import { permissionItems } from '../../core/permission-modes';
import { SwitchPermissionModeCommand } from '../../core/commands/builtin-commands';

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
        iconStyles,
        css`
            :host {
                display: contents;
            }
            /* Trigger is a <fluent-button> — keep it pure (layout only). */
            .trigger svg {
                width: 16px;
                height: 16px;
                margin-right: 4px;
            }
            /* Two-word labels ("Edit automatically") otherwise wrap inside the button and make the
               whole toolbar two rows tall. On the span too: the label is our own light-DOM node,
               so it takes the rule even where the Fluent shadow wouldn't inherit it. */
            .trigger,
            .trigger span {
                white-space: nowrap;
            }
        `,
    ];

    @state() private _current: PermissionMode = appState.permissionMode;
    @state() private _models = appState.models;

    // The menu row for the same thing: its description is the one place that says what picking a
    // mode does, so the tooltip borrows it instead of wording it a second time.
    private readonly _command = new SwitchPermissionModeCommand();

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
                class="trigger"
                appearance="subtle"
                size="small"
                title=${`${item.label}\n${item.description}\n${this._command.description} — Shift+Tab to switch`}
                @click=${this._onClick}
            >
                ${unsafeHTML(item.icon)}
                <span>${item.label}</span>
            </fluent-button>
        `;
    }
}

declare global {
    interface HTMLElementTagNameMap {
        'cv-permission-selector': CvPermissionSelector;
    }
}
