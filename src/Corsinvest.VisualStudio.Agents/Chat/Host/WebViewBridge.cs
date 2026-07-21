/*
 * SPDX-FileCopyrightText: Copyright Corsinvest Srl
 * SPDX-License-Identifier: GPL-3.0-only
 */

using Corsinvest.VisualStudio.Agents.Helpers;
using Microsoft.VisualStudio.Shell;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Corsinvest.VisualStudio.Agents.Chat.Host;

/// <summary>
/// Manages the WebView2 control: initialisation, posting messages to JS,
/// and dispatching messages received from JS to the host via <see cref="MessageReceived"/>.
/// </summary>
internal sealed partial class WebViewBridge(Microsoft.Web.WebView2.Wpf.WebView2 webView, Dispatcher dispatcher)
{
    private bool _ready;
    private readonly ConcurrentQueue<(string type, object data)> _pending = new();
    private bool? _pendingTheme;
    private string _docCreatedScriptId;

    /// <summary>Fires on the UI thread when the JS sends a message.</summary>
    // id is the JSON-RPC-style correlation id: present on requests (WebView awaits a response),
    // null on fire-and-forget notifications. The handler echoes it back via SendResponse.
    public event Action<string, JObject, int?> MessageReceived;

    public async Task InitAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            Directory.CreateDirectory(AppPaths.WebView2Folder);

