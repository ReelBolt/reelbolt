using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Video.Events.SyntheticFrames;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// The older pixel reader on synthetic in-memory frame sequences: camera moves and the impacts of a tracked object,
/// the two things it alone reports. Every expected frame is where the sequence's own timeline puts the event, and a
/// detection counts within exactly +/-2 frames. What an edit does to the picture (cuts, things appearing, text) is
/// covered by <see cref="EditMomentAnalyzerTests"/>.
/// </summary>
public class FrameEventDetectorTests
{
    private const int Frames = 100;

    private static byte[] Scene(int t, Func<int, int, int, double> layer) => Render((x, y) => layer(t, x, y));

    private static double Over(double under, double over, double opacity) => under + opacity * (over - under);

    [Fact]
    public void A_ball_that_bounces_forever_cannot_grow_the_impact_list_past_its_cap()
    {
        // Falls and rises at 2.5 px/frame, reversing every 30 frames: far more impacts than the cap in 600 frames.
        double Y(int t) => 5 + 2.5 * (t % 60 < 30 ? t % 30 : 30 - t % 30);
        FrameEventAnalyzer unbounded = new();
        FrameEventAnalyzer capped = new() { ImpactCap = 3 };
        for (int t = 0; t < 600; t++)
        {
            byte[] frame = Scene(t, (tt, x, y) => Over(Background(x, y), 200 + 40 * Math.Sin(0.7 * (x - 80)) * Math.Cos(0.7 * (y - Y(tt))), Box(x, y, 80, Y(tt), 14, 14)));
            unbounded.OnFrame(t, frame);
            capped.OnFrame(t, frame);
        }

        int total = unbounded.Complete().Impacts.Count;
        int kept = capped.Complete().Impacts.Count;

        total.Should().BeGreaterThan(3, "the input must produce more impacts than the cap for this test to mean anything");
        kept.Should().Be(3);
    }

    [Fact]
    public void A_ball_that_bounces_off_the_floor_is_a_Hit_at_the_reversal()
    {
        // Falls at 2.5 px/frame from frame 20, turns around at frame 50.
        double Y(int t) => t <= 50 ? 5 + 2.5 * Math.Max(t - 20, 0) : 80 - 2.5 * (t - 50);
        List<VideoEvent> events = Detect(Sequence(Frames, t => Scene(t, (tt, x, y) =>
        {
            double cover = Box(x, y, 80, Y(tt), 14, 14);
            double texture = 200 + 40 * Math.Sin(0.7 * (x - 80)) * Math.Cos(0.7 * (y - Y(tt)));
            return Over(Background(x, y), texture, cover);
        })));

        Has(events, VideoEventType.Hit, 50).Should().BeTrue(Describe(events));
    }

    [Fact]
    public void A_panning_camera_gives_CameraMoveStart_and_CameraMoveStop_and_nothing_else()
    {
        // The whole picture shifts 0.6 px/frame from frame 30 to frame 70.
        double Shift(int t) => 0.6 * Math.Clamp(t - 30, 0, 40);
        List<VideoEvent> events = Detect(Sequence(Frames, t => Scene(t, (tt, x, y) => Background(x - Shift(tt), y))));

        Has(events, VideoEventType.CameraMoveStart, 31).Should().BeTrue(Describe(events));
        Has(events, VideoEventType.CameraMoveStop, 70).Should().BeTrue(Describe(events));
        events.Should().OnlyContain(e => e.Type == VideoEventType.CameraMoveStart || e.Type == VideoEventType.CameraMoveStop, Describe(events));
    }

    private static string? CameraDirectionOf(Func<int, int, int, double> layer, int frames = 140)
    {
        List<VideoEvent> events = Detect(Sequence(frames, t => Scene(t, layer)));
        events.Should().ContainSingle(e => e.Type == VideoEventType.CameraMoveStart, Describe(events));
        events.Single(e => e.Type == VideoEventType.CameraMoveStop).Direction.Should().BeNull("only the start says which way");
        return events.Single(e => e.Type == VideoEventType.CameraMoveStart).Direction;
    }

    private static double Travel(int t) => Math.Clamp(t - 30, 0, 60);

    [Fact]
    public void A_camera_move_start_says_which_way_the_camera_goes()
    {
        // The content slides one way, so the camera goes the other way.
        CameraDirectionOf((t, x, y) => Background(x - 0.5 * Travel(t), y)).Should().Be("left");
        CameraDirectionOf((t, x, y) => Background(x + 0.5 * Travel(t), y)).Should().Be("right");
        CameraDirectionOf((t, x, y) => Background(x, y - 0.5 * Travel(t))).Should().Be("up");
        CameraDirectionOf((t, x, y) => Background(x, y + 0.5 * Travel(t))).Should().Be("down");
        CameraDirectionOf((t, x, y) => Background(x - 0.4 * Travel(t), y - 0.4 * Travel(t))).Should().Be("up-left");
        CameraDirectionOf((t, x, y) => Background(x + 0.4 * Travel(t), y - 0.4 * Travel(t))).Should().Be("up-right");
    }

