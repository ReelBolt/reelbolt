using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary><see cref="MusicBeatTracker"/> against synthetic variable-tempo click tracks.</summary>
public class MusicBeatTrackerTests
{
    internal const int Rate = 16000;
    private readonly ITestOutputHelper _output;

    public MusicBeatTrackerTests(ITestOutputHelper output) => _output = output;

    /// <summary>Beat times from a tempo function (BPM at time t), starting at <paramref name="phase"/>.</summary>
    internal static List<double> BeatsFromTempo(Func<double, double> bpmAt, double phase, double durationSec)
    {
        var beats = new List<double>();
        for (double t = phase; t < durationSec - 0.1; t += 60.0 / bpmAt(t))
            beats.Add(t);
        return beats;
    }

    /// <summary>The ClickTrack burst shape on every beat (accent on every 4th), optional off-beat hats at <paramref name="hatFraction"/> of the beat.</summary>
    internal static float[] Render(
        IReadOnlyList<double> beats, double durationSec, double hatFraction = 0, double hatLevel = 0.5,
        Func<int, double>? beatLevel = null, int seed = 7)
    {
        var rng = new Random(seed);
        var samples = new float[(int)(durationSec * Rate)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (float)((rng.NextDouble() - 0.5) * 0.002);

        for (int n = 0; n < beats.Count; n++)
        {
            double level = beatLevel?.Invoke(n) ?? (n % 4 == 0 ? 0.15 * 1.3 : 0.15);
            Burst(samples, beats[n], level, 0.012, 1000);
            if (hatFraction > 0 && n + 1 < beats.Count)
                Burst(samples, beats[n] + hatFraction * (beats[n + 1] - beats[n]), level * hatLevel, 0.004, 4000);
        }

        return samples;
    }

    private static void Burst(float[] samples, double t, double level, double decaySec, double hz)
    {
        int start = (int)(t * Rate);
        int length = (int)(0.06 * Rate);
        for (int s = 0; s < length && start + s < samples.Length; s++)
        {
            double env = Math.Exp(-s / (decaySec * Rate));
            samples[start + s] += (float)(level * env * Math.Sin(2 * Math.PI * hz * s / Rate));
        }
    }

    /// <summary>How many reference beats have a tracked beat within <paramref name="tolSec"/>.</summary>
    internal static int Matched(IReadOnlyList<double> reference, IReadOnlyList<double> tracked, double tolSec)
    {
        int count = 0;
        foreach (double r in reference)
        {
            if (tracked.Any(t => Math.Abs(t - r) <= tolSec))
                count++;
        }

        return count;
    }

    private static double Fraction(IReadOnlyList<double> reference, IReadOnlyList<double> tracked, double tolSec) =>
        reference.Count == 0 ? 0 : Matched(reference, tracked, tolSec) / (double)reference.Count;

    [Theory]
    [InlineData(70.0, 0.1)]
    [InlineData(95.0, 0.4)]
    [InlineData(120.0, 0.25)]
    [InlineData(134.25, 0.115)]
    [InlineData(150.0, 0.33)]
    [InlineData(172.0, 0.05)]
    [InlineData(180.0, 0.2)]
    public void Steady_click_tracks_are_tracked_exactly(double bpm, double phase)
    {
        List<double> reference = BeatsFromTempo(_ => bpm, phase, 40);
        MusicBeatTrack? track = MusicBeatTracker.Track(Render(reference, 40), Rate);

        track.Should().NotBeNull();
        _output.WriteLine($"bpm={track!.Bpm} matched={Fraction(reference, track.BeatTimesSec, 0.05):P1} downbeat={track.FirstDownbeatIndex}");
        Fraction(reference, track.BeatTimesSec, 0.05).Should().BeGreaterThanOrEqualTo(0.98);
        track.Bpm.Should().BeApproximately(bpm, 0.5);
        track.Steady.Should().BeTrue();
        // Beat 0 is the accented downbeat; the first tracked beat may lie a few beats later.
        int index = track.BeatTimesSec.Select((t, i) => (t, i)).First(p => Math.Abs(p.t - reference[0]) < 0.05 || p.t > reference[0]).i;
        int refIndexOfFirst = reference.FindIndex(r => Math.Abs(r - track.BeatTimesSec[0]) < 0.05);
        refIndexOfFirst.Should().BeGreaterThanOrEqualTo(0);
        ((refIndexOfFirst + track.FirstDownbeatIndex) % 4).Should().Be(0);
        index.Should().BeGreaterThanOrEqualTo(0);
        track.BarTimesSec.Should().NotBeEmpty();
        track.BarTimesSec[0].Should().Be(track.BeatTimesSec[track.FirstDownbeatIndex]);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.13)]
    [InlineData(0.31)]
    public void Steady_phase_does_not_matter(double phase)
    {
        List<double> reference = BeatsFromTempo(_ => 110, phase, 30);
        MusicBeatTrack track = MusicBeatTracker.Track(Render(reference, 30), Rate)!;
        Fraction(reference, track.BeatTimesSec, 0.05).Should().BeGreaterThanOrEqualTo(0.98);
        double beat = 60 / 110.0;
        double err = Math.Abs(track.BeatPhaseSec - phase) % beat;
        Math.Min(err, beat - err).Should().BeLessThan(0.03);
    }

