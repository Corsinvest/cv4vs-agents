/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { ContextTokenGroupDto } from './ContextTokenGroupDto';

export interface ContextMessageBreakdownDto {
    toolCallTokens: number;
    toolResultTokens: number;
    attachmentTokens: number;
    assistantMessageTokens: number;
    userMessageTokens: number;
    redirectedContextTokens: number;
    unattributedTokens: number;
    toolCallsByType: ContextTokenGroupDto[];
    attachmentsByType: ContextTokenGroupDto[];
}
