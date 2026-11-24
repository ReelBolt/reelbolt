using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Moderation;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// The packet draw of the sampler on the constructions an attacker can pad with: how often the one red packet is judged
/// over 400 draws. The table is printed so a reviewer can read it.
/// </summary>
public sealed class ModerationMediaSamplerPickTests(ITestOutputHelper output)
{
    private const int Draws = 400;

    private static FfmpegModerationMediaSampler.PacketList Build(IEnumerable<double> times, double lastDuration)
    {
        StringBuilder csv = new();
        foreach (double t in times)
            csv.Append(t.ToString("0.######", CultureInfo.InvariantCulture)).Append(",,").Append(lastDuration.ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
        return FfmpegModerationMediaSampler.ParsePackets(csv.ToString());
    }

    /// <summary>The share of 400 draws that judge at least one packet in [from, to).</summary>
    private static double Reach(FfmpegModerationMediaSampler.PacketList packets, int from, int to)
    {
        int reached = 0;
        for (int seed = 0; seed < Draws; seed++)
        {
            IReadOnlyList<int> picked = FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(seed));
            picked.Should().OnlyHaveUniqueItems().And.HaveCountLessThanOrEqualTo(20);
            if (picked.Any(i => i >= from && i < to)) reached++;
        }

        return reached / (double)Draws;
    }

    private static FfmpegModerationMediaSampler.PacketList RedHold()
    {
        // 10,000 blue packets in 10 s, one red still of 10.8 h, then 73 blue stills of 59 s: 43,197 s, under the limit.
        List<double> times = Enumerable.Range(0, 10000).Select(i => i * 0.001).ToList();
        times.Add(10);
        for (int i = 0; i < 73; i++) times.Add(10 + 38880 + (i * 59.0));
        times.Add(10 + 38880 + (73 * 59.0));
        return Build(times, 0.001);
    }

    [Fact]
    public void The_simulation_table_every_padding_is_judged_the_closing_or_the_long_hold()
    {
        List<(string Name, double Reached)> table = [];

        FfmpegModerationMediaSampler.PacketList redhold = RedHold();
        table.Add(("redhold (10,000 tiny, red 10.8 h, 73 stills of 59 s)", Reach(redhold, 10000, 10001)));

        // tail: 300 blue frames, one red frame, 10 s of video under 1 h of audio. The last packet counts to the audio's end.
        List<double> tail = Enumerable.Range(0, 300).Select(i => i / 30.0).Append(10).ToList();
        table.Add(("tail (300 frames + red, 1 h audio)", Reach(Build(tail, 1 / 30.0).ExtendedTo(3600), 300, 301)));

        List<double> tailtiny = Enumerable.Range(0, 10000).Select(i => i * 0.001).Append(10).ToList();
        table.Add(("tailtiny (10,000 packets + red, 1 h audio)", Reach(Build(tailtiny, 0.001).ExtendedTo(3600), 10000, 10001)));

        // pos10k: 10,000 blue packets in 10 s, then 25 red frames of 59 s each.
        List<double> pos = Enumerable.Range(0, 10000).Select(i => i * 0.001).ToList();
        for (int i = 0; i < 25; i++) pos.Add(10 + (i * 59.0));
        pos.Add(10 + (25 * 59.0));
        table.Add(("pos10k (10,000 tiny, 25 red stills of 59 s)", Reach(Build(pos, 0.001), 10000, 10025)));

        // gap: 60 s at 30 fps, the last frame an hour later.
        List<double> gap = Enumerable.Range(0, 1800).Select(i => i / 30.0).Append(3660).ToList();
        table.Add(("gap (60 s, last frame an hour later)", Reach(Build(gap, 1 / 30.0), 1799, 1800)));

        // hold59: 100 s of 25 fps, a 59 s red still, then 100 s again, then a closing frame.
        List<double> hold59 = Enumerable.Range(0, 2500).Select(i => i / 25.0).ToList();
        hold59.Add(100);
        hold59.AddRange(Enumerable.Range(0, 2500).Select(i => 159 + (i / 25.0)));
        hold59.Add(259);
        table.Add(("hold59 (59 s red still among 200 s)", Reach(Build(hold59, 0.04), 2500, 2501)));

        // m12h: 11 h 40 min of blue stills of 59 s, then 25 red stills of 59 s (25 minutes): reached in proportion to its time.
        List<double> m12h = Enumerable.Range(0, 725).Select(i => i * 59.0).ToList();
        m12h.Add(725 * 59.0);
        table.Add(("m12h (700 blue stills of 59 s + 25 red ones)", Reach(Build(m12h, 59), 700, 725)));

        // Ten red stills of one day cannot be padded: the length is over 12 h, a final refusal (the draw is still made).
        FfmpegModerationMediaSampler.PacketList h1d = Build(new double[] { 0, 1, 2, 3, 86400 + 3 }, 0.04);
        h1d.Length.Should().BeGreaterThan(FfmpegModerationMediaSampler.MaxJudgedVideoSeconds);
        FfmpegModerationMediaSampler.PacketList m1k = Build(Enumerable.Range(0, 1000).Select(i => i * 60.0), 60);
        m1k.Length.Should().BeGreaterThan(FfmpegModerationMediaSampler.MaxJudgedVideoSeconds);

        foreach ((string name, double reached) in table)
            output.WriteLine($"{name}: reached {reached:P1}");

        table[0].Reached.Should().Be(1, "the 10.8 h red still is a hold: always judged");
        table[1].Reached.Should().Be(1, "the last frame counts up to the end of the audio: a hold");
        table[2].Reached.Should().Be(1);
        table[3].Reached.Should().BeGreaterThan(0.99, "the red stills fill 99% of the time that plays");
        table[4].Reached.Should().Be(1);
        table[5].Reached.Should().BeGreaterThan(0.9, "37% of the time, ten time draws");
        table[6].Reached.Should().BeGreaterThan(0.15, "25 minutes of 12 hours, in proportion to its time and its packets: a share, not a guarantee");
    }

