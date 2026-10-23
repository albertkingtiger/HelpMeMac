using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

    private async Task<bool> DebugEndpointAvailableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
        try
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{Port}/json/version");
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
                        ? uri.Host.Trim().TrimStart("www.".ToCharArray())
                        : "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (url, exists) in new[]
                {
                    ("https://chatgpt.com/", hosts.Any(IsChatGptHost)),
                    ("https://claude.ai/", hosts.Any(IsClaudeHost))
                })
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
