#nullable enable
using System;
using System.IO;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The two live answers that our own preconditions used to refuse. F2 replays the committed answer under
/// <c>TestData/prompted-region-effects/f2-face</c>. F1 (about 6 MB) is not committed: it is read from the
/// directory named by the <c>REELBOLT_SEGMENTATION_TRIALS</c> environment variable (<c>F1/FACE/response.json</c>
/// below it) and skipped silently where that is unset.
/// </summary>
/// <remarks>
/// <para>
/// <b>F2/FACE named the slot rule.</b> The request asked for <c>max_num_objects: 4</c> with the prompt
/// "person" and the vendor answered with 6–10 object slots on every one of its 141 frames. The reader's
/// <c>maxObjects + 1</c> precondition refused the whole document for output the vendor produces
/// ordinarily; the analyze path selects ONE object, so the extra slots never mattered.
/// </para>
/// <para>
/// <b>F1/FACE named the box rule.</b> With the prompt "face" the box-agreement floor refused frame 125
/// object 2 at 94.66% — and the document's worst object is lower still (84.83% at frame 132 object 2).
/// D-05 already decided the vendor's boxes are centre-anchored, approximate metadata and are never a
/// correctness measure: an object that touches the frame edge gets a clipped box, so the number is a
/// diagnostic. It is reported here, and only structural RLE errors remain refusals.
/// </para>
/// </remarks>
public sealed class FalAiVideoSegmentationTrialReplayTests
{
    private const string Model = FalAiVideoSegmentationClient.Sam31VideoRle;

    /// <summary>The FACE clip both trials segmented: 1366x768, 141 frames, 24 fps.</summary>
    private const int Width = 1366;
    private const int Height = 768;
    private const int Frames = 141;

    /// <summary>
    /// The recorded answer of a run: the committed copy where there is one (F2), else the directory named by
    /// <c>REELBOLT_SEGMENTATION_TRIALS</c>. Null when neither is present on this machine.
    /// </summary>
    private static string? TrialResponsePath(string run)
    {
        string committed = Path.Combine(
            AppContext.BaseDirectory, "TestData", "prompted-region-effects", run.ToLowerInvariant() + "-face", "response.json");
        if (File.Exists(committed)) return committed;

        string? configured = Environment.GetEnvironmentVariable("REELBOLT_SEGMENTATION_TRIALS");
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string path = Path.Combine(configured, run, "FACE", "response.json");
        return File.Exists(path) ? path : null;
    }

    private static VideoSegmentationResult Read(string path, int maxObjects)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        return Sam31VideoRleResponseReader.Read(
            document.RootElement, Model, Width, Height, expectedFrames: Frames, maxObjects);
    }

    [Fact]
    public void F1_face_decodes_all_141_frames_and_reports_the_box_disagreement_instead_of_refusing()
    {
        string? path = TrialResponsePath("F1");
        if (path is null) return;

        VideoSegmentationResult result = Read(path, maxObjects: 4);

        (result.Width, result.Height).Should().Be((Width, Height));
        result.Frames.Should().HaveCount(Frames);
        result.InputFrames.Should().Be(Frames);
        result.Frames.Should().OnlyContain(frame => frame.Objects.Count > 0 && frame.Boxes != null,
            "no frame of this answer is empty");

        result.Diagnostics.Should().NotBeNull("the reader records what it saw instead of refusing it");
        VideoSegmentationDiagnostics diagnostics = result.Diagnostics!;
        diagnostics.ObservedMaxObjectSlots.Should().Be(3);
        diagnostics.TotalObjectSlots.Should().Be(326);

        diagnostics.BoxAgreementMin.Should().NotBeNull();
        diagnostics.BoxAgreementMin!.Value.Should().BeApproximately(84.8276, 0.001,
            "the worst object is frame 132 object 2; the box is clipped where the object reaches the frame edge");
        diagnostics.BoxAgreementMin.Should().BeLessThan(Sam31VideoRleResponseReader.MinBoxAgreementPercent,
            "the old floor is exactly what refused this valid answer (frame 125 object 2, 94.66%, was the first)");
        diagnostics.BoxAgreementFrame.Should().Be(132);
        diagnostics.BoxAgreementObject.Should().Be(2);
    }

    [Fact]
    public void F2_face_decodes_all_141_frames_with_ten_object_slots_and_reports_the_observed_count()
    {
        string? path = TrialResponsePath("F2");
        if (path is null) return;

        VideoSegmentationResult result = Read(path, maxObjects: 4);

        result.Frames.Should().HaveCount(Frames);
        result.Frames.Should().OnlyContain(frame => frame.Objects.Count >= 6,
            "the vendor ignored max_num_objects 4 on every frame of this answer");

        result.Diagnostics.Should().NotBeNull();
        VideoSegmentationDiagnostics diagnostics = result.Diagnostics!;
        diagnostics.ObservedMaxObjectSlots.Should().Be(10)
            .And.BeGreaterThan(4 + 1, "the request's cap plus one slot of slack is what used to refuse this answer");
        diagnostics.TotalObjectSlots.Should().Be(1_274);

        (Frames * diagnostics.TotalObjectSlots).Should().Be(179_634)
            .And.BeLessThanOrEqualTo(Sam31VideoRleResponseReader.MaxFrameSlotWork,
                "the work bound has to sit above the vendor's real answer");

        diagnostics.BoxAgreementMin.Should().NotBeNull();
        diagnostics.BoxAgreementMin!.Value.Should().BeApproximately(85.2071, 0.001);
        diagnostics.BoxAgreementFrame.Should().Be(120);
        diagnostics.BoxAgreementObject.Should().Be(8);
    }
}
