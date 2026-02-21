using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ShaderExplorer.App.Services;

public class MonacoEditorService
{
    /// <summary>
    ///     Pre-created WebView2 environment with a persistent user data folder.
    ///     Starts on first access (static constructor), so the browser process is
    ///     already booting while the WPF window loads.
    /// </summary>
    private static readonly Lazy<Task<CoreWebView2Environment>> SharedEnvironment = new(CreateEnvironmentAsync);

    private readonly WebView2 _webView;
    private string? _pendingContent;

    public MonacoEditorService(WebView2 webView)
    {
        _webView = webView;
        // Touch the lazy to kick off environment creation immediately
        _ = SharedEnvironment.Value;
    }

    public bool IsReady { get; private set; }

    public event Action<string>? ContentChanged;
    public event Action? EditorReady;

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShaderExplorer", "WebView2");
        Directory.CreateDirectory(userDataFolder);
        return CoreWebView2Environment.CreateAsync(null, userDataFolder);
    }

    public async Task InitializeAsync()
    {
        var env = await SharedEnvironment.Value;
        await _webView.EnsureCoreWebView2Async(env);

        // Map virtual host to Monaco assets folder
        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Monaco");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.local", assetsPath,
            CoreWebView2HostResourceAccessKind.Allow);

        _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        // Navigate to the Monaco editor page
        _webView.CoreWebView2.Navigate("https://app.local/index.html");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Use TryGetWebMessageAsString to get the decoded string value
            // (WebMessageAsJson returns raw JSON with escaped quotes, which breaks Contains checks)
            var message = e.TryGetWebMessageAsString();

            if (message.Contains("\"ready\""))
            {
                IsReady = true;
                if (_pendingContent != null)
                {
                    _ = SetContentAsync(_pendingContent);
                    _pendingContent = null;
                }

                EditorReady?.Invoke();
            }
            else if (message.Contains("\"contentChanged\""))
            {
                ContentChanged?.Invoke("changed");
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    public async Task SetContentAsync(string hlsl)
    {
        if (!IsReady)
        {
            _pendingContent = hlsl;
            return;
        }

        // Use JSON serialization for robust escaping (handles NUL, unicode, etc.)
        var jsonStr = JsonSerializer.Serialize(hlsl);

        await _webView.CoreWebView2.ExecuteScriptAsync(
            $"window.editorApi.setValue({jsonStr})");
    }

    public async Task<string> GetContentAsync()
    {
        if (!IsReady) return string.Empty;

        var result = await _webView.CoreWebView2.ExecuteScriptAsync(
            "window.editorApi.getValue()");

        // Result comes back as JSON string with quotes
        if (result.StartsWith("\"") && result.EndsWith("\""))
            result = JsonSerializer.Deserialize<string>(result) ?? string.Empty;

        return result;
    }

    public async Task SetLanguageAsync(string language)
    {
        if (!IsReady) return;
        var jsonLang = JsonSerializer.Serialize(language);
        await _webView.CoreWebView2.ExecuteScriptAsync(
            $"window.editorApi.setLanguage({jsonLang})");
    }

    public async Task SetReadOnlyAsync(bool readOnly)
    {
        if (!IsReady) return;
        await _webView.CoreWebView2.ExecuteScriptAsync(
            $"window.editorApi.setReadOnly({(readOnly ? "true" : "false")})");
    }

    public async Task HighlightLinesAsync(int[] lines)
    {
        if (!IsReady) return;
        var arr = "[" + string.Join(",", lines) + "]";
        await _webView.CoreWebView2.ExecuteScriptAsync(
            $"window.editorApi.highlightLines({arr})");
    }

    public async Task RevealLineAsync(int line)
    {
        if (!IsReady) return;
        await _webView.CoreWebView2.ExecuteScriptAsync(
            $"window.editorApi.revealLine({line})");
    }
}