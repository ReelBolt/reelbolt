#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue 8 (2026-11-03): TypeScript sources named <c>.ts</c> were stored as
/// <c>video/vnd.dlna.mpeg-tts</c>, counted as video clips and offered to video steps. For the
/// <c>.ts</c> family the bytes decide; every other name keeps its declared type.
/// </summary>
public class TransportStreamMimeSnifferTests
{
    internal static byte[] TypeScript(string text = "import { defineConfig } from 'vitest/config';\nexport default defineConfig({});\n") =>
        Encoding.UTF8.GetBytes(text);

    /// <summary>A transport stream: <paramref name="packets"/> packets, sync byte first, binary payload.</summary>
    internal static byte[] MpegTs(int packets = 6)
    {
        byte[] bytes = new byte[packets * 188];
        for (int p = 0; p < packets; p++)
        {
            int at = p * 188;
            bytes[at] = 0x47;
            bytes[at + 1] = 0x40;
            bytes[at + 2] = 0x00;
            bytes[at + 3] = 0x10;
            for (int i = 4; i < 188; i++)
                bytes[at + i] = (byte)((i * 37 + p) % 256 == 0x47 ? 0x00 : (i * 37 + p) % 256);
        }

        return bytes;
    }

    /// <summary>An M2TS (AVCHD/Blu-ray) stream: 192-byte packets whose sync byte sits after a 4-byte timecode.</summary>
    private static byte[] M2ts(int packets = 6)
    {
        byte[] bytes = new byte[packets * 192];
        for (int p = 0; p < packets; p++)
        {
            int at = p * 192;
            bytes[at] = 0x00;
            bytes[at + 1] = 0x01;
            bytes[at + 2] = 0x02;
            bytes[at + 3] = 0x03;
            bytes[at + 4] = 0x47;
            for (int i = 5; i < 192; i++)
                bytes[at + i] = (byte)(i % 0x40);
        }

        return bytes;
    }

    [Theory]
    [InlineData("video/vnd.dlna.mpeg-tts", "proxy.ts")]
    [InlineData("video/mp2t", "paint.ts")]
    [InlineData("application/octet-stream", "vitest.config.ts")]
    [InlineData(null, "index.ts")]
    [InlineData("", "index.ts")]
    [InlineData("video/mp2t", "module.mts")]
    [InlineData("video/vnd.dlna.mpeg-tts", "config.cts")]
    [InlineData("VIDEO/VND.DLNA.MPEG-TTS", "PROXY.TS")]
    public void A_typescript_source_labelled_as_video_is_stored_as_text(string? declared, string fileName)
    {
        TransportStreamMimeSniffer.Resolve(declared, fileName, TypeScript()).Should().Be("text/plain");
    }

    [Fact]
    public void A_typescript_source_starting_with_G_is_still_text()
    {
        byte[] text = TypeScript("Generic types live here.\n" + new string('x', 400) + "\n");
        TransportStreamMimeSniffer.Resolve("video/mp2t", "generics.ts", text).Should().Be("text/plain");
    }

    [Theory]
    [InlineData("video/mp2t", "video/mp2t")]
    [InlineData("video/vnd.dlna.mpeg-tts", "video/vnd.dlna.mpeg-tts")]
    [InlineData("application/octet-stream", "video/mp2t")]
    [InlineData(null, "video/mp2t")]
    public void A_real_transport_stream_stays_video(string? declared, string expected)
    {
        TransportStreamMimeSniffer.Resolve(declared, "broadcast.ts", MpegTs()).Should().Be(expected);
    }

    [Fact]
    public void An_avchd_m2ts_stream_named_mts_stays_video()
    {
        TransportStreamMimeSniffer.Resolve("video/vnd.dlna.mpeg-tts", "00001.mts", M2ts()).Should().Be("video/vnd.dlna.mpeg-tts");
        TransportStreamMimeSniffer.LooksLikeTransportStream(M2ts()).Should().BeTrue();
    }

    [Fact]
    public void Unknown_binary_bytes_keep_the_declared_type()
    {
        byte[] binary = [0x00, 0x01, 0xFF, 0xFE, 0x00, 0x00, 0x9C, 0x81, 0x00, 0x7F, 0x00, 0x03];
        TransportStreamMimeSniffer.Resolve("video/mp2t", "short.ts", binary).Should().Be("video/mp2t");
    }

    [Fact]
    public void One_sync_byte_is_not_a_transport_stream()
    {
        TransportStreamMimeSniffer.LooksLikeTransportStream("G"u8).Should().BeFalse();
        TransportStreamMimeSniffer.LooksLikeTransportStream(MpegTs(1)).Should().BeFalse("two packets must be in view");
        TransportStreamMimeSniffer.LooksLikeTransportStream(MpegTs(2)).Should().BeTrue();
    }

    [Theory]
    [InlineData("video/mp4", "clip.mp4")]
    [InlineData("video/mp2t", "clip.m2ts")]
    [InlineData("text/plain", "index.ts")]
    [InlineData("application/typescript", "index.ts")]
    [InlineData("video/vnd.dlna.mpeg-tts", "component.tsx")]
    public void Names_outside_the_family_or_a_specific_declaration_are_never_re_typed(string declared, string fileName)
    {
        TransportStreamMimeSniffer.NeedsSniff(declared, fileName).Should().BeFalse();
        TransportStreamMimeSniffer.Resolve(declared, fileName, TypeScript()).Should().Be(declared);
    }

    [Fact]
    public async Task The_stream_form_rewinds_so_every_byte_can_still_be_stored()
    {
        byte[] bytes = TypeScript();
        using MemoryStream stream = new(bytes);

        string? mime = await TransportStreamMimeSniffer.ResolveAsync("video/vnd.dlna.mpeg-tts", "proxy.ts", stream, CancellationToken.None);

        mime.Should().Be("text/plain");
        stream.Position.Should().Be(0);
    }

    [Fact]
    public async Task The_stream_form_reads_nothing_for_a_name_outside_the_family()
    {
        using MemoryStream stream = new(MpegTs());
        stream.Position = 10;

        string? mime = await TransportStreamMimeSniffer.ResolveAsync("video/mp4", "clip.mp4", stream, CancellationToken.None);

        mime.Should().Be("video/mp4");
        stream.Position.Should().Be(10);
    }
}
