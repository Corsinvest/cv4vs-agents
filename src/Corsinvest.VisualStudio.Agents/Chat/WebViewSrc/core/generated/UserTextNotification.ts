/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { UserImageDto } from './UserImageDto';
import { UserFileDto } from './UserFileDto';

export interface UserTextNotification {
    text: string;
    images: UserImageDto[] | null;
    files: UserFileDto[] | null;
    parentToolUseId: string | null;
    uuid: string | null;
}
