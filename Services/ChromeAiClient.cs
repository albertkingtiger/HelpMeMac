using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HelpMe.Services;

// All browser work is asynchronous. No cookies, tokens, or private service APIs are used.
public sealed class ChromeAiClient
{
    public static string DiagnosticPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpMe", "logs", "ai-reader.log");
    public static async Task<string> SendAsync(int port, string provider,
        string prompt, Action<string>? progress, CancellationToken cancellation)
    {
        var host = provider == "Claude" ? "claude.ai" : "chatgpt.com";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancellation);
        using var pages = JsonDocument.Parse(json);
        var page = pages.RootElement.EnumerateArray().FirstOrDefault(x =>
            x.GetProperty("type").GetString() == "page" &&
            Uri.TryCreate(x.GetProperty("url").GetString(), UriKind.Absolute, out var uri) && uri.Host == host);
        if (page.ValueKind == JsonValueKind.Undefined)
            throw new IOException($"Open {provider} in Help Me!'s Chrome window and sign in first.");
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        using var page = new CdpPage();
        diagnostics?.Invoke("stage=connect");
        await page.ConnectAsync(socketUri, token);
        diagnostics?.Invoke("stage=find-input");
        await WaitForAsync(async () => (await page.EvaluateAsync(BrowserScripts.ComposerReady, token)).GetBoolean(),
            "The AI message input is unavailable. Sign in to the selected provider and open a conversation in Chrome.", token);
        // Wait for conversation history to settle before taking the assistant baseline.
        await Task.Delay(600, token);
        diagnostics?.Invoke("stage=read-baseline");
        var baseline = await SnapshotAsync(page, provider, prompt, token);
        var oldMessages = baseline.Messages.ToDictionary(m => m.Key, m => m.Text);
        diagnostics?.Invoke("stage=prepare-input");
        var prepared = await page.EvaluateAsync(BrowserScripts.PrepareInput(prompt), token);
        diagnostics?.Invoke("input=" + prepared.GetString());
        if (prepared.GetString() is not ("ready" or "reuse"))
            throw new IOException("The AI input already contains an unsent draft. Send or clear that draft in Chrome first.");
        if (prepared.GetString() == "ready")
        {
            diagnostics?.Invoke("stage=insert-text");
            await page.CommandAsync("Input.insertText", new { text = prompt }, token);
        }
        diagnostics?.Invoke("stage=click-send");
        await WaitForAsync(async () => (await page.EvaluateAsync(BrowserScripts.ClickSend, token)).GetBoolean(),
            "The AI Send button did not become available. Check the Chrome tab for a sign-in or usage-limit message.", token);
        diagnostics?.Invoke($"submitted baselineMessages={baseline.Messages.Length}");

        var lastText = "";
        var stable = Stopwatch.StartNew();
        var elapsed = Stopwatch.StartNew();
        var lastDiagnostic = TimeSpan.FromSeconds(-5);
        while (true)
        {
            await Task.Delay(350, token);
            var snapshot = await SnapshotAsync(page, provider, prompt, token);
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

    private sealed record Message(string Key, string Text);
    private sealed record Reply(string Key, string Text, bool Busy, string UserKey, bool AfterUser, Message[] Messages);
    private static async Task<Reply> SnapshotAsync(CdpPage page, string provider, string prompt, CancellationToken token)
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

internal sealed class CdpPage : IDisposable
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
    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken token)
    {
        var result = await CommandAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true }, token);
        if (result.TryGetProperty("exceptionDetails", out var details))
            throw new IOException("The AI page reader failed: " +
                (details.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
                    ? description.GetString() : details.GetProperty("text").GetString()));
        return result.GetProperty("result").GetProperty("value").Clone();
    }
    public async Task NavigateAsync(string url, CancellationToken token)
    {
        var result = await CommandAsync("Page.navigate", new { url }, token);
        if (result.TryGetProperty("errorText", out var error)) throw new IOException("Could not open AI destination: " + error.GetString());
        await Task.Delay(500, token);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(25))
        {
            try
            {
                if ((await EvaluateAsync("document.readyState === 'complete'", token)).GetBoolean()) return;
            }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(20)) { }
            await Task.Delay(250, token);
        }
        throw new IOException("The AI page did not finish loading.");
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
    public static string PrepareInput(string prompt) => "(() => {" + Helpers + $$"""
        const e = composer();
        if (!e) return 'missing';
        const current = (e.value ?? e.innerText ?? '').trim();
        const prompt = {{JsonSerializer.Serialize(prompt.Trim())}};
        if (current && current !== prompt) return 'draft';
        if (current === prompt) { e.focus(); return 'reuse'; }
        e.focus(); return 'ready';
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
