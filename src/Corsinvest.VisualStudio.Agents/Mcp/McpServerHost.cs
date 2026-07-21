/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Core.Profiles;
using Corsinvest.VisualStudio.Agents.Helpers;
using Corsinvest.VisualStudio.Agents.Ide;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Corsinvest.VisualStudio.Agents.Mcp;

/// <summary>
/// In-process MCP server (one per VS process, N CLI clients) exposing the IDE's editor/solution/
/// diagnostics APIs to the Claude CLI over a loopback WebSocket on an OS-picked port.
/// Writes an identical lock file (pid + workspaceFolders + authToken) as <c>&lt;port&gt;.lock</c> into
/// the <c>ide/</c> folder of every enabled profile's config-dir (the native "Claude" included),
/// kept in sync as profiles change, so each <c>claude --ide</c> (which only scans its own
/// config-dir) discovers this single server. See <see cref="Start"/>/
/// <see cref="Stop"/>/<see cref="RewriteLockFileAsync"/> for lifecycle.
/// </summary>
internal sealed partial class McpServerHost
{
    private static readonly Lazy<McpServerHost> _instance = new(() => new McpServerHost());
    public static McpServerHost Instance => _instance.Value;

    private HttpListener _listener;
    private CancellationTokenSource _cts;
    // The ide/ folders we've written a lock into. The server is single, but the lock is
    // published into every active config-dir so each `claude --ide` (scanning its own
    // config-dir) discovers us. Always includes the system default while running.
    // Guarded by _lockFoldersGate: mutated from the UI thread (registry Changed → SyncLockFolders)
    // AND from a background thread (RewriteLockFileAsync's Task.Run), so access must be serialized.
    private readonly HashSet<string> _lockFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lockFoldersGate = new();
    private string _authToken;
    private int _port;

    /// <summary>Loopback port the server listens on (0 when stopped). Passed to <c>claude --ide</c>
    /// via <c>CLAUDE_CODE_SSE_PORT</c> so the CLI connects directly without scanning the lock dir.</summary>
    public int Port => _port;

    /// <summary>Bearer token the CLI must present (also written to the lock).</summary>
    public string AuthToken => _authToken;

    /// <summary>True while the listener is up.</summary>
    public bool IsRunning => _listener != null;
    private JsonRpcDispatcher _dispatcher;

    /// <summary>Connected CLI clients; we broadcast <c>selection_changed</c> to all on editor
    /// selection changes (drives <c>&lt;ide_selection&gt;</c> injection). Snapshot-on-broadcast so a
    /// slow client can't block new connections.</summary>
    private readonly List<ClientConn> _clients = [];
    private readonly object _clientsLock = new();

    /// <summary>One connected <c>claude --ide</c> client.</summary>
    private sealed class ClientConn
    {
        public WebSocket Ws { get; set; }
    }

    private McpServerHost() { }

