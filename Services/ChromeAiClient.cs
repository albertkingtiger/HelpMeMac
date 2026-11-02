using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HelpMe.Services;

// A chat destination driven through its web client in the managed Chrome window.
// These are delivery targets, not assistants: nothing is read back.
public sealed record Messenger(string Name, string Host, string Url, string Composer, string Send)
{
    public static readonly Messenger Telegram = new("Telegram", "web.telegram.org", "https://web.telegram.org/k/",
        ".input-message-input[contenteditable=\"true\"], #editable-message-text[contenteditable=\"true\"]",
        "button.btn-send:not(.record), button.send, button[aria-label='Send Message']");

    public static readonly Messenger WhatsApp = new("WhatsApp", "web.whatsapp.com", "https://web.whatsapp.com/",
        "footer div[contenteditable=\"true\"], [data-testid=\"conversation-compose-box-input\"]",
        "button[data-testid='send'], span[data-icon='send'], button[aria-label='Send']");

    public static readonly IReadOnlyList<Messenger> All = new[] { Telegram, WhatsApp };

    public static Messenger? Find(string? name)
        => All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}

// All browser work is asynchronous. No cookies, tokens, or private service APIs are used.
public sealed class ChromeAiClient
{
    // Mac applications put their logs in ~/Library/Logs, where Console.app
    // and anyone looking for them will find them.
    public static string DiagnosticPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Logs", "HelpMe", "ai-reader.log");
    public static async Task<string> SendAsync(int port, string provider,
        string prompt, Action<string>? progress, CancellationToken cancellation)
    {
        var messenger = Messenger.Find(provider);
        var host = messenger?.Host ?? (provider == "Claude" ? "claude.ai" : "chatgpt.com");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellation);
        using var pages = JsonDocument.Parse(json);
        var page = pages.RootElement.EnumerateArray().FirstOrDefault(x =>
            x.GetProperty("type").GetString() == "page" &&
            Uri.TryCreate(x.GetProperty("url").GetString(), UriKind.Absolute, out var uri) && uri.Host == host);
        if (page.ValueKind == JsonValueKind.Undefined)
            throw new IOException($"Open {provider} in panel 05 and sign in first.");
        // Diagnostic metadata only: never write prompts, replies, titles, URLs or cookies.
        void Trace(string message)
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(DiagnosticPath)!); File.AppendAllText(DiagnosticPath, $"{DateTimeOffset.Now:O} {message}\n"); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        try { File.WriteAllText(DiagnosticPath, ""); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Trace($"provider={provider} starting");
        try
        {
            return await SendToPageAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!),
                provider, prompt, progress, cancellation, Trace);
        }
        catch (Exception error) { Trace("failed=" + error.GetType().Name); throw; }
    }

    // Separate entry point lets integration tests exercise the real CDP flow against local pages.
    internal static async Task<string> SendToPageAsync(Uri socketUri, string provider,
        string prompt, Action<string>? progress, CancellationToken cancellation, Action<string>? diagnostics = null)
    {
        using var page = new CdpPage();
        diagnostics?.Invoke("stage=connect");
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connect.CancelAfter(TimeSpan.FromSeconds(30));
        await page.ConnectAsync(socketUri, connect.Token);
        return await SendOnPageAsync(page, provider, prompt, progress, cancellation, diagnostics);
    }

    // The engine is settled by the time a page exists: a WebView2 panel and a
    // Chrome tab are driven by exactly these steps.
    internal static async Task<string> SendOnPageAsync(IBrowserPage page, string provider,
        string prompt, Action<string>? progress, CancellationToken cancellation, Action<string>? diagnostics = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await EmulateFocusAsync(page, token, diagnostics);
        // A messenger is a destination, not an assistant: the text is delivered to
        // the open chat and there is no reply to wait for or read back.
        if (Messenger.Find(provider) is { } target) return await SendToMessengerAsync(page, target, prompt, token, diagnostics);
        diagnostics?.Invoke("stage=find-input");
        await WaitForAsync(async () => (await page.EvaluateAsync(BrowserScripts.ComposerReady, token)).GetBoolean(),
            "The AI message input is unavailable. Sign in to the selected provider and open a conversation in panel 05.", token);
        // Wait for conversation history to settle before taking the assistant baseline.
        await Task.Delay(600, token);
        diagnostics?.Invoke("stage=read-baseline");
        var baseline = await SnapshotAsync(page, provider, prompt, token);
        var oldMessages = baseline.Messages.ToDictionary(m => m.Key, m => m.Text);
        diagnostics?.Invoke("stage=prepare-input");
        var prepared = await page.EvaluateAsync(BrowserScripts.PrepareInput(prompt), token);
        var state = prepared.GetProperty("state").GetString();
        diagnostics?.Invoke("input=" + state);
        if (state == "missing")
            throw new IOException("The AI message input is unavailable. Sign in to the selected provider and open a conversation in panel 05.");
        // A draft the user typed is never discarded or sent on its own: the
        // caption is appended below it, one blank line apart, and the two go up
        // together, so what the page submits is no longer just the prompt.
        var submitted = prepared.GetProperty("text").GetString()!;
        if (prepared.GetProperty("insert").GetString() is { Length: > 0 } insertion)
        {
            diagnostics?.Invoke("stage=insert-text");
            await page.CommandAsync("Input.insertText", new { text = insertion }, token);
        }
        diagnostics?.Invoke("stage=click-send");
        await WaitForAsync(async () => (await page.EvaluateAsync(BrowserScripts.ClickSend, token)).GetBoolean(),
            "The AI Send button did not become available. Check panel 05 for a sign-in or usage-limit message.", token);
        diagnostics?.Invoke($"submitted baselineMessages={baseline.Messages.Length}");

        var lastText = "";
        var stable = Stopwatch.StartNew();
        var elapsed = Stopwatch.StartNew();
        var lastDiagnostic = TimeSpan.FromSeconds(-5);
        while (true)
        {
            await Task.Delay(350, token);
            var snapshot = await SnapshotAsync(page, provider, submitted, token);
            // Page virtualization can remove older messages, so counts cannot identify a new reply.
            var afterSubmittedUser = snapshot.AfterUser && snapshot.UserKey != baseline.UserKey;
            var changedMessage = oldMessages.TryGetValue(snapshot.Key, out var oldText)
                ? snapshot.Text != oldText
                : !baseline.Messages.Any(m => m.Text == snapshot.Text);
            var fresh = afterSubmittedUser || changedMessage;
            if (elapsed.Elapsed - lastDiagnostic >= TimeSpan.FromSeconds(5))
            {
                diagnostics?.Invoke($"poll messages={snapshot.Messages.Length} matchedUser={snapshot.AfterUser} newUser={snapshot.UserKey != baseline.UserKey} fresh={fresh} chars={snapshot.Text.Length} busy={snapshot.Busy}");
                lastDiagnostic = elapsed.Elapsed;
            }
            if (lastText.Length == 0 && elapsed.Elapsed > TimeSpan.FromSeconds(60) && !snapshot.Busy && (!fresh || snapshot.Text.Length == 0))
                throw new IOException($"The prompt was sent to {provider}, but its reply could not be read. Reader details: {DiagnosticPath}");
            if (!fresh || snapshot.Text.Length == 0) continue;
            if (snapshot.Text != lastText)
            {
                lastText = snapshot.Text;
                stable.Restart();
                progress?.Invoke(lastText);
            }
            if (!snapshot.Busy && stable.Elapsed >= TimeSpan.FromSeconds(2))
            {
                diagnostics?.Invoke($"completed chars={lastText.Length}");
                return lastText;
            }
        }
    }

    // A tab the user is not looking at is throttled: Chrome stops its animation
    // frames, so a composer's send button may never re-enable after the text is
    // inserted, and the prompt sits there unsent. Focus emulation makes the page
    // behave as the focused, active tab without raising its window or taking
    // focus away from whatever the user is actually doing.
    private static async Task EmulateFocusAsync(IBrowserPage page, CancellationToken token, Action<string>? diagnostics)
    {
        try
        {
            await page.CommandAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }, token);
            diagnostics?.Invoke("focus=emulated");
        }
        catch (IOException)
        {
            // The domain is experimental; without it the send still works as long
            // as the provider tab happens to be the active one.
            diagnostics?.Invoke("focus=unavailable");
        }
    }

    private static async Task<string> SendToMessengerAsync(IBrowserPage page, Messenger messenger, string message,
        CancellationToken token, Action<string>? diagnostics)
    {
        diagnostics?.Invoke($"stage=find-{messenger.Name.ToLowerInvariant()}-input");
        await WaitForAsync(async () => (await page.EvaluateAsync(BrowserScripts.MessengerComposerReady(messenger), token)).GetBoolean(),
            $"The {messenger.Name} message box is unavailable. Open the chat you want in panel 06 and sign in first.", token);
        var prepared = await page.EvaluateAsync(BrowserScripts.PrepareMessengerInput(messenger, message), token);
        diagnostics?.Invoke("input=" + prepared.GetString());
        if (prepared.GetString() is not ("ready" or "reuse"))
            throw new IOException($"The {messenger.Name} message box already contains an unsent draft. Send or clear that draft in panel 06 first.");
        if (prepared.GetString() == "ready")
        {
            diagnostics?.Invoke("stage=insert-text");
            await page.CommandAsync("Input.insertText", new { text = message }, token);
        }
        diagnostics?.Invoke("stage=messenger-send");
        await PressEnterAsync(page, token);
        // The composer emptying is the confirmation that Telegram accepted it.
        // The button is only a fallback: until the box has text it is the
        // voice-record control, and a user may have rebound Enter to a newline.
        if (!await ComposerClearedAsync(page, messenger, token))
        {
            diagnostics?.Invoke("stage=messenger-send-button");
            await page.EvaluateAsync(BrowserScripts.ClickMessengerSend(messenger), token);
            if (!await ComposerClearedAsync(page, messenger, token))
                throw new IOException($"{messenger.Name} did not accept the message. Check panel 06 for the open chat or a sign-in prompt.");
        }
        diagnostics?.Invoke($"completed chars={message.Length}");
        return $"Sent {message.Length} characters to the open {messenger.Name} chat.";
    }

    private static async Task PressEnterAsync(IBrowserPage page, CancellationToken token)
    {
        await page.CommandAsync("Input.dispatchKeyEvent", new
        {
            type = "keyDown", key = "Enter", code = "Enter", text = "\r",
            windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13
        }, token);
        await page.CommandAsync("Input.dispatchKeyEvent", new
        {
            type = "keyUp", key = "Enter", code = "Enter",
            windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13
        }, token);
    }

    private static async Task<bool> ComposerClearedAsync(IBrowserPage page, Messenger messenger, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            if ((await page.EvaluateAsync(BrowserScripts.MessengerComposerEmpty(messenger), token)).GetBoolean()) return true;
            await Task.Delay(250, token);
        }
        return false;
    }

    private sealed record Message(string Key, string Text);
    private sealed record Reply(string Key, string Text, bool Busy, string UserKey, bool AfterUser, Message[] Messages);
    private static async Task<Reply> SnapshotAsync(IBrowserPage page, string provider, string prompt, CancellationToken token)
    {
        var value = await page.EvaluateAsync(BrowserScripts.Reply(provider, prompt), token);
        return new Reply(value.GetProperty("key").GetString()!, value.GetProperty("text").GetString()!,
            value.GetProperty("busy").GetBoolean(), value.GetProperty("userKey").GetString()!, value.GetProperty("afterUser").GetBoolean(),
            value.GetProperty("messages").EnumerateArray().Select(m => new Message(m.GetProperty("key").GetString()!, m.GetProperty("text").GetString()!)).ToArray());
    }

    private static async Task WaitForAsync(Func<Task<bool>> predicate, string error, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(25))
        {
            if (await predicate()) return;
            await Task.Delay(250, token);
        }
        throw new IOException(error);
    }
}

