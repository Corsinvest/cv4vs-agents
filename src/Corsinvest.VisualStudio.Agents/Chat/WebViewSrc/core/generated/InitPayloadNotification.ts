/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { InitConfigDto } from './InitConfigDto';
import { VsOptionsDto } from './VsOptionsDto';

export interface InitPayloadNotification {
    config: InitConfigDto;
    vsOptions: VsOptionsDto | null;
}
