/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Client;
using Corsinvest.VisualStudio.Agents.Core.Panes;
using Microsoft.Terminal.Wpf;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Windows;

namespace Corsinvest.VisualStudio.Agents.Cli.Pane;

/// <summary>
/// <para>
/// Hosts a real interactive Claude CLI (<c>claude --ide</c>) inside a VS tool
/// window. Bridges <see cref="TerminalControl"/>
/// (rendering, input) and <see cref="TerminalProcess"/> (ConPTY-attached
/// child process). Each pane owns its OWN process — opening multiple CLI
/// panes spawns multiple independent <c>claude</c> sessions.
/// </para>
/// <para>
/// Lifecycle:
///   • <see cref="OnLoaded"/>     resolve workdir, spawn <c>claude --ide</c>.
///   • <see cref="OnUnloaded"/>   no-op (don't kill on hide; user may dock-toggle).
///   • <see cref="Dispose"/>      kill process, drop control.
///   • <see cref="OnSolutionChanged"/> respawn with the new workdir.
/// </para>
/// </summary>
internal class CliPaneControl : PaneControlBase, ITerminalConnection, IDisposable
{
    /// <summary>Fresh terminal session in THIS pane (respawn with no --resume).</summary>
    public override void NewSession() => SetSession(null);

    /// <summary>Resume a past session in THIS pane (respawn with --resume id).</summary>
    public override void LoadSession(string sessionId) => SetSession(sessionId);

    /// <summary>Focus = the terminal.</summary>
    public override void FocusInput() => FocusTerminal();

    /// <summary>No DOM caret in the ConPTY terminal — nothing to blur.</summary>
    public override void BlurInput() { }

    // No editable title for the CLI pane: the interactive terminal doesn't expose its live
    // session id (raw ConPTY, not stream-json), so there's nothing to track/rename. The base's
    // SupportsTitleEditing default is false, but state it explicitly for clarity; SessionTitle
    // stays null (SetSessionTitle is never called) and RenameSession keeps the base no-op.
    public override bool SupportsTitleEditing => false;

    /// <summary>No kind-specific "More" actions for the terminal pane.</summary>
    public override IEnumerable<ButtonAction> MoreMenuActions => [];

    /// <summary>ITerminalConnection requires IDisposable; route the terminal-driven
    /// dispose through the shared pane teardown (guarded by _disposed in the base).</summary>
    public void Dispose() => DisposePane();

    // Built lazily: depends on _activeSessionId (toolbar picker → --resume id) and on the
    // live MCP server (port+token) so the CLI can reach our tools as a second "vs" server.
    private string BuildCommand(int mcpPort, string mcpAuthToken)
    {
        var baseCmd = ClaudeCliLauncher.BuildConPtyCommandLine(ide: true, mcpPort, mcpAuthToken);
        if (string.IsNullOrEmpty(_activeSessionId)) { return baseCmd; }   // safety (shouldn't happen after Step 1)
        // Fresh id we minted → create it (--session-id). Existing id (picker/restore) → resume it.
        return _sessionIsNew
            ? $"{baseCmd} --session-id {_activeSessionId}"
            : $"{baseCmd} --resume {_activeSessionId}";
    }

    private TerminalControl _term;
    private TerminalProcess _process;
    private bool _started;

    /// <summary>Currently active session id (null = fresh <c>claude --ide</c>,
    /// non-null = resume). Driven by the toolbar's history picker via
    /// <see cref="SetSession"/>.</summary>
    private string _activeSessionId;

    /// <summary>True when _activeSessionId is an id WE minted for a fresh terminal (→ --session-id),
    /// false when it came from the picker/restore and already exists on disk (→ --resume).</summary>
    private bool _sessionIsNew;


    public CliPaneControl()
    {
        OutputWindowLogger.Perf("CliPaneControl: ctor begin");
        try
        {
            // Probe Microsoft.Terminal.Wpf loadability first: otherwise the
            // next `new TerminalControl()` throws a generic TypeInitializationException.
            OutputWindowLogger.Perf(() => $"CliPaneControl: TerminalControl assembly = {typeof(TerminalControl).Assembly.Location ?? "<dynamic>"}");
            _term = new TerminalControl
            {
                Focusable = true,
                Connection = this,
                AutoResize = true,
            };
            Content = _term;
            OutputWindowLogger.Perf("CliPaneControl: ctor done");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("CliPaneControl.ctor", ex);
            throw;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        GotFocus += (_, e) => { e.Handled = true; _term?.Focus(); };
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { _term?.Focus(); } };

        VSColorTheme.ThemeChanged += OnThemeChanged;
    }

