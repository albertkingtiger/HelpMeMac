using System.IO;
using System.Diagnostics;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HelpMe.Services;

// Each source owns its capture buffer and bounded queue; both share one Whisper model.
public sealed class AudioSession : ICaptionSession
{
    private readonly IWaveIn capture;
    private readonly SpeechEngine engine;
    private readonly AppSettings settings;
    private readonly string language;
    private readonly Action<string> caption;
    private readonly Action<string>? preview;
    private readonly Action<string> status;
    private readonly Channel<AudioChunk> queue = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(8)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly CancellationTokenSource cancellation = new();
    private readonly object sync = new();
    private readonly MemoryStream pending = new();
    private readonly WaveFormat format;
    private Task? worker;
    private Task? silenceMonitor;
    private long lastDataTimestamp;
    private bool stopping;
    private bool faulted;
    private bool hasSpeech;
    private double silence;
    private double lastQueuedSeconds;

    private sealed record AudioChunk(byte[] Bytes, bool Final);

    public AudioSession(bool microphone, SpeechEngine engine, AppSettings settings, string language,
        Action<string> caption, Action<string> status, Action<string>? preview = null)
        : this(microphone ? new WasapiCapture() : new WasapiLoopbackCapture(),
            engine, settings, language, caption, status, preview) { }

    public AudioSession(IWaveIn capture, SpeechEngine engine, AppSettings settings, string language,
        Action<string> caption, Action<string> status, Action<string>? preview = null)
    {
        this.engine = engine; this.settings = settings; this.language = language;
        this.caption = caption; this.status = status; this.preview = preview;
        this.capture = capture;
        format = StandardFormat(capture.WaveFormat);
        capture.DataAvailable += OnData;
        capture.RecordingStopped += (_, e) =>
        {
            lock (sync)
            {
                if (!stopping) queue.Writer.TryComplete(e.Exception ?? new IOException("Audio device stopped. Stop and restart this panel."));
            }
        };
    }

    public void Start()
    {
        capture.StartRecording();
        worker = Task.Run(ProcessAsync);
        silenceMonitor = Task.Run(MonitorSilenceAsync);
    }

    public Task StartAsync() { Start(); return Task.CompletedTask; }

    private static WaveFormat StandardFormat(WaveFormat value)
    {
        if (value is not WaveFormatExtensible extensible) return value;
        return extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71")
            ? WaveFormat.CreateIeeeFloatWaveFormat(value.SampleRate, value.Channels)
            : new WaveFormat(value.SampleRate, value.BitsPerSample, value.Channels);
    }

    private float[] Samples(byte[] bytes, bool resample)
    {
        using var stream = new RawSourceWaveStream(new MemoryStream(bytes), format);
        ISampleProvider source = stream.ToSampleProvider();
        if (format.Channels > 1) source = new MonoMix(source);
        if (resample && source.WaveFormat.SampleRate != 16000)
            source = new WdlResamplingSampleProvider(source, 16000);
        var result = new List<float>();
        var buffer = new float[4096];
        int count;
        while ((count = source.Read(buffer, 0, buffer.Length)) > 0) result.AddRange(buffer.Take(count));
        return result.ToArray();
    }

    private void OnData(object? sender, WaveInEventArgs args)
    {
        lock (sync)
        {
            if (stopping || faulted) return;
            lastDataTimestamp = Stopwatch.GetTimestamp();
            try
            {
                var bytes = args.Buffer.AsSpan(0, args.BytesRecorded).ToArray();
                var samples = Samples(bytes, false);
                var rms = samples.Length == 0 ? 0 : Math.Sqrt(samples.Average(x => (double)x * x));
                var duration = (double)args.BytesRecorded / format.AverageBytesPerSecond;
                if (rms >= settings.SpeechThreshold) { hasSpeech = true; silence = 0; }
                else silence += duration;
                pending.Write(bytes);
                var seconds = (double)pending.Length / format.AverageBytesPerSecond;
                if (hasSpeech && (silence >= settings.SilenceSeconds || seconds >= settings.MaxSegmentSeconds))
                {
                    FlushPending();
                }
                else if (hasSpeech && seconds >= lastQueuedSeconds + 1)
                {
                    QueueSnapshot(final: false);
                }
                else if (!hasSpeech && seconds > 0.25)
                {
                    // Retain the most recent block as a little pre-roll, without buffering silence forever.
                    pending.SetLength(0); pending.Write(bytes);
                }
            }
            catch (Exception e) { faulted = true; queue.Writer.TryComplete(e); }
        }
    }

    // Caller holds sync. WASAPI loopback stops sending frames when playback stops,
    // so a wall-clock monitor must flush the last phrase even without another callback.
    private void FlushPending()
    {
        QueueSnapshot(final: true);
        pending.SetLength(0); hasSpeech = false; silence = 0;
        lastQueuedSeconds = 0;
    }

    private void QueueSnapshot(bool final)
    {
        if (pending.Length == 0) return;
        var seconds = (double)pending.Length / format.AverageBytesPerSecond;
        if (!final && seconds <= lastQueuedSeconds) return;
        if (!queue.Writer.TryWrite(new AudioChunk(pending.ToArray(), final)))
            status("Caption queue full · audio snapshot skipped; try a smaller model");
        else if (!final) lastQueuedSeconds = seconds;
    }

    private async Task MonitorSilenceAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token))
            {
                lock (sync)
                {
                    if (!stopping && !faulted && hasSpeech &&
                        Stopwatch.GetElapsedTime(lastDataTimestamp).TotalSeconds >= settings.SilenceSeconds)
                        FlushPending();
                    else if (!stopping && !faulted && hasSpeech && pending.Length > 0 &&
                        (double)pending.Length / format.AverageBytesPerSecond >= lastQueuedSeconds + 1)
                        QueueSnapshot(final: false);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var chunk in queue.Reader.ReadAllAsync(cancellation.Token))
            {
                status("Recognizing speech…");
                var samples = Samples(chunk.Bytes, true);
                // Whisper behaves poorly on extremely short inputs; pad the final silence to one second.
                if (samples.Length < 16000) Array.Resize(ref samples, 16000);
                var text = await engine.TranscribeAsync(samples, language, cancellation.Token);
                if (!cancellation.IsCancellationRequested && text.Length > 0)
                {
                    if (chunk.Final) caption(text); else preview?.Invoke(text);
                }
                status("Listening…");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            lock (sync) { faulted = true; }
            status("Error: " + e.Message + " · stop and restart");
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (sync) { stopping = true; }
        cancellation.Cancel();
        capture.StopRecording();
        capture.Dispose();
        queue.Writer.TryComplete();
        if (silenceMonitor is not null) await silenceMonitor;
        if (worker is not null) await worker;
        pending.Dispose(); cancellation.Dispose();
    }

    private sealed class MonoMix(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var interleaved = new float[count * channels];
            var read = source.Read(interleaved, 0, interleaved.Length) / channels;
            for (var i = 0; i < read; i++)
            {
                double sum = 0;
                for (var c = 0; c < channels; c++) sum += interleaved[i * channels + c];
                buffer[offset + i] = (float)(sum / channels);
            }
            return read;
        }
    }
}
