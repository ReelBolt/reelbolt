#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.Archives;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

public class NormalisationCostTests(ITestOutputHelper output)
{
    [Fact]
    public void Two_thousand_entries_of_combining_marks_are_refused_fast()
    {
        // 2000 distinct 255-character names, each a base letter plus 250+ combining marks: the worst case for
        // canonical reordering if it were ever normalised. The mark cap refuses each before Normalize runs.
        (string, byte[])[] files = Enumerable.Range(0, 2000)
            .Select(i => (i.ToString("D4") + new string('́', 251) + ".css", new byte[] { 0x61 }))
            .ToArray();
        byte[] zip = ZipFixtures.Build(files);

        Stopwatch watch = Stopwatch.StartNew();
        SafeZipResult result = HostileInput.RunWithin(TimeSpan.FromSeconds(10), () => SafeZipReader.Read(new MemoryStream(zip), new SafeZipLimits()));
        watch.Stop();
        output.WriteLine($"2000 x 255-char mark-flood names refused in {watch.ElapsedMilliseconds} ms");

        result.Entries.Should().BeEmpty();
        result.Rejected.Should().HaveCount(2000).And.OnlyContain(r => r.Reason == "PATH_INVALID");
    }
}
