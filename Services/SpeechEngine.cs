using System.IO;
using Whisper.net;

namespace HelpMe.Services;

public sealed class SpeechEngine(AppSettings settings) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private WhisperFactory? factory;
    private readonly Dictionary<string, WhisperProcessor> processors = new();

    public async Task InitializeAsync()
    {
        await gate.WaitAsync();
        try
        {
            var path = AppSettings.Resolve(settings.WhisperModelPath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Whisper model missing at '{path}'. Run scripts/setup.sh first.", path);
            factory ??= await Task.Run(() => WhisperFactory.FromPath(path));
        }
        finally { gate.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] samples, string language, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (!processors.TryGetValue(language, out var processor))
            {
                processor = factory!.CreateBuilder()
                    .WithLanguage(language)
                    .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
                    .WithNoContext()
                    .WithTemperature(0)
                    .WithTemperatureInc(0)
                    .WithBeamSearchSamplingStrategy(strategy => strategy.WithBeamSize(5).WithPatience(1))
                    .Build();
                processors.Add(language, processor);
            }
            var parts = new List<string>();
            await foreach (var segment in processor.ProcessAsync(samples, token))
            {
                var text = CaptionTextCleaner.Clean(segment.Text);
                if (text.Length > 0) parts.Add(text);
            }
            return string.Join(" ", parts);
        }
        finally { gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var processor in processors.Values) processor.Dispose();
        factory?.Dispose(); gate.Dispose();
    }
}
