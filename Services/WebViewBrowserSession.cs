using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using HelpMe.Services.Mac;

namespace HelpMe.Services;

// The AI and chat web clients hosted inside Help Me!, in panels 05 and 06.
//
// On Windows these were two WebView2 controls sharing one profile folder. On
// macOS they are two WKWebViews, which need no profile folder of their own:
// WebKit's default data store is persistent and per-application, so signing in
// once on each page survives a restart the same way.
public sealed class WebViewBrowserSession : IAsyncDisposable
{
    private readonly MacWebView ai;
    private readonly MacWebView chat;
    private bool ready;

    public WebViewBrowserSession(MacWebView aiPanel, MacWebView chatPanel)
    {
        ai = aiPanel;
        chat = chatPanel;
    }

    // WebKit keeps cookies and local storage for the application here. It is
    // reported rather than created: the framework owns the directory, and the
    // uninstall notes name it so a sign-in can be cleared deliberately.
    public static string ProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "WebKit", "HelpMe");

    public bool IsReady => ready;

    public Task StartAsync(string provider, string messenger)
    {
        if (!OperatingSystem.IsMacOS())
            throw new IOException("The built-in web panels need macOS. The captions and translation work without them.");
        if (!ai.IsCreated || !chat.IsCreated)
            throw new IOException("The web panels did not finish starting. Show the window once and try again.");
        ready = true;
        Navigate(provider, messenger);
        return Task.CompletedTask;
    }

    // Changing provider or destination in Settings points the panels at the new
    // pages; an unchanged one is left alone so its conversation is not reloaded.
    public void Navigate(string provider, string messenger)
    {
        if (!ready) return;
        GoTo(ai, ProviderUrl(provider));
        GoTo(chat, Messenger.Find(messenger)?.Url ?? Messenger.Telegram.Url);
    }

    // What the user highlighted on the AI page. That is the answer as the page
    // rendered it, which is what they were reading when they chose it, rather
    // than the text this app scraped out of the page.
    public async Task<string> GetAiSelectionAsync()
    {
        if (!ready) return "";
        try
        {
            var json = await ai.EvaluateJsonAsync(
                "(() => { const s = window.getSelection(); return s ? s.toString() : ''; })()",
                CancellationToken.None);
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or JsonException)
        {
            // A page that cannot be asked has nothing selected as far as the
            // caller is concerned, and the whole answer goes instead.
            return "";
        }
    }

    public void Reload(bool aiPanel)
    {
        if (!ready) return;
        (aiPanel ? ai : chat).Reload();
    }

    private static void GoTo(MacWebView view, string url)
    {
        var current = view.CurrentUrl();
        if (current.Length > 0 && Uri.TryCreate(current, UriKind.Absolute, out var open) &&
            Uri.TryCreate(url, UriKind.Absolute, out var wanted) &&
            string.Equals(StripWww(open.Host), StripWww(wanted.Host), StringComparison.OrdinalIgnoreCase))
            return;
        view.Navigate(url);
    }

    // TrimStart(char[]) would strip every leading 'w' and '.', turning
    // web.telegram.org into eb.telegram.org and reloading the page every time.
    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string ProviderUrl(string provider) => Messenger.Find(provider)?.Url
        ?? (provider == "Claude" ? "https://claude.ai/" : "https://chatgpt.com/");

    // A messenger is a delivery destination and lives in panel 06; an assistant
    // answers and lives in panel 05.
    public Task<string> SendPromptAsync(string prompt, string provider,
        Action<string>? progress, CancellationToken cancellation)
    {
        if (!ready) throw new IOException("The built-in browser panels are still starting. Try again in a moment.");
        var panel = Messenger.Find(provider) is null ? ai : chat;
        // Diagnostic metadata only, and the same log the Windows build wrote:
        // only one send runs at a time whichever panel is driven.
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
        Trace($"provider={provider} engine=wkwebview starting");
        return ChromeAiClient.SendOnPageAsync(new WebViewPage(panel), provider, prompt, progress, cancellation, Trace);
    }

    public ValueTask DisposeAsync()
    {
        ready = false;
        // The views belong to the window and are torn down with it; releasing
        // them here would race the window's own shutdown.
        return ValueTask.CompletedTask;
    }
}

// One WKWebView panel, answering the handful of DevTools protocol commands that
// drive a provider page.
//
// WebView2 spoke the protocol itself; WKWebView does not, so the five commands
// Help Me! actually sends are carried out here with the page's own scripting
// instead. Everything above this class is unchanged: ChromeAiClient still works
// in DevTools terms, so the reader, the composer handling and the messenger
// delivery are the same code that ran on Windows.
internal sealed class WebViewPage : IBrowserPage
{
    private readonly MacWebView view;

