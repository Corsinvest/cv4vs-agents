/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { UsageInsightDto } from './UsageInsightDto';
import { UsageAttributionDto } from './UsageAttributionDto';

export interface UsageBehaviorsDto {
    insights: UsageInsightDto[];
    skills: UsageAttributionDto[];
    subagents: UsageAttributionDto[];
    plugins: UsageAttributionDto[];
    mcpServers: UsageAttributionDto[];
    hasAttribution: boolean;
}
