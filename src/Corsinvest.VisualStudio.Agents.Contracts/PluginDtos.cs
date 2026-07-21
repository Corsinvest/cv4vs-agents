/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

namespace Corsinvest.VisualStudio.Agents.Contracts;

// Plugin-manager DTOs mirroring `claude plugin … --json` output, parsed by PluginService.

// `id` is "name@marketplace"; Name/Marketplace are split from it host-side (the CLI's installed
// list carries only the fused id).
public class PluginDto
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Marketplace { get; set; }
    public string Version { get; set; }
    public string Scope { get; set; }
    public bool Enabled { get; set; }
    // ISO timestamps from the CLI. LastUpdated == InstalledAt until the plugin is first updated.
    public string InstalledAt { get; set; }
    public string LastUpdated { get; set; }
}

public class AvailablePluginDto
{
    public string PluginId { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public string MarketplaceName { get; set; }
    public string Version { get; set; }
    public int InstallCount { get; set; }
    // How to read Source: a ready-to-open URL (git-subdir already resolved to repo/tree/main/<path>
    // host-side), a marketplace-relative "./path" (the WebView joins it onto the marketplace repo,
    // like VS Code), or None.
    public PluginSourceKindDto SourceKind { get; set; }
    public string Source { get; set; }
}

public enum PluginSourceKindDto
{
    None,
    Url,
    Relative,
}

// Url/Repo/Path are set per source type (git/url → Url, github → Repo, directory/file → Path).
public class MarketplaceDto
{
    public string Name { get; set; }
    public string Source { get; set; }
    public string Location { get; set; }
    public string Url { get; set; }
    public string Repo { get; set; }
    public string Path { get; set; }
}

public class PluginListResponse
{
    public PluginDto[] Installed { get; set; }
    public AvailablePluginDto[] Available { get; set; }
}

public class MarketplaceListResponse
{
    public MarketplaceDto[] Marketplaces { get; set; }
}

// AffectsActive = the change touched active plugins → the live chats need a reload banner.
public class PluginOpResultNotification
{
    public string Op { get; set; }
    public bool Ok { get; set; }
    public string Message { get; set; }
    public bool AffectsActive { get; set; }
}

public class PluginInstallNotification
{
    public string PluginId { get; set; }
    public string Scope { get; set; }
}

public class PluginUninstallNotification
{
    public string PluginId { get; set; }
    public string Scope { get; set; }
}

public class PluginSetEnabledNotification
{
    public string PluginId { get; set; }
    public bool Enabled { get; set; }
    public string Scope { get; set; }
}

public class MarketplaceAddNotification
{
    public string Source { get; set; }
}

public class MarketplaceRemoveNotification
{
    public string Name { get; set; }
}

public class MarketplaceRefreshNotification
{
    public string Name { get; set; }
}
