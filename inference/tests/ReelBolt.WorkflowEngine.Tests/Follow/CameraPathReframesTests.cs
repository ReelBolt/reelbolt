using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>Camera path to VideoReframe spans, and the tracker adapters.</summary>
public class CameraPathReframesTests
{
    private static CameraPathResult Path(int keys) =>
        new([.. Enumerable.Range(0, keys).Select(i => new CameraKey(i * 0.5, 0.5, 0.5, 1 + (i % 3) * 0.1))], 8, false, [], 0, 0);

    [Fact]
    public void HundredKeys_BecomeFourValidSpansSharingBoundaryKeys()
    {
        CameraPathResult path = Path(100);
        IReadOnlyList<VideoReframe> spans = CameraPathReframes.ToReframes(path, 2, 0, 49.5);
        spans.Should().HaveCount(4);
        spans.Select(s => s.Keyframes.Count).Should().Equal(32, 32, 32, 7);
        spans.Should().OnlyContain(s => s.SourceIndex == 2 && s.Keyframes.All(k => k.Easing == ReframeEasing.Linear));
        VideoReframe.Validate(spans).IsValid.Should().BeTrue();
        for (int i = 0; i + 1 < spans.Count; i++)
        {
            VideoReframe a = spans[i], b = spans[i + 1];
            a.ToSec.Should().Be(b.FromSec);
            ReframeKeyframe last = a.Keyframes[^1];
            ReframeKeyframe first = b.Keyframes[0];
            first.AtSec.Should().Be(0);
            (a.FromSec!.Value + last.AtSec).Should().BeApproximately(b.FromSec!.Value, 1e-9);
            first.Zoom.Should().Be(last.Zoom);
        }

        spans[0].Keyframes[0].AtSec.Should().Be(0);
        spans[^1].ToSec.Should().Be(49.5);
    }

    [Fact]
    public void AtSecIsRelativeToTheSpanStart()
    {
        IReadOnlyList<VideoReframe> spans = CameraPathReframes.ToReframes(Path(5), 0, 0, 2);
        spans.Should().ContainSingle();
        spans[0].Keyframes.Select(k => k.AtSec).Should().Equal(0, 0.5, 1, 1.5, 2);
    }

    [Fact]
    public void MaximumKeyCountFits_OneMoreIsRefused()
    {
        CameraPathReframes.MaxKeysForWorkflow.Should().Be(497);
        IReadOnlyList<VideoReframe> spans = CameraPathReframes.ToReframes(Path(497), 0, 0, 248);
        spans.Should().HaveCount(VideoReframe.MaxReframesPerSource);
        VideoReframe.Validate(spans).IsValid.Should().BeTrue();
        Action tooMany = () => CameraPathReframes.ToReframes(Path(498), 0, 0, 249);
        tooMany.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ComputedPathConvertsAndValidates()
    {
        var s = Enumerable.Range(0, 3001).Select(i => new SubjectSample(i * 0.1, 0.4 + 0.2 * Math.Sin(i * 0.02), 0.4, 0.1, 0.15, true)).ToList();
        var o = new FollowOptions(FollowTightness.Medium, FollowSmoothness.Normal, FollowOnLost.Hold, true, FollowFraming.Full, 1080, 1080, CameraPathReframes.MaxKeysForWorkflow);
        CameraPathResult r = SubjectCameraPath.Compute(s, 0, 300, o);
        IReadOnlyList<VideoReframe> spans = CameraPathReframes.ToReframes(r, 0, 0, 300);
        VideoReframe.Validate(spans).IsValid.Should().BeTrue();
        VideoReframe.Validate(spans).Clamps.Should().BeEmpty();
    }

    [Fact]
    public void InsertTrackAdapter_UsesCornerBoundsAndDropsOutsideTimes()
    {
        var keys = new List<VideoInsertQuadKeyframe>
        {
            new(0.5, 0.2, 0.3, 0.6, 0.35, 0.25, 0.7, 0.65, 0.75),
            new(1.0, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5, 0.5),
            new(9.0, 0.2, 0.3, 0.6, 0.35, 0.25, 0.7, 0.65, 0.75),
            new(1.5, -0.2, 0.0, 0.4, 0.0, 0.4, 0.5, -0.2, 0.5),
        };
        var track = new VideoInsertRegionTrack("r1", null, 0.5, 2.0, keys, 0.9, 0.1, 1.0, "Static", "green");
        IReadOnlyList<SubjectSample> s = SubjectPathAdapters.FromInsertTrack(track);
        s.Should().HaveCount(3);
        s[0].Present.Should().BeTrue();
        s[0].X.Should().Be(0.2);
        s[0].Y.Should().Be(0.3);
        s[0].Width.Should().BeApproximately(0.45, 1e-9);
        s[0].Height.Should().BeApproximately(0.45, 1e-9);
        s[1].Present.Should().BeFalse();
        s[2].X.Should().Be(0);
        s[2].Width.Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void SegmentationTrackAdapter_MapsBoxesAndHandlesEmpty()
    {
        VideoSegmentationTrack Make(IReadOnlyList<VideoSegmentationBoxFrame>? boxes) =>
            new("g1", 0, "person", 0, 5, 3, 10, 0.9, 0.9, "k", "h", "m", null, boxes);
        IReadOnlyList<SubjectSample> s = SubjectPathAdapters.FromSegmentationTrack(Make(
            [new(0, 0.1, 0.2, 0.3, 0.4), new(0.1, 0.1, 0.2, 0, 0.4)]));
        s.Should().HaveCount(2);
        s[0].Should().Be(new SubjectSample(0, 0.1, 0.2, 0.3, 0.4, true));
        s[1].Present.Should().BeFalse();
        SubjectPathAdapters.FromSegmentationTrack(Make(null)).Should().BeEmpty();
        SubjectPathAdapters.FromSegmentationTrack(Make([])).Should().BeEmpty();
    }
}
