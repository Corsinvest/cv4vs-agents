/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// The permission modes and their user-facing labels. Lives in core (not in the picker)
// because the toolbar trigger, the picker and the command palette all need the same list.

import HandRight20Regular from '@fluentui/svg-icons/icons/hand_right_20_regular.svg';
import Code20Regular from '@fluentui/svg-icons/icons/code_20_regular.svg';
import ClipboardTask20Regular from '@fluentui/svg-icons/icons/clipboard_task_20_regular.svg';
import Flash20Regular from '@fluentui/svg-icons/icons/flash_20_regular.svg';
import ShieldDismiss20Regular from '@fluentui/svg-icons/icons/shield_dismiss_20_regular.svg';
import { state as appState } from './state';
import { resolveModelValue } from './ai-models';
import type { PermissionMode } from './types';

export interface PermissionItem {
    value: PermissionMode;
    label: string;
    description: string;
    icon: string;
}

export const PERMISSION_ITEMS: PermissionItem[] = [
    // Descriptions say what happens to your files and what still gets asked — that's what
    // you need to choose. No "Claude will": the subject is obvious in a chat with it, and
    // the spare width is better spent on the limits of each mode (verified in the CLI:
    // acceptEdits only auto-approves writes inside the working directory).
    {
        value: 'default',
        label: 'Ask before edits',
        description: 'Every edit and command waits for your approval',
        icon: HandRight20Regular,
    },
    {
        value: 'acceptEdits',
        label: 'Edit automatically',
        description:
            'Files in the working directory are edited without asking — anything outside it, and commands, still ask',
        icon: Code20Regular,
    },
    {
        value: 'plan',
        label: 'Plan mode',
        description: 'Reads and explores freely, then proposes a plan — no file is changed',
        icon: ClipboardTask20Regular,
    },
    {
        value: 'auto',
        label: 'Auto mode',
        description: 'Decides per task when to act and when to ask, based on how risky it is',
        icon: Flash20Regular,
    },
    {
        value: 'bypassPermissions',
        label: 'Bypass permissions',
        description: 'Nothing is ever asked, including commands that can destroy data',
        icon: ShieldDismiss20Regular,
    },
];

/** Gate optional modes like VS Code: `auto` only when the current model supports it,
 *  `bypassPermissions` only when the option is enabled. Before the model catalogue
 *  arrives, keep `auto` shown. */
export function permissionItems(): PermissionItem[] {
    const value = resolveModelValue(appState.currentModel);
    const m = appState.models.find((x) => x.value === value);
    const autoOk = m ? m.supportsAutoMode : true;
    const bypassOk = appState.ui.allowDangerouslySkipPermissions;
    return PERMISSION_ITEMS.filter((it) => {
        if (it.value === 'auto') {
            return autoOk;
        }
        if (it.value === 'bypassPermissions') {
            return bypassOk;
        }
        return true;
    });
}

/** User-facing label for a mode (falls back to the raw value if unknown). */
export function permissionLabel(mode: PermissionMode): string {
    return PERMISSION_ITEMS.find((it) => it.value === mode)?.label ?? mode;
}
