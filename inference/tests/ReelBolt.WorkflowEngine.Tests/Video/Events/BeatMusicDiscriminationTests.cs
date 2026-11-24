using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>Beats must only come from music: generated signals through the real PCM detector, no fixtures.</summary>
public sealed class BeatMusicDiscriminationTests
{
    private const int Rate = SpectralFluxBeatDetector.SampleRate;

    /// <summary>Syllable-rate (3.8..5.4 Hz) amplitude-modulated band noise plus a voiced hum, phrases of 1..2.5 s with 0.2..0.7 s pauses; optional plosive bursts.</summary>
    private static float[] Speech(double seconds, int seed, bool plosives)
    {
        Random rnd = new(seed);
        float[] x = new float[(int)(seconds * Rate)];
        int i = 0;
        while (i < x.Length)
        {
            int length = (int)((1.0 + rnd.NextDouble() * 1.5) * Rate);
            double syllableRate = 3.8 + rnd.NextDouble() * 1.6, f0 = 110 + rnd.NextDouble() * 90;
            double lp = 0;
            for (int k = 0; k < length && i + k < x.Length; k++)
            {
                double t = (double)k / Rate;
                lp += 0.45 * ((rnd.NextDouble() * 2 - 1) - lp);
                double syllable = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * syllableRate * t)), 0.7);
                double hum = 0.5 * Math.Sin(2 * Math.PI * f0 * t) + 0.25 * Math.Sin(4 * Math.PI * f0 * t);
                x[i + k] = (float)((1.4 * lp + 0.35 * hum) * (0.15 + 0.85 * syllable) * 0.3);
            }

            if (plosives)
            {
                for (int p = 0; p < length; p += (int)((0.3 + rnd.NextDouble() * 0.2) * Rate))
                {
                    int burst = (int)(0.015 * Rate);
                    for (int k = 0; k < burst && i + p + k < x.Length; k++)
                        x[i + p + k] += (float)((rnd.NextDouble() * 2 - 1) * 0.8 * Math.Sin(Math.PI * k / burst));
                }
            }

            i += length + (int)((0.2 + rnd.NextDouble() * 0.5) * Rate);
        }

        return x;
    }

    /// <summary>A kick (decaying pitch-dropped sine) on every beat from 0.2 s, plus a constant three-note pad.</summary>
    private static float[] Music(double seconds, double bpm, out double[] kickTimes)
    {
        float[] x = new float[(int)(seconds * Rate)];
        List<double> kicks = [];
        for (double beat = 0.2; beat < seconds - 0.25; beat += 60.0 / bpm)
        {
            kicks.Add(beat);
            int start = (int)(beat * Rate);
            for (int k = 0; k < Rate / 5; k++)
            {
                double t = (double)k / Rate;
                x[start + k] += (float)(0.8 * Math.Sin(2 * Math.PI * (50 * t + 35 * (1 - Math.Exp(-30 * t)) / 30)) * Math.Exp(-18 * t));
            }
        }

        for (int i = 0; i < x.Length; i++)
        {
            double t = (double)i / Rate;
            x[i] += (float)(0.08 * (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277.2 * t) + Math.Sin(2 * Math.PI * 329.6 * t)) * (0.8 + 0.2 * Math.Sin(2 * Math.PI * 0.25 * t)));
        }

        kickTimes = [.. kicks];
        return x;
    }

    private static IReadOnlyList<(double TimeSec, double Strength, double Pulse)> Detect(float[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (short)(Math.Clamp(samples[i], -1f, 1f) * 32767));
        using MemoryStream pcm = new(bytes);
        return SpectralFluxBeatDetector.Detect(pcm);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void Speech_only_has_no_beats(int seed, bool plosives)
    {
        Detect(Speech(60, seed, plosives)).Should().BeEmpty();
    }

    [Fact]
    public void Perfectly_periodic_syllable_phrases_with_pauses_are_not_music()
    {
        // Three-second phrases of a strictly periodic 4 Hz syllable modulation, 2.5 s of dead air between them.
        float[] x = new float[40 * Rate];
        Random rnd = new(21);
        for (double phrase = 0.5; phrase < 36; phrase += 5.5)
        {
            for (int k = 0; k < 3 * Rate; k++)
            {
                double t = (double)k / Rate;
                x[(int)(phrase * Rate) + k] = (float)((rnd.NextDouble() * 2 - 1) * 0.3 * (0.15 + 0.85 * Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 4 * t)), 0.7)));
            }
        }

        Detect(x).Should().BeEmpty();
    }

    [Fact]
    public void Silence_and_quiet_noise_have_no_beats()
    {
        Detect(new float[60 * Rate]).Should().BeEmpty();
        Random rnd = new(5);
        Detect(Enumerable.Range(0, 30 * Rate).Select(_ => (float)((rnd.NextDouble() * 2 - 1) * 0.1)).ToArray()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(120)]
    [InlineData(100)]
    public void A_kick_and_pad_track_yields_beats_on_the_kicks(double bpm)
    {
        float[] music = Music(30, bpm, out double[] kicks);
        var beats = Detect(music);

        // Onset time is the analysis window centre (256 samples after the transient starts), so allow 2 frames (20 ms) around the kick plus that offset.
        int hit = kicks.Count(k => beats.Any(b => Math.Abs(b.TimeSec - 0.016 - k) <= 0.06));
        hit.Should().BeGreaterThanOrEqualTo((int)(kicks.Length * 0.8), $"kicks recalled out of {kicks.Length}");
        beats.Should().OnlyContain(b => b.Pulse >= 0.28);
    }

    [Fact]
    public void Music_with_speech_on_top_keeps_its_beats()
    {
        float[] music = Music(30, 128, out double[] kicks);
        float[] speech = Speech(30, 9, false);
        float[] mix = music.Select((m, i) => m + speech[i] * 0.8f).ToArray();

        var beats = Detect(mix);

        int hit = kicks.Count(k => beats.Any(b => Math.Abs(b.TimeSec - 0.016 - k) <= 0.06));
        hit.Should().BeGreaterThanOrEqualTo((int)(kicks.Length * 0.3), $"kicks recalled out of {kicks.Length} (speech masks some kicks in the peak picker)");
    }

    [Fact]
    public void Speech_then_music_only_beats_in_the_music_half()
    {
        float[] speech = Speech(30, 6, true);
        float[] music = Music(30, 120, out _);

        var beats = Detect([.. speech, .. music]);

        beats.Should().NotBeEmpty();
        beats.Count(b => b.TimeSec < 25).Should().Be(0, "no beats inside the speech-only half (a few seconds of window bleed allowed)");
    }

    [Fact]
    public void Beat_confidence_rises_with_the_pulse_and_stays_in_range()
    {
        VideoEventDetector.BeatConfidence(0.28).Should().BeInRange(0.55, 0.65);
        VideoEventDetector.BeatConfidence(1.0).Should().BeApproximately(0.9, 1e-9);
        VideoEventDetector.BeatConfidence(0.9).Should().BeGreaterThan(VideoEventDetector.BeatConfidence(0.4));
    }
}
