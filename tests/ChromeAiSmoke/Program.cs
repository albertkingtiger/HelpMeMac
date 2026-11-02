using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HelpMe.Services;

using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = limit.Token;
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
var visited = new ConcurrentQueue<string>();
var provider = "ChatGPT";
var server = Task.Run(async () =>
{
    while (!token.IsCancellationRequested)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        var request = await reader.ReadLineAsync(token);
        if (request is null) continue;
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
        var path = request.Split(' ')[1];
        visited.Enqueue(path);
        var projectPath = provider == "Claude" ? "/project/p" : "/g/p/project";
        var sessionPath = provider == "Claude" ? "/chat/s" : "/c/s";
        var assistant = provider == "Claude" ? "class='font-claude-response'" : "data-message-author-role='assistant'";
        var composer = provider == "Claude" ? "<div contenteditable='true' role='textbox'></div>" : "<textarea id='prompt-textarea'></textarea>";
        var body = provider == "WhatsApp" ? $$"""
            <!doctype html><html><body>
            <div id="search" contenteditable="true" data-tab="3"></div>
            <footer><div contenteditable="true" data-tab="10"></div></footer>
            <div contenteditable="true" data-tab="10" style="display:none">Offscreen copy</div>
            <button data-testid="send" aria-label="Send"><span data-icon="send"></span></button>
            <script>
              const input = document.querySelector('footer div[contenteditable]');
              const send = document.querySelector('button[data-testid=send]');
              window.sends = 0;
              const deliver = () => {
                const text = input.innerText.trim();
                if (!text) return;
                window.sends++; window.lastPrompt = input.innerText; input.innerText = '';
              };
              if (!location.pathname.includes('button'))
                  input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); deliver(); } });
              send.onclick = deliver;
            </script></body></html>
            """ : provider == "Telegram" ? $$"""
            <!doctype html><html><body>
            <div class="chat-input"><div class="input-message-input" contenteditable="true"></div></div>
            <div class="input-message-input" contenteditable="true" style="display:none">Offscreen copy</div>
            <button class="btn-send record" aria-label="Send Message"></button>
            <script>
              const input = document.querySelector('.chat-input .input-message-input');
              const send = document.querySelector('.btn-send');
              window.sends = 0;
              const deliver = () => {
                const text = input.innerText.trim();
                if (!text) return;
                window.sends++; window.lastPrompt = input.innerText;
                input.innerText = ''; send.classList.add('record');
              };
              input.addEventListener('input', () => send.classList.toggle('record', !input.innerText.trim()));
              // The "button" fixture ignores Enter so the send-button fallback runs.
              if (!location.pathname.includes('button'))
                  input.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); deliver(); } });
              send.onclick = deliver;
            </script></body></html>
            """ : $$"""
            <!doctype html><html><body>
            <aside><a href="{{projectPath}}">Work</a><a href="/c/other">Unrelated</a></aside>
            <main>
              {{(path == projectPath || path == "/" ? $"<a href='{sessionPath}'>Meeting</a>" : "")}}
              <div id="messages"><div {{assistant}}><div class="markdown">Old answer one</div></div><div {{assistant}}><div class="markdown">Old answer two</div></div><div {{assistant}}><div class="markdown">Previous answer</div></div></div>
              <textarea style="display:none">Hidden draft</textarea>
              {{composer}}
              <button data-testid="send-button" aria-label="Send message" disabled>Send</button>
            </main><footer>Decoy footer text is not an answer</footer>
            <script>
              const input = document.querySelector('#prompt-textarea,[contenteditable=true]');
              const send = document.querySelector('button');
              // The "background" fixture only enables Send while the page is the
              // focused, active tab, the way a throttled provider page behaves.
              const active = () => !location.pathname.includes('background') || document.hasFocus();
              const sync = () => send.disabled = !((input.value || input.innerText).trim() && active());
              input.addEventListener('input', sync);
              setInterval(sync, 100);
              window.sends = 0;
              send.onclick = () => {
                window.sends++; window.lastPrompt = input.value || input.innerText;
                if ('value' in input) input.value = ''; else input.innerText = '';
                send.disabled = true;
                if (location.pathname.includes('virtualized') || location.pathname.includes('fallback'))
                    document.querySelector('#messages').replaceChildren();
                const user = document.createElement('div');
                user.dataset.messageAuthorRole = 'user';
                user.dataset.testid = 'conversation-turn-user';
                user.textContent = window.lastPrompt; document.querySelector('#messages').append(user);
                const stop = document.createElement('button');
                stop.dataset.testid = 'stop-button'; stop.setAttribute('aria-label', 'Stop generating');
                document.body.append(stop);
                setTimeout(() => {
                  const reply = document.createElement('div');
                  {{(provider == "Claude" ? "reply.className='font-claude-response';" : "reply.dataset.messageAuthorRole='assistant';")}}
                  if (location.pathname.includes('fallback')) {
                    reply.removeAttribute('class'); reply.removeAttribute('data-message-author-role');
                  }
                  reply.innerHTML = '<div class="markdown">Partial</div>'; document.querySelector('#messages').append(reply);
                  // Pause longer than the completion stability interval while still generating.
                  setTimeout(() => { reply.querySelector('.markdown').innerText = 'Complete answer\nSecond line'; stop.remove(); }, 2800);
                }, 700);
              };
            </script></body></html>
            """;
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
        await stream.WriteAsync(bytes, token);
    }
}, token);

