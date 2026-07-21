/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>WebViewMessageHandler: Plugins-namespace message handlers (install/uninstall/enable, marketplace, list).</summary>
internal sealed partial class WebViewMessageHandler
{
    private void HandleInstall(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.PluginInstallNotification>();
        HandlePluginOp("install", affectsActive: true,
            "plugin", "install", p.PluginId, "--scope", p.Scope ?? "user");
    }

    private void HandleUninstall(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.PluginUninstallNotification>();
        HandlePluginOp("uninstall", affectsActive: true,
            "plugin", "uninstall", p.PluginId, "--scope", p.Scope ?? "user");
    }

    private void HandleSetEnabled(JObject data, int? id)
    {
        var p = data.ToObject<Contracts.PluginSetEnabledNotification>();
        HandlePluginOp("set-enabled", affectsActive: true,
            "plugin", p.Enabled ? "enable" : "disable", p.PluginId);
    }

    private void HandleMarketplaceAdd(JObject data, int? id)
    {
        // Adding a marketplace only changes what's installable, not the active plugins.
        HandlePluginOp("marketplace-add", affectsActive: false,
            "plugin", "marketplace", "add", data.ToObject<Contracts.MarketplaceAddNotification>().Source);
    }

    private void HandleMarketplaceRemove(JObject data, int? id)
    {
        // Removing a marketplace also disables its plugins → touches active plugins.
        HandlePluginOp("marketplace-remove", affectsActive: true,
            "plugin", "marketplace", "remove", data.ToObject<Contracts.MarketplaceRemoveNotification>().Name);
    }

    private void HandleMarketplaceRefresh(JObject data, int? id)
    {
        // Refresh re-fetches a marketplace's catalog; installed/active plugins are untouched.
        HandlePluginOp("marketplace-refresh", affectsActive: false,
            "plugin", "marketplace", "update", data.ToObject<Contracts.MarketplaceRefreshNotification>().Name);
    }

    private void HandlePluginList(int? id)
    {
        if (id is not int reqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var (installed, available) = await Core.Plugins.PluginService.ListAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.Plugins.ListResult, reqId,
                new Contracts.PluginListResponse { Installed = installed, Available = available });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandleMarketplaceList(int? id)
    {
        if (id is not int reqId) { return; }
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var marketplaces = await Core.Plugins.PluginService.MarketplaceListAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.SendResponse(BridgeMessages.ToWebView.Plugins.MarketplaceListResult, reqId,
                new Contracts.MarketplaceListResponse { Marketplaces = marketplaces });
        }).FileAndForget(nameof(WebViewMessageHandler));
    }

    private void HandlePluginOp(string op, bool affectsActive, params string[] args)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var (ok, message) = await Core.Plugins.PluginService.RunOpAsync(args);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bridge.Send(BridgeMessages.ToWebView.Plugins.OpResult, new Contracts.PluginOpResultNotification
            {
                Op = op,
                Ok = ok,
                Message = message,
                AffectsActive = affectsActive && ok,
            });
            // TODO(Task 5): broadcast plugins_changed to ALL live chats when (ok && affectsActive).
            if (ok && affectsActive) { bridge.Send(BridgeMessages.ToWebView.Plugins.Changed, null); }
        }).FileAndForget(nameof(WebViewMessageHandler));
    }
}
