/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { StatsModelDto } from './StatsModelDto';
import { StatsDayDto } from './StatsDayDto';
import { StatsDayModelDto } from './StatsDayModelDto';
import { StatsToolDto } from './StatsToolDto';

export interface StatsResponse {
    indexing: boolean;
    totalSessions: number;
    totalMessages: number;
    totalTokens: number;
    activeDays: number;
    currentStreak: number;
    longestStreak: number;
    peakHour: number;
    favoriteModel: string;
    imageCount: number;
    fileCount: number;
    subagentSessions: number;
    subagentTokens: number;
    modelBreakdown: StatsModelDto[];
    dailyActivity: StatsDayDto[];
    dailyModelTokens: StatsDayModelDto[];
    topTools: StatsToolDto[];
}