var portProbe = new TcpListener(IPAddress.Loopback, 0);
portProbe.Start();
var debugPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
portProbe.Stop();
var chromePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google/Chrome/Application/chrome.exe");
var profile = Path.GetFullPath(Path.Combine(".tools", "chrome-ai-smoke-" + Guid.NewGuid().ToString("N")));
var start = new ProcessStartInfo(chromePath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
foreach (var arg in new[] { "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-background-networking", "--remote-debugging-port=" + debugPort, "--user-data-dir=" + profile, origin.AbsoluteUri }) start.ArgumentList.Add(arg);
using var chrome = Process.Start(start)!;
try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    Uri? socket = null;
    for (var i = 0; i < 60 && socket is null; i++)
    {
        try
        {
            using var tabs = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{debugPort}/json/list", token));
            foreach (var tab in tabs.RootElement.EnumerateArray())
                if (tab.GetProperty("type").GetString() == "page" && tab.GetProperty("url").GetString()!.StartsWith(origin.AbsoluteUri))
                    socket = new Uri(tab.GetProperty("webSocketDebuggerUrl").GetString()!);
        }
        catch (HttpRequestException) { }
        if (socket is null) await Task.Delay(200, token);
    }
    Check(socket is not null, "Fixture Chrome debugging endpoint ready");
    async Task Run(string selectedProvider, string currentPath)
    {
        provider = selectedProvider;
        using var inspection = new CdpPage();
        await inspection.ConnectAsync(socket!, token);
        await inspection.NavigateAsync(new Uri(origin, currentPath).AbsoluteUri, token);
        var identity = (await inspection.EvaluateAsync("window.fixtureIdentity = crypto.randomUUID()", token)).GetString();
        visited.Clear();
        for (var sendNumber = 1; sendNumber <= 2; sendNumber++)
        {
            var updates = new List<string>();
            var prompt = $"Caption {sendNumber}: What's next?\nSecond sentence.";
            Task Prefill(string text) => inspection.EvaluateAsync($"(() => {{ const e=document.querySelector('#prompt-textarea,[contenteditable=true]'); if ('value' in e) e.value={JsonSerializer.Serialize(text)}; else e.innerText={JsonSerializer.Serialize(text)}; e.dispatchEvent(new InputEvent('input',{{bubbles:true}})); return true; }})()", token);
            if (currentPath.Contains("draft") && sendNumber == 1) await Prefill(prompt);
            // A draft the user typed themselves must survive: the caption goes
            // below it rather than replacing it or being refused.
            string? typed = null;
            if (currentPath.Contains("typed") && sendNumber == 1)
            {
                typed = "My own half-written question";
                await Prefill(typed);
            }
            var answer = await ChromeAiClient.SendToPageAsync(socket!, provider, prompt, updates.Add, token);
            Check(answer == "Complete answer\nSecond line", provider + " complete assistant reply");
            Check(updates.Contains("Partial") && updates.Last() == answer, "Progress streaming");
            var state = await inspection.EvaluateAsync("({path:location.pathname,prompt:window.lastPrompt,sends:window.sends,identity:window.fixtureIdentity})", token);
            Check(state.GetProperty("path").GetString() == currentPath && state.GetProperty("identity").GetString() == identity,
                "Current conversation preserved without navigation or reload");
            // A contenteditable may represent a break as \n or \r\n, and a blank
            // line between two blocks as one break more than was inserted, so the
            // separator is checked for a blank line rather than byte for byte.
            var sent = (state.GetProperty("prompt").GetString() ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            if (typed is null) Check(sent == prompt, "Caption text sent unchanged");
            else
            {
                var framed = sent.Length >= typed.Length + prompt.Length && sent.StartsWith(typed) && sent.EndsWith(prompt);
                var separator = framed ? sent[typed.Length..^prompt.Length] : "";
                Check(framed && separator.Trim('\n').Length == 0 && separator.Count(c => c == '\n') >= 2,
                    "A typed draft is kept with the caption a blank line below it");
            }
            Check(state.GetProperty("sends").GetInt32() == sendNumber, "Exactly one submission per request");
        }
    }
    await Run("ChatGPT", "/c/already-open");
    await Run("Claude", "/chat/already-open");
    await Run("ChatGPT", "/c/virtualized");
    await Run("Claude", "/chat/virtualized");
    await Run("ChatGPT", "/c/fallback");
    await Run("Claude", "/chat/fallback");
    await Run("ChatGPT", "/c/draft");
    await Run("Claude", "/chat/draft");
    await Run("ChatGPT", "/c/typed");
    await Run("Claude", "/chat/typed");

    // Telegram is a delivery destination: the text must reach the open chat and
    // the call must return without waiting for any reply.
    Task RunTelegram(string currentPath, string label) => RunMessenger("Telegram", currentPath, label);
    async Task RunMessenger(string messenger, string currentPath, string label)
    {
        provider = messenger;
        using var inspection = new CdpPage();
        await inspection.ConnectAsync(socket!, token);
        await inspection.NavigateAsync(new Uri(origin, currentPath).AbsoluteUri, token);
        var identity = (await inspection.EvaluateAsync("window.fixtureIdentity = crypto.randomUUID()", token)).GetString();
        for (var sendNumber = 1; sendNumber <= 2; sendNumber++)
        {
            var message = $"Caption {sendNumber}: What's next?\nSecond sentence.";
            var result = await ChromeAiClient.SendToPageAsync(socket!, provider, message, null, token);
            Check(result.StartsWith("Sent ") && result.Contains(messenger), label + " reports delivery without a reply");
            var state = await inspection.EvaluateAsync("({path:location.pathname,prompt:window.lastPrompt,sends:window.sends,identity:window.fixtureIdentity})", token);
            Check(state.GetProperty("path").GetString() == currentPath && state.GetProperty("identity").GetString() == identity,
                label + " keeps the open chat without navigation or reload");
            // A contenteditable may represent the break as \n or \r\n; what matters
            // is that no caption text was dropped on the way in.
            var sent = (state.GetProperty("prompt").GetString() ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
            Check(sent.Replace("\n", " ").Trim() == message.Replace("\n", " ").Trim(), label + " delivers the caption text unchanged");
            Check(state.GetProperty("sends").GetInt32() == sendNumber, label + " sends exactly once per request");
            Check((await inspection.EvaluateAsync("(document.querySelector('#search')?.innerText ?? '').length", token)).GetInt32() == 0,
                label + " leaves the chat search box empty");
        }
    }
    await RunTelegram("/k/enter", "Telegram Enter");
    await RunTelegram("/k/button", "Telegram send button fallback");
    await RunMessenger("WhatsApp", "/w/enter", "WhatsApp Enter");
    await RunMessenger("WhatsApp", "/w/button", "WhatsApp send button fallback");

    // The user works in another tab while Help Me! sends. Chrome throttles the
    // provider tab, so without focus emulation the prompt is inserted and then
    // sits there unsent because Send never re-enables.
    provider = "ChatGPT";
    string? spareTab = null;
    try
    {
        using var inspection = new CdpPage();
        await inspection.ConnectAsync(socket!, token);
        await inspection.NavigateAsync(new Uri(origin, "/c/background").AbsoluteUri, token);
        using (var opened = await http.PutAsync($"http://127.0.0.1:{debugPort}/json/new?{Uri.EscapeDataString(new Uri(origin, "/c/elsewhere").AbsoluteUri)}", null, token))
        {
            opened.EnsureSuccessStatusCode();
            using var created = JsonDocument.Parse(await opened.Content.ReadAsStringAsync(token));
            spareTab = created.RootElement.GetProperty("id").GetString();
        }
        using (await http.GetAsync($"http://127.0.0.1:{debugPort}/json/activate/{spareTab}", token)) { }
        await Task.Delay(1000, token);
        Check(!(await inspection.EvaluateAsync("document.hasFocus()", token)).GetBoolean(),
            "Provider tab is genuinely in the background");
        // Headless Chrome does not throttle background tabs the way a real window
        // does, so the end-to-end check below cannot prove the fix. Assert the
        // mechanism directly: Chrome must accept the command and the page must
        // then report itself focused while another tab is active.
        await inspection.CommandAsync("Emulation.setFocusEmulationEnabled", new { enabled = true }, token);
        Check((await inspection.EvaluateAsync("document.hasFocus()", token)).GetBoolean(),
            "Focus emulation makes a background provider tab report itself focused");
        await inspection.CommandAsync("Emulation.setFocusEmulationEnabled", new { enabled = false }, token);
        var updates = new List<string>();
        var answer = await ChromeAiClient.SendToPageAsync(socket!, provider, "Caption while elsewhere.", updates.Add, token);
        Check(answer == "Complete answer\nSecond line", "Reply arrives while another tab holds focus");
        var sends = await inspection.EvaluateAsync("window.sends", token);
        Check(sends.GetInt32() == 1, "Exactly one submission from the background tab");
    }
    finally
    {
        if (spareTab is not null)
            try { using (await http.GetAsync($"http://127.0.0.1:{debugPort}/json/close/{spareTab}", token)) { } } catch (HttpRequestException) { }
    }
    using (var inspection = new CdpPage())
    {
        await inspection.ConnectAsync(socket!, token);
        try { await inspection.EvaluateAsync("(() => {throw Error('fixture failure')})()", token); throw new Exception("JavaScript errors must propagate"); }
        catch (IOException e) when (e.Message.Contains("reader failed")) { Console.WriteLine("PASS JavaScript errors propagate"); }
    }
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await ChromeAiClient.SendToPageAsync(socket!, provider, "No send", null, cancelled.Token); throw new Exception("Cancellation must propagate"); }
    catch (OperationCanceledException) { Console.WriteLine("PASS Cancellation"); }
    Console.WriteLine("ALL CHROME AI SMOKE CHECKS PASSED (local fixtures only)");
}
finally
{
    if (!chrome.HasExited) chrome.Kill(entireProcessTree: true);
    await chrome.WaitForExitAsync();
    limit.Cancel(); listener.Stop();
    try { await server; } catch (OperationCanceledException) { } catch (SocketException) { } catch (IOException) when (token.IsCancellationRequested) { }
    // This is a unique test-owned directory, never the user's browser profile.
    if (profile.StartsWith(Path.GetFullPath(".tools") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        try { Directory.Delete(profile, true); } catch (IOException) { }
    }
}
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL " + label);
    Console.WriteLine("PASS " + label);
}
