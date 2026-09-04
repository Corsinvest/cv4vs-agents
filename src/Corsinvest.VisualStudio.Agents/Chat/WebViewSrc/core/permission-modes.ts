/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The permission modes and their user-facing labels. Lives in core (not in the picker)
// because the toolbar trigger, the picker and the command palette all need the same list.

import HandRight20Regular from '@fluentui/svg-icons/icons/hand_right_20_regular.svg';
import Code20Regular from '@fluentui/svg-icons/icons/code_20_regular.svg';
// A bullet list, not the task clipboard: that one carries a tick, and at 16px next to the
// menu's own selection check it reads as a second checkbox saying the opposite.
import ClipboardBulletListLtr20Regular from '@fluentui/svg-icons/icons/clipboard_bullet_list_ltr_20_regular.svg';
import Flash20Regular from '@fluentui/svg-icons/icons/flash_20_regular.svg';
import ShieldDismiss20Regular from '@fluentui/svg-icons/icons/shield_dismiss_20_regular.svg';
import { state as appState } from './state';
import { resolveModelValue } from './ai-models';
import { PERMISSION_MODE } from './types';
import type { PermissionMode } from './types';

export interface PermissionItem {
    value: PermissionMode;
    label: string;
    /** What the composer button shows: the menu has room to explain, a docked pane hasn't. Set on
     *  every mode, equal to `label` where that is already short enough. */
    short: string;
    description: string;
    icon: string;
}

const PERMISSION_ITEMS: PermissionItem[] = [
    // Descriptions say what happens to your files and what still gets asked — that's what
    // you need to choose. No "Claude will": the subject is obvious in a chat with it, and
    // the spare width is better spent on the limits of each mode (verified in the CLI:
    // acceptEdits only auto-approves writes inside the working directory).
    {
        value: PERMISSION_MODE.default,
        label: 'Manual',
        short: 'Manual',
        description: 'Every edit and command waits for your approval',
        icon: HandRight20Regular,
    },
    {
        value: PERMISSION_MODE.acceptEdits,
        label: 'Edit automatically',
        short: 'Auto-edit',
        description:
            'Files in the working directory are edited without asking — anything outside it, and commands, still ask',
        icon: Code20Regular,
    },
    {
        value: PERMISSION_MODE.plan,
        label: 'Plan',
        short: 'Plan',
        description: 'Reads and explores freely, then proposes a plan — no file is changed',
        icon: ClipboardBulletListLtr20Regular,
    },
    {
        value: PERMISSION_MODE.auto,
        label: 'Auto',
        short: 'Auto',
        description: 'Decides per task when to act and when to ask, based on how risky it is',
        icon: Flash20Regular,
    },
    {
        value: PERMISSION_MODE.bypassPermissions,
        label: 'Bypass permissions',
        short: 'Bypass',
        description: 'Nothing is ever asked, including commands that can destroy data',
        icon: ShieldDismiss20Regular,
    },
];

/** Gate the optional modes: `auto` only when the current model supports it,
 *  `bypassPermissions` only when the option is enabled. Before the model catalogue
 *  arrives we cannot know, and the two ways to be wrong are not equal: offering `auto`
 *  to a model that refuses it makes the row vanish from under the cursor, while hiding
 *  it costs nothing the user notices — unless it is the mode they are already in, which
 *  is why that one case keeps it. */
export function permissionItems(): PermissionItem[] {
    const value = resolveModelValue(appState.currentModel);
    const m = appState.models.find((x) => x.value === value);
    const autoOk = m ? m.supportsAutoMode : appState.permissionMode === PERMISSION_MODE.auto;
    // Two gates: our own VS option AND the CLI's effective settings — an org can
    // forbid the mode via managed settings. Without the second one we would offer a mode the CLI
    // then refuses. Note the different sources: `ui.*` is a VS Option, the other is CLI state.
    const bypassOk =
        appState.ui.allowDangerouslySkipPermissions && !appState.bypassPermissionsDisabled;
    return PERMISSION_ITEMS.filter((it) => {
        if (it.value === PERMISSION_MODE.auto) {
            return autoOk;
        }
        if (it.value === PERMISSION_MODE.bypassPermissions) {
            return bypassOk;
        }
        return true;
    });
}
