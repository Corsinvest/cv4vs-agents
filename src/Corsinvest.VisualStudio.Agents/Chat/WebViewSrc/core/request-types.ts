/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */
// Typed request descriptors (vscode-jsonrpc RequestType<P,R> pattern): each couples a
// channel with its params type and its result type, in ONE place. bridge.sendRequest(rt, params)
// then infers the response type and constrains params: impossible to send the wrong payload
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
    CancelPromptRequest,
    CancelPromptResponse,
    RewindRequest,
    RewindResultNotification,
    RewindPointsNotification,
    ChangedFilesNotification,
    ChangedFilesActionResult,
    ChangedFileRequest,
    SetRemoteControlAtStartupRequest,
    SetRemoteControlAtStartupResponse,
} from './types';
import type { PluginListResponse } from './generated/PluginListResponse';
import type { MarketplaceListResponse } from './generated/MarketplaceListResponse';

export class RequestType<TParams, TResult> {
    // Phantom markers: never read at runtime; they exist so TS binds TParams/TResult.
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

/** Take a prompt back from the CLI's queue: `cancelled` is false once the CLI has read it. */
export const CancelPromptReq = new RequestType<CancelPromptRequest, CancelPromptResponse>(
    Msg.fromWebView.cli.cancelPrompt,
    Msg.toWebView.cli.promptCancelResult,
);

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

/** Restore the files to the CLI's snapshot before a user message, or, with `dryRun`, only ask
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

export const SetRemoteControlAtStartupReq = new RequestType<
    SetRemoteControlAtStartupRequest,
    SetRemoteControlAtStartupResponse
>(Msg.fromWebView.cli.setRemoteControlAtStartup, Msg.toWebView.cli.remoteControlAtStartupResult);

/** The files Claude changed in this session. Asking is also what makes the host re-measure them
 *  against the disk, so the panel asks each time it opens. */
export const ChangedFilesReq = new RequestType<Record<string, never>, ChangedFilesNotification>(
    Msg.fromWebView.changedFiles.get,
    Msg.toWebView.changedFiles.list,
);

/** Accept a file as it is, or put it back as it was before Claude's first edit. All four actions
 *  answer on one channel; the correlation id tells the calls apart. */
export const KeepChangedFileReq = new RequestType<ChangedFileRequest, ChangedFilesActionResult>(
    Msg.fromWebView.changedFiles.keep,
    Msg.toWebView.changedFiles.actionResult,
);
export const UndoChangedFileReq = new RequestType<ChangedFileRequest, ChangedFilesActionResult>(
    Msg.fromWebView.changedFiles.undo,
    Msg.toWebView.changedFiles.actionResult,
);
export const KeepAllChangedFilesReq = new RequestType<
    Record<string, never>,
    ChangedFilesActionResult
>(Msg.fromWebView.changedFiles.keepAll, Msg.toWebView.changedFiles.actionResult);
export const UndoAllChangedFilesReq = new RequestType<
    Record<string, never>,
    ChangedFilesActionResult
>(Msg.fromWebView.changedFiles.undoAll, Msg.toWebView.changedFiles.actionResult);

/** Show every listed file's changes together, in the one window Visual Studio 18 has for it. Nothing
 *  is changed: a failure comes back like the others, as a reason to show. */
export const ReviewAllChangedFilesReq = new RequestType<
    Record<string, never>,
    ChangedFilesActionResult
>(Msg.fromWebView.changedFiles.reviewAll, Msg.toWebView.changedFiles.actionResult);
