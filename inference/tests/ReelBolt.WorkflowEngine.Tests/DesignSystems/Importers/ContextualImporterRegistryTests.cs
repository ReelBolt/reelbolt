using System;
using System.Collections.Generic;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class ContextualImporterRegistryTests
{
    [Fact]
    public void Contextual_importer_receives_claimed_and_the_whole_set()
    {
        var importer = new RecordingContextualImporter();
        ImportFile claim = File("claim.json", "{\"source\":[\"a\"],\"platforms\":{}}");
        ImportFile second = File("other.json", "{}");
        ImportFile third = File("third.json", "{}");

        DesignImportRegistry.ImportAll([claim, second, third], [importer]);

        importer.ClaimedCount.Should().Be(1);
        importer.AllCount.Should().Be(3);
    }

    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), string.Empty, null);

    private sealed class RecordingContextualImporter : IContextualDesignImporter
    {
        public string Id => "recording";

        public int ClaimedCount { get; private set; }

        public int AllCount { get; private set; }

        public bool CanRead(string path, ReadOnlySpan<byte> head) => path == "claim.json";

        public DesignImportResult Import(IReadOnlyList<ImportFile> files) => Import(files, files);

        public DesignImportResult Import(IReadOnlyList<ImportFile> claimed, IReadOnlyList<ImportFile> all)
        {
            ClaimedCount = claimed.Count;
            AllCount = all.Count;
            return new DesignImportResult(null, null, Array.Empty<ImportSourceRef>(), Array.Empty<ImportDiagnostic>());
        }
    }
}
