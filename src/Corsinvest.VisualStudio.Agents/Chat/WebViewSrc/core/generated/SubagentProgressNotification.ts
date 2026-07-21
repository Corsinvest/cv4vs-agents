/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { SubagentUsageDto } from './SubagentUsageDto';

export interface SubagentProgressNotification {
    taskId: string;
    description: string;
    lastToolName: string | null;
    summary: string | null;
    toolUseId: string | null;
    usage: SubagentUsageDto;
}
