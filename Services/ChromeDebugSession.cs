using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

namespace HelpMe.Services;

public sealed class ChromeDebugSession : IAsyncDisposable
{
    private Process? process;
    public int Port { get; }
    public bool IsRunning => process is { HasExited: false };
    public ChromeDebugSession(int port) => Port = port;

    public async Task StartAsync()
    {
        if (!IsRunning)
        {
            if (await DebugEndpointAvailableAsync())
            {
                await EnsureProviderTabsAsync();
                return;
            }
            var executable = FindChrome() ?? throw new FileNotFoundException("Google Chrome was not found.");
        // Keep the dedicated profile so provider logins survive application restarts.
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HelpMe", "ChromeDebug");
        Directory.CreateDirectory(profile);
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=" + Port);
        startInfo.ArgumentList.Add("--user-data-dir=" + profile);
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        process = Process.Start(startInfo) ?? throw new IOException("Could not start Google Chrome.");
        }
        await EnsureProviderTabsAsync();
    }

    private Task<bool> DebugEndpointAvailableAsync() => IsDebugEndpointAvailableAsync(Port);

    // A Chrome started before Help Me! leaves this session's `process` null, so
    // the debug endpoint, not the child process, is what "running" means here.
    public static async Task<bool> IsDebugEndpointAvailableAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/json/version");
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private async Task EnsureProviderTabsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var document = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{Port}/json/list"));
                var hosts = document.RootElement.EnumerateArray()
                    .Where(page => page.TryGetProperty("type", out var type) && type.GetString() == "page")
                    .Select(page => page.TryGetProperty("url", out var value) &&
                        Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
                        ? StripWww(uri.Host.Trim())
                        : "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var wanted = new List<(string Url, bool Exists)>
                {
                    ("https://chatgpt.com/", hosts.Any(IsChatGptHost)),
                    ("https://claude.ai/", hosts.Any(IsClaudeHost))
                };
                // Every messenger gets its tab, so switching destination in
                // Settings never means hunting for a window that is not open.
                wanted.AddRange(Messenger.All.Select(messenger =>
                    (messenger.Url, hosts.Any(host => host.Equals(messenger.Host, StringComparison.OrdinalIgnoreCase)))));
                foreach (var (url, exists) in wanted)
                {
                    if (exists) continue;
                    using var response = await http.PutAsync($"http://127.0.0.1:{Port}/json/new?{Uri.EscapeDataString(url)}", null);
                    response.EnsureSuccessStatusCode();
                }
                return;
            }
            catch (HttpRequestException) when (DateTime.UtcNow < deadline) { await Task.Delay(250); }
            catch (TaskCanceledException) when (DateTime.UtcNow < deadline) { await Task.Delay(250); }
        }
        throw new IOException("Chrome remote debugging did not become available.");
    }

    // TrimStart(char[]) would strip every leading 'w' and '.', turning
    // web.telegram.org into eb.telegram.org and reopening the tab every launch.
    internal static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static bool IsChatGptHost(string host) =>
        host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("chat.openai.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaudeHost(string host) =>
        host.Equals("claude.ai", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".claude.ai", StringComparison.OrdinalIgnoreCase);


    public Task<string> SendPromptAsync(string prompt, string provider,
        Action<string>? progress, CancellationToken cancellation)
    {
        if (!IsRunning) throw new IOException("Managed Chrome is not running. Restart Help Me! to reopen it.");
        return ChromeAiClient.SendAsync(Port, provider, prompt, progress, cancellation);
    }

    // Closing from Settings is an explicit instruction, so it must work even for
    // a Chrome that was already running when Help Me! started: that session owns
    // no process, and DisposeAsync alone would silently do nothing. Browser.close
    // shuts the browser down through the debug protocol regardless of who
    // launched it. Application shutdown deliberately still uses DisposeAsync, so
    // quitting Help Me! never closes a browser it did not start.
    public async Task CloseAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var document = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{Port}/json/version"));
            if (document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var value) &&
                Uri.TryCreate(value.GetString(), UriKind.Absolute, out var socket))
            {
                using var browser = new ClientWebSocket();
                using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await browser.ConnectAsync(socket, connect.Token);
                await browser.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = 1, method = "Browser.close" }),
                    WebSocketMessageType.Text, true, connect.Token);
                // Chrome tears the connection down instead of replying, so wait
                // for the endpoint to go away rather than for a response.
                for (var attempt = 0; attempt < 20 && await IsDebugEndpointAvailableAsync(Port); attempt++)
                    await Task.Delay(250);
            }
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (JsonException) { }
        // Reap a process this session started, whether or not the request landed.
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var running = process;
        process = null;
        if (running is null) return;
        try
        {
            if (running.HasExited) return;
            try { running.CloseMainWindow(); } catch (InvalidOperationException) { }
            try { await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { }
            if (!running.HasExited)
            {
                running.Kill(entireProcessTree: true);
                await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (InvalidOperationException) { }
        finally { running.Dispose(); }
    }

    private static string? FindChrome() => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
    }.FirstOrDefault(File.Exists);
}