    void ITerminalConnection.Start()
    {
        // No-op: the actual process is started lazily on the first Resize
        // callback (when the control knows its character grid dimensions).
    }

    void ITerminalConnection.WriteInput(string data) => SendInput(data);

    /// <summary>
    /// Forward raw input bytes to the running CLI. Public so the parent
    /// <see cref="CliPaneWindow"/> can route key sequences (Esc, Ctrl-letter)
    /// that VS would otherwise intercept before they reach the terminal control.
    /// </summary>
    internal void SendInput(string data)
    {
        // No fallback: a dead process auto-closes the pane (see OnProcessExited).
        if (_process?.IsRunning == true)
        {
            _process.WriteInput(data);
        }
    }

    void ITerminalConnection.Resize(uint rows, uint columns)
    {
        if (rows == 0 || columns == 0) { return; }
        var cols = (short)columns;
        var r = (short)rows;

        if (!_started)
        {
            // First Resize: now we know the character grid → spawn the CLI.
            _started = true;
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await StartOrRestartAsync(cols, r);
                }
                catch (Exception ex)
                {
                    OutputWindowLogger.LogException("CliPane.Start", ex);
                }
            }));
        }
        else
        {
            // Forward to ConPTY. Thread-safe — TerminalProcess.Resize locks internally.
            _process?.Resize(cols, r);
        }
    }

    void ITerminalConnection.Close()
    {
        // Lifecycle is owned by this UserControl — nothing to do here.
    }

    private async System.Threading.Tasks.Task StartOrRestartAsync(short cols = 120, short rows = 40)
    {
        // Stop any previous instance first (used by both first-start and
        // solution-change-respawn paths).
        DisposeProcess();

        // Wipe stale output from the previous session by pushing VT reset
        // sequences through the same event ConPTY uses (avoids the
        // version-dependent TerminalControl API):
        //   ESC c (RIS reset), ESC[2J (erase screen), ESC[3J (scrollback), ESC[H (home)
        OutputToControl("c[2J[3J[H");

        // A fresh terminal (no id from picker/restore): mint the session id ourselves and pass it with
        // --session-id, so the CLI creates the session with THAT id and we know it (for stats/titles/
        // restore). ConPTY is output-only, so we can't otherwise learn the id it would self-assign.
        if (string.IsNullOrEmpty(_activeSessionId))
        {
            _activeSessionId = Guid.NewGuid().ToString();
            _sessionIsNew = true;
            Entry.ActiveSessionId = _activeSessionId;
        }
        // Start the in-process MCP server (idempotent) so we know the port+token BEFORE building
        // the command line — the CLI needs them in the --mcp-config "vs" server entry.
        var ssePort = Mcp.McpServerHost.Instance.EnsureStarted();
        var mcpToken = Mcp.McpServerHost.Instance.AuthToken;
        var cmd = BuildCommand(ssePort, mcpToken);
        // The session now exists on disk. A later respawn (e.g. solution-change) must --resume it,
        // NOT re-pass --session-id (which the CLI rejects as "already in use").
        _sessionIsNew = false;
        OutputWindowLogger.Info($"CliPane: starting {cmd ?? "<null>"} in {Entry.WorkingDirectory}");

        // claude.exe must be installed via the official npm package (see
        // ClaudeCliLauncher for why no cmd-shim/PATH fallback). When missing,
        // swap in the inline "not installed" panel instead of a modal dialog.
        if (cmd == null)
        {
            OutputWindowLogger.Warn("[cli] claude.exe not found — showing 'not installed' panel");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ShowMissingPanel();
            return;
        }

        try
        {
            _process = new TerminalProcess();
            _process.OutputReceived += OnOutputReceived;
            _process.ProcessExited += OnProcessExited;
            // FORCE_CODE_TERMINAL=1: CLI's documented "IDE-supported terminal" flag — auto-connect,
            // PID-ancestry disambiguation across VS windows, no onboarding dialogs.
            // CLAUDE_CODE_SSE_PORT: hand the CLI THIS VS's MCP port (auto-connect trigger).
            // CLAUDE_CODE_ENTRYPOINT=claude-vscode: match the chat path — full model catalogue (the
            // server keys off the cc_entrypoint header) + first-party auth.
            // Injected PER-PROCESS via the ConPTY env block (not the parent/global env): a global
            // SetEnvironmentVariable save/restore races when a Chat pane and a CLI pane start at
            // the same time, and would let a profile's env leak into an unrelated process. Profile
            // env is applied first so these three keys — required for the IDE integration to work
            // — always win. (ssePort resolved above, before BuildCommand.)
            var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Entry.Profile.Env != null) { foreach (var kv in Entry.Profile.Env) { env[kv.Key] = kv.Value; } }
            env["FORCE_CODE_TERMINAL"] = "1";
            if (ssePort > 0) { env["CLAUDE_CODE_SSE_PORT"] = ssePort.ToString(); }
            env["CLAUDE_CODE_ENTRYPOINT"] = "claude-vscode";
            _process.Start(cmd, Entry.WorkingDirectory, cols, rows, env);

            // Re-apply theme after Start: the control resets its palette on new connection.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            SetReady(true);
            ApplyTheme();
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("CliPane.StartOrRestart", ex);
            _process?.Dispose();
            _process = null;
        }
    }

    private void DisposeProcess()
    {
        if (_process == null) { return; }
        _process.OutputReceived -= OnOutputReceived;
        _process.ProcessExited -= OnProcessExited;
        _process.Dispose();
        _process = null;
    }

    private void OnOutputReceived(string data)
    {
        if (_disposed) { return; }
        OutputToControl(data);
    }

    private void OutputToControl(string data)
    {
        try
        {
            // We ARE the ITerminalConnection: raise its TerminalOutput event directly.
            _onOutput?.Invoke(this, new TerminalOutputEventArgs(data));
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("CliPane.OutputToControl", ex);
        }
    }

    /// <summary>Backing field for the <see cref="ITerminalConnection.TerminalOutput"/> event.</summary>
    private EventHandler<TerminalOutputEventArgs> _onOutput;
    event EventHandler<TerminalOutputEventArgs> ITerminalConnection.TerminalOutput
    {
        add => _onOutput += value;
        remove => _onOutput -= value;
    }

    /// <summary>Replace the embedded terminal with the shared "Claude Code
    /// not installed" panel produced by <see cref="ClaudeInstall.BuildMissingPanel"/>.
    /// The chat pane will use the same factory.</summary>
    private void ShowMissingPanel()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Content = ClaudeInstall.BuildMissingPanel();
    }

    private void OnProcessExited()
    {
        // claude.exe ended (exit/Ctrl-C/crash). Auto-close the pane VS Code-style —
        // process gone means the terminal pane goes too.

        // Marshal off the ConPTY read thread; ClosePane → VS → Dispose (which
        // removes us from the registry).
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed) { return; }
            OutputWindowLogger.Info("[cli] claude.exe exited — auto-closing pane");
            Pane?.ClosePane();
        }));
    }

    private void OnThemeChanged(ThemeChangedEventArgs e)
    {
        if (_disposed) { return; }
        _ = Dispatcher.BeginInvoke(new Action(ApplyTheme));
    }

    private void ApplyTheme()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            _term?.SetTheme(TerminalThemer.GetTheme(), "Cascadia Mono", 12);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("CliPane.ApplyTheme", ex);
        }
    }

    /// <summary>Move keyboard focus into the embedded terminal. Called by
    /// the toolbar when the user picks a session so typing immediately
    /// reaches claude (otherwise focus stays elsewhere
    /// after <c>frame.Show()</c> brings the pane to front).</summary>
    internal void FocusTerminal()
    {
        if (_disposed || _term == null) { return; }
        // Post at Input priority: the frame is mid-show and the visual tree
        // may not be ready for Focus() until the terminal is parented/visible.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (_disposed) { return; }
            _term?.Focus();
            System.Windows.Input.Keyboard.Focus(_term);
        }));
    }

    /// <summary>Point this pane at a session id (null = fresh, re-minted on start). Used by the toolbar
    /// picker (NewSession/LoadSession, pane already running → respawn to apply) AND by workspace restore
    /// (pane not started yet → the first Resize starts it with the id already set). The <see cref="_started"/>
    /// gate is what makes one method serve both: only respawn when there IS a live process to replace.</summary>
    private void SetSession(string sessionId)
    {
        _activeSessionId = sessionId;
        _sessionIsNew = false;   // picker/restore ids exist on disk (or null → the start block re-mints a fresh one)
        Entry.ActiveSessionId = sessionId;
        OutputWindowLogger.Info($"CliPane: session → {sessionId ?? "<new>"}");
        if (_started) { _ = StartOrRestartAsync(); }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Real start happens on the first Resize (needs cols/rows). Re-push the
        // caption here: RegisterInstance runs before the IVsWindowFrame is wired, so
        // the earlier OwnerCaption set may have no-op'd silently.
        RepushCaption();
    }

    protected override void DisposeCore()
    {
        VSColorTheme.ThemeChanged -= OnThemeChanged;
        DisposeProcess();
        _term = null;
    }
}