    [Fact]
    public void A_zoom_is_a_camera_move_in_or_out()
    {
        // The content spreads out from the centre: the camera pushes in. It draws together: the camera pulls out.
        CameraDirectionOf((t, x, y) =>
        {
            double zoom = 1 + 0.005 * Travel(t);
            return Background(80 + (x - 80) / zoom, 45 + (y - 45) / zoom);
        }).Should().Be("in");
        CameraDirectionOf((t, x, y) =>
        {
            double zoom = 1.3 - 0.005 * Travel(t);
            return Background(80 + (x - 80) / zoom, 45 + (y - 45) / zoom);
        }).Should().Be("out");
    }

    [Fact]
    public void A_camera_that_shakes_back_and_forth_goes_no_way()
    {
        string? direction = CameraDirectionOf((t, x, y) =>
            Background(x - (t is >= 30 and <= 110 ? 2.0 * Math.Sin((t - 30) * Math.PI / 10.0) : 0), y), frames: 160);

        direction.Should().BeNull();
    }

    [Fact]
    public void The_net_travel_decides_the_direction_and_a_small_net_travel_gives_none()
    {
        List<CameraShift> pan = [.. Enumerable.Repeat(new CameraShift(0.5f, 0.05f, 0.02f), 40)];
        List<CameraShift> zoomWithDrift = [.. Enumerable.Repeat(new CameraShift(0.1f, 0f, -0.4f), 40)];
        List<CameraShift> shake = [.. Enumerable.Range(0, 40).Select(i => new CameraShift(i % 2 == 0 ? 0.5f : -0.5f, 0f, 0f))];
        double Total(List<CameraShift> shifts) => shifts.Sum(s => Math.Abs(s.X) + Math.Abs(s.Y) + Math.Abs(s.Zoom));

        VisualEventClassifier.CameraDirection(pan, 0, 39, Total(pan)).Should().Be("left");
        VisualEventClassifier.CameraDirection(zoomWithDrift, 0, 39, Total(zoomWithDrift)).Should().Be("out");
        VisualEventClassifier.CameraDirection(shake, 0, 39, Total(shake)).Should().BeNull();
        VisualEventClassifier.CameraDirection(pan, 0, 39, 0).Should().BeNull();
        // a run that reaches past the list reads what is there and does not throw
        VisualEventClassifier.CameraDirection(pan, 30, 500, 5).Should().Be("left");
    }

    [Fact]
    public void A_hard_cut_and_a_dissolve_are_not_camera_moves()
    {
        List<VideoEvent> cut = Detect(Sequence(Frames, t => Scene(t, (tt, x, y) => tt < 50 ? SlideA(x, y) : SlideB(x, y))));
        List<VideoEvent> dissolve = Detect(Sequence(Frames, t => Scene(t, (tt, x, y) =>
            Over(SlideA(x, y), SlideB(x, y), Ramp(tt, 40, 60)))));

        cut.Should().NotContain(e => e.Type == VideoEventType.CameraMoveStart, Describe(cut));
        dissolve.Should().NotContain(e => e.Type == VideoEventType.CameraMoveStart, Describe(dissolve));
    }

    [Fact]
    public void A_box_that_fades_in_or_travels_slowly_is_left_to_the_other_reader()
    {
        List<VideoEvent> fade = Detect(Sequence(Frames, t => Scene(t, (tt, x, y) =>
            Over(Flat(x, y), 215, Ramp(tt, 30, 42) * Box(x, y, 60, 30, 40, 30)))));
        double X(int t) => 20 + 1.2 * Math.Clamp(t - 30, 0, 40);
        List<VideoEvent> travel = Detect(Sequence(Frames, t => Scene(t, (tt, x, y) =>
            Over(Background(x, y), 190 + 35 * Math.Sin(0.55 * (x - X(tt))) * Math.Cos(0.5 * y), Box(x, y, X(tt), 30, 28, 20)))));

        fade.Should().BeEmpty(Describe(fade));
        travel.Should().BeEmpty(Describe(travel));
    }

    [Fact]
    public void A_still_picture_has_no_events()
    {
        Detect(Sequence(Frames, t => Scene(t, (tt, x, y) => Background(x, y)))).Should().BeEmpty();
    }

    [Fact]
    public void Two_runs_over_the_same_frames_give_identical_events()
    {
        // A pan, then a ball that falls and turns back: both kinds of event in one sequence.
        double Y(int t) => t <= 170 ? 5 + 2.5 * Math.Max(t - 140, 0) : 80 - 2.5 * (t - 170);
        List<byte[]> frames = Sequence(220, t => Scene(t, (tt, x, y) => tt < 110
            ? Background(x - 0.6 * Math.Clamp(tt - 30, 0, 40), y)
            : Over(Background(x - 24, y), 200 + 40 * Math.Sin(0.7 * (x - 80)) * Math.Cos(0.7 * (y - Y(tt))), Box(x, y, 80, Y(tt), 14, 14)))).ToList();

        List<VideoEvent> first = Detect(frames);
        string second = Describe(Detect(frames));

        first.Should().Contain(e => e.Type == VideoEventType.CameraMoveStart);
        first.Should().Contain(e => e.Type == VideoEventType.Hit);
        second.Should().Be(Describe(first));
    }
}
