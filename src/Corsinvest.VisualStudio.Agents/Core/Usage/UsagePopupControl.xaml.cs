/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Microsoft.VisualStudio.Shell;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;

namespace Corsinvest.VisualStudio.Agents.Core.Usage;

/// <summary>The status bar popup: the profile and plan, every limit window with its bar and reset, the
/// account (collapsed), and when the numbers were fetched. Rendered from a snapshot; its links go to the
/// service, the Usage tab and claude.ai.</summary>
public partial class UsagePopupControl : UserControl
{
    private sealed class WindowRow
    {
        public string Name { get; set; }
        public string PercentText { get; set; }
        public GridLength FilledWidth { get; set; }
        public GridLength EmptyWidth { get; set; }
        public string ResetText { get; set; }

        // A string, so the XAML triggers compare it without a converter.
        public string Level { get; set; }
    }

    private sealed class AccountRow(string label, string value)
    {
        public string Label { get; } = label;
        public string Value { get; } = value;
    }

    public UsagePopupControl() => InitializeComponent();

    /// <summary>The popup should close; true when that was Esc, so focus goes back where the user was.</summary>
    public event Action<bool> CloseRequested;

    internal void Render(UsageSnapshot snapshot, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.Local;
        var culture = CultureInfo.CurrentCulture;
        var usage = snapshot.Usage;
        var plan = usage != null && usage.Plan != "—" ? usage.Plan : null;

        ProfileText.Text = snapshot.ProfileName;
        PlanText.Text = plan ?? "";

        var windows = snapshot.State == UsageAvailability.Available ? usage.Windows ?? [] : [];
        WindowsList.ItemsSource = windows.Select(w =>
        {
            var percent = UsageStatusFormat.EffectivePercent(w, now);
            return new WindowRow
            {
                Name = w.Name,
                PercentText = percent + "%",
                FilledWidth = new GridLength(percent, GridUnitType.Star),
                EmptyWidth = new GridLength(100 - percent, GridUnitType.Star),
                ResetText = UsageStatusFormat.ResetText(w.ResetsAt, now, zone, culture),
                Level = UsageStatusFormat.WindowLevel(w, now).ToString(),
            };
        }).ToList();
        WindowsList.Visibility = windows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var message = snapshot.State switch
        {
            UsageAvailability.NoPlan => "Plan usage isn't available for this profile: it doesn't sign in to a Claude plan.",
            UsageAvailability.CliMissing => "Claude Code isn't installed, or its path in Options → cv4vs Agents → General is wrong.",
            UsageAvailability.Unavailable => "Usage couldn't be fetched.",
            UsageAvailability.Unknown when !snapshot.IsFetching => "Not fetched yet.",
            _ => null,
        };
        MessageText.Text = message ?? "";
        MessageText.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        LoadingPanel.Visibility = snapshot.IsFetching && usage == null ? Visibility.Visible : Visibility.Collapsed;

        var account = usage?.Account;
        var rows = new[]
        {
            new AccountRow("Auth method", usage?.AuthMethod),
            new AccountRow("Email", account?.Email),
            new AccountRow("Organization", account?.Organization),
            new AccountRow("Plan", plan),
        }.Where(r => !string.IsNullOrEmpty(r.Value)).ToList();
        AccountList.ItemsSource = rows;
        AccountExpander.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdatedText.Text = snapshot.IsFetching && usage != null ? "Refreshing…" : UsageStatusFormat.UpdatedText(snapshot, zone, culture);
        UpdatedText.Visibility = UpdatedText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = snapshot.LastErrorAt != null ? "Last refresh failed: " + snapshot.LastError : "";
        ErrorText.Visibility = snapshot.LastErrorAt != null ? Visibility.Visible : Visibility.Collapsed;
        RefreshLink.IsEnabled = !snapshot.IsFetching;

        var provider = account?.ApiProvider;
        ManageLinkHost.Visibility = snapshot.State != UsageAvailability.NoPlan && (string.IsNullOrEmpty(provider) || provider == "firstParty")
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(true);
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        UsageStatusService.Instance.RefreshNow();
    }

    private void OnOpenUsageClick(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(false);
        UsageWindow.Open();
    }

    private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke(false);
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { OutputWindowLogger.Global.LogException("[usage-status] open claude.ai", ex); }
    }
}
