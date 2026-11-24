using System;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: reads the pixel size of an ExtractFrame image from its own header, never from a probe process.</summary>
public class ImageDimensionsTests
{
    private static byte[] Png(int w, int h)
    {
        byte[] b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        b[11] = 13; // IHDR length
        "IHDR"u8.CopyTo(b.AsSpan(12));
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    private static byte[] Jpeg(int w, int h)
    {
        // SOI, APP0 (len 16), DQT-ish filler segment, SOF0.
        var bytes = new System.Collections.Generic.List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange(new byte[14]);
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 3 });
        return [.. bytes];
    }

    [Fact]
    public void Reads_png_ihdr() => ImageDimensions.TryRead(Png(1920, 1080)).Should().Be((1920, 1080));

    [Fact]
    public void Reads_jpeg_sof_past_other_segments() => ImageDimensions.TryRead(Jpeg(1280, 720)).Should().Be((1280, 720));

    [Fact]
    public void Truncated_or_unknown_bytes_answer_null()
    {
        ImageDimensions.TryRead([]).Should().BeNull();
        ImageDimensions.TryRead(Png(10, 10)[..20]).Should().BeNull();
        ImageDimensions.TryRead("GIF89a......"u8.ToArray()).Should().BeNull();
        ImageDimensions.TryRead(Jpeg(10, 10)[..22]).Should().BeNull();
    }

    [Fact]
    public void A_zero_size_is_not_a_size() => ImageDimensions.TryRead(Png(0, 5)).Should().BeNull();
}
