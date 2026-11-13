using System;
using System.IO;
using System.Media;
using AutoZamena.Core.Models;

namespace AutoZamena.App.UI;

/// <summary>
/// Synthesizes two ultra-short, unobtrusive 20ms soft cosine-enveloped clicks in memory
/// (higher pitch for Russian, lower pitch for English — Caramba / Punto style audio cue without loud OS chimes).
/// </summary>
internal static class SoftSwitchSoundPlayer
{
    private static readonly Lazy<SoundPlayer> RuTick = new(() => CreateTonePlayer(frequencyHz: 780, durationMs: 22, amplitude: 0.14));
    private static readonly Lazy<SoundPlayer> EnTick = new(() => CreateTonePlayer(frequencyHz: 560, durationMs: 22, amplitude: 0.14));

    public static void PlayForLanguage(TargetLanguage target)
    {
        try
        {
            if (target == TargetLanguage.English)
            {
                EnTick.Value.Play();
            }
            else
            {
                RuTick.Value.Play();
            }
        }
        catch
        {
            // Ignore machines without audio output device
        }
    }

    private static SoundPlayer CreateTonePlayer(double frequencyHz, int durationMs, double amplitude)
    {
        const int sampleRate = 44100;
        int sampleCount = (sampleRate * durationMs) / 1000;
        int dataBytes = sampleCount * 2; // 16-bit mono PCM

        var ms = new MemoryStream(44 + dataBytes);
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            bw.Write("RIFF"u8);
            bw.Write(36 + dataBytes);
            bw.Write("WAVE"u8);
            bw.Write("fmt "u8);
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)1); // Mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write("data"u8);
            bw.Write(dataBytes);

            for (int i = 0; i < sampleCount; i++)
            {
                // Hann window envelope so there is zero speaker pop at start/end
                double env = 0.5 * (1.0 - Math.Cos((2.0 * Math.PI * i) / (sampleCount - 1)));
                double t = (double)i / sampleRate;
                double sample = Math.Sin(2.0 * Math.PI * frequencyHz * t) * amplitude * env;
                short pcm = (short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue);
                bw.Write(pcm);
            }
        }

        ms.Position = 0;
        var player = new SoundPlayer(ms);
        player.Load();
        return player;
    }
}
