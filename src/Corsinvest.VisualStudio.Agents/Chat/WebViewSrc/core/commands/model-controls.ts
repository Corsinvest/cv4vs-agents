/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Model-section controls with inline state: Thinking (toggle), Fast mode
// (toggle), Effort (slider), Ultracode (toggle). Each mirrors its value in app
// state so the menu shows the current setting; Fast mode/Effort/Ultracode push
// into the CLI flag-settings layer via host.applyFlagSettings, Thinking
// hot-swaps the runtime thinking budget directly via host.setMaxThinkingTokens.
// keepMenuOpen so toggling doesn't dismiss.

import Lightbulb16Regular from '@fluentui/svg-icons/icons/lightbulb_16_regular.svg';
import Rocket16Regular from '@fluentui/svg-icons/icons/rocket_16_regular.svg';
import Dumbbell16Regular from '@fluentui/svg-icons/icons/dumbbell_16_regular.svg';
import ArrowSwap16Regular from '@fluentui/svg-icons/icons/arrow_swap_16_regular.svg';
import AnimalRabbit16Regular from '@fluentui/svg-icons/icons/animal_rabbit_16_regular.svg';
import {
    ChatCommand,
    type CommandHost,
    type CommandSection,
    type CommandTrailing,
    type TrailingControl,
} from './base';
import { state as appState } from '../state';
import { resolveModelValue } from '../ai-models';
import {
    effortLabel,
    ULTRACODE_LABEL,
    type EffortLevelDto,
    type EffortSliderLevel,
} from '../types';

/** The current model's catalogue entry (from the CLI), or undefined before init. */
function currentModelInfo() {
    const value = resolveModelValue(appState.currentModel);
    return appState.models.find((x) => x.value === value);
}

/** Effort levels the current model supports (from the CLI), or null when the
 *  model has no effort (e.g. Haiku): the slider is then hidden. Exported so the
 *  composer's effort chip hides on exactly the same condition as this row. */
export function currentEffortLevels(): EffortSliderLevel[] | null {
    const m = currentModelInfo();
    const levels = (m?.supportedEffortLevels ?? []) as EffortSliderLevel[];
    return m?.supportsEffort && levels.length > 0 ? levels : null;
}

export class ThinkingCommand extends ChatCommand {
    readonly id = 'thinking';
    readonly label = 'Thinking';
    readonly description = 'Extended reasoning before answering';
    readonly section: CommandSection = 'model';
    readonly order = 30;
    readonly icon = Lightbulb16Regular;
    readonly trailing: CommandTrailing = 'toggle';
    override readonly aliases = ['thinking', 'reasoning', 'extended'];
    readonly keepMenuOpen = true;
    get checked(): boolean {
        return appState.thinkingEnabled;
    }
    override isEnabled(): boolean {
        const m = currentModelInfo();
        return m ? m.supportsAdaptiveThinking : true;
    }
    override run(host: CommandHost): void {
        const next = !appState.thinkingEnabled;
        appState.thinkingEnabled = next;
        // Runtime thinking channel: a non-zero budget + 'summarized' enables, 0 disables.
        host.setMaxThinkingTokens(next ? 31999 : 0, next ? 'summarized' : null);
    }
}

export class FastModeCommand extends ChatCommand {
    readonly id = 'fast-mode';
    readonly label = 'Fast mode';
    readonly description = 'Faster responses on supported models';
    readonly section: CommandSection = 'model';
    readonly order = 40;
    readonly icon = AnimalRabbit16Regular;
    readonly trailing: CommandTrailing = 'toggle';
    override readonly aliases = ['fast', 'speed', 'turbo'];
    readonly keepMenuOpen = true;
    get checked(): boolean {
        return appState.fastMode;
    }
    override isEnabled(): boolean {
        const m = currentModelInfo();
        // Before the catalogue arrives (m undefined) keep it shown, like the default.
        return m ? m.supportsFastMode : true;
    }
    override run(host: CommandHost): void {
        const next = !appState.fastMode;
        appState.fastMode = next;
        host.applyFlagSettings({ fastMode: next });
    }
}

