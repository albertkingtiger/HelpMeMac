using System.Diagnostics;
using System.IO;
using HelpMe.Services;
using NAudio.Wave;

if (args.Length != 1) throw new ArgumentException("Pass a mono 16 kHz PCM speech WAV path.");
var settings = AppSettings.Load();
using var engine = new SpeechEngine(settings);
await engine.InitializeAsync();
using var wav = new WaveFileReader(Path.GetFullPath(args[0]));
if (wav.WaveFormat.SampleRate != 16000 || wav.WaveFormat.Channels != 1 || wav.WaveFormat.BitsPerSample != 16)
    throw new ArgumentException("Fixture must be mono 16 kHz, 16-bit PCM.");

var source = new FakeInput(wav.WaveFormat);
var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var session = new AudioSession(source, engine, settings, "en", text => result.TrySetResult(text),
    status => { if (status.StartsWith("Error:")) result.TrySetException(new Exception(status)); });
session.Start();

// Send less than a full segment, then send NO silence frames. This reproduces
// loopback capture when playback stops before the maximum segment duration.
wav.Position = wav.WaveFormat.AverageBytesPerSecond;
var bytes = new byte[(int)(wav.WaveFormat.AverageBytesPerSecond * 0.75)];
var count = wav.Read(bytes, 0, bytes.Length);
var timer = Stopwatch.StartNew();
source.Emit(bytes, count);
var text = await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
if (string.IsNullOrWhiteSpace(text)) throw new Exception("Expected speech caption.");
Console.WriteLine($"PASS: playback-stop caption in {timer.Elapsed.TotalMilliseconds:F0} ms: {text}");

sealed class FakeInput(WaveFormat format) : IWaveIn
{
    public WaveFormat WaveFormat { get; set; } = format;
    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;
    public void Emit(byte[] bytes, int count) => DataAvailable?.Invoke(this, new WaveInEventArgs(bytes, count));
    public void StartRecording() { }
    public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());
    public void Dispose() { }
}
