/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { InitConfigDto } from './InitConfigDto';
import { CliStateDto } from './CliStateDto';
import { VsOptionsDto } from './VsOptionsDto';

export interface InitPayloadNotification {
    config: InitConfigDto;
    cliState: CliStateDto | null;
    vsOptions: VsOptionsDto | null;
}