    public WebViewPage(MacWebView view) => this.view = view;

    public async Task<JsonElement> CommandAsync(string method, object parameters, CancellationToken token)
    {
        var arguments = JsonSerializer.SerializeToElement(parameters);
        switch (method)
        {
            case "Runtime.evaluate":
                return await EvaluateAsync(arguments.GetProperty("expression").GetString() ?? "", token);

            case "Page.navigate":
                var url = arguments.GetProperty("url").GetString() ?? "";
                await Dispatcher.UIThread.InvokeAsync(() => view.Navigate(url));
                return Parse("{}");

            // Typed into whatever the page has focused, the way a keystroke
            // would arrive: the page's own editor keeps its state, and a React
            // composer sees the value change it listens for.
            case "Input.insertText":
                var text = arguments.GetProperty("text").GetString() ?? "";
                await EvaluateAsync(InsertTextScript(text), token);
                return Parse("{}");

            case "Input.dispatchKeyEvent":
                await EvaluateAsync(KeyEventScript(arguments), token);
                return Parse("{}");

            // WebKit throttles a view whose window is hidden, and the page's own
            // scripts check whether the document is focused before enabling a
            // send button. Telling the page it is visible and focused is the
            // nearest equivalent of the DevTools focus emulation.
            case "Emulation.setFocusEmulationEnabled":
                if (arguments.TryGetProperty("enabled", out var enabled) && enabled.GetBoolean())
                    await EvaluateAsync(FocusEmulationScript, token);
                return Parse("{}");

            default:
                throw new IOException($"The built-in browser panel does not implement {method}.");
        }
    }

    private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        string raw;
        try
        {
            raw = await view.EvaluateJsonAsync(expression, token);
        }
        catch (IOException error)
        {
            // Shaped like a DevTools evaluation failure so the caller reports it
            // through the same path as a thrown page script.
            return Parse("{\"exceptionDetails\":{\"text\":" + JsonSerializer.Serialize(error.Message) + "}}");
        }
        if (string.IsNullOrEmpty(raw)) raw = "null";
        return Parse("{\"result\":{\"value\":" + raw + "}}");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string InsertTextScript(string text) => "(() => {" + $$"""
        const value = {{JsonSerializer.Serialize(text)}};
        const target = document.activeElement;
        if (!target) return false;
        if ('value' in target && typeof target.value === 'string') {
            const start = target.selectionStart ?? target.value.length;
            const end = target.selectionEnd ?? start;
            const next = target.value.slice(0, start) + value + target.value.slice(end);
            // A React-controlled textarea ignores a plain assignment: its own
            // value setter is what the framework is listening to.
            const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(target), 'value')?.set;
            if (setter) setter.call(target, next); else target.value = next;
            target.selectionStart = target.selectionEnd = start + value.length;
            target.dispatchEvent(new Event('input', {bubbles: true}));
            return true;
        }
        // execCommand is deprecated but is still what a rich-text composer
        // listens for, and it produces the beforeinput/input pair they expect.
        if (document.execCommand('insertText', false, value)) return true;
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) return false;
        const range = selection.getRangeAt(0);
        range.deleteContents();
        range.insertNode(document.createTextNode(value));
        range.collapse(false);
        target.dispatchEvent(new InputEvent('input', {bubbles: true, data: value, inputType: 'insertText'}));
        return true;
        })()
        """;

    private static string KeyEventScript(JsonElement arguments)
    {
        var type = arguments.TryGetProperty("type", out var kind) ? kind.GetString() : "keyDown";
        var key = arguments.TryGetProperty("key", out var name) ? name.GetString() ?? "" : "";
        var code = arguments.TryGetProperty("code", out var physical) ? physical.GetString() ?? "" : "";
        var keyCode = arguments.TryGetProperty("windowsVirtualKeyCode", out var virtualKey) ? virtualKey.GetInt32() : 0;
        var eventName = type == "keyUp" ? "keyup" : "keydown";
        return "(() => {" + $$"""
        const target = document.activeElement ?? document.body;
        if (!target) return false;
        target.dispatchEvent(new KeyboardEvent({{JsonSerializer.Serialize(eventName)}}, {
            key: {{JsonSerializer.Serialize(key)}},
            code: {{JsonSerializer.Serialize(code)}},
            keyCode: {{keyCode}},
            which: {{keyCode}},
            bubbles: true, cancelable: true, composed: true
        }));
        return true;
        })()
        """;
    }

    private const string FocusEmulationScript = """
        (() => {
            try {
                Object.defineProperty(document, 'visibilityState', {configurable: true, get: () => 'visible'});
                Object.defineProperty(document, 'hidden', {configurable: true, get: () => false});
                document.hasFocus = () => true;
                return true;
            } catch (error) { return false; }
        })()
        """;
}
