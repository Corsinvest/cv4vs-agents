/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

export interface RewindResultNotification {
    messageUuid: string;
    canRewind: boolean;
    error: string;
    filesChanged: string[] | null;
    insertions: number;
    deletions: number;
    skippedLinks: number;
}
