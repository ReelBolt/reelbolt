#nullable enable
using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;
using MediaDerivativeRequests = ReelBolt.Inference.Api.Controllers.Dto.MediaDerivativeRequests;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// RD-15 / d2 N4, N5: a request that names no kinds asked for every enum member, so <c>thumbnail</c>
/// and <c>waveform</c> were advertised on every upload and editor-open although the derivative run
/// produces neither, and their chips never filled. The default set is exactly what a request that
/// names no kinds should produce — which is the run's whole catalogue except <c>stills</c>, extracted
/// on demand (T-101) when someone asks to look at an output, never for every upload.
/// </summary>
public class MediaDerivativeDefaultKindsTests
{
    [Fact]
    public void The_default_request_names_exactly_the_kinds_the_derivative_run_produces()
    {
        MediaDerivativeKind[] produced = Enum.GetValues<MediaDerivativeKindSlug>()
            .Where(slug => slug is not (MediaDerivativeKindSlug.Stills or MediaDerivativeKindSlug.StabilizedProxy))
            .Select(MediaDerivativeDispatcher.ToPersistedKind)
            .ToArray();

        MediaDerivativeRequests.DefaultKinds.Should().BeEquivalentTo(produced);
    }

    [Fact]
    public void The_default_request_does_not_advertise_a_thumbnail_or_a_bitmap_waveform()
    {
        MediaDerivativeRequests.DefaultKinds.Should().NotContain(MediaDerivativeKind.Thumbnail);
        MediaDerivativeRequests.DefaultKinds.Should().NotContain(MediaDerivativeKind.Waveform);
    }
}
