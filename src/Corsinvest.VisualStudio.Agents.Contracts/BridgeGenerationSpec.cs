/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using TypeGen.Core.SpecGeneration;

namespace Corsinvest.VisualStudio.Agents.Contracts;

// Lists the wire DTOs to emit as TS interfaces. Keeps the POCOs above attribute-free.
// Split by direction to mirror the two DTO files (ToWebViewDtos / FromWebViewDtos).
//
// Nullability rule: a reference field the emitter can leave absent is marked .Null() →
// generated `| null`, so the TS type never lies about what the wire can carry. Fields the
// emitter always sets (Val(x,""), ?? "", ?? [], .ToArray(), computed literals,
// enum.ToString()) stay non-null.
public class BridgeGenerationSpec : GenerationSpec
{
    public BridgeGenerationSpec()
    {
        RegisterToWebView();
        RegisterFromWebView();
    }

    // C# → WebView payloads (ToWebViewDtos.cs). Pass-through fields sourced from nullable
    // CLI event args are the bulk of the .Null() entries here.
    private void RegisterToWebView()
    {
        AddInterface<ContextUsageDto>();
        AddInterface<ModelInfoDto>();
        AddInterface<SlashCommandDto>();
        // permissionMode stays a plain string in C# (the wire value from the CLI), but
        // the WebView wants the narrowed union — point the member at the hand-written type.
        AddInterface<CliStateDto>()
            .Member(x => nameof(x.PermissionMode)).Type("PermissionMode", "../types")
            .Member(x => nameof(x.EffortLevel)).Null()
            .Member(x => nameof(x.AlwaysThinkingEnabled)).Null()
            .Member(x => nameof(x.SwitchModelsOnFlag)).Null()
            .Member(x => nameof(x.Ultracode)).Null()
            .Member(x => nameof(x.SpinnerVerbsConfig)).Null();
        AddInterface<AtItemDto>();
        AddInterface<GetImageResponse>();
        AddInterface<SubagentUsageDto>();
        AddInterface<SubagentStartedNotification>().Member(x => nameof(x.ToolUseId)).Null();
        AddInterface<SubagentProgressNotification>()
            .Member(x => nameof(x.LastToolName)).Null()
            .Member(x => nameof(x.Summary)).Null()
            .Member(x => nameof(x.ToolUseId)).Null();
        AddInterface<SubagentEndedNotification>();
        AddInterface<CompactedNotification>();
        AddInterface<StatusNotification>();
        AddInterface<CliExitedNotification>();
        AddInterface<ToolResultNotification>()
            .Member(x => nameof(x.ParentToolUseId)).Null()
            .Member(x => nameof(x.AgentId)).Null();
        AddEnum<NoticeVariantDto>(asUnionType: true).StringInitializers();
        AddEnum<EffortLevelDto>(asUnionType: true).StringInitializers();
        AddInterface<RateLimitNotification>();
        AddInterface<ModelChangedNotification>();
        AddInterface<PermissionModeChangedNotification>();
        AddInterface<SetComposerNotification>();
        AddInterface<PromptHistoryNotification>();
        AddInterface<SlashCommandsNotification>();
        AddInterface<AssistantTextDeltaNotification>().Member(x => nameof(x.ParentToolUseId)).Null();
        AddInterface<ThinkingDeltaNotification>().Member(x => nameof(x.ParentToolUseId)).Null();
        AddInterface<ThinkingEndedNotification>().Member(x => nameof(x.ParentToolUseId)).Null();
        AddInterface<ToolProgressNotification>().Member(x => nameof(x.ParentToolUseId)).Null();
        AddInterface<ThemeChangedNotification>();
        AddInterface<GetSuggestionsResponse>();
        AddInterface<ModelsNotification>();
        // usage rides on the first block of a turn only (null afterwards) / is null when
        // the result carried none — so it's genuinely nullable on the wire.
        AddInterface<AssistantTextNotification>()
            .Member(x => nameof(x.ParentToolUseId)).Null()
            .Member(x => nameof(x.Usage)).Null();
        AddInterface<ExchangeEndedNotification>().Member(x => nameof(x.Usage)).Null();
        AddInterface<IdeContextNotification>();
        AddInterface<CliErrorNotification>();
        AddInterface<UserImageDto>()
            .Member(x => nameof(x.Uuid)).Null()
            .Member(x => nameof(x.Preview)).Null();
        AddInterface<UserFileDto>().Member(x => nameof(x.Uuid)).Null();
        AddInterface<UserTextNotification>()
            .Member(x => nameof(x.Images)).Null()
            .Member(x => nameof(x.Files)).Null()
            .Member(x => nameof(x.ParentToolUseId)).Null()
            .Member(x => nameof(x.Uuid)).Null();
        AddInterface<ToolPermissionNotification>().Member(x => nameof(x.ParentToolUseId)).Null();
        AddInterface<ToolPermissionCancelNotification>();
        AddInterface<HistoryEventDto>();
        AddInterface<GetHistoryResponse>().Member(x => nameof(x.SessionId)).Null();
        AddInterface<HistoryLoadedNotification>().Member(x => nameof(x.SessionId)).Null();
        AddInterface<GetSubagentResponse>();
        AddInterface<GetCompactSummaryResponse>();
        AddInterface<SpinnerVerbsConfigDto>();
        AddInterface<InitConfigDto>();
        AddInterface<VsOptionsDto>();
        AddInterface<InitPayloadNotification>()
            .Member(x => nameof(x.CliState)).Null()
            .Member(x => nameof(x.VsOptions)).Null();
        AddInterface<AccountDto>();
        // Usage (get_usage), decoded once in HandleGetUsage into this typed shape.
        AddInterface<RateWindowDto>()
            .Member(x => nameof(x.ResetsAt)).Null();
        AddInterface<UsageInsightDto>();
        AddInterface<UsageAttributionDto>();
        AddInterface<UsageBehaviorsDto>();
        AddInterface<UsageDto>()
            .Member(x => nameof(x.Account)).Null()
            .Member(x => nameof(x.Day)).Null()
            .Member(x => nameof(x.Week)).Null();

        // Context-usage DTOs (get_context_usage). The handler always maps every field from the
        // JObject, so arrays/objects are non-null (empty [] when the CLI reports none).
        AddInterface<ContextCategoryDto>();
        AddInterface<ContextGridCellDto>();
        AddInterface<ContextMemoryFileDto>();
        AddInterface<ContextAgentDto>();
        AddInterface<ContextSkillDto>();
        AddInterface<ContextSkillsDto>();
        AddInterface<ContextCommandsDto>();
        AddInterface<ContextMcpToolDto>();
        AddInterface<ContextTokenGroupDto>();
        AddInterface<ContextMessageBreakdownDto>();
        AddInterface<GetContextUsageResponse>();

        // Statistics DTOs (get_stats).
        AddEnum<StatsScopeDto>(asUnionType: true).StringInitializers();
        AddEnum<StatsRangeDto>(asUnionType: true).StringInitializers();
        AddInterface<StatsModelDto>();
        AddInterface<StatsDayDto>();
        AddInterface<StatsDayModelDto>();
        AddInterface<StatsToolDto>();
        AddInterface<StatsResponse>();

        // Plugin-manager DTOs (from `claude plugin … --json`, mapped in PluginService).
        AddInterface<PluginDto>();
        AddEnum<PluginSourceKindDto>(asUnionType: true).StringInitializers();
        AddInterface<AvailablePluginDto>();
        // Url/Repo/Path are set only for matching source types → genuinely nullable on the wire.
        AddInterface<MarketplaceDto>()
            .Member(x => nameof(x.Url)).Null()
            .Member(x => nameof(x.Repo)).Null()
            .Member(x => nameof(x.Path)).Null();
        AddInterface<PluginListResponse>();
        AddInterface<MarketplaceListResponse>();
        AddInterface<PluginOpResultNotification>();
    }

