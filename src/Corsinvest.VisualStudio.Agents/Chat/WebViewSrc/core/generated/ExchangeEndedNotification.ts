/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { ContextUsageDto } from './ContextUsageDto';

export interface ExchangeEndedNotification {
    costUsd: number;
    durationMs: number;
    isError: boolean;
    usage: ContextUsageDto | null;
    contextWindow: number;
    maxOutputTokens: number;
    errorText: string;
    errorKind: string;
}
