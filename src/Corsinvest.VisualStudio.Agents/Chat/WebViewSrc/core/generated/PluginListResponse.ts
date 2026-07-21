/**
 * This is a TypeGen auto-generated file.
 * Any changes made to this file can be lost when this file is regenerated.
 */

import { PluginDto } from './PluginDto';
import { AvailablePluginDto } from './AvailablePluginDto';

export interface PluginListResponse {
    installed: PluginDto[];
    available: AvailablePluginDto[];
}
