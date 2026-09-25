/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { ComposerMention } from './ComposerMention';

export interface SetComposerNotification {
    text: string;
    enableIdeContext: boolean;
    send: boolean;
    append: boolean;
    mentions: ComposerMention[] | null;
}
