using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace HelpMe.Services;

public sealed class TranslationSession : IAsyncDisposable
{
    private readonly Process process;
    private sealed record Request(string Text, TaskCompletionSource<string>? Completion = null);
    private readonly Channel<Request> queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action<string> caption;
    private readonly Action<string> status;
    private readonly string sourceLanguage;
    private readonly StringBuilder errors = new();
    private Task? worker;

    public TranslationSession(AppSettings settings, string sourceLanguage, Action<string> caption, Action<string> status)
    {
        this.caption = caption; this.status = status; this.sourceLanguage = sourceLanguage;
        var model = AppSettings.Resolve(settings.TranslationModelPath);
        if (!File.Exists(Path.Combine(model, "model.bin")))
            throw new FileNotFoundException($"Translation model missing at '{model}'. Run scripts/setup.ps1 -Translation first.");
        var python = AppSettings.Resolve(settings.PythonPath);
        if (!File.Exists(python)) throw new FileNotFoundException($"Python environment missing at '{python}'. Run scripts/setup.ps1 -Translation first.");
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = AppSettings.Root
        };
        var pythonDirectory = Path.GetDirectoryName(python) ?? AppSettings.Root;
        var packageSitePackages = new[]
        {
            Path.Combine(pythonDirectory, "Lib", "site-packages"),
            Path.Combine(AppSettings.Root, ".venv", "Lib", "site-packages")
        }.FirstOrDefault(Directory.Exists);
        if (packageSitePackages is not null)
        {
            var existing = Environment.GetEnvironmentVariable("PYTHONPATH");
            start.Environment["PYTHONPATH"] = packageSitePackages +
                (string.IsNullOrWhiteSpace(existing) ? "" : Path.PathSeparator + existing);
            var nativePackage = Path.Combine(packageSitePackages, "ctranslate2");
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            start.Environment["PATH"] = string.Join(Path.PathSeparator,
                new[] { nativePackage, packageSitePackages, pythonDirectory, path }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(AppSettings.Resolve("translation/worker.py"));
        start.ArgumentList.Add("--model"); start.ArgumentList.Add(model);
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        process = new Process { StartInfo = start };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            lock (errors) { errors.AppendLine(args.Data); if (errors.Length > 4000) errors.Remove(0, errors.Length - 4000); }
        };
    }

    public async Task StartAsync()
    {
        process.Start(); process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
        if (line is null) throw WorkerError();
        using var reply = JsonDocument.Parse(line);
        if (!reply.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
            throw new InvalidOperationException(line);
        worker = Task.Run(ProcessAsync);
    }

    public void Enqueue(string text)
    {
        if (!queue.Writer.TryWrite(new Request(text))) status("Translation queue full · phrase skipped");
    }

    public async Task<string> TranslateAsync(string text)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Request(text, completion), cancellation.Token);
        return await completion.Task.WaitAsync(cancellation.Token);
    }

    private Exception WorkerError()
    {
        lock (errors) return new IOException("Translation worker exited. " + errors.ToString().Trim());
    }

    private async Task ProcessAsync()
    {
        Request? active = null;
        Exception? failure = null;
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(cancellation.Token))
            {
                active = item;
                status("Translating into Korean…");
                var request = JsonSerializer.Serialize(new { text = item.Text, source = sourceLanguage });
                await process.StandardInput.WriteLineAsync(request.AsMemory(), cancellation.Token);
                await process.StandardInput.FlushAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromMinutes(2));
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token) ?? throw WorkerError();
                using var reply = JsonDocument.Parse(line);
                if (reply.RootElement.TryGetProperty("error", out var error)) throw new IOException(error.GetString());
                var translated = reply.RootElement.GetProperty("text").GetString() ?? "";
                if (!cancellation.IsCancellationRequested)
                {
                    if (item.Completion is not null) item.Completion.TrySetResult(translated);
                    else caption(translated);
                }
                active = null;
                status("Listening to PC audio…");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (OperationCanceledException e) { failure = e; status("Error: translation timed out · stop and restart"); }
        catch (Exception e) { failure = e; status("Error: " + e.Message + " · stop and restart"); }
        finally
        {
            queue.Writer.TryComplete();
            void Finish(Request? item)
            {
                if (item?.Completion is null) return;
                if (failure is not null) item.Completion.TrySetException(failure);
                else item.Completion.TrySetCanceled();
            }
            Finish(active);
            while (queue.Reader.TryRead(out var pending)) Finish(pending);
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel(); queue.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        if (worker is not null) await worker;
        process.Dispose(); cancellation.Dispose();
    }
}
