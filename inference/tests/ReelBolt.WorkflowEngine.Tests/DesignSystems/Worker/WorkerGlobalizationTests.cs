#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// T-021: zip entry names get full Unicode support, which needs ICU in the import worker. The worker's csproj
/// setting is what flows into its runtimeconfig.json (and so into the image); the image-level proof (ICU libraries
/// mapped into the live process) is the container smoke test.
/// </summary>
public class WorkerGlobalizationTests
{
    private static string WorkerRuntimeConfig()
    {
        string assemblyDir = Path.GetDirectoryName(typeof(WorkerOptions).Assembly.Location)!;
        string beside = Path.Combine(assemblyDir, "ReelBolt.ImportWorker.runtimeconfig.json");
        if (File.Exists(beside))
            return beside;

        // The worker is only referenced from the test project: its own runtimeconfig sits in its own bin folder.
        string? dir = assemblyDir;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "ReelBolt.ImportWorker")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repository layout must contain src/ReelBolt.ImportWorker");

        return Directory.EnumerateFiles(Path.Combine(dir!, "src", "ReelBolt.ImportWorker", "bin"), "ReelBolt.ImportWorker.runtimeconfig.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
    }

    [Fact]
    public void The_workers_runtimeconfig_does_not_switch_on_invariant_globalization()
    {
        using JsonDocument config = JsonDocument.Parse(File.ReadAllText(WorkerRuntimeConfig()));
        JsonElement options = config.RootElement.GetProperty("runtimeOptions");
        options.TryGetProperty("configProperties", out JsonElement properties).Should().BeTrue();

        properties.TryGetProperty("System.Globalization.Invariant", out JsonElement invariant).Should().BeTrue(
            "the csproj states InvariantGlobalization=false explicitly so a base-image default can never apply silently");
        invariant.GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void The_worker_csproj_states_invariant_globalization_is_false()
    {
        string? dir = Path.GetDirectoryName(typeof(WorkerOptions).Assembly.Location);
        while (dir is not null && !File.Exists(Path.Combine(dir, "src", "ReelBolt.ImportWorker", "ReelBolt.ImportWorker.csproj")))
            dir = Path.GetDirectoryName(dir);

        string csproj = File.ReadAllText(Path.Combine(dir!, "src", "ReelBolt.ImportWorker", "ReelBolt.ImportWorker.csproj"));
        csproj.Should().Contain("<InvariantGlobalization>false</InvariantGlobalization>");
    }

    [IcuFact]
    public void Icu_data_is_loaded_so_turkish_culture_and_normalisation_work()
    {
        "i".ToUpper(new CultureInfo("tr-TR")).Should().Be("İ");
        "é".Normalize(System.Text.NormalizationForm.FormC).Should().Be("é");
    }
}