    /// <summary>The distinct ide/ folders across ALL profiles (native "Claude" included via
    /// ProfileStore). The single server publishes an identical lock into each so a <c>claude --ide</c>
    /// on any configured config-dir discovers it. Two profiles on the same CLAUDE_CONFIG_DIR collapse
    /// to one folder (Distinct).</summary>
    private static List<string> ProfileIdeFolders() =>
        ProfileStore.Load(forEdit: false)
            .Select(p => ClaudePaths.ForProfile(p).IdeFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Start()
    {
        if (_listener != null) { return; } // idempotent

        try
        {
            CleanupOrphanLockFiles(ProfileIdeFolders());

            // Port 0 → OS picks a free ephemeral port.
            var port = AllocateFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _port = port;
            _authToken = Guid.NewGuid().ToString("N");
            _cts = new CancellationTokenSource();

            _dispatcher = new JsonRpcDispatcher(BuildToolRegistry());

            SyncLockFolders(); // publish a lock into every profile's config-dir
            OutputWindowLogger.Info($"Mcp: server started on 127.0.0.1:{_port}");

            // Subscribe to editor changes (idempotent; the WebView chat path may also call it).
            Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                IdeContextService.Instance.SubscribeToEditorEvents();
            });
            IdeContextService.Instance.ContextChanged += OnEditorContextChanged;
            // Re-sync the locks when the profile list changes (Options → Profiles Apply): add locks
            // for new profiles' config-dirs, remove those of deleted ones — no server restart needed.
            Options.AgentsOptions.Applied += OnProfilesChanged;

            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Mcp.Start", ex);
            // Best-effort cleanup so we don't leave a half-open listener.
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }
    }

    /// <summary>Start the server if needed and return its port (idempotent). Called before spawning
    /// claude so the port is ready for CLAUDE_CODE_SSE_PORT.</summary>
    public int EnsureStarted()
    {
        Start();
        return _port;
    }

    /// <summary>Serve one MCP JSON-RPC message through the same tool dispatcher the
    /// WebSocket transport uses. This is the in-process channel for the stream-json
    /// chat: the CLI relays the chat's tool calls as `mcp_message`, the host forwards
    /// them here. Returns the JSON-RPC response string (null for notifications).</summary>
    public Task<string> ServeMcpMessageAsync(string jsonRpc)
    {
        EnsureStarted();
        return _dispatcher.HandleMessageAsync(jsonRpc);
    }

    public void Stop()
    {
        try
        {
            IdeContextService.Instance.ContextChanged -= OnEditorContextChanged;
            Options.AgentsOptions.Applied -= OnProfilesChanged;
            _cts?.Cancel();
            _listener?.Stop();
            _ = IdeDiffViewer.Instance.CancelAllPendingAsync();
            DeleteAllLockFiles();
            OutputWindowLogger.Info("Mcp: server stopped");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Mcp.Stop", ex);
        }
        finally
        {
            _listener = null;
            _cts = null;
        }
    }

    /// <summary>Re-emit the lock files with up-to-date workspace folders (called on solution
    /// open/close), one rewrite per currently-published ide/ folder.</summary>
    public async Task RewriteLockFileAsync()
    {
        if (_listener == null) { return; }
        try
        {
            await Task.Run(() =>
            {
                string[] folders;
                lock (_lockFoldersGate) { folders = _lockFolders.ToArray(); }
                // A pane closing concurrently could remove a folder from _lockFolders (and delete
                // its lock) between this snapshot and the write below, resurrecting an orphan lock.
                // Tolerated: the lock still points at this live VS pid, so the CLI accepts it, and
                // the next SyncLockFolders / Start cleanup removes it. Not worth holding the gate
                // across the (slow) file I/O.
                foreach (var folder in folders) { WriteLockFile(folder); }
            });
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Mcp.RewriteLockFile", ex); }
    }

    //  Tool registry

    private static IEnumerable<IMcpTool> BuildToolRegistry()
    {
        // Add new tools here (order irrelevant; dispatcher is name-keyed). One file per tool under Mcp/Tools/.
        yield return new Tools.GetWorkspaceFoldersTool();
        yield return new Tools.GetCurrentSelectionTool();
        yield return new Tools.GetLatestSelectionTool();
        yield return new Tools.GetOpenEditorsTool();
        yield return new Tools.GetDiagnosticsTool();
        yield return new Tools.OpenFileTool();
        yield return new Tools.OpenDiffTool();
        yield return new Tools.CloseTabTool();
        yield return new Tools.CloseAllDiffTabsTool();
        yield return new Tools.ExecuteCodeTool();
        yield return new Tools.FormatDocumentTool();
        yield return new Tools.OrganizeImportsTool();
        yield return new Tools.RunCleanupTool();
        yield return new Tools.SaveDocumentTool();
        yield return new Tools.CheckDocumentDirtyTool();
        yield return new Tools.GetVisualStudioVersionTool();
        yield return new Tools.GetVisualStudioEditionTool();
        yield return new Tools.BuildSolutionTool();
        yield return new Tools.BuildProjectTool();
        yield return new Tools.SetStartupProjectTool();
        yield return new Tools.GetProjectStructureTool();
        yield return new Tools.GoToDefinitionTool();
        yield return new Tools.FindReferencesTool();
        yield return new Tools.GoToImplementationTool();
        yield return new Tools.GetDocumentSymbolsTool();
        yield return new Tools.RenameSymbolTool();
        yield return new Tools.SearchWorkspaceSymbolsTool();
        yield return new Tools.StartDebugTool();
        yield return new Tools.StartNoDebuggerTool();
        yield return new Tools.StopDebugTool();
        yield return new Tools.GetDebugStateTool();
        yield return new Tools.SetBreakpointTool();
        yield return new Tools.SetFunctionBreakpointTool();
        yield return new Tools.RemoveBreakpointTool();
        yield return new Tools.ClearBreakpointsTool();
        yield return new Tools.ListBreakpointsTool();
        yield return new Tools.BreakDebugTool();
        yield return new Tools.ListProcessesTool();
        yield return new Tools.AttachDebugTool();
        yield return new Tools.ContinueDebugTool();
        yield return new Tools.StepDebugTool();
        yield return new Tools.GetDebugCallStackTool();
        yield return new Tools.GetDebugLocalsTool();
        yield return new Tools.EvaluateExpressionTool();
        yield return new Tools.ReadOutputTool();
        yield return new Tools.ClearOutputTool();
        yield return new Tools.ActivateOutputTool();
        yield return new Tools.RestartDebugTool();
        yield return new Tools.SetExceptionBreakTool();
        yield return new Tools.ApplyHotReloadTool();
    }

    //  Accept loop + per-client loop

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }     // listener stopped
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                OutputWindowLogger.LogException("Mcp.Accept", ex);
                continue;
            }

            // Dedicated task so the next client doesn't wait on this one's handshake.
            _ = Task.Run(() => HandleContextAsync(ctx, ct));
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        // Bearer token check: loopback alone isn't enough — any local process could connect.
        var auth = ctx.Request.Headers["x-claude-code-ide-authorization"]
                   ?? ctx.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(auth) || !auth.EndsWith(_authToken, StringComparison.Ordinal))
        {
            OutputWindowLogger.Warn("[mcp] rejected client: missing/invalid auth token (401)");
            ctx.Response.StatusCode = 401;
            ctx.Response.Close();
            return;
        }
        if (!ctx.Request.IsWebSocketRequest)
        {
            OutputWindowLogger.Debug(() => "[mcp] non-websocket request dropped (400)");
            ctx.Response.StatusCode = 400;
            ctx.Response.Close();
            return;
        }

        WebSocketContext wsCtx;
        // MUST echo the "mcp" subprotocol or the CLI treats the upgrade as failed and aborts the
        // TCP connection (matches the official VS Code extension behavior).
        try { wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: "mcp"); }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Mcp.AcceptWebSocket", ex);
            return;
        }

        await ClientLoopAsync(wsCtx.WebSocket, ct);
    }

    private async Task ClientLoopAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        // Register so broadcasts reach this client; deregistered in finally.
        var conn = new ClientConn { Ws = ws };
        lock (_clientsLock) { _clients.Add(conn); }
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                }
                while (!res.EndOfMessage);

                var raw = sb.ToString();
                OutputWindowLogger.Trace(() => $"Mcp: <- {StringHelpers.Truncate(raw, 200)}");
                var reply = await _dispatcher.HandleMessageAsync(raw);
                // Seed initial context on tools/list (not initialized): the CLI's useIdeSelection
                // hook only subscribes once the server reaches 'connected', around tools/list time.
                // The later 1s delay covers the React effect lagging the state transition.
                if (raw.IndexOf("\"tools/list\"", StringComparison.Ordinal) >= 0)
                {
                    _ = DelayedSendInitialContextAsync(ws);
                }
                if (reply != null && ws.State == WebSocketState.Open)
                {
                    OutputWindowLogger.Trace(() => $"Mcp: -> {StringHelpers.Truncate(reply, 200)}");
                    var bytes = Encoding.UTF8.GetBytes(reply);
                    await ws.SendAsync(new ArraySegment<byte>(bytes),
                        WebSocketMessageType.Text, endOfMessage: true, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* expected on shutdown */ }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("Mcp.ClientLoop", ex);
            // WebSocket exceptions wrap the real cause in InnerException — unwrap for diagnostics.
            var inner = ex.InnerException;
            int depth = 0;
            while (inner != null && depth++ < 5)
            {
                OutputWindowLogger.LogException($"Mcp.ClientLoop.inner[{depth}]", inner);
                inner = inner.InnerException;
            }
        }
        finally
        {
            bool noClientsLeft;
            lock (_clientsLock)
            {
                _clients.Remove(conn);
                noClientsLeft = _clients.Count == 0;
            }
            try { ws.Dispose(); } catch { }
            // Only on the LAST disconnect: with another client still up its
            // diffs are legitimately pending.
            if (noClientsLeft)
            {
                _ = IdeDiffViewer.Instance.CancelAllPendingAsync();
            }
        }
    }

    /// <summary>Builds a <c>selection_changed</c> notification and broadcasts it. Payload shape
    /// mirrors the official VS Code extension so the CLI's <c>useIdeSelection</c> hook accepts it
    /// and injects an <c>&lt;ide_selection&gt;</c> block: { text, filePath, fileUrl, selection }.</summary>
    private void OnEditorContextChanged(EditorContext ctx)
    {
        if (ctx == null)
        {
            // No active document: empty text is the CLI's signal to drop its cached selection.
            BroadcastNotification(BuildSelectionNotification(
                text: string.Empty, filePath: null, fileUrl: null,
                startLine: 0, startChar: 0, endLine: 0, endChar: 0, isEmpty: true));
            return;
        }
        // VS gives 1-based lines; LSP/MCP wants 0-based. Columns are already
        // 0-based from the editor snapshot.
        var startLine = Math.Max(0, ctx.StartLine - 1);
        var endLine = Math.Max(0, ctx.EndLine - 1);
        BroadcastNotification(BuildSelectionNotification(
            text: ctx.SelectedText ?? string.Empty,
            filePath: ctx.FilePath,
            fileUrl: PathHelpers.ToFileUri(ctx.FilePath),
            startLine: startLine, startChar: Math.Max(0, ctx.StartColumn),
            endLine: endLine, endChar: Math.Max(0, ctx.EndColumn),
            isEmpty: !ctx.HasSelection));
    }

    /// <summary>Send the initial context after a pause, giving the CLI time to reach 'connected' and
    /// register its useIdeSelection handler (otherwise the broadcast fires into the void).</summary>
    private async Task DelayedSendInitialContextAsync(WebSocket ws)
    {
        try { await Task.Delay(1000); }
        catch { /* never throws here, but be safe */ }
        if (ws.State != WebSocketState.Open) { return; }
        await SendInitialContextAsync(ws);
    }

    /// <summary>Send a one-shot selection_changed to a freshly handshook client so the first prompt
    /// has IDE context for the current active file. Read on UI thread.</summary>
    private async Task SendInitialContextAsync(WebSocket ws)
    {
        try
        {
            await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var ctx = IdeContextService.Instance.GetCurrentContext();
            string json;
            if (ctx == null)
            {
                json = BuildSelectionNotification(
                    text: string.Empty, filePath: null, fileUrl: null,
                    startLine: 0, startChar: 0, endLine: 0, endChar: 0, isEmpty: true);
            }
            else
            {
                var startLine = Math.Max(0, ctx.StartLine - 1);
                var endLine = Math.Max(0, ctx.EndLine - 1);
                json = BuildSelectionNotification(
                    text: ctx.SelectedText ?? string.Empty,
                    filePath: ctx.FilePath,
                    fileUrl: PathHelpers.ToFileUri(ctx.FilePath),
                    startLine: startLine, startChar: 0,
                    endLine: endLine, endChar: 0,
                    isEmpty: !ctx.HasSelection);
            }
            if (ws.State != WebSocketState.Open) { return; }
            OutputWindowLogger.Trace(() => $"Mcp: -> (initial) {StringHelpers.Truncate(json, 200)}");
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        catch (Exception ex) { OutputWindowLogger.LogException("Mcp.SendInitialContext", ex); }
    }

    private static string BuildSelectionNotification(
        string text, string filePath, string fileUrl,
        int startLine, int startChar, int endLine, int endChar, bool isEmpty)
    {
        return JsonConvert.SerializeObject(new
        {
            jsonrpc = "2.0",
            method = "selection_changed",
            @params = new
            {
                text,
                filePath,
                fileUrl,
                selection = new
                {
                    start = new { line = startLine, character = startChar },
                    end = new { line = endLine, character = endChar },
                    isEmpty,
                },
            },
        }, JsonRpcDispatcher.CamelCaseSettings);
    }

    private void BroadcastNotification(string json)
    {
        // Snapshot under lock — broadcasting while holding it would serialize sends and block new connections.
        ClientConn[] snapshot;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) { return; }
            snapshot = [.. _clients];
        }
        OutputWindowLogger.Trace(() => $"Mcp: broadcast to {snapshot.Length} client(s) -> {StringHelpers.Truncate(json, 200)}");
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var conn in snapshot)
        {
            var ws = conn.Ws;
            if (ws.State != WebSocketState.Open) { continue; }
            // Fire-and-forget: a slow/dead client must not break the others.
            _ = ws.SendAsync(new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.Exception != null)
                    {
                        OutputWindowLogger.LogException("Mcp.Broadcast", t.Exception);
                    }
                }, TaskScheduler.Default);
        }
    }


    //  Helpers

    private static int AllocateFreePort()
    {
        // Bind to 0, read back the OS-assigned port, close; HttpListener then reuses that number.
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
