/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { EffortLevelDto } from './EffortLevelDto';
import { SpinnerVerbsConfigDto } from './SpinnerVerbsConfigDto';
import { PermissionMode } from '../types';

export interface CliStateDto {
    model: string;
    permissionMode: PermissionMode;
    effortLevel?: EffortLevelDto | null;
    alwaysThinkingEnabled?: boolean | null;
    switchModelsOnFlag?: boolean | null;
    ultracode?: boolean | null;
    fastModeState: string;
    spinnerVerbsConfig: SpinnerVerbsConfigDto | null;
}
