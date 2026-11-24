using System;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

public class MagicSnifferTests
{
    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    public static TheoryData<byte[], MagicKind> Heads => new()
    {
        { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, MagicKind.Png },
        { new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, MagicKind.Jpeg },
        { Utf8("RIFF\0\0\0\0WEBP"), MagicKind.WebP },
        { Utf8("GIF89a"), MagicKind.Gif },
        { Utf8("%PDF-1.7"), MagicKind.Pdf },
        { Utf8("PK\x03\x04rest"), MagicKind.Zip },
        { Utf8("PK\x05\x06rest"), MagicKind.Zip },
        { new byte[] { 0x1F, 0x8B, 0x08 }, MagicKind.Gzip },
        { new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }, MagicKind.SevenZip },
        { Utf8("Rar!\x1A\x07"), MagicKind.Rar },
        { Utf8("BZh9"), MagicKind.Bzip2 },
        { new byte[] { 0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00 }, MagicKind.Xz },
        { new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02 }, MagicKind.Elf },
        { Utf8("MZ\x90\x00"), MagicKind.PortableExecutable },
        { new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, MagicKind.MachO },
        { new byte[] { 0xFE, 0xED, 0xFA, 0xCF }, MagicKind.MachO },
        { new byte[] { 0x00, 0x61, 0x73, 0x6D, 0x01 }, MagicKind.Wasm },
        { Utf8("wOFF" + "\0\0\0\0"), MagicKind.Woff },
        { Utf8("wOF2" + "\0\0\0\0"), MagicKind.Woff2 },
        { new byte[] { 0x4F, 0x54, 0x54, 0x4F, 0x00, 0x01 }, MagicKind.OpenType },
        { new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x01 }, MagicKind.TrueType },
        { Utf8("<!DOCTYPE html><html></html>"), MagicKind.Html },
        { Utf8("<HTML><head></head></HTML>"), MagicKind.Html },
        { Utf8("  <svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"), MagicKind.Svg },
        { Utf8("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"), MagicKind.Svg },
        { Utf8("<?xml version=\"1.0\"?><root/>"), MagicKind.Xml },
        { Utf8("{\"a\":1}"), MagicKind.Text },
        { Utf8("body{}"), MagicKind.Text },
        { new byte[] { 0x41, 0x00, 0x42 }, MagicKind.Unknown },
        { Utf8("\uFEFFhello world"), MagicKind.Text },
    };

    [Theory]
    [MemberData(nameof(Heads))]
    public void Sniff_classifies_head(byte[] head, MagicKind expected)
        => MagicSniffer.Sniff(head).Should().Be(expected);

    [Fact]
    public void Head_with_control_byte_other_than_whitespace_is_not_text()
        => MagicSniffer.Sniff(new byte[] { (byte)'a', 0x07, (byte)'b' }).Should().Be(MagicKind.Unknown);

    [Fact]
    public void Empty_head_is_text()
        => MagicSniffer.Sniff(Array.Empty<byte>()).Should().Be(MagicKind.Text);
}
