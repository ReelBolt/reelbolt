using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Video.Events.SyntheticFrames;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// Pins the whole pixel side of the event detector: one pass of frames through both readers and the combine step,
/// wired the way <see cref="VideoEventDetector"/> wires them. Every field of every event is written to
/// <c>pixel-event-pipeline.golden.txt</c>; a change to any of them fails here. Regenerate with
/// <c>UPDATE_PIXEL_EVENT_PIPELINE_PIN=1</c> only when the event output is meant to change.
/// </summary>
public class PixelEventPipelinePinTests
{
    private const string UpdateVariable = "UPDATE_PIXEL_EVENT_PIPELINE_PIN";
    private const string GoldenFile = "pixel-event-pipeline.golden.txt";

    private sealed record Clip(string Name, int Frames, Func<int, byte[]> Frame, double Fps = 60, int Width = 1920, int Height = 1080);

    /// <summary>The detector's own wiring: one tee, both readers, the classifier, the combine step.</summary>
    private static List<VideoEvent> RunPipeline(Clip s)
    {
        FrameEventAnalyzer analyzer = new();
        (double plannedFps, _) = FrameSignalSampler.PlanRate(s.Fps);
        EditMomentAnalyzer moments = new(plannedFps, s.Width, s.Height);
        TeeConsumer tee = new(analyzer, moments);
        for (int i = 0; i < s.Frames; i++)
            tee.OnFrame(i, s.Frame(i));
        FrameAnalysis analysis = analyzer.Complete();
        List<VideoEvent> momentList = moments.Complete();
        return MomentEvents.Combine(VisualEventClassifier.Classify(analysis), momentList, moments.InFullPicture, plannedFps);
    }

    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string Line(VideoEvent e) => string.Join(' ',
        e.Type.ToString(),
        "frame=" + e.Frame.ToString(CultureInfo.InvariantCulture),
        "end=" + (e.EndFrame?.ToString(CultureInfo.InvariantCulture) ?? "-"),
        "region=" + (e.Region is { } r ? $"{N(r.X)},{N(r.Y)},{N(r.W)},{N(r.H)}" : "-"),
        "strength=" + N(e.Strength),
        "origin=" + e.Origin,
        "confidence=" + N(e.Confidence),
        "direction=" + (e.Direction ?? "-"),
        "speed=" + (e.Speed ?? "-"),
        "kind=" + (e.Kind ?? "-"));

    private static byte[] Scene(int t, Func<int, int, int, double> layer) => Render((x, y) => layer(t, x, y));

    private static double Over(double under, double over, double opacity) => under + opacity * (over - under);

    /// <summary>Grain that differs per pixel and per frame, from a hash: the same bytes on every run.</summary>
    private static double Grain(int x, int y, int t, double amplitude)
    {
        uint n = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(t * 83492791);
        n = n * 1664525u + 1013904223u;
        return (n >> 24) / 255.0 * amplitude - amplitude / 2.0;
    }

    private static double BallY(int t, int start, int turn) => t <= turn ? 5 + 2.5 * Math.Max(t - start, 0) : 5 + 2.5 * (turn - start) - 2.5 * (t - turn);

    private static double Ball(int tt, int x, int y, double ballY) =>
        Over(Background(x, y), 200 + 40 * Math.Sin(0.7 * (x - 80)) * Math.Cos(0.7 * (y - ballY)), Box(x, y, 80, ballY, 14, 14));

