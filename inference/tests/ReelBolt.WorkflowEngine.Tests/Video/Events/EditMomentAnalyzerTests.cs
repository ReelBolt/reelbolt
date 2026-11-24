using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using ReelBolt.WorkflowEngine.Tests.Quality;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// Generated 160x90 frames of an edited video with exactly known moments: cuts, a montage, flashes, pop-ins, a grow-in,
/// a slide, running caption words, a title, a stamp over footage that holds frames, a panel cut and an eased move of
/// the whole picture. Pure in memory: no ffmpeg, no file, no model.
/// </summary>
public class EditMomentAnalyzerTests
{
    private const int W = FrameSignalSampler.Width, H = FrameSignalSampler.Height;

    internal static byte[] Flat(byte v) => Enumerable.Repeat(v, W * H).ToArray();

    /// <summary>A patch with a fine checker pattern that travels with the patch: full of edges, like a photo or a logo.</summary>
    internal static void Textured(byte[] f, int x0, int y0, int w, int h, byte dark = 30, byte light = 200)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + w); x++)
                f[y * W + x] = ((x - x0) / 2 + (y - y0) / 2) % 2 == 0 ? dark : light;
    }

    internal static void Solid(byte[] f, int x0, int y0, int w, int h, byte v)
    {
        for (int y = Math.Max(0, y0); y < Math.Min(H, y0 + h); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(W, x0 + w); x++)
                f[y * W + x] = v;
    }

    /// <summary>Footage inside a panel: grain that changes only when the footage shows a new frame, plus a blob that drifts.</summary>
    internal static void Footage(byte[] f, int x0, int y0, int w, int h, int footageFrame)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                uint n = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(footageFrame * 83492791);
                n = n * 1664525u + 1013904223u;
                f[y * W + x] = (byte)(90 + (x * 3 + y * 5) % 40 + (int)(n >> 28));
            }
        }

        int bx = x0 + 10 + footageFrame * 2 % Math.Max(1, w - 30);
        Solid(f, bx, y0 + h / 2, 12, 8, 210);
    }

    internal static List<VideoEvent> Run(int frames, Func<int, byte[]> make, double fps = 60, int width = 1920, int height = 1080)
    {
        EditMomentAnalyzer analyzer = new(fps, width, height);
        for (int i = 0; i < frames; i++)
            analyzer.OnFrame(i, make(i));
        return analyzer.Complete();
    }

    private static bool Worthy(VideoEvent e) => SfxWorthyEvents.IsWorthy(
        new VideoAnalysisRenderEvent("e0", e.Type.ToString(), e.Type.ToString(), e.Frame, e.Frame / 60.0, 0, Type: e.Type.ToString(), Strength: e.Strength));

    private static string Dump(IEnumerable<VideoEvent> events) =>
        string.Join(", ", events.Select(e => $"{e.Type}@{e.Frame}{(e.EndFrame is { } end ? "-" + end : "")}({e.Strength:F2}{(e.Direction is null ? "" : " " + e.Direction)})"));

    [Fact]
    public void A_hard_cut_is_a_Transition_on_its_exact_frame()
    {
        List<VideoEvent> events = Run(120, i => Flat(i < 45 ? (byte)40 : (byte)200));

        events.Should().ContainSingle(e => e.Type == VideoEventType.Transition, Dump(events));
        events.Single(e => e.Type == VideoEventType.Transition).Frame.Should().Be(45);
        events.Should().NotContain(e => e.Type == VideoEventType.Flash);
    }

    [Fact]
    public void A_picture_that_changes_every_third_frame_has_no_cuts()
    {
        List<VideoEvent> events = Run(180, i => Flat((byte)(60 + (i / 3 % 2) * 14)));

        events.Should().NotContain(e => e.Type == VideoEventType.Transition, Dump(events));
    }

    [Fact]
    public void Every_cut_of_a_fast_montage_is_found()
    {
        // a new still every 10 frames, like a run of highlighted pages
        byte[] levels = [40, 200, 90, 240, 20, 150, 70, 220, 110, 30, 180, 60];
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(levels[i / 10 % levels.Length]);
            Textured(f, 20 + i / 10 % 5 * 10, 20, 60, 40);
            return f;
        });

        int[] cuts = [.. events.Where(e => e.Type == VideoEventType.Transition).Select(e => e.Frame)];
        cuts.Should().BeEquivalentTo(Enumerable.Range(1, 23).Select(k => k * 10), Dump(events));
    }

    [Fact]
    public void A_few_bright_frames_that_fall_back_are_a_Flash_not_a_cut()
    {
        List<VideoEvent> events = Run(120, i => Flat(i is >= 50 and <= 52 ? (byte)170 : (byte)60));

        events.Should().Contain(e => e.Type == VideoEventType.Flash && e.Frame == 50, Dump(events));
        events.Should().NotContain(e => e.Type == VideoEventType.Transition);
    }

    [Fact]
    public void A_bright_shot_between_two_other_shots_is_two_cuts_not_a_flash()
    {
        // six frames of a bright page inside a montage: it comes back to a different picture, so nothing flashed
        List<VideoEvent> events = Run(180, i =>
        {
            byte[] f = Flat(i < 60 ? (byte)50 : i < 66 ? (byte)230 : (byte)70);
            if (i < 60)
                Textured(f, 10, 10, 60, 40);
            else if (i >= 66)
                Textured(f, 90, 40, 60, 40);
            return f;
        });

        events.Where(e => e.Type == VideoEventType.Transition).Select(e => e.Frame).Should().BeEquivalentTo([60, 66], Dump(events));
        events.Should().NotContain(e => e.Type == VideoEventType.Flash);
    }

    [Fact]
    public void A_cut_between_two_alike_pages_is_found_when_nothing_else_moves()
    {
        // the same pale page with the text block in another place: the mean change is small, the cut is real
        List<VideoEvent> events = Run(180, i =>
        {
            byte[] f = Flat(225);
            Textured(f, i < 90 ? 10 : 12, i < 90 ? 10 : 13, 140, 70, 205, 225);
            return f;
        });

        events.Should().ContainSingle(e => e.Type == VideoEventType.Transition, Dump(events)).Which.Frame.Should().Be(90);
    }

    [Fact]
    public void A_white_page_that_flickers_around_the_bright_level_is_not_a_flash()
    {
        // near-white paper: encoder noise moves many pixels across the level by one grey level, back and forth
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(234);
            if (i % 9 is 4 or 5)
                Solid(f, 60, 30, 40, 20, 235);
            return f;
        });

        events.Should().NotContain(e => e.Type == VideoEventType.Flash, Dump(events));
    }

    [Fact]
    public void Something_bright_that_only_passes_through_is_not_a_flash()
    {
        // a white bubble drops in fast from the top: bright pixels arrive in one part of the frame and move on to the next
        List<VideoEvent> events = Run(180, i =>
        {
            byte[] f = Flat(60);
            if (i >= 60)
                Solid(f, 20, Math.Min(60, -20 + (i - 60) * 12), 14, 14, 250);
            return f;
        });

        events.Should().NotContain(e => e.Type == VideoEventType.Flash, Dump(events));
    }

    [Fact]
    public void A_small_bright_spark_in_a_dark_picture_is_a_Flash_with_its_region()
    {
        List<VideoEvent> events = Run(120, i =>
        {
            byte[] f = Flat(50);
            Textured(f, 0, 0, W, H, 40, 70);
            if (i is 60 or 61)
                Solid(f, 120, 12, 6, 5, 255);
            return f;
        });

        VideoEvent flash = events.Should().ContainSingle(e => e.Type == VideoEventType.Flash, Dump(events)).Subject;
        flash.Frame.Should().Be(60);
        VideoEventWords.RegionWord(flash.Region!).Should().Be("top-right");
    }

    [Fact]
    public void A_photo_that_pops_in_on_a_flat_page_is_an_Appear_on_its_frame_and_leaving_is_a_Disappear()
    {
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(245);
            if (i is >= 60 and < 160)
                Textured(f, 20, 5, 40, 30);
            return f;
        });

        VideoEvent appear = events.Should().ContainSingle(e => e.Type == VideoEventType.Appear, Dump(events)).Subject;
        appear.Frame.Should().Be(60);
        VideoEventWords.RegionWord(appear.Region!).Should().Be("top-left");
        events.Should().ContainSingle(e => e.Type == VideoEventType.Disappear).Which.Frame.Should().Be(160);
    }

    [Fact]
    public void Something_that_scales_in_from_a_dot_is_an_Appear_that_grows()
    {
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(245);
            if (i >= 90)
            {
                int size = Math.Min(50, 2 + (i - 90) * 2);
                Textured(f, 80 - size / 2, 45 - size / 2, size, size);
            }

            return f;
        });

        VideoEvent appear = events.Should().ContainSingle(e => e.Type == VideoEventType.Appear, Dump(events)).Subject;
        appear.Frame.Should().BeInRange(90, 92);
        appear.Direction.Should().Be("grow");
        appear.EndFrame.Should().BeGreaterThan(appear.Frame);
    }

    [Fact]
    public void A_card_that_slides_in_from_the_left_reports_the_direction()
    {
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(245);
            if (i >= 60)
                Textured(f, Math.Min(60, -50 + (i - 60) * 5), 30, 50, 30);
            return f;
        });

        events.Where(Worthy).Should().NotBeEmpty(Dump(events));
        VideoEvent first = events.Where(Worthy).OrderBy(e => e.Frame).First();
        first.Frame.Should().BeInRange(60, 63, Dump(events));
        first.Direction.Should().Be("right", Dump(events));
    }

    [Fact]
    public void Words_of_running_captions_are_CaptionTicks_and_never_worth_a_sound()
    {
        // one word after another in the same band, a new one every 12 frames
        List<VideoEvent> events = Run(360, i =>
        {
            byte[] f = Flat(10);
            int word = i / 12;
            Textured(f, 60 + word % 3 * 4, 42, 30 + word % 4 * 3, 6, 10, 250);
            return f;
        });

        events.Count(e => e.Type == VideoEventType.CaptionTick).Should().BeGreaterThanOrEqualTo(20, Dump(events));
        events.Where(Worthy).Should().BeEmpty(Dump(events));
    }

    [Fact]
    public void A_title_that_shows_once_is_text_coming_in_not_a_caption_tick()
    {
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(245);
            if (i >= 80)
                Textured(f, 30, 12, 100, 8, 20, 245);
            return f;
        });

        VideoEvent title = events.Should().ContainSingle(e => e.Type == VideoEventType.TextIn, Dump(events)).Subject;
        title.Frame.Should().Be(80);
        Worthy(title).Should().BeTrue();
    }

    [Fact]
    public void Footage_that_holds_its_frames_is_not_a_stream_of_moments()
    {
        // a 30 fps clip inside a 60 fps edit: the panel changes on every other frame, and a blob drifts across it
        List<VideoEvent> events = Run(360, i =>
        {
            byte[] f = Flat(245);
            Footage(f, 0, 20, W, 50, i / 2);
            return f;
        });

        events.Where(Worthy).Should().BeEmpty(Dump(events));
    }

    [Fact]
    public void A_stamp_that_grows_over_footage_is_found_on_its_first_frames()
    {
        List<VideoEvent> events = Run(360, i =>
        {
            byte[] f = Flat(245);
            Footage(f, 0, 20, W, 50, i / 2);
            if (i >= 180)
            {
                int size = Math.Min(36, 4 + (i - 180) * 2);
                Solid(f, 80 - size / 2, 45 - size / 2, size, size, 20);
            }

            return f;
        });

        List<VideoEvent> worthy = [.. events.Where(Worthy)];
        worthy.Should().ContainSingle(Dump(events));
        worthy[0].Frame.Should().BeInRange(178, 184);
        worthy[0].Type.Should().BeOneOf(VideoEventType.Appear, VideoEventType.Change);
        VideoEventWords.RegionWord(worthy[0].Region!).Should().Be("center");
    }

    [Fact]
    public void A_cut_inside_a_footage_panel_is_a_Transition_with_the_panels_region()
    {
        List<VideoEvent> events = Run(240, i =>
        {
            byte[] f = Flat(245);
            if (i < 120)
                Footage(f, 20, 25, 120, 40, i / 2);
            else
                Textured(f, 20, 25, 120, 40, (byte)(40 + i / 2 % 2), 160);
            return f;
        });

        VideoEvent cut = events.Should().ContainSingle(e => e.Type == VideoEventType.Transition, Dump(events)).Subject;
        cut.Frame.Should().Be(120);
        cut.Region.Should().NotBeNull();
        cut.Region!.W.Should().BeLessThan(0.9);
    }

    /// <summary>A filmed shot filling the area: grain and a drifting blob, a new picture on every second frame (30 in 60).</summary>
    private static byte[] Shot(int i, int look, int y0 = 0, int h = H, byte bars = 0)
    {
        byte[] f = Flat(bars);
        Footage(f, 0, y0, W, h, look * 1000 + i / 2);
        // another shot is another picture: mirrored, and lighter or darker
        if (look % 2 == 0)
        {
            for (int y = y0; y < y0 + h; y++)
                Array.Reverse(f, y * W, W);
        }

        int shift = look * 35 - 60;
        for (int y = y0; y < y0 + h; y++)
            for (int x = 0; x < W; x++)
                f[y * W + x] = (byte)Math.Clamp(f[y * W + x] + shift, 0, 255);
        return f;
    }

    [Fact]
    public void A_cut_between_two_filmed_shots_is_marked_footage()
    {
        List<VideoEvent> events = Run(180, i => Shot(i, i < 90 ? 1 : 2));

        VideoEvent cut = events.Should().ContainSingle(e => e.Type == VideoEventType.Transition, Dump(events)).Subject;
        cut.Frame.Should().Be(90);
        cut.Kind.Should().Be(VideoEventKinds.Footage);
    }

    [Fact]
    public void A_cut_between_two_letterboxed_shots_is_marked_footage_too()
    {
        // a 16:9 picture in a taller frame: flat bars above and below never change
        List<VideoEvent> events = Run(180, i => Shot(i, i < 90 ? 1 : 2, y0: 25, h: 40));

        VideoEvent cut = events.Should().ContainSingle(e => e.Type == VideoEventType.Transition, Dump(events)).Subject;
        cut.Kind.Should().Be(VideoEventKinds.Footage);
    }

    [Fact]
    public void A_cut_to_or_from_a_designed_page_keeps_no_kind()
    {
        // a white page with a card on it, then a filmed shot, then a dark page with a title block
        List<VideoEvent> events = Run(270, i =>
        {
            if (i < 90)
            {
                byte[] page = Flat(240);
                Textured(page, 40, 30, 60, 30);
                return page;
            }

            if (i < 180)
                return Shot(i, 3);
            byte[] dark = Flat(20);
            Textured(dark, 30, 35, 100, 16, dark: 20, light: 230);
            return dark;
        });

        List<VideoEvent> cuts = [.. events.Where(e => e.Type == VideoEventType.Transition)];
        cuts.Select(c => c.Frame).Should().Equal([90, 180], Dump(events));
        cuts.Should().OnlyContain(c => c.Kind == null, "a designed page on either side makes the cut a designed moment");
    }

    [Fact]
    public void Two_flat_pages_are_not_footage()
    {
        List<VideoEvent> events = Run(120, i => Flat(i < 45 ? (byte)40 : (byte)200));

        events.Single(e => e.Type == VideoEventType.Transition).Kind.Should().BeNull();
    }

    [Fact]
    public void A_whole_layout_that_eases_into_a_pan_is_a_camera_move_start()
    {
        // a page of scattered marks stands still, then the whole of it glides left, slowly at first (sub-pixel steps, soft edges)
        List<VideoEvent> events = Run(360, i =>
        {
            double shift = i < 120 ? 0 : Math.Min(60.0, Math.Pow((i - 120) / 90.0, 2) * 60.0);
            return SyntheticFrames.Render((x, y) =>
            {
                double v = 245;
                for (int k = 0; k < 40; k++)
                    v -= 200 * SyntheticFrames.Box(x, y, k * 37 % 200 - shift, k * 23 % 80, 9, 7);
                return v;
            });
        });

        VideoEvent move = events.Should().ContainSingle(e => e.Type == VideoEventType.CameraMoveStart, Dump(events)).Subject;
        move.Frame.Should().BeInRange(120, 150);
        move.EndFrame.Should().BeGreaterThan(move.Frame + 30);
        events.Should().NotContain(e => e.Type == VideoEventType.Transition);
    }

    [Fact]
    public void A_long_take_is_read_in_pieces_and_every_moment_is_reported_once()
    {
        // 70 s without a cut: pops at known frames, some of them right where one piece ends and the next begins
        int[] pops = [300, 1190, 1200, 1210, 1500, 2395, 2400, 2405, 3000, 3900];
        EditMomentAnalyzer analyzer = new(60, 1920, 1080);
        int peak = 0;
        for (int i = 0; i < 4200; i++)
        {
            byte[] f = Flat(245);
            for (int k = 0; k < pops.Length; k++)
                if (i >= pops[k])
                    Textured(f, 4 + k * 15, 10 + k % 3 * 25, 10, 10);
            analyzer.OnFrame(i, f);
            peak = Math.Max(peak, analyzer.BufferedFrames);
        }

        List<VideoEvent> events = analyzer.Complete();

        events.Where(e => e.Type == VideoEventType.Appear).Select(e => e.Frame).Should().BeEquivalentTo(pops, Dump(events));
        peak.Should().BeLessThan((int)((EditMomentAnalyzer.MaxSpanSeconds + EditMomentAnalyzer.SpanAheadSeconds + EditMomentAnalyzer.SpanOverlapSeconds + 2) * 60));
    }

    [Fact]
    public void The_moment_list_is_capped_keeping_the_strongest_and_says_so()
    {
        EditMomentAnalyzer analyzer = new();
        for (int i = 0; i < 24000; i++)
        {
            // a hard cut to a new grey level every 6 frames, never going back to the level before
            analyzer.OnFrame(i, Flat((byte)(20 + i / 6 * 97 % 200)));
        }

        List<VideoEvent> events = analyzer.Complete();

        events.Count.Should().Be(EditMomentAnalyzer.MaxMoments);
        analyzer.DroppedMoments.Should().BeGreaterThan(0);
        events.Select(e => e.Frame).Should().BeInAscendingOrder();
    }

    [Fact]
    public void A_30_fps_source_scales_its_time_windows()
    {
        List<VideoEvent> events = Run(150, i =>
        {
            byte[] f = Flat(i < 40 ? (byte)40 : (byte)230);
            if (i >= 90)
                Textured(f, 20, 15, 40, 30);
            return f;
        }, fps: 30);

        events.Should().ContainSingle(e => e.Type == VideoEventType.Transition, Dump(events)).Which.Frame.Should().Be(40);
        events.Should().ContainSingle(e => e.Type == VideoEventType.Appear).Which.Frame.Should().Be(90);
    }

    [Fact]
    public void Combining_keeps_camera_moves_and_real_impacts_and_drops_what_the_edit_moments_already_hold()
    {
        List<VideoEvent> classified =
        [
            new(VideoEventType.CameraMoveStart, 100, null, null, 0.8, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStop, 160, null, null, 0.8, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStart, 400, null, null, 1.0, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStop, 900, null, null, 1.0, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStart, 1000, null, null, 0.9, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStop, 1060, null, null, 0.9, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.Hit, 300, null, new VideoEventRegion(0.4, 0.4, 0.1, 0.1), 0.7, VideoEventOrigin.Pixels, 0.6),
            new(VideoEventType.Hit, 1202, null, new VideoEventRegion(0.4, 0.4, 0.1, 0.1), 0.7, VideoEventOrigin.Pixels, 0.6),
            new(VideoEventType.TextIn, 700, null, new VideoEventRegion(0.4, 0.4, 0.1, 0.1), 0.7, VideoEventOrigin.Pixels, 0.6),
            new(VideoEventType.Transition, 800, null, null, 0.7, VideoEventOrigin.Pixels, 0.6)
        ];
        List<VideoEvent> moments =
        [
            new(VideoEventType.Transition, 1004, null, null, 1.0, VideoEventOrigin.Pixels, 0.9),
            new(VideoEventType.Appear, 1200, 1210, new VideoEventRegion(0.4, 0.4, 0.1, 0.1), 0.6, VideoEventOrigin.Pixels, 0.7)
        ];

        List<VideoEvent> combined = MomentEvents.Combine(classified, moments);

        combined.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart && e.Frame == 100 && e.Strength == 0.8);
        combined.Should().Contain(e => e.Type == VideoEventType.CameraMoveStop && e.Frame == 160);
        // a push that runs for more than two seconds is not a moment: it stays, weak
        combined.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart && e.Frame == 400 && e.Strength <= 0.3 && e.EndFrame == 900);
        combined.Should().Contain(e => e.Type == VideoEventType.CameraMoveStop && e.Frame == 900 && e.Strength <= 0.3);
        // the cut at 1004 is the start of that move
        combined.Where(e => e.Frame == 1000 || e.Frame == 1060).Should().BeEmpty();
        combined.Should().Contain(e => e.Type == VideoEventType.Hit && e.Frame == 300);
        combined.Should().NotContain(e => e.Type == VideoEventType.Hit && e.Frame == 1202);
        // the cell classifier's own text and transitions are replaced by the edit moments
        combined.Where(e => e.Frame == 700 || e.Frame == 800).Should().BeEmpty();
        combined.Should().Contain(e => e.Type == VideoEventType.Transition && e.Frame == 1004);
    }

    [Theory]
    [InlineData(60.0, 150, 18, 4)]
    [InlineData(59.94, 150, 18, 4)]
    [InlineData(30.0, 75, 9, 2)]
    [InlineData(25.0, 63, 8, 2)]
    [InlineData(24.0, 60, 7, 2)]
    [InlineData(120.0, 300, 36, 8)]
    [InlineData(0.0, 150, 18, 4)]
    [InlineData(double.NaN, 150, 18, 4)]
    public void The_combine_windows_mean_the_same_seconds_at_every_frame_rate(double fps, int slowMove, int sameMove, int sameInstant) =>
        MomentEvents.FrameWindows(fps).Should().Be((slowMove, sameMove, sameInstant));

    [Fact]
    public void At_30_fps_a_three_second_push_is_slow_and_a_two_second_one_is_not()
    {
        // The same two camera moves, on a grid analysed at 30 frames a second: 90 frames are 3 s, 60 frames are 2 s.
        List<VideoEvent> classified =
        [
            new(VideoEventType.CameraMoveStart, 100, null, null, 0.8, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStop, 190, null, null, 0.8, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStart, 400, null, null, 0.9, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStop, 460, null, null, 0.9, VideoEventOrigin.Pixels, 0.8),
            // 12 frames before a cut: 0.4 s at 30 fps, so it is its own move; at 60 fps it would be the cut's.
            new(VideoEventType.CameraMoveStart, 788, null, null, 0.7, VideoEventOrigin.Pixels, 0.8),
            new(VideoEventType.CameraMoveStop, 795, null, null, 0.7, VideoEventOrigin.Pixels, 0.8),
            // 3 frames after an appear: 0.1 s at 30 fps, so it is its own impact; at 60 fps it would be the appear's.
            new(VideoEventType.Hit, 1203, null, new VideoEventRegion(0.4, 0.4, 0.1, 0.1), 0.7, VideoEventOrigin.Pixels, 0.6)
        ];
        List<VideoEvent> moments =
        [
            new(VideoEventType.Transition, 800, null, null, 1.0, VideoEventOrigin.Pixels, 0.9),
            new(VideoEventType.Appear, 1200, null, new VideoEventRegion(0.4, 0.4, 0.1, 0.1), 0.6, VideoEventOrigin.Pixels, 0.7)
        ];

        List<VideoEvent> at30 = MomentEvents.Combine(classified, moments, fps: 30);
        List<VideoEvent> at60 = MomentEvents.Combine(classified, moments);

        at30.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart && e.Frame == 100 && e.Strength <= 0.3 && e.EndFrame == 190);
        at30.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart && e.Frame == 400 && e.Strength == 0.9 && e.EndFrame == null);
        at30.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart && e.Frame == 788);
        at30.Should().Contain(e => e.Type == VideoEventType.Hit && e.Frame == 1203);

        at60.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart && e.Frame == 100 && e.Strength == 0.8, "90 frames are 1.5 s at 60 fps");
        at60.Should().NotContain(e => e.Frame == 788);
        at60.Should().NotContain(e => e.Type == VideoEventType.Hit && e.Frame == 1203);
    }

    [Fact]
    public void Combining_a_large_pile_of_events_is_fast()
    {
        List<VideoEvent> classified = [.. Enumerable.Range(0, 200_000).Select(i => new VideoEvent(VideoEventType.Hit, i * 2, null, null, 0.5, VideoEventOrigin.Pixels, 0.6))];
        List<VideoEvent> moments = [.. Enumerable.Range(0, 100_000).Select(i => new VideoEvent(VideoEventType.Transition, i * 4 + 1, null, null, 0.9, VideoEventOrigin.Pixels, 0.9))];
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        List<VideoEvent> combined = MomentEvents.Combine(classified, moments);

        watch.Stop();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
        combined.Should().Contain(e => e.Type == VideoEventType.Transition);
    }
}