    [Fact]
    public void Holds_are_judged_up_to_half_the_budget_longest_first_and_the_rest_stay_in_the_slices()
    {
        // 15 stills of 61 s to 75 s among 2,000 tiny packets: the ten longest are held, the other five compete in the slices.
        List<double> times = Enumerable.Range(0, 2000).Select(i => i * 0.01).ToList();
        double t = 20;
        for (int i = 0; i < 15; i++) { times.Add(t); t += 61 + i; }
        times.Add(t);
        FfmpegModerationMediaSampler.PacketList packets = Build(times, 0.01);

        IReadOnlyList<int> picked = FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(3));

        picked.Should().HaveCount(20);
        int[] longest = Enumerable.Range(2000 + 5, 10).ToArray();
        picked.Should().Contain(longest, "the ten longest holds are always judged");
    }

    [Fact]
    public void Packets_before_the_first_shown_time_are_not_drawn()
    {
        FfmpegModerationMediaSampler.PacketList packets = Build(Enumerable.Range(0, 400).Select(i => (i - 50) * 0.04), 0.04);
        for (int seed = 0; seed < 100; seed++)
            FfmpegModerationMediaSampler.PickPackets(packets, 2, 20, new Random(seed), firstDrawable: 50).Should().OnlyContain(i => i >= 50);
    }

    [Fact]
    public void A_last_frame_counts_up_to_the_end_of_the_longest_stream_and_never_less_than_its_own_length()
    {
        FfmpegModerationMediaSampler.PacketList packets = Build([0, 1, 2], 1);
        packets.ExtendedTo(3600).Length.Should().BeApproximately(3600, 0.01);
        packets.ExtendedTo(2.5).Length.Should().Be(packets.Length, "the end of the video itself is not extended");
        Build(Enumerable.Range(0, 30).Select(i => i * 1.0), 1).ExtendedTo(13 * 3600).Length
            .Should().BeGreaterThan(FfmpegModerationMediaSampler.MaxJudgedVideoSeconds, "the 12 h limit is the longest stream's");
    }
}