// A page that answers DevTools protocol commands, whether it is a tab in the
// Chrome window or a WebView2 panel inside Help Me!. Everything that reads or
// drives a provider page is written against this, so the two engines share one
// implementation rather than drifting apart.
internal interface IBrowserPage
{
    Task<JsonElement> CommandAsync(string method, object parameters, CancellationToken token);
}

internal static class BrowserPage
{
    public static async Task<JsonElement> EvaluateAsync(this IBrowserPage page, string expression, CancellationToken token)
    {
        var result = await page.CommandAsync("Runtime.evaluate",
            new { expression, returnByValue = true, awaitPromise = true }, token);
        if (result.TryGetProperty("exceptionDetails", out var details))
            throw new IOException("The AI page reader failed: " +
                (details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
                    ? description.GetString() : details.GetProperty("text").GetString()));
        return result.GetProperty("result").GetProperty("value").Clone();
    }

    public static async Task NavigateAsync(this IBrowserPage page, string url, CancellationToken token)
    {
        var result = await page.CommandAsync("Page.navigate", new { url }, token);
        if (result.TryGetProperty("errorText", out var error)) throw new IOException("Could not open AI destination: " + error.GetString());
        await Task.Delay(500, token);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(25))
        {
            try
            {
                if ((await page.EvaluateAsync("document.readyState === 'complete'", token)).GetBoolean()) return;
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(20)) { }
            await Task.Delay(250, token);
        }
        throw new IOException("The AI page did not finish loading.");
    }
}

