#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>
/// The raw-plate harness (initiative insert-tracking-smoothness, T-011 part 1). Run: <c>REELBOLT_JITTER_BENCH=1 REELBOLT_PLATES_LABEL=&lt;label&gt;
/// dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter "FullyQualifiedName~Promo.Plates"</c> from <c>inference/</c>; writes
/// <c>/tmp/insert-tracking/plates/&lt;label&gt;/{results.json, *.csv, report.md}</c>.
/// </summary>
public class PlatesBench
{
    private readonly ITestOutputHelper _out;

    public PlatesBench(ITestOutputHelper output) => _out = output;

    [JitterBenchFact]
    public async Task Score_the_production_tracker_on_the_raw_promo_plates()
    {
        PlatesEnv env = PlatesEnv.FromEnvironment();
        _out.WriteLine($"label {env.Label} -> {env.OutDir}");
        PlatesResults r = await PlatesRun.RunAsync(env, _out.WriteLine);
        _out.WriteLine(PlatesReport.Render(r));
    }
}

/// <summary>Where the harness reads and writes.</summary>
internal sealed record PlatesEnv(string Label, string OutDir, string Scratch, string CacheDir)
{
    public const string Root = "/tmp/insert-tracking/plates";

    public static PlatesEnv FromEnvironment() =>
        FromLabel(
            Environment.GetEnvironmentVariable("REELBOLT_PLATES_LABEL") is { Length: > 0 } l ? l : "unlabelled",
            Environment.GetEnvironmentVariable("REELBOLT_PLATES_DIR") is { Length: > 0 } d ? d : Root);

    public static PlatesEnv FromLabel(string label, string root)
    {
        if (label.Length == 0 || label.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')) || label.StartsWith('.'))
            throw new ArgumentException("REELBOLT_PLATES_LABEL may contain only letters, digits, '-', '_' and '.' and may not start with '.' (got '" + label + "').");
        string outDir = Path.Combine(root, label);
        Directory.CreateDirectory(outDir);
        string scratch = Path.Combine(root, "scratch", label);
        Directory.CreateDirectory(scratch);
        string cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(cache);
        return new PlatesEnv(label, outDir, scratch, cache);
    }
}

/// <summary>One raw plate with the staging artifact the live engine produced for it.</summary>
internal sealed record PlateCase(string Id, string Film, string PlatePath, string StagingArtifact, string StagingArtifactId)
{
    public static string Promo => Environment.GetEnvironmentVariable("REELBOLT_JITTER_PROMO") is { Length: > 0 } p ? p : "/tmp/insert-tracking/promo";

    public static IReadOnlyList<PlateCase> All { get; } =
    [
        new("filmA", "Film A (Claude Code cold open, slow push-in)", Path.Combine(Promo, "plate-95b0c6b8.mp4"),
            Path.Combine(Promo, "artifact-d0fd129e-03c8-4200-a051-f83dd3484186.json"), "artifact-d0fd129e"),
        new("filmB", "Film B (desk scene, travelling plate)", Path.Combine(Promo, "plate-e8904619.mp4"),
            Path.Combine(Promo, "artifact-c712a68f-f5bc-4919-93ad-490811ab8ca1.json"), "artifact-c712a68f")
    ];
}
