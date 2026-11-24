#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

/// <summary>
/// The safe zip reader is the only archive reader the product will ever use, so every hostile shape a
/// customer bundle can take is pinned here: zip-slip, NUL and device names, duplicates by case and by
/// non-ASCII/NFC-equivalent names, symlinks, entry/total/archive caps, compression bombs, lying sizes, nested archives and
/// non-seekable input. Each refusal names an exact reason and never lets the bad path into the result.
/// </summary>
public class SafeZipReaderTests
{
    private static readonly byte[] Tiny = { 9, 8, 7 };

    private static SafeZipResult R(byte[] zip, SafeZipLimits? limits = null)
        => SafeZipReader.Read(new MemoryStream(zip), limits ?? new SafeZipLimits());

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[] RandomBytes(int count, int seed = 20261015)
    {
        byte[] data = new byte[count];
        new Random(seed).NextBytes(data);
        // Keep the first byte away from any nested-archive magic so the fixture tests what it names.
        data[0] = 0x00;
        return data;
    }

    private static string DeepPath(int segments)
    {
        string[] parts = new string[segments];
        for (int i = 0; i < segments - 1; i++)
            parts[i] = "d";
        parts[segments - 1] = "f.txt";
        return string.Join('/', parts);
    }

    /// <summary>Builds one stored entry under an all-'a' name, then patches that name in both headers.</summary>
    private static byte[] NameArchive(string hostileName, byte[]? content = null)
    {
        content ??= Tiny;
        string placeholder = new string('a', hostileName.Length);
        return ZipFixtures.PatchName(
            ZipFixtures.BuildStored((placeholder, content)),
            placeholder,
            hostileName);
    }

    private static (string Name, byte[] Data)[] ManyZeroByteEntries(int count)
    {
        (string Name, byte[] Data)[] files = new (string Name, byte[] Data)[count];
        for (int i = 0; i < count; i++)
            files[i] = ($"f{i:D4}.txt", Array.Empty<byte>());
        return files;
    }

    // ---------------------------------------------------------------- happy path

    [Fact]
    public void Valid_archive_returns_exact_bytes()
    {
        byte[] readme = Utf8("ReelBolt brand bundle: read me first.\nLine two.");
        byte[] logo = Utf8("<svg viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></svg>");
        byte[] unicode = Utf8("grüße, ünïcödé — exact bytes");

        byte[] zip = ZipFixtures.Build(
            ("readme.txt", readme),
            ("assets/logo.svg", logo),
            ("notes/unicode.txt", unicode));

        SafeZipResult result = R(zip);

        result.Aborted.Should().BeFalse();
        result.AbortReason.Should().BeNull();
        result.Rejected.Should().BeEmpty();
        result.Entries.Should().HaveCount(3);

        result.Entries.Should().ContainSingle(e => e.Path == "readme.txt").Which.Content.Should().Equal(readme);
        result.Entries.Should().ContainSingle(e => e.Path == "assets/logo.svg").Which.Content.Should().Equal(logo);
        result.Entries.Should().ContainSingle(e => e.Path == "notes/unicode.txt").Which.Content.Should().Equal(unicode);
    }

