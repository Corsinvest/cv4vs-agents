/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Lazy-fetch helpers for "stripped" chat blocks: the host sends a placeholder
// `{ uuid, blockIdx }`; fetch the full payload on demand via a correlated request.

import { bridge } from './bridge';
import { Msg } from './bridge-messages';
import {
    GetImageReq,
    GetSubagentReq,
    GetContextUsageReq,
    GetStatsReq,
    PluginListReq,
    MarketplaceListReq,
    GetCompactSummaryReq,
} from './request-types';
import type {
    GetImageResponse,
    GetSubagentResponse,
    OpenDocumentNotification,
    OpenAttachmentNotification,
    GetContextUsageResponse,
    StatsScopeDto,
    StatsRangeDto,
    StatsResponse,
    GetCompactSummaryResponse,
} from './types';
import type { PluginListResponse } from './generated/PluginListResponse';
import type { MarketplaceListResponse } from './generated/MarketplaceListResponse';

/** Request image bytes for a stripped chat block. The bridge correlates the response by id,
 *  so concurrent calls no longer need manual dedup — each awaits its own response. Rejects on
 *  timeout or an error-response (e.g. block not found), which the caller renders as a failure. */
export function fetchChatImage(uuid: string, blockIdx: number): Promise<GetImageResponse> {
    return bridge.sendRequest(GetImageReq, { uuid, blockIdx });
}

/** Open a stripped chat document in Visual Studio. Fire-and-forget: the host writes a temp
 *  file and opens it with the right editor (no reply expected). */
export function openChatDocument(uuid: string, blockIdx: number): void {
    bridge.sendNotification<OpenDocumentNotification>(Msg.fromWebView.chat.openDocument, {
        uuid,
        blockIdx,
    });
}

/** Open an attachment still being composed. The bytes ride along because it isn't in the
 *  transcript yet (no uuid/blockIdx to fetch it by) and the File API gives no path to open
 *  in place — so the host writes a temp copy. Fire-and-forget. */
export function openAttachment(name: string, base64: string, mediaType: string): void {
    bridge.sendNotification<OpenAttachmentNotification>(Msg.fromWebView.chat.openAttachment, {
        name,
        base64,
        mediaType,
    });
}

/** Request a sub-agent's full transcript on expand. Correlated by id; rejects on timeout /
 *  error-response. The caller applies the transcript (upsert children + expand) in the .then. */
export function fetchSubagent(agentId: string): Promise<GetSubagentResponse> {
    return bridge.sendRequest(GetSubagentReq, { agentId });
}

/** Request a compaction summary on first expand (header arrives live/history; the summary
 *  text is read lazily from the session's .jsonl). Correlated by uuid; rejects on timeout /
 *  error-response — the caller leaves the "Loading…" body as-is. */
export function fetchCompactSummary(
    sessionId: string | null,
    uuid: string,
): Promise<GetCompactSummaryResponse> {
    return bridge.sendRequest(GetCompactSummaryReq, { sessionId: sessionId ?? undefined, uuid });
}

/** Fetch the current session's context-window breakdown for the Context dialog. Rejects on
 *  timeout / error-response (the dialog then shows an "unavailable" state). */
export function fetchContextUsage(): Promise<GetContextUsageResponse> {
    return bridge.sendRequest(GetContextUsageReq, {});
}

/** Fetch aggregated usage statistics for the Statistics dialog. Reads only the host's on-disk
 *  cache (fast) — the heavy indexing runs in the background and signals completion via
 *  stats_index_done, so this never needs the long timeout; the default is fine. */
export function fetchStats(scope: StatsScopeDto, range: StatsRangeDto): Promise<StatsResponse> {
    return bridge.sendRequest(GetStatsReq, { scope, range });
}

/** Fetch installed + available plugins for the plugin manager (host spawns
 *  `claude plugin list --available --json`; add's clone can be slow, but list is fast). */
export function fetchPlugins(): Promise<PluginListResponse> {
    return bridge.sendRequest(PluginListReq, {});
}

/** Fetch the configured marketplaces (`claude plugin marketplace list --json`). */
export function fetchMarketplaces(): Promise<MarketplaceListResponse> {
    return bridge.sendRequest(MarketplaceListReq, {});
}
