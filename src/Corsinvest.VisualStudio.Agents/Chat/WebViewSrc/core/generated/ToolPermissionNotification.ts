/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { ContextUsageDto } from './ContextUsageDto';

export interface ToolPermissionNotification {
    id: string;
    name: string;
    preview: string;
    input: Object;
    parentToolUseId: string | null;
    needsPermission: boolean;
    permissionSuggestions: Object[];
    usage: ContextUsageDto;
}
