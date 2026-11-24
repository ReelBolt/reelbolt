using AwesomeAssertions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public sealed class ProjectFileQuotaTests
{
    [Fact]
    public void CountedSize_is_zero_for_a_library_sound_and_for_no_row()
    {
        ProjectFileQuota.CountedSize(new ProjectFile { SizeBytes = 900, SourceKind = ProjectFileQuota.SfxLibrarySourceKind }).Should().Be(0);
        ProjectFileQuota.CountedSize(null).Should().Be(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("github")]
    [InlineData("local_folder")]
    public void CountedSize_is_the_full_size_for_every_other_file(string? sourceKind)
    {
        ProjectFileQuota.CountedSize(new ProjectFile { SizeBytes = 900, SourceKind = sourceKind }).Should().Be(900);
    }
}
