#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

/// <summary>
/// Resource-exhaustion proofs for the safe reader (SEC-1): each test builds the hostile archive by hand,
/// measures the allocation of the calling thread and the wall clock, and asserts the reader refuses early.
/// Every read carries a hard cancellation timeout so an unfixed reader fails instead of hanging.
/// </summary>
public class SafeZipExhaustionTests
{
    private const long MiB = 1024 * 1024;
    private readonly ITestOutputHelper _output;

    public SafeZipExhaustionTests(ITestOutputHelper output) => _output = output;

    private (SafeZipResult? Result, long AllocatedBytes, TimeSpan Elapsed, bool TimedOut) Measure(byte[] zip, SafeZipLimits? limits = null)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch sw = Stopwatch.StartNew();
        SafeZipResult? result = null;
        bool timedOut = false;
        try
        {
            result = SafeZipReader.Read(new MemoryStream(zip), limits ?? new SafeZipLimits(), null, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
        }

        sw.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"allocated={allocated / MiB} MiB elapsed={sw.Elapsed.TotalSeconds:F2}s timedOut={timedOut} "
            + $"aborted={result?.Aborted} reason={result?.AbortReason} rejected={result?.Rejected.Count}");
        return (result, allocated, sw.Elapsed, timedOut);
    }

    /// <summary>
    /// 2000 entries, each 8 MiB of zeros that start with zip magic (so each is rejected AFTER inflating),
    /// each compressing to ~8 KiB while the central directory claims 100 KB so the declared/claimed ratio
    /// passes the 100:1 check. Unbounded, this is 16 GiB of inflate work from a 16 MiB upload.
    /// </summary>
    private static byte[] ForgedNestedBomb(int entries = 2000)
    {
        byte[] blob = RawZip.Deflate(RawZip.ZerosWithPrefix(8 * 1024 * 1024, 0x50, 0x4B, 0x03, 0x04));
        List<RawZip.Entry> list = new(entries);
        for (int i = 0; i < entries; i++)
            list.Add(new RawZip.Entry($"n{i:D4}.bin", blob, 8 * MiB, ClaimedCompressedSize: 100_000));
        return RawZip.Build(list);
    }

    [Fact]
    public void Forged_ratio_nested_archive_bomb_is_refused_early_with_bounded_work()
    {
        byte[] zip = ForgedNestedBomb();
        _output.WriteLine($"archive bytes={zip.Length / MiB} MiB");

        (SafeZipResult? result, long allocated, TimeSpan elapsed, bool timedOut) = Measure(zip);

        timedOut.Should().BeFalse("the reader must stop long before inflating 16 GiB");
        result!.Aborted.Should().BeTrue();
        result.Entries.Should().BeEmpty();
        allocated.Should().BeLessThan(320 * MiB, "inflate work is bounded by the 64 MiB total budget");
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Inflated_bytes_of_rejected_entries_count_against_the_total_budget()
    {
        // 20 entries x 8 MiB nested = 160 MiB of inflate against a 20 MiB budget: the budget must trip after a
        // few rejections, not let 20 silent rejections each inflate in full. The ratio ceiling is lifted so this
        // isolates the budget accounting from the (separately tested) real-ratio check.
        byte[] zip = ForgedNestedBomb(entries: 20);

        (SafeZipResult? result, _, _, bool timedOut) = Measure(zip, new SafeZipLimits(MaxTotalBytes: 20 * MiB, MaxRatio: 1_000_000));

        timedOut.Should().BeFalse();
        result!.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOTAL_TOO_LARGE");
        result.InflatedBytes.Should().BeLessThanOrEqualTo(20 * MiB + 8 * MiB, "at most one more entry than the budget");
    }

    [Fact]
    public void Entry_count_is_checked_before_the_central_directory_is_materialised()
    {
        const int Count = 400_000;
        byte[] empty = Array.Empty<byte>();
        List<RawZip.Entry> list = new(Count);
        for (int i = 0; i < Count; i++)
            list.Add(new RawZip.Entry("a", empty, 0, Method: 0));
        byte[] zip = RawZip.Build(list, zip64Eocd: true);
        zip.Length.Should().BeLessThan(32 * 1024 * 1024, "the fixture must fit the 32 MiB archive cap");
        _output.WriteLine($"archive bytes={zip.Length / MiB} MiB entries={Count}");

        (SafeZipResult? result, long allocated, _, _) = Measure(zip);

        result!.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOO_MANY_ENTRIES");
        allocated.Should().BeLessThan(8 * MiB, "no ZipArchiveEntry may be created for a hostile count");
    }

    [Fact]
    public void An_eocd_that_understates_the_entry_count_cannot_smuggle_a_huge_central_directory()
    {
        const int Count = 400_000;
        byte[] empty = Array.Empty<byte>();
        List<RawZip.Entry> list = new(Count);
        for (int i = 0; i < Count; i++)
            list.Add(new RawZip.Entry("a", empty, 0, Method: 0));
        byte[] zip = RawZip.Build(list, forgedEocdCount: 5);

        (SafeZipResult? result, long allocated, _, _) = Measure(zip);

        result!.Aborted.Should().BeTrue();
        allocated.Should().BeLessThan(8 * MiB);
    }

    [Fact]
    public void A_forged_compressed_length_does_not_buy_a_better_ratio()
    {
        // 8 MiB of zeros really compresses ~1000:1. The central directory claims 100 KB so the declared/claimed
        // ratio (83) passes the pre-check; the real ratio, measured on the bytes the inflater consumed, must not.
        byte[] blob = RawZip.Deflate(new byte[8 * 1024 * 1024]);
        byte[] zip = RawZip.Build(new[] { new RawZip.Entry("z.bin", blob, 8 * MiB, ClaimedCompressedSize: 100_000) });

        (SafeZipResult? result, _, _, _) = Measure(zip);

        result!.Entries.Should().BeEmpty();
        result.Rejected.Should().ContainSingle().Which.Reason.Should().Be("RATIO_EXCEEDED");
        result.InflatedBytes.Should().BeLessThan(2 * MiB, "the read stops at the ratio ceiling, not at 8 MiB");
    }

    [Fact]
    public void Honest_varied_css_well_inside_the_ratio_is_accepted()
    {
        byte[] text = System.Text.Encoding.ASCII.GetBytes(string.Concat(System.Linq.Enumerable.Range(0, 20000).Select(i => $"--brand-{i}: #{i * 2654435761 % 16777216:x6};\n")));
        byte[] zip = RawZip.Build(new[] { new RawZip.Entry("theme.css", RawZip.Deflate(text), text.Length) });

        (SafeZipResult? result, _, _, _) = Measure(zip);

        result!.Entries.Should().ContainSingle().Which.Content.Should().Equal(text);
    }
}