            // Initialize CoreWebView2 with our own user-data folder. Required
            // before touching any CoreWebView2 member below.
            var env = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2Folder);
            var controllerOpts = env.CreateCoreWebView2ControllerOptions();
            controllerOpts.AllowHostInputProcessing = true;
            await webView.EnsureCoreWebView2Async(env, controllerOpts);

            // WebView2 defaults to an opaque WHITE background before the first paint, which flashes as
            // a blank/black block until the bundle renders. Make it transparent so the WPF host's
            // VsBrush.Window shows through (matches the theme) during that gap. On the WPF control the
            // color lives on the control itself, not on CoreWebView2Controller.
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            webView.CoreWebView2.WebMessageReceived += OnRawMessage;

            // Trim the context menu to editing + spell-check items only (see
            // OnContextMenuRequested).
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            webView.CoreWebView2.ContextMenuRequested += OnContextMenuRequested;

            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

            webView.CoreWebView2.PermissionRequested += (s, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                {
                    args.State = CoreWebView2PermissionState.Allow;
                }
            };

            webView.CoreWebView2.NewWindowRequested += (s, args) =>
            {
                args.Handled = true;
                ShellHelpers.OpenExternal(args.Uri);
            };

            var isFirstLoad = true;
            webView.CoreWebView2.NavigationCompleted += (s, args) =>
            {
                _ready = true;
                if (isFirstLoad)
                {
                    isFirstLoad = false;
                    // Boot paint: give the bundle the VS Options (theme/font) for its first frame before the CLI
                    // client exists. The one real ui_init (with model/permission/toggles) comes from
                    // ChatPaneControl.OnCliStateReceived once initialize + get_settings land (no user turn needed).
                    SendDirect(BridgeMessages.ToWebView.Ui.VsSettings, WebViewBridge.BuildVsOptions());
                    while (_pending.TryDequeue(out var msg)) { SendDirect(msg.type, msg.data); }
                }
            };

            // Inject boot-time theme script BEFORE navigation to avoid FOUC (refreshed via InjectTheme).
            if (_pendingTheme.HasValue) { await RegisterBootThemeScriptAsync(_pendingTheme.Value); }

            // Virtual https host instead of file:// — file:// makes every resource cross-origin
            // (browser warnings, hljs CSS silently ignored). Virtual host gives same-origin + correct MIME.
            var indexPath = AppPaths.WebViewHtml();
            var folder = Path.GetDirectoryName(indexPath);
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping("cv4vs.local", folder, CoreWebView2HostResourceAccessKind.Allow);

            // Lazy-served icons: virtual-host would serve from disk and 404 on not-yet-generated PNGs,
            // so intercept the request instead and generate on demand from KnownMonikers on the UI thread.
            Directory.CreateDirectory(AppPaths.IconCacheFolder);
            webView.CoreWebView2.AddWebResourceRequestedFilter("https://cv4vs-icons.local/*", CoreWebView2WebResourceContext.Image);
            webView.CoreWebView2.WebResourceRequested += OnIconRequested;

            webView.Source = new Uri("https://cv4vs.local/" + Path.GetFileName(indexPath));
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("WebViewBridge.Init", ex);
            throw;
        }
    }

    /// <summary>Trim the WebView2 context menu to editing + spell-check items only:
    /// drop the browser-specific entries (Copy link, Print, Save as, Inspect, More
    /// tools, …) and any submenu, then any separators left orphaned. Names are the
    /// English label in lower camelCase; a blocklist (not an allow-list) keeps the
    /// spell-check suggestions, whose Names aren't documented.</summary>
    private void OnContextMenuRequested(object sender, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var items = args.MenuItems;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            var name = it.Name ?? "";
            var isBrowserItem =
                name.StartsWith("copyLink") || name.StartsWith("saveLink")
                || name.StartsWith("saveImage") || name.StartsWith("openLink")
                || name == "print" || name == "share" || name == "saveAs"
                || name == "sendTabToSelf"
                || name == "reload" || name == "back" || name == "forward"
                || name == "emoji" || name == "webSelect" || name == "webCapture"
                || name.StartsWith("inspect") || name.StartsWith("viewSource")
                || name == "createQrCode" || name == "translate"
                // In a plain textarea "Paste as plain text" duplicates "Paste".
                || name == "pasteAsPlainText" || name == "pasteAndMatchStyle";
            var isSubmenu = it.Kind == CoreWebView2ContextMenuItemKind.Submenu;
            if (isBrowserItem || isSubmenu) { items.RemoveAt(i); }
        }
        TrimSeparators(items);
        if (items.Count == 0) { args.Handled = true; }
    }

    /// <summary>Drop leading/trailing separators and collapse consecutive ones,
    /// left behind after removing browser items from the context menu.</summary>
    private static void TrimSeparators(System.Collections.Generic.IList<CoreWebView2ContextMenuItem> items)
    {
        bool IsSep(int i) => items[i].Kind == CoreWebView2ContextMenuItemKind.Separator;
        // Walk backwards so removals don't shift pending indices.
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!IsSep(i)) { continue; }
            var last = i == items.Count - 1;
            var dupNext = i + 1 < items.Count && IsSep(i + 1);
            if (last || dupNext) { items.RemoveAt(i); }
        }
        while (items.Count > 0 && IsSep(0)) { items.RemoveAt(0); }
    }

    public void OpenDevTools() => webView.CoreWebView2?.OpenDevToolsWindow();

    /// <summary>Give the WebView2 control the native (WPF) focus, so the keyboard
    /// actually reaches the page. Without this a JS `element.focus()` only shows a
    /// blinking caret while keystrokes still go to VS — the WebView host must own
    /// the focus first. Call before posting ui_focus_input.</summary>
    public void FocusWebView() => webView.Focus();

    /// <summary>Open WebView2's native find bar over the chat content (Ctrl+F). We host it
    /// ourselves because AreBrowserAcceleratorKeysEnabled=false turns off the browser's own Ctrl+F.
    /// Empty term just shows the bar.</summary>
    public void ShowFind()
    {
        var core = webView.CoreWebView2;
        if (core == null) { return; }
        try
        {
            var options = core.Environment.CreateFindOptions();
            options.FindTerm = string.Empty;
            _ = core.Find.StartAsync(options);
        }
        catch (Exception ex)
        {
            OutputWindowLogger.LogException("WebViewBridge.ShowFind", ex);
        }
    }

    /// <summary>
    /// Set the active theme. The boot-time script (AddScriptToExecuteOnDocumentCreated)
    /// is updated so the next navigation/reload paints with the new theme
    /// from the very first frame; the live page also gets a `ui_theme_changed`
    /// message so the JS toggles classes without a reload.
    /// </summary>
    public void InjectTheme(bool isDark)
    {
        _pendingTheme = isDark;
        if (webView.CoreWebView2 != null)
        {
            _ = RegisterBootThemeScriptAsync(isDark);
        }
        if (_ready) { SendDirect(BridgeMessages.ToWebView.Ui.ThemeChanged, new Contracts.ThemeChangedNotification { Dark = isDark }); }
    }

    /// <summary>
    /// (Re)installs a script that runs BEFORE the document loads on every navigation, applying the theme
    /// from the first frame (no boot flash). Removes the previous script first to avoid two competing.
    /// </summary>
    private async Task RegisterBootThemeScriptAsync(bool isDark)
    {
        var core = webView.CoreWebView2;
        if (core == null) { return; }
        try
        {
            if (!string.IsNullOrEmpty(_docCreatedScriptId))
            {
                core.RemoveScriptToExecuteOnDocumentCreated(_docCreatedScriptId);
                _docCreatedScriptId = null;
            }
            // Tag <html> with the active theme; index.html's inline boot script reads it before first paint.
            // This runs BEFORE the DOM is parsed, so documentElement may be null — fall back to readystatechange.
            var theme = isDark ? "dark" : "light";
            var script = $@"
                (function() {{
                    function apply() {{
                        if (document.documentElement) {{
                            document.documentElement.dataset.cvTheme = '{theme}';
                        }}
                    }}
                    if (document.documentElement) {{ apply(); }}
                    else {{ document.addEventListener('readystatechange', apply, {{ once: true }}); }}
                }})();
            ";
            _docCreatedScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex) { OutputWindowLogger.LogException("RegisterBootThemeScript", ex); }
    }

    public void Send(string type, object data)
    {
#if DEBUG
        // A request response must go through SendResponse (carries the id). Send on a response
        // channel means a case forgot Send→SendResponse → the WebView Promise would time out.
        if (_responseChannels.Contains(type))
        {
            OutputWindowLogger.Warn($"!!! Send() on response channel '{type}' — use SendResponse(id). The request Promise will time out.");
        }
#endif
        if (!_ready) { _pending.Enqueue((type, data)); return; }
        SendDirect(type, data);
    }

    /// <summary>Reply to a request (WebView sent an `id`): echoes the same id so the
    /// WebView's sendRequest Promise correlates the response. The channel `type` is the
    /// existing ToWebView response channel (e.g. chat_image_data). Never queued — a
    /// request only arrives after the WebView is ready.</summary>
    public void SendResponse(string type, int id, object data)
        => SendDirect(type, data, id);

    /// <summary>Reply to a request with an ERROR instead of a result (chat block not found,
    /// bad input, …). The WebView's sendRequest Promise rejects with `message`, so the caller
    /// gets the real reason instead of a misleading 15s timeout. Correlated by the same id.</summary>
    public void SendError(string type, int id, string message)
    {
        try
        {
            var json = JsonConvert.SerializeObject(new { type, id, error = message }, _jsonSettings);
            OutputWindowLogger.Trace(() => $"[bridge → web] {type} ERROR#{id} {message}");
            webView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception ex) { OutputWindowLogger.LogException($"WebViewBridge.SendError[{type}]", ex); }
    }

    // The 5 ToWebView channels that carry request responses. In DEBUG, Send() warns if called
    // on one of these (a case that forgot Send→SendResponse would silently time out the Promise).
    // chat_history is now a pure response channel (the unprompted push moved to
    // chat_history_loaded). subagent_loaded is added once its ToWebView listener is removed
    // (its logic moves into the sendRequest .then).
    private static readonly System.Collections.Generic.HashSet<string> _responseChannels =
    [
        BridgeMessages.ToWebView.Chat.ImageData,
        BridgeMessages.ToWebView.Chat.History,
        BridgeMessages.ToWebView.Chat.Usage,
        BridgeMessages.ToWebView.File.Suggestions,
    ];

    // camelCase so the PascalCase Contracts DTOs serialize to the wire names the
    // WebView expects (InputTokens → inputTokens); anonymous objects already use
    // camelCase and JObject passthrough is unaffected. StringEnumConverter (camelCase)
    // makes enum DTOs like NoticeVariantDto emit "warning"/"error", not 0/1.
    private static readonly JsonSerializerSettings _jsonSettings = new()
    {
        StringEscapeHandling = StringEscapeHandling.Default,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        Converters = { new Newtonsoft.Json.Converters.StringEnumConverter(new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy()) },
    };

    private void SendDirect(string type, object data, int? id = null)
    {
        try
        {
            // id is emitted only for request responses; notifications omit it entirely.
            object envelope = id.HasValue ? new { type, data, id = id.Value } : new { type, data };
            var json = JsonConvert.SerializeObject(envelope, _jsonSettings);
            // Trace bridge traffic; skip streaming/progress channels that would flood the log.
            if (!IsNoisyChannel(type))
            {
                OutputWindowLogger.Trace(() => $"[bridge → web] {type} {StringHelpers.Truncate(json)}");
            }
            // CoreWebView2 is thread-affine (UI thread). Callers on a background thread
            // (e.g. a CLI-event handler continuation) would otherwise throw. Marshal the
            // actual post to the dispatcher; skip the hop when we're already on it.
            if (dispatcher.CheckAccess()) { webView.CoreWebView2?.PostWebMessageAsJson(json); }
            else { dispatcher.BeginInvoke(new Action(() => webView.CoreWebView2?.PostWebMessageAsJson(json))); }
        }
        catch (Exception ex) { OutputWindowLogger.LogException($"WebViewBridge.SendDirect[{type}]", ex); }
    }

    private static bool IsNoisyChannel(string type)
        => type == BridgeMessages.ToWebView.Chat.AssistantTextDelta
        || type == BridgeMessages.ToWebView.Chat.ToolProgress;

    /// <summary>
    /// Intercepts every `https://cv4vs-icons.local/<key>.png` fetch from
    /// the WebView. Ensures the PNG exists on disk (generating it from
    /// the matching VS KnownMoniker on first hit), then serves the bytes
    /// back as the HTTP response.
    /// </summary>
    private void OnIconRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            var uri = new Uri(e.Request.Uri);
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            if (string.IsNullOrEmpty(name)) { return; }

            // EnsureIconPng touches IVsImageService which requires the UI thread.
            var pngPath = ThreadHelper.JoinableTaskFactory.Run(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                return IconCacheService.EnsureIconPng(name);
            });

            if (!File.Exists(pngPath))
            {
                e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    null, 404, "Not Found", "");
                return;
            }
            var bytes = File.ReadAllBytes(pngPath);
            var stream = new MemoryStream(bytes);
            e.Response = webView.CoreWebView2.Environment.CreateWebResourceResponse(
                stream, 200, "OK",
                "Content-Type: image/png\r\nCache-Control: public, max-age=31536000");
        }
        catch (Exception ex)
        {
            OutputWindowLogger.Warn("!!! IconRequested error: " + ex.Message);
        }
    }

    private void OnRawMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var raw = e.WebMessageAsJson;
            var node = JObject.Parse(raw);
            var type = node.Val("type", "");
            var data = node["data"] is JObject dataObj ? dataObj : [];
            // Correlation id for request/response; absent (null) for notifications.
            int? id = node["id"]?.Type == JTokenType.Integer ? (int)node["id"] : null;
            OutputWindowLogger.Trace(() => $"[bridge ← web] {type} {StringHelpers.Truncate(raw)}");
            dispatcher.Invoke(() => MessageReceived?.Invoke(type, data, id));
        }
        catch (Exception ex) { OutputWindowLogger.LogException("WebViewBridge.OnRawMessage", ex); }
    }

}
