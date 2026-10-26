/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { ChangedFileDto } from './ChangedFileDto';

export interface ChangedFilesNotification {
    sessionId: string | null;
    files: ChangedFileDto[];
    overflow: boolean;
    maxFiles: number;
    disabled: string | null;
}
