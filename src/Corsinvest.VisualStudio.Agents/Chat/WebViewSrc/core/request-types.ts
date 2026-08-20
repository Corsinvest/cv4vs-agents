/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Typed request descriptors (vscode-jsonrpc RequestType<P,R> pattern): each couples a
// channel with its params type and its result type, in ONE place. bridge.sendRequest(rt, params)
// then infers the response type and constrains params — impossible to send the wrong payload
// or expect the wrong result. The phantom fields carry the generics at compile time only.

import { Msg } from './bridge-messages';
import type {
    GetImageRequest,
    GetImageResponse,
    GetHistoryRequest,
    GetHistoryResponse,
    GetSubagentRequest,
    GetSubagentResponse,
    GetUsageRequest,
    UsageDto,
    GetContextUsageRequest,
    GetContextUsageResponse,
    GetStatsRequest,
    StatsResponse,
    GetSuggestionsRequest,
    GetSuggestionsResponse,
    GetCompactSummaryRequest,
    GetCompactSummaryResponse,
    RewindRequest,
    RewindResultNotification,
    RewindPointsNotification,
} from './types';
import type { PluginListResponse } from './generated/PluginListResponse';
import type { MarketplaceListResponse } from './generated/MarketplaceListResponse';

export class RequestType<TParams, TResult> {
    // Phantom markers — never read at runtime; they exist so TS binds TParams/TResult.
    declare readonly __params: TParams;
    declare readonly __result: TResult;
    constructor(
        public readonly requestChannel: string,
        public readonly responseChannel: string,
    ) {}
}

// requestChannel = FromWebView (TS→C#), responseChannel = ToWebView (C#→TS, carries the same
// id back).
export const GetImageReq = new RequestType<GetImageRequest, GetImageResponse>(
    Msg.fromWebView.chat.getImage,
    Msg.toWebView.chat.imageData,
);

export const GetHistoryReq = new RequestType<GetHistoryRequest, GetHistoryResponse>(
    Msg.fromWebView.chat.getHistory,
    Msg.toWebView.chat.history,
);

export const GetSubagentReq = new RequestType<GetSubagentRequest, GetSubagentResponse>(
    Msg.fromWebView.chat.getSubagent,
    Msg.toWebView.chat.subagentLoaded,
);

export const GetCompactSummaryReq = new RequestType<
    GetCompactSummaryRequest,
    GetCompactSummaryResponse
>(Msg.fromWebView.chat.getCompactSummary, Msg.toWebView.chat.compactSummaryResult);

export const GetUsageReq = new RequestType<GetUsageRequest, UsageDto>(
    Msg.fromWebView.chat.getUsage,
    Msg.toWebView.chat.usage,
);

export const GetContextUsageReq = new RequestType<GetContextUsageRequest, GetContextUsageResponse>(
    Msg.fromWebView.chat.getContextUsage,
    Msg.toWebView.chat.contextUsage,
);

export const GetStatsReq = new RequestType<GetStatsRequest, StatsResponse>(
    Msg.fromWebView.chat.getStats,
    Msg.toWebView.chat.stats,
);

export const GetSuggestionsReq = new RequestType<GetSuggestionsRequest, GetSuggestionsResponse>(
    Msg.fromWebView.file.getSuggestions,
    Msg.toWebView.file.suggestions,
);

/** Restore the files to the CLI's snapshot before a user message — or, with `dryRun`, only ask
 *  whether it could. The probe is what tells a message with a checkpoint from one without. */
export const RewindReq = new RequestType<RewindRequest, RewindResultNotification>(
    Msg.fromWebView.session.rewind,
    Msg.toWebView.session.rewindResult,
);

/** Which messages a rewind could restore something for. Read from the transcript in one pass, so
 *  the picker can leave out the turns that changed no file instead of offering a row that would
 *  refuse. */
export const RewindPointsReq = new RequestType<Record<string, never>, RewindPointsNotification>(
    Msg.fromWebView.session.getRewindPoints,
    Msg.toWebView.session.rewindPoints,
);

export const PluginListReq = new RequestType<Record<string, never>, PluginListResponse>(
    Msg.fromWebView.plugins.list,
    Msg.toWebView.plugins.listResult,
);

export const MarketplaceListReq = new RequestType<Record<string, never>, MarketplaceListResponse>(
    Msg.fromWebView.plugins.marketplaceList,
    Msg.toWebView.plugins.marketplaceListResult,
);