    [Fact]
    public void A_linear_tempo_ramp_is_followed_and_the_fixed_grid_is_not()
    {
        List<double> reference = BeatsFromTempo(t => 96 + 32 * t / 90, 0.2, 90);
        float[] samples = Render(reference, 90);
        MusicBeatTrack track = MusicBeatTracker.Track(samples, Rate)!;

        double tracked = Fraction(reference, track.BeatTimesSec, 0.07);
        MusicBeatAnalysis? old = MusicBeatAnalyzer.Analyze(samples, Rate);
        double fixedGrid = old is null ? 0 : Fraction(reference, old.BeatTimes(90), 0.07);
        _output.WriteLine($"ramp: tracked={tracked:P1} fixedGrid={fixedGrid:P1} bpm={track.Bpm} steady={track.Steady}");
        tracked.Should().BeGreaterThanOrEqualTo(0.9);
        fixedGrid.Should().BeLessThan(0.7);
        track.Steady.Should().BeFalse();
    }

    [Fact]
    public void A_tempo_step_is_followed_and_the_fixed_grid_is_not()
    {
        List<double> reference = BeatsFromTempo(t => t < 40 ? 100 : 125, 0.3, 80);
        float[] samples = Render(reference, 80);
        MusicBeatTrack track = MusicBeatTracker.Track(samples, Rate)!;

        double tracked = Fraction(reference, track.BeatTimesSec, 0.07);
        MusicBeatAnalysis? old = MusicBeatAnalyzer.Analyze(samples, Rate);
        double fixedGrid = old is null ? 0 : Fraction(reference, old.BeatTimes(80), 0.07);
        _output.WriteLine($"step: tracked={tracked:P1} fixedGrid={fixedGrid:P1} bpm={track.Bpm}");
        tracked.Should().BeGreaterThanOrEqualTo(0.9);
        fixedGrid.Should().BeLessThan(0.7);
    }

    [Fact]
    public void Live_style_jitter_is_tracked()
    {
        var rng = new Random(11);
        List<double> reference = BeatsFromTempo(_ => 118, 0.2, 60).Select(t => t + (rng.NextDouble() * 2 - 1) * 0.02).ToList();
        MusicBeatTrack track = MusicBeatTracker.Track(Render(reference, 60), Rate)!;

        _output.WriteLine($"jitter: bpm={track.Bpm} matched={Fraction(reference, track.BeatTimesSec, 0.07):P1}");
        Fraction(reference, track.BeatTimesSec, 0.07).Should().BeGreaterThanOrEqualTo(0.9);
        track.Bpm.Should().BeApproximately(118, 3);
    }

    [Fact]
    public void Swing_off_beat_hats_do_not_cause_an_octave_error()
    {
        List<double> reference = BeatsFromTempo(_ => 104, 0.15, 50);
        MusicBeatTrack track = MusicBeatTracker.Track(Render(reference, 50, hatFraction: 2.0 / 3, hatLevel: 0.6), Rate)!;

        _output.WriteLine($"swing: bpm={track.Bpm} matched={Fraction(reference, track.BeatTimesSec, 0.07):P1}");
        Fraction(reference, track.BeatTimesSec, 0.07).Should().BeGreaterThanOrEqualTo(0.9);
        track.Bpm.Should().BeApproximately(104, 3);
    }

    [Fact]
    public void Half_time_ambiguity_reports_one_octave_consistently_and_exposes_the_other()
    {
        // 75 BPM kick/snare with eighth-note hats: the onsets are also a 150 BPM pulse.
        List<double> reference = BeatsFromTempo(_ => 75, 0.2, 60);
        float[] samples = Render(reference, 60, hatFraction: 0.5, hatLevel: 0.5, beatLevel: n => n % 2 == 0 ? 0.2 : 0.14);
        MusicBeatTrack a = MusicBeatTracker.Track(samples, Rate)!;
        MusicBeatTrack b = MusicBeatTracker.Track(samples, Rate)!;

        _output.WriteLine($"half-time: bpm={a.Bpm} half={a.HalfTempoScore} double={a.DoubleTempoScore}");
        a.Bpm.Should().Be(b.Bpm);
        (Math.Abs(a.Bpm - 75) <= 1.5 || Math.Abs(a.Bpm - 150) <= 3).Should().BeTrue($"reported {a.Bpm}");
        // The octave that was not reported is exposed with real support.
        double other = Math.Abs(a.Bpm - 75) <= 1.5 ? a.DoubleTempoScore : a.HalfTempoScore;
        other.Should().BeGreaterThan(0.3);
        a.HalfTempoScore.Should().BeInRange(0, 1);
        a.DoubleTempoScore.Should().BeInRange(0, 1);
    }

