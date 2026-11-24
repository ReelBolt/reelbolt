#nullable enable
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Archives;

public class UnpackExclusionsTests
{
    [Theory]
    [InlineData("node_modules/a/index.js", ArchiveUnpackExclusions.Dependencies)]
    [InlineData("pkg/node_modules/a.js", ArchiveUnpackExclusions.Dependencies)]
    [InlineData(".git/config", ArchiveUnpackExclusions.Vcs)]
    [InlineData("sub/.git/HEAD", ArchiveUnpackExclusions.Vcs)]
    public void Always_skipped_paths_have_a_reason(string path, string reason) =>
        ArchiveUnpackExclusions.AlwaysSkipReason(path).Should().Be(reason);

    [Fact]
    public void Plain_source_is_not_skipped() =>
        ArchiveUnpackExclusions.AlwaysSkipReason("src/app.ts").Should().BeNull();

    [Fact]
    public void Dist_only_zip_keeps_dist()
    {
        var filter = ArchiveUnpackExclusions.BuildFilter(new[] { "dist/index.js", "dist/a.css" });
        filter("dist/index.js").Should().BeNull();
    }

    [Fact]
    public void Dist_plus_source_zip_skips_dist()
    {
        var filter = ArchiveUnpackExclusions.BuildFilter(new[] { "dist/index.js", "src/app.ts" });
        filter("dist/index.js").Should().Be(ArchiveUnpackExclusions.BuildOutput);
        filter("src/app.ts").Should().BeNull();
    }

    [Fact]
    public void Only_always_skipped_files_outside_dist_do_not_cause_dist_to_be_dropped()
    {
        var filter = ArchiveUnpackExclusions.BuildFilter(new[] { "dist/index.js", "node_modules/x/y.js" });
        filter("dist/index.js").Should().BeNull();
        filter("node_modules/x/y.js").Should().Be(ArchiveUnpackExclusions.Dependencies);
    }

    [Fact]
    public void Reasons_list_has_the_four_codes() =>
        ArchiveUnpackExclusions.Reasons.Should().BeEquivalentTo(new[]
        {
            "EXCLUDED_DEPENDENCIES", "EXCLUDED_VCS", "EXCLUDED_BUILD_OUTPUT", "EXCLUDED_SYSTEM",
        });
}

public class UnpackLimitsTests
{
    [Fact]
    public void Unpack_profile_matches_the_contract()
    {
        SafeZipLimits limits = UnpackLimits.ToSafeZipLimits();
        limits.MaxArchiveBytes.Should().Be(64L * 1024 * 1024);
        limits.MaxEntries.Should().Be(20_000);
        limits.MaxIncludedEntries.Should().Be(2_000);
        limits.MaxTotalBytes.Should().Be(128L * 1024 * 1024);
        limits.MaxEntryBytes.Should().Be(32L * 1024 * 1024);
        limits.MaxRatio.Should().Be(100);
        limits.MaxDepth.Should().Be(16);
    }

    [Fact]
    public void Default_limits_leave_included_entries_unbounded() =>
        new SafeZipLimits().MaxIncludedEntries.Should().BeNull();

    [Fact]
    public void More_accepted_entries_than_the_cap_aborts_with_TOO_MANY_FILES()
    {
        byte[] zip = DesignSystems.Archives.ZipFixtures.Build(
            ("a.txt", new byte[] { 1 }), ("b.txt", new byte[] { 2 }), ("c.txt", new byte[] { 3 }));
        SafeZipResult result = SafeZipReader.Read(new MemoryStream(zip), new SafeZipLimits(MaxIncludedEntries: 2), _ => true);
        result.Aborted.Should().BeTrue();
        result.AbortReason.Should().Be("TOO_MANY_FILES");
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Entries_filtered_out_do_not_count_toward_the_cap()
    {
        byte[] zip = DesignSystems.Archives.ZipFixtures.Build(
            ("a.txt", new byte[] { 1 }), ("b.txt", new byte[] { 2 }), ("c.txt", new byte[] { 3 }));
        SafeZipResult result = SafeZipReader.Read(new MemoryStream(zip), new SafeZipLimits(MaxIncludedEntries: 2), p => p != "c.txt");
        result.Aborted.Should().BeFalse();
        result.Entries.Select(e => e.Path).Should().BeEquivalentTo(new[] { "a.txt", "b.txt" });
    }
}
