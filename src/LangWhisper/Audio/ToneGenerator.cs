using System.IO;
using LangWhisper.Configuration;

namespace LangWhisper.Audio;

/// <summary>Builds short cue sounds as in-memory PCM WAV files.</summary>
public static class ToneGenerator
{
    private const int SampleRate = 44100;
    private const short BitsPerSample = 16;

    /// <param name="sound">Shape of the cue.</param>
    /// <param name="frequencyHz">Base pitch.</param>
    /// <param name="durationMs">Total length of the cue.</param>
    /// <param name="amplitude">0.0-1.0 loudness.</param>
    public static byte[] CreateCueWav(BeepSound sound, double frequencyHz, int durationMs, double amplitude)
    {
        amplitude = Math.Clamp(amplitude, 0.0, 1.0);
        durationMs = Math.Max(60, durationMs);
        var pcm = new List<short>(SampleRate * durationMs / 1000);

        switch (sound)
        {
            case BeepSound.DoubleBeep:
                int beepMs = durationMs * 2 / 5;
                AppendTone(pcm, frequencyHz, beepMs, amplitude, decay: false);
                AppendSilence(pcm, durationMs - 2 * beepMs);
                AppendTone(pcm, frequencyHz, beepMs, amplitude, decay: false);
                break;

            case BeepSound.Chime:
                // Rising perfect fifth with a bell-like decay on each note.
                AppendTone(pcm, frequencyHz, durationMs / 2, amplitude, decay: true);
                AppendTone(pcm, frequencyHz * 1.5, durationMs - durationMs / 2, amplitude, decay: true);
                break;

            default:
                AppendTone(pcm, frequencyHz, durationMs, amplitude, decay: false);
                break;
        }

        return ToWav(pcm.ToArray());
    }

    private static void AppendTone(List<short> pcm, double frequencyHz, int durationMs, double amplitude, bool decay)
    {
        int total = Math.Max(1, SampleRate * durationMs / 1000);

        // ~10 ms linear attack/release so the tone doesn't click.
        int fade = Math.Min(total / 2, SampleRate * 10 / 1000);

        for (int i = 0; i < total; i++)
        {
            double envelope = 1.0;
            if (i < fade)
            {
                envelope = (double)i / fade;
            }
            else if (i >= total - fade)
            {
                envelope = (double)(total - i) / fade;
            }

            if (decay)
            {
                envelope *= Math.Exp(-4.0 * i / total);
            }

            double sample = Math.Sin(2.0 * Math.PI * frequencyHz * i / SampleRate) * amplitude * envelope;
            pcm.Add((short)(sample * short.MaxValue));
        }
    }

    private static void AppendSilence(List<short> pcm, int durationMs)
    {
        int total = Math.Max(0, SampleRate * durationMs / 1000);
        for (int i = 0; i < total; i++)
        {
            pcm.Add(0);
        }
    }

    private static byte[] ToWav(short[] pcm)
    {
        int totalSamples = pcm.Length;
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
