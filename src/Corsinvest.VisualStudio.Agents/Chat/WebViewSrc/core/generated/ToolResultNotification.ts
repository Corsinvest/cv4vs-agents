/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

export interface ToolResultNotification {
    toolUseId: string;
    result: string;
    isError: boolean;
    parentToolUseId: string | null;
    agentId: string | null;
    fullLineCount: number;
}
