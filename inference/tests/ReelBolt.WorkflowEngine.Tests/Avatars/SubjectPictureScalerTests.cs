#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L6 of docs/design/avatar-objects.md: the copy of a subject's picture that leaves the host. First-party argv,
/// still-image demuxers and the file protocol only, scaled down only; the original is sent only when the copy cannot
/// be made and the original is small.
/// </summary>
public class SubjectPictureScalerTests
{
    /// <summary>Stands in for ffmpeg: records the argv and writes <see cref="Output"/> to the output path (the last argument).</summary>
    private sealed class FakeFfmpeg : IProcessRunner
    {
        public byte[]? Output { get; set; }
        public int ExitCode { get; set; }
        public List<(string File, IReadOnlyList<string> Args)> Runs { get; } = [];

        public Task<ProcessRunResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Runs.Add((fileName, arguments));
            if (Output is not null) File.WriteAllBytes(arguments[^1], Output);
            return Task.FromResult(new ProcessRunResult(ExitCode, string.Empty, string.Empty, TimedOut: false));
        }
    }

    private static readonly byte[] Jpeg640x360 =
    [
        0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x68, 0x02, 0x80, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1, 0xFF, 0xD9
    ];

    private static ProjectSubjectPicture Picture(byte[] bytes) =>
        new(Guid.NewGuid(), Guid.NewGuid(), AvatarReferenceRole.Front, "image/png", bytes, 4000, 3000);

    [Fact]
    public async Task The_copy_is_a_jpeg_made_with_fixed_arguments_and_its_size_is_read_from_its_own_header()
    {
        FakeFfmpeg ffmpeg = new() { Output = Jpeg640x360 };
        FfmpegSubjectPictureScaler scaler = new(ffmpeg, NullLogger<FfmpegSubjectPictureScaler>.Instance);

        ScaledSubjectPicture? copy = await scaler.ScaleAsync(Picture(AvatarTestHarness.Png(4000, 3000)), CancellationToken.None);

        copy!.ContentType.Should().Be("image/jpeg");
        copy.Bytes.Should().Equal(Jpeg640x360);
        (copy.Width, copy.Height).Should().Be((640, 360), "the size of the copy, not of the original");
        copy.ToDataUri().Should().Be("data:image/jpeg;base64," + Convert.ToBase64String(Jpeg640x360));

        (string file, IReadOnlyList<string> args) = ffmpeg.Runs.Should().ContainSingle().Subject;
        file.Should().Be("ffmpeg");
        string input = args[args.ToList().IndexOf("-i") + 1];
        args.Should().Equal(FfmpegSubjectPictureScaler.BuildArgs(input, args[^1]));
        args.Should().ContainInOrder("-protocol_whitelist", "file", "-format_whitelist", "image2,png_pipe,jpeg_pipe,webp_pipe", "-i");
        args.Should().Contain("scale=w='min(1536,iw)':h='min(1536,ih)':force_original_aspect_ratio=decrease", "scaled down only, never up");
        args.Should().ContainInOrder("-frames:v", "1", "-c:v", "mjpeg");
        Directory.Exists(Path.GetDirectoryName(input)).Should().BeFalse("the scratch directory is always removed");
    }

    [Fact]
    public async Task When_the_copy_cannot_be_made_a_small_original_is_sent_and_a_large_one_is_not()
    {
        FakeFfmpeg failing = new() { ExitCode = 1 };
        FfmpegSubjectPictureScaler scaler = new(failing, NullLogger<FfmpegSubjectPictureScaler>.Instance);
        byte[] small = AvatarTestHarness.Png(800, 600);

        ScaledSubjectPicture? asItIs = await scaler.ScaleAsync(Picture(small), CancellationToken.None);
        asItIs!.Bytes.Should().Equal(small);
        asItIs.ContentType.Should().Be("image/png");
        (asItIs.Width, asItIs.Height).Should().Be((800, 600));

        byte[] large = new byte[FfmpegSubjectPictureScaler.MaxUnscaledBytes + 1];
        (await scaler.ScaleAsync(Picture(large), CancellationToken.None)).Should().BeNull("too much of a picture to send unscaled");

        // An answer that is not a JPEG is not trusted either.
        FakeFfmpeg odd = new() { Output = [1, 2, 3] };
        ScaledSubjectPicture? fallback = await new FfmpegSubjectPictureScaler(odd, NullLogger<FfmpegSubjectPictureScaler>.Instance)
            .ScaleAsync(Picture(small), CancellationToken.None);
        fallback!.Bytes.Should().Equal(small);
    }
}
