/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { ContextCategoryDto } from './ContextCategoryDto';
import { ContextGridCellDto } from './ContextGridCellDto';
import { ContextMemoryFileDto } from './ContextMemoryFileDto';
import { ContextAgentDto } from './ContextAgentDto';
import { ContextMcpToolDto } from './ContextMcpToolDto';
import { ContextSkillsDto } from './ContextSkillsDto';
import { ContextCommandsDto } from './ContextCommandsDto';
import { ContextMessageBreakdownDto } from './ContextMessageBreakdownDto';

export interface GetContextUsageResponse {
    model: string;
    totalTokens: number;
    maxTokens: number;
    rawMaxTokens: number;
    percentage: number;
    autocompactSource: string;
    autoCompactThreshold: number;
    isAutoCompactEnabled: boolean;
    categories: ContextCategoryDto[];
    gridRows: ContextGridCellDto[][];
    memoryFiles: ContextMemoryFileDto[];
    agents: ContextAgentDto[];
    mcpTools: ContextMcpToolDto[];
    skills: ContextSkillsDto;
    slashCommands: ContextCommandsDto;
    messageBreakdown: ContextMessageBreakdownDto;
}
