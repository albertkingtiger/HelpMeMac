using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HelpMe.Services;

// The AI and chat web clients hosted inside Help Me!, in panels 05 and 06.
//
// Both panels share one WebView2 environment and therefore one profile folder,
// so a sign-in is done once and survives restarts, exactly as the dedicated
// Chrome profile does. Sharing is not optional: two WebView2 controls on one
// user data folder must come from the same environment or the second fails.
public sealed class WebViewBrowserSession : IAsyncDisposable
{
    private readonly WebView2 ai;
    private readonly WebView2 chat;
    private readonly Dispatcher dispatcher;
    private bool ready;

    public WebViewBrowserSession(WebView2 aiPanel, WebView2 chatPanel)
    {
        ai = aiPanel;
        chat = chatPanel;
        dispatcher = aiPanel.Dispatcher;
    }

    public static string ProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpMe", "WebView2");

    public bool IsReady => ready;

    // The runtime ships with Windows 11, so a missing one means a stripped
    // install rather than a first run, and the message has to say which.
    public async Task StartAsync(string provider, string messenger)
    {
        Directory.CreateDirectory(ProfilePath);
        CoreWebView2Environment environment;
        try
        {
            environment = await CoreWebView2Environment.CreateAsync(null, ProfilePath);
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or COMException or DllNotFoundException)
        {
            throw new IOException(
                "The Microsoft Edge WebView2 runtime is not available. Install it, or choose Chrome as the browser in Settings.", error);
        }
        await ai.EnsureCoreWebView2Async(environment);
        await chat.EnsureCoreWebView2Async(environment);
        ready = true;
        Navigate(provider, messenger);
    }

    // Changing provider or destination in Settings points the panels at the new
    // pages; an unchanged one is left alone so its conversation is not reloaded.
    public void Navigate(string provider, string messenger)
    {
        if (!ready) return;
        GoTo(ai, ProviderUrl(provider));
        GoTo(chat, Messenger.Find(messenger)?.Url ?? Messenger.Telegram.Url);
    }

    public void Reload(bool aiPanel)
    {
        if (!ready) return;
        (aiPanel ? ai : chat).CoreWebView2.Reload();
    }

    private static void GoTo(WebView2 view, string url)
    {
        var current = view.Source?.AbsoluteUri;
        if (current is not null && Uri.TryCreate(current, UriKind.Absolute, out var open) &&
            Uri.TryCreate(url, UriKind.Absolute, out var wanted) &&
            string.Equals(ChromeDebugSession.StripWww(open.Host), ChromeDebugSession.StripWww(wanted.Host),
                StringComparison.OrdinalIgnoreCase))
            return;
        view.Source = new Uri(url);
    }

    private static string ProviderUrl(string provider) => Messenger.Find(provider)?.Url
        ?? (provider == "Claude" ? "https://claude.ai/" : "https://chatgpt.com/");

    // A messenger is a delivery destination and lives in panel 06; an assistant
    // answers and lives in panel 05.
    public Task<string> SendPromptAsync(string prompt, string provider,
        Action<string>? progress, CancellationToken cancellation)
    {
        if (!ready) throw new IOException("The built-in browser panels are still starting. Try again in a moment.");
        var panel = Messenger.Find(provider) is null ? ai : chat;
        // Diagnostic metadata only, and the same log the Chrome path writes:
        // only one send runs at a time whichever engine is chosen.
        void Trace(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ChromeAiClient.DiagnosticPath)!);
                File.AppendAllText(ChromeAiClient.DiagnosticPath, $"{DateTimeOffset.Now:O} {message}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { File.WriteAllText(ChromeAiClient.DiagnosticPath, ""); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Trace($"provider={provider} engine=webview2 starting");
        return ChromeAiClient.SendOnPageAsync(new WebViewPage(panel, dispatcher), provider, prompt, progress, cancellation, Trace);
    }

    public ValueTask DisposeAsync()
    {
        ready = false;
        // The controls belong to the window and are torn down with it; disposing
        // them here would race the window's own shutdown.
        return ValueTask.CompletedTask;
    }
}

// One WebView2 panel, driven through the DevTools protocol so that every page
// script, wait and keystroke is the same code the Chrome window runs.
internal sealed class WebViewPage : IBrowserPage
{
    private readonly WebView2 view;
    private readonly Dispatcher dispatcher;

    public WebViewPage(WebView2 view, Dispatcher dispatcher)
    {
        this.view = view;
        this.dispatcher = dispatcher;
    }

    public async Task<JsonElement> CommandAsync(string method, object parameters, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(parameters);
        string result;
        try
        {
            // WebView2 is owned by the UI thread, while sending runs off it.
            result = await dispatcher.InvokeAsync(
                () => view.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json)).Task.Unwrap().WaitAsync(token);
        }
        catch (COMException error)
        {
            throw new IOException($"The built-in browser rejected {method}: {error.Message}", error);
        }
        catch (InvalidOperationException error)
        {
            throw new IOException("The built-in browser panel is not ready.", error);
        }
        using var document = JsonDocument.Parse(result);
        return document.RootElement.Clone();
    }
}
