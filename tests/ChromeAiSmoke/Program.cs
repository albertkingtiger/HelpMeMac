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
        var body = $$"""
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
              input.addEventListener('input', () => send.disabled = !(input.value || input.innerText).trim());
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
            if (currentPath.Contains("draft") && sendNumber == 1)
                await inspection.EvaluateAsync($"(() => {{ const e=document.querySelector('#prompt-textarea,[contenteditable=true]'); if ('value' in e) e.value={JsonSerializer.Serialize(prompt)}; else e.innerText={JsonSerializer.Serialize(prompt)}; e.dispatchEvent(new InputEvent('input',{{bubbles:true}})); return true; }})()", token);
            var answer = await ChromeAiClient.SendToPageAsync(socket!, provider, prompt, updates.Add, token);
            Check(answer == "Complete answer\nSecond line", provider + " complete assistant reply");
            Check(updates.Contains("Partial") && updates.Last() == answer, "Progress streaming");
            var state = await inspection.EvaluateAsync("({path:location.pathname,prompt:window.lastPrompt,sends:window.sends,identity:window.fixtureIdentity})", token);
            Check(state.GetProperty("path").GetString() == currentPath && state.GetProperty("identity").GetString() == identity,
                "Current conversation preserved without navigation or reload");
            Check(state.GetProperty("prompt").GetString() == prompt, "Caption text sent unchanged");
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