internal sealed class CdpPage : IBrowserPage, IDisposable
{
    private readonly ClientWebSocket socket = new();
    private int nextId;
    public Task ConnectAsync(Uri uri, CancellationToken token) => socket.ConnectAsync(uri, token);
    public async Task<JsonElement> CommandAsync(string method, object parameters, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(20));
        var id = ++nextId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, limit.Token);
        var buffer = new byte[16384];
        while (true)
        {
            using var data = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), limit.Token);
                if (result.MessageType == WebSocketMessageType.Close) throw new IOException("The AI browser tab disconnected.");
                data.Write(buffer, 0, result.Count);
                if (data.Length > 16 * 1024 * 1024) throw new IOException("Chrome returned an unexpectedly large message.");
            } while (!result.EndOfMessage);
            using var message = JsonDocument.Parse(data.ToArray());
            var root = message.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
            if (root.TryGetProperty("error", out var error)) throw new IOException("Chrome debugging error: " + error.GetProperty("message").GetString());
            return root.GetProperty("result").Clone();
        }
    }
    public void Dispose() => socket.Dispose();
}

internal static class BrowserScripts
{
    private const string Helpers = """
        const visible = e => e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
        const composer = () => [...document.querySelectorAll('#prompt-textarea, textarea, [contenteditable="true"]')]
            .find(e => visible(e) && !e.disabled && e.getAttribute('contenteditable') !== 'false');
        """;
    public static string ComposerReady => "(() => {" + Helpers + "return !!composer(); })()";