    // WebView → C# payloads (FromWebViewDtos.cs). sessionId/agentId can arrive absent (the
    // handler falls back to the current session / main transcript) → nullable.
    private void RegisterFromWebView()
    {
        AddInterface<SendPromptNotification>().Member(x => nameof(x.Attachments)).Null();
        // denyMessage/updatedInput/updatedPermissions are each sent only on some paths
        // (allow-with-input vs deny vs allow-with-suggestion) → optional.
        AddInterface<RespondPermissionNotification>()
            .Member(x => nameof(x.DenyMessage)).Optional()
            .Member(x => nameof(x.UpdatedInput)).Optional()
            .Member(x => nameof(x.UpdatedPermissions)).Optional();
        AddInterface<SetSendSelectionNotification>();
        AddInterface<IdeFileNotification>();
        AddInterface<IdeFileAtEditNotification>();
        AddInterface<GetSuggestionsRequest>();
        // agentId/toolName omitted by openError() → optional.
        AddInterface<ToolOutputNotification>()
            .Member(x => nameof(x.AgentId)).Optional()
            .Member(x => nameof(x.ToolName)).Optional();
        // sessionId is OMITTED by the WebView on these (the handler falls back to the current
        // session) → optional `?`, not `| null`, so the call sites don't have to pass null.
        AddInterface<GetSubagentRequest>().Member(x => nameof(x.SessionId)).Optional();
        AddInterface<GetImageRequest>().Member(x => nameof(x.SessionId)).Optional();
        AddInterface<GetCompactSummaryRequest>().Member(x => nameof(x.SessionId)).Optional();
        AddInterface<SubagentCancelNotification>();
        // getHistory always sends sessionId → keep it required-nullable.
        AddInterface<GetHistoryRequest>().Member(x => nameof(x.SessionId)).Null();
        AddInterface<GetUsageRequest>();
        AddInterface<GetContextUsageRequest>();
        AddInterface<GetStatsRequest>();
        AddInterface<OpenDocumentNotification>().Member(x => nameof(x.SessionId)).Optional();
        AddInterface<OpenAttachmentNotification>();
        AddInterface<DiffDialogNotification>();
        AddInterface<SetPermissionModeNotification>();
        AddInterface<SetModelNotification>();
        AddInterface<ForkNotification>().Member(x => nameof(x.SessionId)).Optional();
        AddInterface<ExternalUrlNotification>();

        // Plugin-manager requests. Scope is omitted by the WebView (host defaults to "user").
        AddInterface<PluginInstallNotification>().Member(x => nameof(x.Scope)).Optional();
        AddInterface<PluginUninstallNotification>().Member(x => nameof(x.Scope)).Optional();
        AddInterface<PluginSetEnabledNotification>().Member(x => nameof(x.Scope)).Optional();
        AddInterface<MarketplaceAddNotification>();
        AddInterface<MarketplaceRemoveNotification>();
        AddInterface<MarketplaceRefreshNotification>();
    }
}
