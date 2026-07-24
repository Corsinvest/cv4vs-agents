/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { AccountDto } from './AccountDto';
import { RateWindowDto } from './RateWindowDto';
import { UsageBehaviorsDto } from './UsageBehaviorsDto';

export interface UsageDto {
    account: AccountDto | null;
    authMethod: string;
    plan: string;
    rateLimitsAvailable: boolean;
    windows: RateWindowDto[];
    day: UsageBehaviorsDto | null;
    week: UsageBehaviorsDto | null;
}