    [Fact]
    public void Noise_silence_and_short_tracks_give_null_without_throwing()
    {
        var rng = new Random(3);
        var noise = new float[Rate * 30];
        for (int i = 0; i < noise.Length; i++)
            noise[i] = (float)((rng.NextDouble() - 0.5) * 0.4);

        MusicBeatTracker.Track(noise, Rate).Should().BeNull();
        MusicBeatTracker.Track(new float[Rate * 20], Rate).Should().BeNull();
        MusicBeatTracker.Track(Render(BeatsFromTempo(_ => 120, 0, 2), 2), Rate).Should().BeNull();
        MusicBeatTracker.Track([], Rate).Should().BeNull();
        MusicBeatTracker.Track(noise, 0).Should().BeNull();
    }

    [Fact]
    public void Tracking_is_deterministic()
    {
        List<double> reference = BeatsFromTempo(t => 100 + t / 3, 0.1, 30);
        float[] samples = Render(reference, 30);
        MusicBeatTrack a = MusicBeatTracker.Track(samples, Rate)!;
        MusicBeatTrack b = MusicBeatTracker.Track(samples, Rate)!;
        a.BeatTimesSec.Should().Equal(b.BeatTimesSec);
        a.Should().BeEquivalentTo(b);
    }

    [Fact]
    public void Drop_lands_on_a_tracked_downbeat()
    {
        const double bpm = 128, phase = 0.2;
        double beat = 60 / bpm;
        double drop = phase + 32 * beat;
        List<double> reference = BeatsFromTempo(_ => bpm, phase, 60);
        MusicBeatTrack track = MusicBeatTracker.Track(
            Render(reference, 60, beatLevel: n => reference[n] >= drop - 1e-6 ? 0.6 : 0.15), Rate)!;

        track.DropSec.Should().NotBeNull();
        track.DropSec!.Value.Should().BeApproximately(drop, beat * 0.6);
        track.BeatTimesSec.Should().Contain(track.DropSec.Value);
        track.BarTimesSec.Should().Contain(track.DropSec.Value);
        track.TempoWord.Should().Be("fast");
    }

    [Fact]
    public void Ten_minutes_track_in_reasonable_time()
    {
        List<double> reference = BeatsFromTempo(t => 110 + 20 * Math.Sin(t / 60), 0.2, 600);
        float[] samples = Render(reference, 600);
        var sw = Stopwatch.StartNew();
        MusicBeatTrack? track = MusicBeatTracker.Track(samples, Rate);
        sw.Stop();

        _output.WriteLine($"10 min tracked in {sw.ElapsedMilliseconds} ms");
        track.Should().NotBeNull();
        sw.Elapsed.TotalSeconds.Should().BeLessThan(10);
    }

    [Fact]
    public void A_sustained_drone_is_not_a_beat()
    {
        float[] pad = Enumerable.Range(0, Rate * 30).Select(i => (float)(0.2 * Math.Sin(2 * Math.PI * 220 * i / Rate)
            + 0.1 * Math.Sin(2 * Math.PI * 330 * i / Rate) + 0.001 * Math.Sin(i * 0.37))).ToArray();
        MusicBeatTracker.Track(pad, Rate).Should().BeNull();
    }

    [Fact]
    public void One_isolated_transient_in_silence_is_not_a_beat()
    {
        var one = new float[Rate * 20];
        for (int i = 0; i < 200; i++)
            one[Rate * 5 + i] = (float)(0.9 * Math.Exp(-i / 40.0) * Math.Sin(i * 0.9));
        MusicBeatTracker.Track(one, Rate).Should().BeNull();
    }

    [Fact]
    public void Silence_before_and_after_the_music_holds_no_beats()
    {
        List<double> beats = BeatsFromTempo(_ => 120, 0.1, 30).Select(b => b + 15).ToList();
        MusicBeatTrack? track = MusicBeatTracker.Track(Render(beats, 60), Rate);
        track.Should().NotBeNull();
        _output.WriteLine($"first={track!.BeatTimesSec[0]} last={track.BeatTimesSec[^1]} n={track.BeatTimesSec.Count} dur={track.DurationSec}");
        track.BeatTimesSec[0].Should().BeGreaterThan(14.0);
        track.BeatTimesSec[^1].Should().BeLessThan(45.6);
        track.DurationSec.Should().BeApproximately(60, 0.01);
        track.FirstDownbeatIndex.Should().BeInRange(0, 3).And.BeLessThan(track.BeatTimesSec.Count);
        if (track.DropSec is { } d) track.BeatTimesSec.Should().Contain(d);
        if (track.OutroSec is { } o) track.BeatTimesSec.Should().Contain(o);
    }
}
