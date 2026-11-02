using System.Diagnostics;
using System.Threading.Channels;
using HelpMe.Services.Audio;

namespace HelpMe.Services;

// Each source owns its capture buffer and bounded queue; both share one Whisper
// model.
//
// The segmentation, the energy threshold and the wall-clock flush are the ones
// the Windows build arrived at. What changed underneath is only where the
// samples come from: NAudio handed over interleaved bytes with a WaveFormat,
// and IAudioCapture hands over float samples with a rate, so the arithmetic is
// in samples and seconds rather than in bytes per second.
public sealed class AudioSession : ICaptionSession
{
    private readonly IAudioCapture capture;
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
    private readonly List<float> pending = new();
    private readonly List<float> scratch = new();
    private Task? worker;
    private Task? silenceMonitor;
    private long lastDataTimestamp;
    private bool stopping;
    private bool faulted;
    private bool hasSpeech;
    private double silence;
    private double lastQueuedSeconds;

    // Whisper is trained on 16 kHz audio and resamples anything else itself,
    // badly; the capture is converted once here instead.
    private const int RecognitionRate = 16000;

    private sealed record AudioChunk(float[] Samples, bool Final);

    public AudioSession(IAudioCapture capture, SpeechEngine engine, AppSettings settings, string language,
        Action<string> caption, Action<string> status, Action<string>? preview = null)
    {
        this.engine = engine; this.settings = settings; this.language = language;
        this.caption = caption; this.status = status; this.preview = preview;
        this.capture = capture;
        capture.DataAvailable += OnData;
        capture.Stopped += error =>
        {
            lock (sync)
            {
                if (!stopping)
                    queue.Writer.TryComplete(error ?? new IOException("Audio capture stopped. Stop and restart this panel."));
            }
        };
    }

    public void Start()
    {
        capture.Start();
        lastDataTimestamp = Stopwatch.GetTimestamp();
        worker = Task.Run(ProcessAsync);
        silenceMonitor = Task.Run(MonitorSilenceAsync);
    }

    // Opening a device can take a while, and ScreenCaptureKit's first start
    // waits for the user to answer a system permission prompt, so no caller
    // opens a capture on the UI thread.
    public Task StartAsync() => Task.Run(Start);

    private double PendingSeconds => pending.Count / (double)capture.SampleRate;

    private void OnData(float[] buffer, int count)
    {
        lock (sync)
        {
            if (stopping || faulted) return;
            lastDataTimestamp = Stopwatch.GetTimestamp();
            try
            {
                scratch.Clear();
                AudioMath.MixToMono(buffer, count, capture.Channels, scratch);
                if (scratch.Count == 0) return;
                var rms = AudioMath.RootMeanSquare(scratch, 0, scratch.Count);
                var duration = scratch.Count / (double)capture.SampleRate;
                if (rms >= settings.SpeechThreshold) { hasSpeech = true; silence = 0; }
                else silence += duration;
                pending.AddRange(scratch);
                var seconds = PendingSeconds;
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
                    // Retain the most recent block as a little pre-roll, without
                    // buffering silence forever.
                    pending.Clear();
                    pending.AddRange(scratch);
                }
            }
            catch (Exception e) { faulted = true; queue.Writer.TryComplete(e); }
        }
    }

    // Caller holds sync. A loopback device stops sending frames when playback
    // stops, so a wall-clock monitor must flush the last phrase even when no
    // further callback arrives.
    private void FlushPending()
    {
        QueueSnapshot(final: true);
        pending.Clear(); hasSpeech = false; silence = 0;
        lastQueuedSeconds = 0;
    }

    private void QueueSnapshot(bool final)
    {
        if (pending.Count == 0) return;
        var seconds = PendingSeconds;
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
                    else if (!stopping && !faulted && hasSpeech && pending.Count > 0 &&
                        PendingSeconds >= lastQueuedSeconds + 1)
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
                var samples = AudioMath.Resample(chunk.Samples, capture.SampleRate, RecognitionRate);
                // Whisper behaves poorly on extremely short inputs; pad the
                // final silence to one second.
                if (samples.Length < RecognitionRate) Array.Resize(ref samples, RecognitionRate);
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
        capture.Stop();
        capture.Dispose();
        queue.Writer.TryComplete();
        if (silenceMonitor is not null) await silenceMonitor;
        if (worker is not null) await worker;
        cancellation.Dispose();
    }
}
