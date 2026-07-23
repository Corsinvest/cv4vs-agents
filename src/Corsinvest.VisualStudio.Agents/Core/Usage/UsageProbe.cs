/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Helpers;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>Fetches a profile's live usage with a throwaway claude.exe: start it with the profile's
/// env, send get_usage, map the result, then dispose it. The Usage tab has no live pane of its own,
/// so each profile gets its own short-lived process. No IDE MCP server (SsePort=0) — usage doesn't
/// use the bridge. We do NOT wait for system/init: that arrives only after a real user turn (the CLI
/// only emits it once a prompt is sent), and get_usage is a plain control_request that works as soon
/// as the transport is up — so we send it directly without spending a turn.</summary>
internal static class UsageProbe
{
    // How long to wait for the transport to come up before giving up.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Start a CLI for <paramref name="profile"/>, read its usage, kill it. Throws on
    /// timeout/cancel; the caller shows "unavailable". workingDirectory falls back to the user
    /// profile folder (get_usage is account-scoped, not project-scoped).</summary>
    public static async Task<UsageDto> FetchAsync(Profile profile, string workingDirectory, CancellationToken ct)
    {
        var client = new ClaudeClient();
        try
        {
            var wd = string.IsNullOrEmpty(workingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : workingDirectory;
            await client.StartAsync(new ClientOptions
            {
                WorkingDirectory = wd,
                Env = profile?.Env,
                SsePort = 0, // no in-process IDE MCP server: usage doesn't use the bridge
            });

            // StartupAsync sends `initialize` on its own; its control_response carries the account
            // (email/org/apiProvider) — no user turn needed (that's system/init, which we don't wait
            // for). Wait for Account to land, then ask usage.
            await WaitForAccountAsync(client, ct);

            var raw = await client.GetUsageAsync(); // null on error
            var account = ToAccountDto(client.Account);
            return UsageMapper.Build(raw, account);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Warn($"[usage] fetch failed for profile '{profile?.Name}': {ex.Message}");
            throw;
        }
        finally
        {
            try { client.Dispose(); } catch { }
        }
    }

    // Poll until the initialize response has landed (Account populated), honouring cancellation and a
    // hard timeout so a wedged CLI can't hang the tab.
    private static async Task WaitForAccountAsync(ClaudeClient client, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + StartTimeout;
        while (client.Account == null)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("CLI did not initialize in time"); }
            await Task.Delay(100, ct);
        }
    }

    private static AccountDto ToAccountDto(AccountInfo a)
        => a == null
            ? null
            : new AccountDto
            {
                Email = a.Email,
                Organization = a.Organization,
                SubscriptionType = a.SubscriptionType,
                ApiProvider = a.ApiProvider,
            };
}
