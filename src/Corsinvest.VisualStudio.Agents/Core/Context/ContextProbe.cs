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

namespace Corsinvest.VisualStudio.Agents.Core.Context;

/// <summary>Fetches one historical session's context-window breakdown with a throwaway claude.exe:
/// start it with the profile's env and --resume &lt;sessionId&gt; (get_context_usage needs the
/// session's messages loaded), send the control_request, map, dispose. Like UsageProbe, we do NOT
/// wait for system/init (that arrives only after a real user turn, which would append to the .jsonl)
/// — we wait for the initialize control_response (Account), then send get_context_usage, which is
/// pure calculation (count_tokens): no turn, no write.</summary>
internal static class ContextProbe
{
    // How long to wait for the transport to come up (resume reloads the messages) before giving up.
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Resume <paramref name="sessionId"/> under <paramref name="profile"/>, read its
    /// context usage, kill the CLI. Throws on timeout/cancel; the caller shows "unavailable".</summary>
    public static async Task<GetContextUsageResponse> FetchAsync(
        Profile profile, string workingDirectory, string sessionId, CancellationToken ct)
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
                SsePort = 0,                 // no in-process IDE MCP server: context usage doesn't use the bridge
                ResumeSessionId = sessionId, // load this session's messages so count_tokens has context
            });

            // Wait for the initialize control_response (Account), not system/init — same as UsageProbe.
            await WaitForAccountAsync(client, ct);

            var raw = await client.GetContextUsageAsync(); // null on error
            return raw?.ToObject<GetContextUsageResponse>();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Warn($"[context] fetch failed for session '{sessionId}': {ex.Message}");
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
}