export class SwitchModelsOnFlagCommand extends ChatCommand {
    readonly id = 'switch-models-on-flag';
    readonly label = 'Switch models when a message is flagged';
    readonly description =
        'Auto-switch model when safety flags a message (else the session pauses)';
    readonly section: CommandSection = 'model';
    readonly order = 45;
    readonly icon = ArrowSwap16Regular;
    readonly trailing: CommandTrailing = 'toggle';
    override readonly aliases = ['switch', 'flag', 'safety'];
    readonly keepMenuOpen = true;
    get checked(): boolean {
        return appState.switchModelsOnFlag;
    }
    override run(host: CommandHost): void {
        const next = !appState.switchModelsOnFlag;
        appState.switchModelsOnFlag = next;
        host.applyFlagSettings({ switchModelsOnFlag: next });
    }
}

export class EffortCommand extends ChatCommand {
    readonly id = 'effort';
    readonly label = 'Effort';
    readonly description = 'Reasoning effort level';
    readonly section: CommandSection = 'model';
    readonly order = 20;
    readonly icon = Dumbbell16Regular;
    readonly trailing: CommandTrailing = 'slider';
    readonly keepMenuOpen = true;
    override readonly aliases = ['effort', 'reasoning', 'thinking'];

    private stopValues(): string[] {
        return currentEffortLevels() ?? [];
    }

    /** Hidden when the current model has no effort levels (e.g. Haiku). */
    override isEnabled(): boolean {
        return currentEffortLevels() !== null;
    }

    get level(): number {
        return Math.max(0, this.stopValues().indexOf(appState.effortLevel));
    }
    get levelLabel(): string {
        return effortLabel(appState.effortLevel);
    }
    /** Only the level: the flag layer merges, so ultracode stays as it is. `max` is selectable
     *  but not persisted by the CLI enum, so it isn't pushed to settings. */
    setLevel(host: CommandHost, idx: number): void {
        const stops = this.stopValues();
        const value = stops[Math.max(0, Math.min(idx, stops.length - 1))];
        appState.effortLevel = value as EffortLevelDto;
        host.applyFlagSettings({ effortLevel: value });
    }
    override get trailingControl(): TrailingControl {
        return {
            kind: 'slider',
            stops: this.stopValues().map((lvl, i) => ({ label: effortLabel(lvl), value: i })),
            value: this.level,
            label: this.levelLabel,
            onSet: (host, v) => this.setLevel(host, v),
        };
    }
    /** Click on the row cycles to the next stop (the slider handles drags). */
    override run(host: CommandHost): void {
        const stops = this.stopValues();
        if (stops.length > 0) {
            this.setLevel(host, (this.level + 1) % stops.length);
        }
    }
}

/** Whether the current model can run ultracode. Derived: the CLI reports it for the current
 *  model only (get_settings applied.ultracodeAvailable), and on every model probed it matches
 *  the ones that list `xhigh`. */
function ultracodeAvailable(): boolean {
    return currentEffortLevels()?.includes('xhigh') ?? false;
}

export function ultracodeActive(): boolean {
    return appState.ultracodeEnabled && ultracodeAvailable();
}

export class UltracodeCommand extends ChatCommand {
    readonly id = 'ultracode';
    readonly label = ULTRACODE_LABEL;
    readonly description = 'Dynamic workflows on every task, this session only';
    readonly section: CommandSection = 'model';
    readonly order = 25;
    readonly icon = Rocket16Regular;
    readonly trailing: CommandTrailing = 'toggle';
    override readonly aliases = ['ultracode', 'workflow', 'orchestrate'];
    readonly keepMenuOpen = true;
    get checked(): boolean {
        return ultracodeActive();
    }
    override isEnabled(): boolean {
        return ultracodeAvailable();
    }
    override run(host: CommandHost): void {
        const next = !ultracodeActive();
        appState.ultracodeEnabled = next;
        // null, not false: it removes the key from the flag layer rather than storing an "off".
        host.applyFlagSettings({ ultracode: next ? true : null });
    }
}
