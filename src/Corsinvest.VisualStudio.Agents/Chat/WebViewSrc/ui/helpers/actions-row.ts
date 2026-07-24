/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The bottom actions row shared by a normal response (cv-app) and a sub-agent transcript
// (cv-tool-row): a Copy button plus the "x ago" timestamp. Pure render — no state, no component.

import { html, nothing } from 'lit';
import '../components/cv-copy-btn';
import { formatTimeAgo, formatAbsolute } from '../../core/time';

/** Copy button + "x ago" timestamp. Hover-gating is CSS: `.cv-response-actions` has base opacity 0
 *  and a hover rule on the container reveals it — pass that container's reveal class in `extraClass`.
 *  ts=0 hides the timestamp. */
export function renderActionsRow(text: string, ts: number, title: string, extraClass = '') {
    return html`
        <div class="cv-response-actions ${extraClass}">
            <cv-copy-btn .text=${text} title=${title}></cv-copy-btn>
            ${
                ts > 0
                    ? html`<span class="cv-ts" title=${formatAbsolute(ts)}
                          >${formatTimeAgo(ts)}</span
                      >`
                    : nothing
            }
        </div>
    `;
}
