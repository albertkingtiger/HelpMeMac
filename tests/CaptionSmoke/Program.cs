using System.Diagnostics;
using HelpMe.Services;
using HelpMe.Services.Audio;

// Recognition end to end, from a capture source to a finished caption.
//
// The point of the check is the flush: a loopback device stops delivering
// frames the moment playback stops, so less than a full segment arrives and
// nothing follows it. The wall-clock monitor has to notice and send what it has
// to Whisper anyway, or the last phrase of every video is lost.
if (args.Length != 1) throw new ArgumentException("Pass a mono 16 kHz PCM speech WAV path.");
var settings = AppSettings.Load();
using var engine = new SpeechEngine(settings);
await engine.InitializeAsync();

var (rate, channels, samples) = WavFile.ReadMono16(Path.GetFullPath(args[0]));
if (rate != 16000 || channels != 1) throw new ArgumentException("Fixture must be mono 16 kHz, 16-bit PCM.");

var source = new FakeCapture(rate);
var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
await using var session = new AudioSession(source, engine, settings, "en", text => result.TrySetResult(text),
    status => { if (status.StartsWith("Error:")) result.TrySetException(new Exception(status)); });
session.Start();

// Skip the first second, then send three quarters of a second and stop: no
// silence frames follow, exactly as when the sound stops mid-sentence.
var start = rate;
var length = (int)(rate * 0.75);
var chunk = samples.AsSpan(start, Math.Min(length, samples.Length - start)).ToArray();
var timer = Stopwatch.StartNew();
source.Emit(chunk);
var text = await result.Task.WaitAsync(TimeSpan.FromSeconds(30));
if (string.IsNullOrWhiteSpace(text)) throw new Exception("Expected speech caption.");
Console.WriteLine($"PASS: playback-stop caption in {timer.Elapsed.TotalMilliseconds:F0} ms: {text}");

sealed class FakeCapture(int rate) : IAudioCapture
{
    public int SampleRate { get; } = rate;
    public int Channels => 1;
    public event Action<float[], int>? DataAvailable;
    public event Action<Exception?>? Stopped;
    public void Emit(float[] samples) => DataAvailable?.Invoke(samples, samples.Length);
    public void Start() { }
    public void Stop() => Stopped?.Invoke(null);
    public void Dispose() { }
}

// Just enough of a WAV reader for the fixture. NAudio did this on Windows and
// is not part of the macOS build, and a PCM file is a header and a block of
// samples.
static class WavFile
{
    public static (int Rate, int Channels, float[] Samples) ReadMono16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F')
            throw new ArgumentException("Not a RIFF/WAVE file.");
        int rate = 0, channels = 0, bits = 0;
        var offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            var body = offset + 8;
            if (size < 0 || body + size > bytes.Length) size = bytes.Length - body;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                if (bits != 16) throw new ArgumentException("Fixture must be 16-bit PCM.");
                var count = size / 2;
                var samples = new float[count];
                for (var i = 0; i < count; i++)
                    samples[i] = BitConverter.ToInt16(bytes, body + i * 2) / 32768f;
                return (rate, channels, samples);
            }
            // Chunks are word aligned, so an odd size carries a pad byte.
            offset = body + size + (size & 1);
        }
        throw new ArgumentException("No data chunk in the WAV file.");
    }
}
