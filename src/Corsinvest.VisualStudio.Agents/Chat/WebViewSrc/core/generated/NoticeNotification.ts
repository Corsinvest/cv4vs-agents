/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { NoticeVariantDto } from './NoticeVariantDto';
import { NoticePositionDto } from './NoticePositionDto';

export interface NoticeNotification {
    key: string;
    severity?: NoticeVariantDto;
    message: string;
    position?: NoticePositionDto;
    actionLabel: string;
    actionMessage: string;
    sticky: boolean;
}