    // Telegram and WhatsApp are driven the same way; only their selectors differ.
    // Telegram Web K uses .input-message-input and the older /a/ client uses
    // #editable-message-text. WhatsApp Web's composer is matched through its footer:
    // its chat search box is also a contenteditable and comes first in the document.
    // Both keep offscreen copies in the DOM, so visibility decides.
    public static string MessengerHelpers(Messenger messenger) => $$"""
        const visible = e => e && e.getClientRects().length > 0 && getComputedStyle(e).visibility !== 'hidden';
        const composer = () => [...document.querySelectorAll({{JsonSerializer.Serialize(messenger.Composer)}})]
            .find(e => visible(e) && e.getAttribute('contenteditable') !== 'false');
        const draft = e => (e.innerText ?? e.value ?? '').replace(/​/g, '').trim();
        const sendButton = () => {
            for (const found of document.querySelectorAll({{JsonSerializer.Serialize(messenger.Send)}})) {
                const b = found.closest('button') ?? found;
                if (visible(b) && !b.disabled && b.getAttribute('aria-disabled') !== 'true') return b;
            }
            return null;
        };
        """;
    public static string MessengerComposerReady(Messenger messenger)
        => "(() => {" + MessengerHelpers(messenger) + "return !!composer(); })()";
    public static string MessengerComposerEmpty(Messenger messenger)
        => "(() => {" + MessengerHelpers(messenger) + """
        const e = composer();
        return !e || draft(e).length === 0;
        })()
        """;
    public static string PrepareMessengerInput(Messenger messenger, string message)
        => "(() => {" + MessengerHelpers(messenger) + $$"""
        const e = composer();
        if (!e) return 'missing';
        const current = draft(e);
        const message = {{JsonSerializer.Serialize(message.Trim())}};
        if (current && current !== message) return 'draft';
        e.focus();
        return current === message ? 'reuse' : 'ready';
        })()
        """;
    public static string ClickMessengerSend(Messenger messenger)
        => "(() => {" + MessengerHelpers(messenger) + """
        const b = sendButton();
        if (!b) return false;
        b.click(); return true;
        })()
        """;

