using System.IO;

namespace LangWhisper.Audio;

/// <summary>Builds a short sine-wave tone as an in-memory PCM WAV file.</summary>
public static class ToneGenerator
{
    private const int SampleRate = 44100;
    private const short BitsPerSample = 16;

    /// <param name="frequencyHz">Pitch of the tone.</param>
    /// <param name="durationMs">Length of the tone.</param>
    /// <param name="amplitude">0.0-1.0 loudness.</param>
    public static byte[] CreateSineWav(double frequencyHz, int durationMs, double amplitude)
    {
        amplitude = Math.Clamp(amplitude, 0.0, 1.0);
        int totalSamples = Math.Max(1, SampleRate * Math.Max(10, durationMs) / 1000);

        // ~10 ms linear attack/decay so the tone doesn't click.
        int fadeSamples = Math.Min(totalSamples / 2, SampleRate * 10 / 1000);

        var pcm = new short[totalSamples];
        for (int i = 0; i < totalSamples; i++)
        {
            double envelope = 1.0;
            if (i < fadeSamples)
            {
                envelope = (double)i / fadeSamples;
            }
            else if (i >= totalSamples - fadeSamples)
            {
                envelope = (double)(totalSamples - i) / fadeSamples;
            }

            double sample = Math.Sin(2.0 * Math.PI * frequencyHz * i / SampleRate) * amplitude * envelope;
            pcm[i] = (short)(sample * short.MaxValue);
        }

        var data = new byte[totalSamples * 2];
        Buffer.BlockCopy(pcm, 0, data, 0, data.Length);

        using var ms = new MemoryStream(44 + data.Length);
        using var writer = new BinaryWriter(ms);

        const short channels = 1;
        int byteRate = SampleRate * channels * BitsPerSample / 8;
        short blockAlign = channels * BitsPerSample / 8;

        writer.Write("RIFF"u8);
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);                 // PCM fmt chunk size
        writer.Write((short)1);           // audio format = PCM
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(BitsPerSample);

        writer.Write("data"u8);
        writer.Write(data.Length);
        writer.Write(data);

        writer.Flush();
        return ms.ToArray();
    }
}
