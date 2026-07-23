/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Corsinvest.VisualStudio.Agents.Contracts;
using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Utils;
using Microsoft.VisualStudio.Shell;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>The Usage document-tab UI: pick a profile on the left, its live usage (plan / rate-limit
/// windows) is fetched via a throwaway CLI and shown on the right — same content as the WebView's
/// Account &amp; Usage dialog. Fetch is per-select + Refresh, with a spinner; a new select cancels the
/// one in flight.</summary>
public partial class UsageControl : UserControl
{
    // One rate window bound by the ItemsControl (ResetsIn is display-only, computed here).
    private sealed class WindowVm
    {
        public string Name { get; set; }
        public int Utilization { get; set; }
        public string ResetsIn { get; set; }
    }

    // One attribution group (a title + its rows) for the behaviours section.
    private sealed class AttributionVm
    {
        public string Title { get; set; }
        public UsageAttributionDto[] Rows { get; set; }
    }

    private readonly string _workingDirectory;
    private CancellationTokenSource _cts;
    private UsageDto _dto; // last fetched, for the Day/Week toggle

    public UsageControl()
    {
        InitializeComponent();
        _workingDirectory = AgentsPackage.Instance?.CurrentSolutionFolder ?? "";

        Loaded += (_, _) =>
        {
            var profiles = ProfileStore.Load(forEdit: false).ToList();
            ProfileList.ItemsSource = profiles;
            if (profiles.Count > 0) { ProfileList.SelectedIndex = 0; } // triggers the first fetch
        };
        Unloaded += (_, _) => _cts?.Cancel();
    }

    private void OnProfileSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is Profile p) { _ = FetchAsync(p); }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is Profile p) { _ = FetchAsync(p); }
    }

    private async Task FetchAsync(Profile profile)
    {
        // Cancel any fetch already running (rapid profile switching) and start a fresh one.
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        SetFetching(true);
        ShowMessage(null);
        try
        {
            var wd = _workingDirectory;
            var dto = await Task.Run(() => UsageProbe.FetchAsync(profile, wd, cts.Token), cts.Token);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (cts.IsCancellationRequested) { return; } // superseded by a newer select
            Apply(dto);
        }
        catch (OperationCanceledException)
        {
            // Superseded — the newer fetch owns the UI.
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("UsageControl.Fetch", ex);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!cts.IsCancellationRequested) { ShowMessage("Usage unavailable for this profile."); }
        }
        finally
        {
            if (_cts == cts) { SetFetching(false); }
        }
    }

    private void Apply(UsageDto dto)
    {
        if (dto == null) { ShowMessage("Usage unavailable for this profile."); return; }
        _dto = dto;

        var acct = dto.Account;
        AuthText.Text = dto.AuthMethod;
        SetRow(EmailRow, EmailText, acct?.Email);
        SetRow(OrgRow, OrgText, acct?.Organization);
        PlanText.Text = string.IsNullOrEmpty(dto.Plan) ? "—" : dto.Plan;

        // The claude.ai link only makes sense for the first-party account (null = native Claude);
        // for 3rd-party providers (z.ai/GLM, Bedrock, Vertex, gateway) it points nowhere useful.
        var provider = acct?.ApiProvider;
        var firstParty = string.IsNullOrEmpty(provider) || provider == "firstParty";
        ManageLink.Visibility = firstParty ? Visibility.Visible : Visibility.Collapsed;

        var windows = (dto.Windows ?? Array.Empty<RateWindowDto>())
            .Select(w => new WindowVm
            {
                Name = w.Name,
                Utilization = w.Utilization,
                ResetsIn = UsageMapper.ResetsIn(w.ResetsAt),
            })
            .ToList();
        WindowsList.ItemsSource = windows;
        // No windows (or the CLI said none available) → show the WebView dialog's message instead.
        NoLimitsText.Visibility = (!dto.RateLimitsAvailable || windows.Count == 0)
            ? Visibility.Visible : Visibility.Collapsed;

        // Behaviours: shown only when the CLI reported any period; the Day/Week toggle picks which.
        BehaviorsPanel.Visibility = (dto.Day != null || dto.Week != null)
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyBehaviors();

        ContentPanel.Visibility = Visibility.Visible;
    }

    private void OnPeriodChanged(object sender, RoutedEventArgs e)
    {
        if (_dto != null) { ApplyBehaviors(); }
    }

    // Fill the insights + attribution for the selected period (Day/Week).
    private void ApplyBehaviors()
    {
        var week = WeekTab.IsChecked == true;
        var b = week ? _dto?.Week : _dto?.Day;
        PeriodNote.Text = (week ? "Last 7 days" : "Last 24h")
            + " · these are independent characteristics of your usage, not a breakdown";

        // The DTO already carries the composed headline + body; bind them directly.
        InsightsList.ItemsSource = b?.Insights ?? Array.Empty<UsageInsightDto>();

        var groups = new List<AttributionVm>();
        AddGroup(groups, "Skills", b?.Skills);
        AddGroup(groups, "Subagents", b?.Subagents);
        AddGroup(groups, "Plugins", b?.Plugins);
        AddGroup(groups, "MCP servers", b?.McpServers);
        AttributionList.ItemsSource = groups;
        NoAttributionText.Visibility = (b != null && b.HasAttribution) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void AddGroup(List<AttributionVm> groups, string title, UsageAttributionDto[] rows)
    {
        if (rows != null && rows.Length > 0) { groups.Add(new AttributionVm { Title = title, Rows = rows }); }
    }

    private void OnLinkNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { OutputWindowLogger.LogException("UsageControl.LinkNavigate", ex); }
        e.Handled = true;
    }

    private static void SetRow(UIElement row, TextBlock text, string value)
    {
        var has = !string.IsNullOrEmpty(value);
        row.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        text.Text = value ?? "";
    }

    // While fetching, the centered progress bar replaces the content area (clearer than a small
    // toolbar spinner when switching profiles).
    private void SetFetching(bool on)
    {
        LoadingPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        RefreshButton.IsEnabled = !on;
    }

    private void ShowMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            MessageText.Visibility = Visibility.Collapsed;
            return;
        }
        MessageText.Text = message;
        MessageText.Visibility = Visibility.Visible;
        ContentPanel.Visibility = Visibility.Collapsed;
    }
}