    private static List<Clip> Clips() =>
    [
        new("still picture", 100, t => Scene(t, (tt, x, y) => Background(x, y))),
        new("picture slides right 0.6 px per frame (camera pans left), frames 30 to 70", 100, t => Scene(t, (tt, x, y) => Background(x - 0.6 * Math.Clamp(tt - 30, 0, 40), y))),
        new("picture slides left 0.8 px per frame (camera pans right), frames 20 to 80", 120, t => Scene(t, (tt, x, y) => Background(x + 0.8 * Math.Clamp(tt - 20, 0, 60), y))),
        new("picture slides down 0.5 px per frame (camera tilts up), frames 30 to 90", 130, t => Scene(t, (tt, x, y) => Background(x, y - 0.5 * Math.Clamp(tt - 30, 0, 60)))),
        new("picture slides up 0.5 px per frame (camera tilts down), frames 30 to 90", 130, t => Scene(t, (tt, x, y) => Background(x, y + 0.5 * Math.Clamp(tt - 30, 0, 60)))),
        new("push in, one percent per frame, frames 30 to 70", 110, t => Scene(t, (tt, x, y) =>
        {
            double zoom = 1 + 0.01 * Math.Clamp(tt - 30, 0, 40);
            return Background(80 + (x - 80) / zoom, 45 + (y - 45) / zoom);
        })),
        new("pull out, one percent per frame, frames 30 to 70", 110, t => Scene(t, (tt, x, y) =>
        {
            double zoom = 1.4 - 0.01 * Math.Clamp(tt - 30, 0, 40);
            return Background(80 + (x - 80) / zoom, 45 + (y - 45) / zoom);
        })),
        new("picture slides right 0.25 px per frame for 200 frames (long slow pan left)", 280, t => Scene(t, (tt, x, y) => Background(x - 0.25 * Math.Clamp(tt - 30, 0, 200), y))),
        new("picture slides right at 30 fps (camera pans left)", 100, t => Scene(t, (tt, x, y) => Background(x - 0.6 * Math.Clamp(tt - 30, 0, 40), y)), Fps: 30),
        new("ball bounces off the floor at frame 50", 100, t => Scene(t, (tt, x, y) => Ball(tt, x, y, BallY(tt, 20, 50)))),
        new("ball bounces every 30 frames", 300, t => Scene(t, (tt, x, y) => Ball(tt, x, y, 5 + 2.5 * (tt % 60 < 30 ? tt % 30 : 30 - tt % 30)))),
        new("box travels 1.2 px per frame, frames 30 to 70", 100, t => Scene(t, (tt, x, y) =>
        {
            double bx = 20 + 1.2 * Math.Clamp(tt - 30, 0, 40);
            return Over(Background(x, y), 190 + 35 * Math.Sin(0.55 * (x - bx)) * Math.Cos(0.5 * y), Box(x, y, bx, 30, 28, 20));
        })),
        new("box fades in, frames 30 to 42", 100, t => Scene(t, (tt, x, y) => Over(Flat(x, y), 215, Ramp(tt, 30, 42) * Box(x, y, 60, 30, 40, 30)))),
        new("box fades out, frames 40 to 52", 100, t => Scene(t, (tt, x, y) => Over(Flat(x, y), 215, (1 - Ramp(tt, 40, 52)) * Box(x, y, 60, 30, 40, 30)))),
        new("stripes fade in, frames 30 to 42", 100, t => Scene(t, (tt, x, y) =>
            Over(Flat(x, y), (x + y / 3) % 2 == 0 ? 40 : 235, Ramp(tt, 30, 42) * Box(x, y, 40, 30, 80, 24)))),
        new("patch pulses bright, frames 40 to 48", 100, t => Scene(t, (tt, x, y) =>
            Over(Background(x, y), 150 + 90 * (tt is >= 40 and <= 48 ? Math.Sin(Math.PI * (tt - 40) / 8.0) : 0), Box(x, y, 50, 30, 30, 24)))),
        new("hard cut at frame 50", 100, t => Scene(t, (tt, x, y) => tt < 50 ? SlideA(x, y) : SlideB(x, y))),
        new("dissolve, frames 40 to 60", 100, t => Scene(t, (tt, x, y) => Over(SlideA(x, y), SlideB(x, y), Ramp(tt, 40, 60)))),
        new("montage, a new still every 10 frames", 240, t =>
        {
            byte[] levels = [40, 200, 90, 240, 20, 150, 70, 220, 110, 30, 180, 60];
            byte[] f = EditMomentAnalyzerTests.Flat(levels[t / 10 % levels.Length]);
            EditMomentAnalyzerTests.Textured(f, 20 + t / 10 % 5 * 10, 20, 60, 40);
            return f;
        }),
        new("whole layout eases into a pan from frame 120", 360, t =>
        {
            double shift = t < 120 ? 0 : Math.Min(60.0, Math.Pow((t - 120) / 90.0, 2) * 60.0);
            return Render((x, y) =>
            {
                double v = 245;
                for (int k = 0; k < 40; k++)
                    v -= 200 * Box(x, y, k * 37 % 200 - shift, k * 23 % 80, 9, 7);
                return v;
            });
        }),
        new("pops on a still page, then footage in a panel", 300, t =>
        {
            byte[] f = EditMomentAnalyzerTests.Flat(245);
            int[] pops = [40, 90, 95, 150];
            for (int k = 0; k < pops.Length; k++)
                if (t >= pops[k])
                    EditMomentAnalyzerTests.Textured(f, 6 + k * 18, 8 + k % 2 * 20, 12, 10);
            if (t >= 180)
                EditMomentAnalyzerTests.Footage(f, 60, 30, 90, 50, (t - 180) / 2);
            return f;
        }),
        new("grain over a picture that pans, with a cut at frame 150", 260, t => Scene(t, (tt, x, y) =>
            (tt < 150 ? Background(x - 0.7 * Math.Clamp(tt - 40, 0, 60), y) : Background(x, y, 3)) + Grain(x, y, tt, 6))),
        new("heavy grain everywhere", 200, t => Scene(t, (tt, x, y) => Background(x, y) + Grain(x, y, tt, 60))),
        new("pan, then a bounce, then a cut, then a fade in", 420, t => Scene(t, (tt, x, y) =>
        {
            if (tt < 120)
                return Background(x - 0.6 * Math.Clamp(tt - 30, 0, 40), y);
            if (tt < 260)
                return Ball(tt, x, y, BallY(tt, 150, 180));
            return Over(SlideA(x, y), 215, Ramp(tt, 320, 332) * Box(x, y, 70, 40, 30, 30));
        })),
        // Filmed pictures: light grain on every frame. The newer reader sees one long stretch of footage, so the camera
        // moves and impacts below come from the older reader alone.
        new("filmed, picture slides right 0.3 px per frame (camera pans left), frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
            Background(x - 0.3 * Math.Clamp(tt - 60, 0, 60), y) + Grain(x, y, tt, 3))),
        new("filmed, picture slides left 0.3 px per frame (camera pans right), frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
            Background(x + 0.3 * Math.Clamp(tt - 60, 0, 60), y) + Grain(x, y, tt, 3))),
        new("filmed, picture slides down 0.3 px per frame (camera tilts up), frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
            Background(x, y - 0.3 * Math.Clamp(tt - 60, 0, 60)) + Grain(x, y, tt, 3))),
        new("filmed, picture slides up 0.3 px per frame (camera tilts down), frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
            Background(x, y + 0.3 * Math.Clamp(tt - 60, 0, 60)) + Grain(x, y, tt, 3))),
        new("filmed, picture slides down and to the right (camera moves up and to the left), frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
            Background(x - 0.25 * Math.Clamp(tt - 60, 0, 60), y - 0.25 * Math.Clamp(tt - 60, 0, 60)) + Grain(x, y, tt, 3))),
        new("filmed, push in, frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
        {
            double zoom = 1 + 0.004 * Math.Clamp(tt - 60, 0, 60);
            return Background(80 + (x - 80) / zoom, 45 + (y - 45) / zoom) + Grain(x, y, tt, 3);
        })),
        new("filmed, pull out, frames 60 to 120", 200, t => Scene(t, (tt, x, y) =>
        {
            double zoom = 1.3 - 0.004 * Math.Clamp(tt - 60, 0, 60);
            return Background(80 + (x - 80) / zoom, 45 + (y - 45) / zoom) + Grain(x, y, tt, 3);
        })),
        new("filmed, picture slides right 0.2 px per frame for 220 frames (long slow pan left)", 320, t => Scene(t, (tt, x, y) =>
            Background(x - 0.2 * Math.Clamp(tt - 60, 0, 220), y) + Grain(x, y, tt, 3))),
        new("filmed, picture slides right at 30 fps (camera pans left)", 200, t => Scene(t, (tt, x, y) =>
            Background(x - 0.3 * Math.Clamp(tt - 60, 0, 60), y) + Grain(x, y, tt, 3)), Fps: 30),
        new("filmed, the camera shakes from side to side, frames 60 to 140", 220, t => Scene(t, (tt, x, y) =>
            Background(x - (tt is >= 60 and <= 140 ? 2.0 * Math.Sin((tt - 60) * Math.PI / 10.0) : 0), y) + Grain(x, y, tt, 3))),
        new("large box slows hard at frame 55", 140, t => Scene(t, (tt, x, y) =>
        {
            double bx = tt <= 55 ? 10 + 2.0 * Math.Max(tt - 30, 0) : 60 + 0.4 * Math.Min(tt - 55, 40);
            return Over(Background(x, y), 190 + 35 * Math.Sin(0.55 * (x - bx)) * Math.Cos(0.5 * y), Box(x, y, bx, 30, 28, 20));
        })),
        new("large box falls and turns back at frame 55", 140, t => Scene(t, (tt, x, y) =>
        {
            double by = tt <= 55 ? 5 + 2.0 * Math.Max(tt - 30, 0) : 55 - 2.0 * Math.Min(tt - 55, 25);
            return Over(Background(x, y), 190 + 35 * Math.Sin(0.5 * x) * Math.Cos(0.55 * (y - by)), Box(x, y, 60, by, 40, 24));
        })),
        new("vertical source, box travels down", 120, t => Scene(t, (tt, x, y) =>
        {
            double by = 10 + 1.0 * Math.Clamp(tt - 30, 0, 50);
            return Over(Flat(x, y), 200 + 30 * Math.Sin(0.6 * x) * Math.Cos(0.6 * (y - by)), Box(x, y, 60, by, 30, 16));
        }), Width: 1080, Height: 1920)
    ];

    private static string GoldenPath([CallerFilePath] string callerFilePath = "") =>
        Path.Combine(Path.GetDirectoryName(callerFilePath) ?? string.Empty, GoldenFile);

    [Fact]
    public void The_combined_pixel_events_of_every_sequence_match_the_pinned_list()
    {
        StringBuilder sb = new();
        int withCameraStops = 0, withImpacts = 0;
        foreach (Clip s in Clips())
        {
            List<VideoEvent> events = RunPipeline(s);
            if (events.Any(e => e.Type == VideoEventType.CameraMoveStop))
                withCameraStops++;
            if (events.Any(e => e.Type == VideoEventType.Hit && e.EndFrame is null))
                withImpacts++;
            sb.Append("## ").Append(s.Name).Append(" (").Append(events.Count.ToString(CultureInfo.InvariantCulture)).Append(" events)\n");
            foreach (VideoEvent e in events)
                sb.Append(Line(e)).Append('\n');
            sb.Append('\n');
        }

        string actual = sb.ToString();
        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        // Only the older reader reports where a camera move stops and where a tracked object lands: the pin must hold both.
        withCameraStops.Should().BeGreaterThan(5);
        withImpacts.Should().BeGreaterThan(1);
        File.Exists(path).Should().BeTrue($"the pinned list is committed next to this test; create it with {UpdateVariable}=1");
        actual.Should().Be(File.ReadAllText(path).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Two_runs_of_one_sequence_give_the_same_list()
    {
        Clip s = Clips().Single(q => q.Name.StartsWith("pan, then a bounce", StringComparison.Ordinal));

        string first = string.Join('\n', RunPipeline(s).Select(Line));
        string second = string.Join('\n', RunPipeline(s).Select(Line));

        first.Should().NotBeEmpty();
        second.Should().Be(first);
    }
}
