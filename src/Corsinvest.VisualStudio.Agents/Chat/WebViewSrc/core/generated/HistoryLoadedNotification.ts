/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { HistoryEventDto } from './HistoryEventDto';

export interface HistoryLoadedNotification {
    events: HistoryEventDto[];
    sessionId: string | null;
    oldestOffset: number;
    hasMore: boolean;
}