    // The user may have typed in the AI input themselves. That draft is kept and
    // the caption is added after it, separated by a blank line, so one message
    // carries both. The caret is moved to the end first: the user may have left
    // it mid-draft, and inserted text lands wherever the caret is.
    public static string PrepareInput(string prompt) => "(() => {" + Helpers + $$"""
        const e = composer();
        if (!e) return {state: 'missing', text: '', insert: ''};
        const current = (e.value ?? e.innerText ?? '').trim();
        const prompt = {{JsonSerializer.Serialize(prompt.Trim())}};
        e.focus();
        if ('value' in e) { e.selectionStart = e.selectionEnd = e.value.length; }
        else {
            const range = document.createRange();
            range.selectNodeContents(e); range.collapse(false);
            const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
        }
        if (current === prompt) return {state: 'reuse', text: prompt, insert: ''};
        if (!current) return {state: 'ready', text: prompt, insert: prompt};
        return {state: 'append', text: current + '\n\n' + prompt, insert: '\n\n' + prompt};
        })()
        """;
    public static string ClickSend => "(() => {" + Helpers + """
        const buttons = [...document.querySelectorAll('button')];
        const b = buttons.find(b => visible(b) && !b.disabled && b.getAttribute('aria-disabled') !== 'true' &&
          (b.dataset.testid === 'send-button' || /^(send|send message|send prompt|submit|submit message)$/i.test((b.getAttribute('aria-label') || b.innerText || '').trim())));
        if (!b) return false;
        b.click(); return true;
        })()
        """;

    public static string Reply(string provider, string prompt) => "(() => {" + Helpers + $$"""
        const normalize = s => (s || '').replace(/\s+/g, ' ').trim();
        const userSelector = '[data-message-author-role="user"], [data-testid="user-message"], [data-turn="user"]';
        const users = [...document.querySelectorAll(userSelector)].filter(visible);
        const user = users.at(-1);
        const matches = user && normalize(user.innerText) === normalize({{JsonSerializer.Serialize(prompt)}});
        const followsUser = n => matches && !n.contains(user) && !user.contains(n) &&
            !!(user.compareDocumentPosition(n) & Node.DOCUMENT_POSITION_FOLLOWING);
        let nodes = [...document.querySelectorAll({{JsonSerializer.Serialize(provider == "Claude"
            ? ".font-claude-response, .font-claude-response-body, [data-message-author-role='assistant'], [data-testid='assistant-message'], [data-turn='assistant']"
            : "[data-message-author-role='assistant'], [data-testid='assistant-message'], [data-turn='assistant']")}})].filter(visible);
        // A formatted message after the submitted user turn is a conservative fallback.
        // Never read the document body, the composer, or an unclassified user turn.
        if (matches && !nodes.some(followsUser)) {
            nodes.push(...[...document.querySelectorAll('main .markdown, main .prose, main .standard-markdown, [role="main"] .markdown')]
                .filter(n => visible(n) && followsUser(n) && !n.closest(userSelector + ', [contenteditable="true"], nav, aside')));
        }
        nodes = [...new Set(nodes)].filter(n => !nodes.some(other => other !== n && other.contains(n)));
        nodes.sort((a,b) => a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1);
        const stateKey = Symbol.for('HelpMe.replyReader');
        const state = window[stateKey] ||= { ids: new WeakMap(), next: 0, prefix: crypto.randomUUID() };
        const key = n => {
            if (!n) return '';
            const stable = n.getAttribute('data-message-id') || n.id;
            if (stable) return 'message:' + stable;
            if (!state.ids.has(n)) state.ids.set(n, state.prefix + ':' + ++state.next);
            return state.ids.get(n);
        };
        const read = n => {
            const blocks = [...n.querySelectorAll('.markdown, .standard-markdown')].filter(visible)
                .filter(b => !b.parentElement.closest('.markdown, .standard-markdown') || b.parentElement.closest('.markdown, .standard-markdown') === n);
            return (blocks.length ? blocks.map(b => b.innerText).join('\n\n') : n.innerText || '').trim();
        };
        const last = nodes.at(-1);
        const messages = nodes.map(n => ({ key: key(n), text: read(n) }));
        const text = last ? read(last) : '';
        const busy = [...document.querySelectorAll('button')].some(b => visible(b) &&
          (b.dataset.testid === 'stop-button' || /stop (generating|streaming|response)/i.test(b.getAttribute('aria-label') || ''))) ||
          !!document.querySelector('[data-is-streaming="true"]');
        return {key: key(last), text, busy, userKey: key(user), afterUser: !!(last && followsUser(last)), messages};
        })()
        """;
}
