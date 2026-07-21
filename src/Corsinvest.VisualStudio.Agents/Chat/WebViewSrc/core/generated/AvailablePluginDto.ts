/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { PluginSourceKindDto } from './PluginSourceKindDto';

export interface AvailablePluginDto {
    pluginId: string;
    name: string;
    description: string;
    marketplaceName: string;
    version: string;
    installCount: number;
    sourceKind: PluginSourceKindDto;
    source: string;
}