    [Fact]
    public void Empty_archive_is_ok()
    {
        SafeZipResult result = R(ZipFixtures.Build());

        result.Aborted.Should().BeFalse();
        result.Rejected.Should().BeEmpty();
        result.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- path traversal

    [Fact]
    public void Zip_slip_is_rejected()
    {
        SafeZipResult result = R(NameArchive("../../evil"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_TRAVERSAL");
        result.Entries.Should().BeEmpty();
        result.Aborted.Should().BeFalse();
    }

    [Fact]
    public void Absolute_path_is_rejected()
    {
        SafeZipResult result = R(NameArchive("/etc/passwd"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_TRAVERSAL");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Backslash_traversal_is_rejected()
    {
        SafeZipResult result = R(NameArchive("..\\x"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_TRAVERSAL");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Windows_absolute_path_is_rejected()
    {
        SafeZipResult result = R(NameArchive("C:\\x"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_TRAVERSAL");
        result.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- invalid names

    [Fact]
    public void Nul_name_is_rejected()
    {
        SafeZipResult result = R(NameArchive("a\0b"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_INVALID");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Dot_segment_is_rejected()
    {
        SafeZipResult result = R(NameArchive("a/./b"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_INVALID");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Empty_segment_is_rejected()
    {
        SafeZipResult result = R(NameArchive("a//b"));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_INVALID");
        result.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData("con.txt")]
    [InlineData("NUL")]
    [InlineData("lpt1.log")]
    public void Reserved_device_names_are_rejected(string name)
    {
        SafeZipResult result = R(NameArchive(name));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_INVALID");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Trailing_dot_segment_is_rejected()
    {
        SafeZipResult result = R(NameArchive("name."));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_INVALID");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Depth_thirteen_is_rejected()
    {
        SafeZipResult result = R(ZipFixtures.Build((DeepPath(13), Tiny)));

        result.Rejected.Should().Contain(r => r.Reason == "PATH_TOO_DEEP");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Depth_twelve_is_accepted()
    {
        string path = DeepPath(12);
        SafeZipResult result = R(ZipFixtures.Build((path, Tiny)));

        result.Rejected.Should().BeEmpty();
        result.Entries.Should().ContainSingle(e => e.Path == path);
    }

    // ---------------------------------------------------------------- duplicates

    [Fact]
    public void Duplicate_by_case_is_rejected()
    {
        SafeZipResult result = R(ZipFixtures.Build(("A.txt", Utf8("first")), ("a.TXT", Utf8("second"))));

        result.Entries.Should().ContainSingle(e => e.Path == "A.txt");
        result.Rejected.Should().Contain(r => r.Reason == "DUPLICATE_PATH");
        result.Entries.Should().NotContain(e => e.Path == "a.TXT");
    }

    // T-021 reverses SEC-4's "refuse every non-ASCII name": the worker ships ICU, names are NFC-normalised and
    // de-duplicated on NFC + ordinal-ignore-case. NfcInvariantTests covers the NFC/NFD merge in both globalization modes.

    [IcuFact]
    public void Accented_cjk_cyrillic_and_emoji_names_are_accepted_and_kept()
    {
        string[] names = { "caf\u00e9/theme.css", "\u65e5\u672c\u8a9e.json", "\u0442\u0435\u043c\u0430.css", "\U0001F600/\u2764.txt" };
        SafeZipResult result = R(ZipFixtures.Build(names.Select(n => (n, Utf8("a{}"))).ToArray()));

        result.Rejected.Should().BeEmpty();
        result.Entries.Select(e => e.Path).Should().BeEquivalentTo(names);
    }

    [IcuFact]
    public void Turkish_dotted_and_dotless_i_are_distinct_names_not_duplicates()
    {
        // Invariant case folding deliberately does NOT merge i, dotless U+0131 and dotted capital U+0130: a dedupe
        // key may only merge names that every filesystem agrees are one name, and Turkish folding is locale-specific.
        SafeZipResult result = R(ZipFixtures.Build(
            ("\u0131.css", Utf8("dotless")),
            ("i.css", Utf8("plain")),
            ("\u0130.css", Utf8("dotted-capital"))));

        result.Rejected.Should().BeEmpty();
        result.Entries.Select(e => e.Path).Should().BeEquivalentTo("\u0131.css", "i.css", "\u0130.css");
    }

    [IcuFact]
    public void Non_ascii_case_variants_are_duplicates()
    {
        SafeZipResult result = R(ZipFixtures.Build(("\u00c9cole.css", Utf8("a")), ("\u00e9cole.css", Utf8("b"))));

        result.Entries.Should().ContainSingle();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("DUPLICATE_PATH");
    }

    [Theory]
    [InlineData("a\u202eb.css")]      // right-to-left override
    [InlineData("a\u202ab.css")]      // embedding
    [InlineData("a\u2066b.css")]      // isolate
    [InlineData("a\u200bb.css")]      // zero-width space
    [InlineData("a\u200db.css")]      // zero-width joiner
    [InlineData("a\ufeffb.css")]      // BOM / zero-width no-break space
    [InlineData("a\U000E0041b.css")]  // tag character
    [InlineData("a\u0085b.css")]      // C1 control (NEL)
    [InlineData("a\u2028b.css")]      // line separator
    public void Invisible_and_direction_control_characters_are_refused(string name)
    {
        SafeZipResult result = R(ZipFixtures.Build((name, Utf8("a{}")), ("ok.css", Utf8("b{}"))));

        result.Entries.Should().ContainSingle().Which.Path.Should().Be("ok.css");
        result.Rejected.Should().ContainSingle().Which.Should().Be(new SafeZipRejected(name, "PATH_INVALID"));
    }

    [Theory]
    [InlineData("a\u3164b.css")]       // Hangul filler
    [InlineData("a\uffa0b.css")]       // halfwidth Hangul filler
    [InlineData("a\u115fb.css")]
    [InlineData("a\u1160b.css")]
    [InlineData("a\u2800b.css")]       // Braille blank
    [InlineData("a\u034fb.css")]       // combining grapheme joiner
    [InlineData("a\ufe0fb.css")]       // variation selector
    [InlineData("a\U000E0100b.css")]   // variation selector supplement
    [InlineData("a.js\u00a0")]         // trailing NBSP
    [InlineData("a.js\u3000")]         // trailing ideographic space
    [InlineData("\u00a0a.css")]        // leading NBSP
    [InlineData(" a.css")]              // leading space
    [InlineData("d/a.js\u3164")]
    [InlineData("a.html\u2800")]
    [InlineData("COM\u00b9.txt")]      // superscript-digit device names
    [InlineData("lpt\u00b2")]
    [InlineData("LPT\u00b3.log")]
    public void Invisible_non_format_characters_and_unicode_spaces_at_the_ends_are_refused(string name)
    {
        SafeZipResult result = R(ZipFixtures.Build((name, Utf8("a{}")), ("ok.css", Utf8("b{}"))));

        result.Entries.Should().ContainSingle().Which.Path.Should().Be("ok.css");
        result.Rejected.Should().ContainSingle().Which.Should().Be(new SafeZipRejected(name, "PATH_INVALID"));
    }

    [IcuFact]
    public void A_combining_mark_flood_in_one_segment_is_refused_before_normalisation()
    {
        string name = "a" + new string('\u0301', 33) + ".css";
        SafeZipResult result = R(ZipFixtures.Build((name, Utf8("a{}"))));

        result.Entries.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("PATH_INVALID");
    }

    [IcuFact]
    public void Few_combining_marks_are_still_accepted()
    {
        string name = "a" + new string('\u0301', 3) + ".css";
        R(ZipFixtures.Build((name, Utf8("a{}")))).Entries.Should().ContainSingle();
    }

    [Fact]
    public void A_path_over_1024_characters_is_refused_so_the_worker_never_exceeds_what_the_api_accepts()
    {
        string name = string.Join('/', Enumerable.Repeat(new string('a', 100), 11)) + ".css"; // 1104 chars
        SafeZipResult result = R(ZipFixtures.Build((name, Utf8("a{}"))));

        result.Entries.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("PATH_INVALID");
    }

    [IcuFact]
    public void Total_normalised_name_characters_per_archive_are_capped()
    {
        // 1200 distinct non-ASCII names of ~190 characters = ~228k > the 200k archive cap.
        (string, byte[])[] files = Enumerable.Range(0, 1200)
            .Select(i => (new string('\u00e9', 180) + i.ToString("D5") + ".css", Utf8("a")))
            .ToArray();
        SafeZipResult result = R(ZipFixtures.Build(files));

        result.Entries.Count.Should().BeLessThan(1200);
        result.Rejected.Should().NotBeEmpty().And.OnlyContain(r => r.Reason == "PATH_INVALID");
    }

    [Fact]
    public void Invalid_utf8_that_decodes_to_the_replacement_character_is_refused()
    {
        // ED A0 80 is a UTF-8 encoded lone surrogate; the BCL decodes it to U+FFFD, which a name must never carry.
        byte[] zip = ZipFixtures.BuildStored(("aXXXb.css", Utf8("a{}")));
        byte[] patched = ReplaceAscii(zip, "XXX", new byte[] { 0xED, 0xA0, 0x80 });

        SafeZipResult result = R(patched);

        result.Entries.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("PATH_INVALID");
    }

    private static byte[] ReplaceAscii(byte[] zip, string from, byte[] to)
    {
        byte[] result = (byte[])zip.Clone();
        byte[] needle = Encoding.ASCII.GetBytes(from);
        for (int i = 0; i + needle.Length <= result.Length; i++)
        {
            if (result.AsSpan(i, needle.Length).SequenceEqual(needle))
                to.CopyTo(result.AsSpan(i));
        }

        return result;
    }

    // ---------------------------------------------------------------- special files

    [Fact]
    public void Symlink_entry_is_rejected()
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("link-to-elsewhere", CompressionLevel.NoCompression);
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using Stream entryStream = entry.Open();
            entryStream.Write(Tiny, 0, Tiny.Length);
        }

        SafeZipResult result = R(stream.ToArray());

        result.Rejected.Should().Contain(r => r.Reason == "SPECIAL_FILE");
        result.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- count and size caps

    [Fact]
    public void Too_many_entries_aborts()
    {
        SafeZipResult result = R(ZipFixtures.Build(ManyZeroByteEntries(2001)));

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOO_MANY_ENTRIES");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Too_many_entries_aborts_even_when_filtered_out()
    {
        SafeZipResult result = SafeZipReader.Read(
            new MemoryStream(ZipFixtures.Build(ManyZeroByteEntries(2001))),
            new SafeZipLimits(),
            _ => false);

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOO_MANY_ENTRIES");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Exactly_the_entry_cap_is_accepted()
    {
        SafeZipResult result = R(ZipFixtures.Build(ManyZeroByteEntries(2000)));

        result.Aborted.Should().BeFalse();
        result.Entries.Should().HaveCount(2000);
    }

    [Fact]
    public void Zip64_archive_with_zip64_headers_is_read_exactly()
    {
        byte[] payload = Utf8("zip64 payload");
        byte[] zip = RawZip.Build(
            new[]
            {
                new RawZip.Entry("a.txt", payload, payload.Length, Method: 0, Zip64: true),
                new RawZip.Entry("b.txt", RawZip.Deflate(payload), payload.Length, Method: 8, Zip64: true),
            },
            zip64Eocd: true);

        SafeZipResult result = R(zip);

        result.Aborted.Should().BeFalse();
        result.Rejected.Should().BeEmpty();
        result.Entries.Select(e => e.Path).Should().Equal("a.txt", "b.txt");
        result.Entries.Should().OnlyContain(e => e.Content.SequenceEqual(payload));
    }

    /// <summary>Marks the 32-bit local-header offset 0xFFFFFFFF and overwrites the zip64 offset (last 8 bytes of the zip64 extra) of the first central record.</summary>
    private static byte[] WithForgedZip64Offset(ulong forged)
    {
        byte[] payload = Utf8("zip64 payload");
        byte[] zip = RawZip.Build(
            new[] { new RawZip.Entry("a.txt", payload, payload.Length, Method: 0, Zip64: true) },
            zip64Eocd: true);

        int cd = -1;
        for (int i = 0; i + 4 <= zip.Length && cd < 0; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02)
                cd = i;
        }

        cd.Should().BeGreaterThanOrEqualTo(0);
        int nameLength = zip[cd + 28] | (zip[cd + 29] << 8);
        int offsetAt = cd + 46 + nameLength + 4 + 16;
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(zip, cd + 42);
        BitConverter.GetBytes(forged).CopyTo(zip, offsetAt);
        return zip;
    }

    [Theory]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 29)]
    [InlineData(long.MaxValue - 1000)]
    [InlineData(ulong.MaxValue)]
    [InlineData(0x8000000000000000UL)]
    public void Forged_zip64_local_offset_never_throws_and_yields_no_entry(ulong forged)
    {
        byte[] zip = WithForgedZip64Offset(forged);

        SafeZipResult result = null!;
        Action act = () => result = R(zip);

        act.Should().NotThrow();
        result.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(10L)]
    [InlineData(29L)]
    [InlineData(1L << 40)]
    public void Forged_zip64_local_offset_past_the_archive_is_an_unreadable_entry(long forged)
    {
        byte[] zip = WithForgedZip64Offset((ulong)forged);

        SafeZipResult result = null!;
        Action act = () => result = R(zip);

        act.Should().NotThrow();
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Zip64_claim_over_the_entry_cap_aborts_before_any_header_is_parsed()
    {
        byte[] zip = RawZip.Build(new[] { new RawZip.Entry("a.txt", Tiny, 3, Method: 0) }, zip64Eocd: true);

        SafeZipResult result = R(zip, new SafeZipLimits(MaxEntries: 0));

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOO_MANY_ENTRIES");
    }

    [Theory]
    [InlineData(1009, false)] // 1009 / 10 = 100: at the ceiling, passes the declared-size pre-check
    [InlineData(1010, true)]  // 1010 / 10 = 101: over it, refused without reading
    public void Declared_ratio_boundary_is_one_hundred_to_one(int declared, bool refusedWithoutReading)
    {
        byte[] zip = RawZip.Build(new[] { new RawZip.Entry("r.bin", new byte[10], declared) });

        SafeZipResult result = R(zip);

        result.Rejected.Should().ContainSingle();
        if (refusedWithoutReading)
        {
            result.Rejected[0].Reason.Should().Be("RATIO_EXCEEDED");
            result.InflatedBytes.Should().Be(0);
        }
        else
        {
            // Passed the pre-check, then the 10 bytes are not a valid deflate stream.
            result.Rejected[0].Reason.Should().Be("ENTRY_UNREADABLE");
        }
    }

    [Fact]
    public void Declared_huge_entry_is_rejected()
    {
        SafeZipResult result = R(
            ZipFixtures.Build(("big.bin", RandomBytes(5000))),
            new SafeZipLimits(MaxEntryBytes: 1000));

        result.Rejected.Should().Contain(r => r.Reason == "ENTRY_TOO_LARGE");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Ratio_bomb_is_rejected_without_reading()
    {
        byte[] megabyte = new byte[1024 * 1024];
        (string Name, byte[] Data)[] files = new (string Name, byte[] Data)[200];
        for (int i = 0; i < files.Length; i++)
            files[i] = ($"bomb{i:D3}.bin", megabyte);

        SafeZipResult result = R(ZipFixtures.Build(files));

        result.Aborted.Should().BeFalse();
        result.Entries.Should().BeEmpty();
        result.Rejected.Should().HaveCount(200);
        result.Rejected.Should().OnlyContain(r => r.Reason == "RATIO_EXCEEDED");
        result.InflatedBytes.Should().Be(0, "a bomb refused on its declared sizes must not be inflated at all");
    }

    [Fact]
    public void Total_too_large_aborts()
    {
        SafeZipResult result = R(
            ZipFixtures.BuildStored(
                ("one.bin", RandomBytes(500, seed: 1)),
                ("two.bin", RandomBytes(500, seed: 2)),
                ("three.bin", RandomBytes(500, seed: 3))),
            new SafeZipLimits(MaxTotalBytes: 1000, MaxEntryBytes: 600));

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOTAL_TOO_LARGE");
        result.Entries.Should().BeEmpty();
    }

    // The previous version of this test accepted any of three outcomes and only used a STORED fixture, so a
    // DEFLATE entry that the BCL silently truncated at its declared length went unnoticed. Each lie below has
    // one exact expected outcome, and the entry must never be returned (truncated or otherwise).

    [Fact]
    public void Lying_stored_size_aborts_as_bomb_and_never_lands()
    {
        byte[] patched = ZipFixtures.PatchUncompressedSize(ZipFixtures.BuildStored(("lie.bin", RandomBytes(5000))), 10);

        SafeZipResult result = R(patched);

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("BOMB_SUSPECTED");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Lying_deflate_size_100000_zeros_declared_10_is_refused_not_returned_truncated()
    {
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("lie.bin", RawZip.Deflate(new byte[100_000]), DeclaredSize: 10),
        });

        SafeZipResult result = R(zip);

        result.Entries.Should().BeEmpty("the BCL would have returned 10 bytes");
        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("BOMB_SUSPECTED");
    }

    [Fact]
    public void Lying_deflate_size_20_MiB_zeros_declared_50000_is_refused_not_returned_truncated()
    {
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("lie.bin", RawZip.Deflate(new byte[20 * 1024 * 1024]), DeclaredSize: 50_000),
        });

        SafeZipResult result = R(zip, new SafeZipLimits(MaxEntryBytes: 1_000_000));

        // Zeros expand far past 100:1 while the surplus is being probed, long before the 1,000,000 cap is
        // reached, so the lie is judged on what was seen (> 2x declared) and the archive is refused.
        result.Entries.Should().BeEmpty("the BCL would have returned 50000 bytes");
        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("BOMB_SUSPECTED");
        result.InflatedBytes.Should().BeLessThan(5_000_000, "the probe stops at the ratio ceiling, not at 20 MiB");
    }

    [Fact]
    public void Lying_deflate_size_with_incompressible_surplus_hits_the_entry_cap()
    {
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("lie.bin", RawZip.Deflate(RandomBytes(1_500_000)), DeclaredSize: 50_000),
        });

        SafeZipResult result = R(zip, new SafeZipLimits(MaxEntryBytes: 1_000_000));

        result.Entries.Should().BeEmpty();
        result.Aborted.Should().BeFalse();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("ENTRY_TOO_LARGE");
    }

    [Fact]
    public void Lying_deflate_size_slightly_over_declared_is_unreadable()
    {
        // 150 real bytes declared as 100: more than declared but within 2x, so it is a per-entry refusal.
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("lie.bin", RawZip.Deflate(RandomBytes(150)), DeclaredSize: 100),
        });

        SafeZipResult result = R(zip);

        result.Entries.Should().BeEmpty();
        result.Aborted.Should().BeFalse();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("ENTRY_UNREADABLE");
    }

    [Fact]
    public void Deflate_entry_that_ends_short_of_its_declared_size_is_unreadable()
    {
        byte[] zip = RawZip.Build(new[]
        {
            new RawZip.Entry("lie.bin", RawZip.Deflate(RandomBytes(50)), DeclaredSize: 100),
        });

        SafeZipResult result = R(zip);

        result.Entries.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("ENTRY_UNREADABLE");
    }

    [Fact]
    public void Entry_cap_stops_a_stream_longer_than_its_declared_size_even_when_it_expands_slowly()
    {
        // declared=10, actual=5000, MaxEntryBytes=1000: only ReadBounded-style capping can say ENTRY_TOO_LARGE.
        // If the cap is removed the reader reports BOMB_SUSPECTED (5000 > 2*10) instead and this fails.
        byte[] zip = ZipFixtures.PatchUncompressedSize(ZipFixtures.BuildStored(("big.bin", RandomBytes(5000))), 10);

        SafeZipResult result = R(zip, new SafeZipLimits(MaxEntryBytes: 1000));

        result.Aborted.Should().BeFalse();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("ENTRY_TOO_LARGE");
        result.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- nested archives

    [Fact]
    public void Nested_zip_is_rejected()
    {
        byte[] inner = ZipFixtures.Build(("inside.txt", Utf8("nested")));
        SafeZipResult result = R(ZipFixtures.Build(("inner.zip", inner)));

        result.Rejected.Should().Contain(r => r.Reason == "NESTED_ARCHIVE");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Nested_gzip_is_rejected()
    {
        byte[] gzip = { 0x1F, 0x8B, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03 };
        SafeZipResult result = R(ZipFixtures.Build(("inner.gz", gzip)));

        result.Rejected.Should().Contain(r => r.Reason == "NESTED_ARCHIVE");
        result.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- archive-level refusals

    [Fact]
    public void Archive_over_max_archive_bytes_aborts()
    {
        byte[] zip = ZipFixtures.Build(("payload.bin", RandomBytes(500)));
        zip.Length.Should().BeGreaterThan(100);

        SafeZipResult result = R(zip, new SafeZipLimits(MaxArchiveBytes: 100));

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("ARCHIVE_TOO_LARGE");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Garbage_bytes_are_invalid()
    {
        SafeZipResult result = R(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22 });

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("ARCHIVE_INVALID");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Non_seekable_stream_is_invalid()
    {
        byte[] zip = ZipFixtures.Build(("a.txt", Tiny));

        SafeZipResult result = SafeZipReader.Read(new NonSeekableStream(zip), new SafeZipLimits());

        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("ARCHIVE_INVALID");
        result.Entries.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- filtering and cancellation

    [Fact]
    public void Include_filter_does_not_read_filtered_entries()
    {
        byte[] css = Utf8("body { margin: 0; }");
        byte[] js = Utf8("export const x = 1;");

        SafeZipResult result = SafeZipReader.Read(
            new MemoryStream(ZipFixtures.Build(("theme.css", css), ("app.js", js))),
            new SafeZipLimits(),
            path => path.EndsWith(".css", StringComparison.Ordinal));

        result.Entries.Should().ContainSingle(e => e.Path == "theme.css").Which.Content.Should().Equal(css);
        result.Entries.Should().NotContain(e => e.Path == "app.js");
        result.Rejected.Should().NotContain(r => r.Path == "app.js");
        result.Rejected.Should().BeEmpty();
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        byte[] zip = ZipFixtures.Build(("a.txt", Tiny));
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Action act = () => SafeZipReader.Read(new MemoryStream(zip), new SafeZipLimits(), null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    private sealed class NonSeekableStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public NonSeekableStream(byte[] data) => _data = data;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _data.Length - _position);
            if (available <= 0)
                return 0;

            Array.Copy(_data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
