namespace HelpMe.Services.Audio;

// Mixing down to mono and resampling to the 16 kHz Whisper expects.
//
// NAudio did both on Windows. The replacements are deliberately plain: a
// capture callback runs on a real-time audio thread, and anything allocating or
// blocking in there costs frames.
public static class AudioMath
{
    // Interleaved frames to one channel. Whisper wants mono, and averaging the
    // channels keeps a speaker who is only in one of them audible.
    public static void MixToMono(float[] source, int count, int channels, List<float> destination)
    {
        if (channels <= 1)
        {
            for (var i = 0; i < count; i++) destination.Add(source[i]);
            return;
        }
        var frames = count / channels;
        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            var offset = frame * channels;
            for (var channel = 0; channel < channels; channel++) sum += source[offset + channel];
            destination.Add((float)(sum / channels));
        }
    }

    public static double RootMeanSquare(IReadOnlyList<float> samples, int start, int count)
    {
        if (count <= 0) return 0;
        double sum = 0;
        for (var i = start; i < start + count; i++) sum += (double)samples[i] * samples[i];
        return Math.Sqrt(sum / count);
    }

    // A windowed-sinc resampler. Dropping or repeating samples to reach 16 kHz
    // folds everything above 8 kHz back into the speech band as aliasing, and
    // recognition accuracy pays for it, so the band is filtered as it is
    // converted rather than afterwards.
    public static float[] Resample(float[] samples, int sourceRate, int targetRate)
    {
        if (sourceRate == targetRate || samples.Length == 0) return samples;
        if (sourceRate <= 0 || targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));

        var ratio = (double)targetRate / sourceRate;
        var outputLength = (int)(samples.Length * ratio);
        if (outputLength <= 0) return Array.Empty<float>();
        var result = new float[outputLength];

        // Cut off just below whichever Nyquist limit is lower. Downsampling is
        // the case that matters: 48 kHz capture into a 16 kHz model.
        var cutoff = 0.5 * Math.Min(1.0, ratio);
        // Twelve zero crossings each side is a good compromise between the
        // transition width and the work done per output sample.
        const int zeroCrossings = 12;
        var half = (int)Math.Ceiling(zeroCrossings / (2 * cutoff));

        for (var n = 0; n < outputLength; n++)
        {
            var position = n / ratio;
            var centre = (int)Math.Floor(position);
            double sum = 0;
            double weight = 0;
            for (var k = centre - half + 1; k <= centre + half; k++)
            {
                if (k < 0 || k >= samples.Length) continue;
                var distance = k - position;
                var window = Hann(distance, half);
                if (window <= 0) continue;
                var tap = 2 * cutoff * Sinc(2 * cutoff * distance) * window;
                sum += samples[k] * tap;
                weight += tap;
            }
            // Normalising by the taps actually used keeps the first and last
            // few samples at the right level instead of fading them.
            result[n] = weight > 1e-9 ? (float)(sum / weight) : 0f;
        }
        return result;
    }

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1.0;
        var pi = Math.PI * x;
        return Math.Sin(pi) / pi;
    }

    private static double Hann(double distance, int half)
    {
        if (Math.Abs(distance) > half) return 0;
        return 0.5 * (1 + Math.Cos(Math.PI * distance / half));
    }
}
