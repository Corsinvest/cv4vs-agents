/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Microsoft.VisualStudio.Shell;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;

namespace Corsinvest.VisualStudio.Agents.Options;

/// <summary>Custom Options page hosting the environments (profiles) editor. Profiles are
/// persisted to a plain JSON file (<see cref="ProfileStore"/>), NOT the VS settings store,
/// so the View menu and launch path can read them without opening this page first. The page
/// is just the editor UI over that file.</summary>
[ComVisible(true)]
public class AgentsProfilesPage : UIElementDialogPage
{
    private List<Profile> _profiles;

    /// <summary>In-memory profile list. Loaded from disk on first access (each time the page
    /// is created), written back to disk on Apply.</summary>
    public List<Profile> Profiles
    {
        get => _profiles ??= [.. ProfileStore.Load(forEdit: true)];
        set => _profiles = value ?? [];
    }

    // Rebuilt each time the page is shown. Reload from disk here so the editor always
    // reflects the current file (e.g. edited since the page was last opened).
    protected override UIElement Child
    {
        get
        {
            _profiles = [.. ProfileStore.Load(forEdit: true)];
            return new ProfilesControl(this);
        }
    }

    // Persist to the profiles file on Apply, then notify (this page derives from
    // UIElementDialogPage, not the AgentsOptionsPage base that raises Applied).
    protected override void OnApply(PageApplyEventArgs e)
    {
        if (e.ApplyBehavior == ApplyKind.Apply && _profiles != null)
        {
            // Validate before persisting: names must be non-blank and unique
            // (OrdinalIgnoreCase). Block the Apply so the user can fix it rather than
            // silently dropping/overwriting profiles.
            if (Validate(_profiles, out var error))
            {
                ProfileStore.Save(_profiles);
            }
            else
            {
                MessageBox.Show(error, "Profiles",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                e.ApplyBehavior = ApplyKind.Cancel; // keep the Options dialog open, don't save
                return;
            }
        }
        base.OnApply(e);
        if (e.ApplyBehavior == ApplyKind.Apply)
        {
            AgentsOptions.RaiseApplied();
        }
    }

    private static bool Validate(List<Profile> profiles, out string error)
    {
        var blank = profiles.Any(p => string.IsNullOrWhiteSpace(p.Name));
        if (blank)
        {
            error = "A profile has an empty name. Give every profile a name before saving.";
            return false;
        }
        var dup = profiles
            .GroupBy(p => p.Name, System.StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (dup != null)
        {
            error = $"Duplicate profile name \"{dup.Key}\" (names are case-insensitive). Make each name unique before saving.";
            return false;
        }
        error = null;
        return true;
    }
}
