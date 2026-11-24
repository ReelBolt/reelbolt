using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The export IS the truth for colour: this test pins a frame through
/// <see cref="GradeFilterBuilder.Build"/> and the REAL ffmpeg, and commits the resulting numbers to
/// <c>web/lib/editing/color/__fixtures__/grade-parity.json</c> so the viewer's CPU reference (and the
/// WebGL shader behind it) can be checked against the same pixels. Regenerate with
/// <c>UPDATE_GRADE_PARITY_FIXTURE=1</c>; otherwise every case is re-rendered and any channel that
/// differs by more than one code value, or any chain-text change, fails — the drift alarm for the export.
/// </summary>
public class GradeParityFixtureTests
{
    private const string UpdateVariable = "UPDATE_GRADE_PARITY_FIXTURE";

    // 8x8 blocks: even, so the yuv conversion ffmpeg inserts for `hue` cannot bleed the patch centre.
    private const int PatchSize = 8;
    private const int Columns = 8;
    private const int Width = Columns * PatchSize;
    private const int Height = 8 * PatchSize;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record Case(string Name, GradeStack Stack);

    public sealed record RenderedCase(string Name, GradeStack Stack, string Chain, List<int[]> Out);

    public sealed record Fixture(string Ffmpeg, string Invocation, List<int[]> Patches, List<RenderedCase> Cases);

    [FfmpegFact]
    public void Every_case_re_renders_to_the_pinned_fixture()
    {
        List<int[]> patches = Patches();
        byte[] input = Frame(patches);
        List<Case> cases = Cases();
        string ffmpegVersion = FfmpegVersion();
        string invocation = "ffmpeg -v error -f rawvideo -pix_fmt rgb24 -s " + Width + "x" + Height +
            " -i in.rgb -vf format=rgb24,{chain},format=rgb24 -frames:v 1 -f rawvideo -pix_fmt rgb24 out.rgb";
        string path = FixturePath();

        List<RenderedCase> rendered = cases.Select(c =>
        {
            List<GradeFilterBuilder.LutRequest> luts = [];
            int lutIndex = 0;
            string chain = GradeFilterBuilder.Build(c.Stack, _ => throw new InvalidOperationException("parity cases carry no LUT"), luts, ref lutIndex);
            luts.Should().BeEmpty();
            byte[] output = RunFfmpeg(input, chain);
            return new RenderedCase(c.Name, c.Stack, chain, ReadPatches(output, patches.Count));
        }).ToList();

        // Sanity: an identity chain is a pass-through, so the readback must equal the patches exactly.
        RenderedCase identity = rendered.Single(r => r.Name == "identity");
        MaxChannelDifference(patches, identity.Out).Should().Be(0, "format=rgb24 on rgb24 is a pass-through");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            string json = JsonSerializer.Serialize(new Fixture(ffmpegVersion, invocation, patches, rendered), Json) + "\n";
            File.WriteAllText(path, json);
            return;
        }

        Fixture fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(path), Json)!;
        List<string> problems = [];

        // A different ffmpeg build is not a failure by itself: the chain text and the pixels (within 1 code
        // value) are the pin. The version is reported so a drift on another host is easy to explain.
        if (fixture.Ffmpeg != ffmpegVersion)
            Console.WriteLine($"GradeParityFixture: fixture made with \"{fixture.Ffmpeg}\", this host runs \"{ffmpegVersion}\"");
        fixture.Patches.Count.Should().Be(patches.Count, "the committed patch set is part of the pin");
        fixture.Cases.Select(c => c.Name).Should().Equal(cases.Select(c => c.Name));

        foreach (RenderedCase actual in rendered)
        {
            RenderedCase pinned = fixture.Cases.Single(c => c.Name == actual.Name);
            if (pinned.Chain != actual.Chain)
                problems.Add($"{actual.Name}: chain changed (fixture \"{pinned.Chain}\", now \"{actual.Chain}\")");
            for (int i = 0; i < patches.Count && i < pinned.Out.Count; i++)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs(pinned.Out[i][channel] - actual.Out[i][channel]);
                    if (delta > 1)
                        problems.Add($"{actual.Name}: patch {i} channel {channel} moved {pinned.Out[i][channel]} -> {actual.Out[i][channel]}");
                }
            }

            if (pinned.Out.Count != patches.Count)
                problems.Add($"{actual.Name}: fixture has {pinned.Out.Count} outputs, expected {patches.Count}");
        }

        problems.Should().BeEmpty($"the export no longer matches the committed fixture; regenerate with {UpdateVariable}=1 if the change is intended");
    }

    // ── the frame ────────────────────────────────────────────────────────────

    /// <summary>A 33-step neutral ramp (0, 8, … 248, 255) then the primaries, secondaries, skin tones and dark/bright tints.</summary>
    private static List<int[]> Patches()
    {
        List<int[]> patches = [];
        for (int v = 0; v <= 248; v += 8) patches.Add([v, v, v]);
        patches.Add([255, 255, 255]);
        patches.AddRange(
        [
            [255, 0, 0], [0, 255, 0], [0, 0, 255],             // primaries
            [255, 255, 0], [0, 255, 255], [255, 0, 255],       // secondaries
            [230, 180, 150], [200, 140, 110], [140, 90, 70],   // skin tones
            [64, 0, 0], [0, 64, 0], [0, 0, 64], [40, 30, 50],  // dark and saturated
            [255, 128, 0], [128, 0, 255], [0, 200, 180], [255, 240, 200], // bright and saturated
            [2, 0, 0], [0, 2, 0], [0, 0, 2], [4, 3, 2],         // near-black tints
            [253, 255, 255], [255, 253, 255], [255, 255, 253],  // near-white tints
        ]);
        return patches;
    }

    private static byte[] Frame(List<int[]> patches)
    {
        byte[] rgb = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int patch = (y / PatchSize) * Columns + (x / PatchSize);
                int i = (y * Width + x) * 3;
                if (patch < patches.Count)
                {
                    rgb[i] = (byte)patches[patch][0];
                    rgb[i + 1] = (byte)patches[patch][1];
                    rgb[i + 2] = (byte)patches[patch][2];
                }
            }
        }

        return rgb;
    }

    private static List<int[]> ReadPatches(byte[] rgb, int count)
    {
        List<int[]> patches = [];
        for (int p = 0; p < count; p++)
        {
            int x = (p % Columns) * PatchSize + PatchSize / 2;
            int y = (p / Columns) * PatchSize + PatchSize / 2;
            int i = (y * Width + x) * 3;
            patches.Add([rgb[i], rgb[i + 1], rgb[i + 2]]);
        }

        return patches;
    }

    private static int MaxChannelDifference(List<int[]> a, List<int[]> b) =>
        a.Zip(b, (p, q) => Enumerable.Range(0, 3).Max(c => Math.Abs(p[c] - q[c]))).Max();

    // ── ffmpeg ───────────────────────────────────────────────────────────────

    /// <summary>One rgb24 frame through <c>format=rgb24,&lt;chain&gt;,format=rgb24</c>; returns the output frame.</summary>
    private static byte[] RunFfmpeg(byte[] rgb, string chain)
    {
        string dir = Path.Combine(Path.GetTempPath(), "grade-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "in.rgb"), output = Path.Combine(dir, "out.rgb");
            File.WriteAllBytes(input, rgb);
            string graph = chain.Length == 0 ? "format=rgb24" : $"format=rgb24,{chain},format=rgb24";
            ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[]
                     {
                         "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{Width}x{Height}", "-i", input,
                         "-vf", graph, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", output
                     })
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            System.Threading.Tasks.Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException("ffmpeg did not finish within 120 s");
            }

            string err = stderr.GetAwaiter().GetResult();
            p.ExitCode.Should().Be(0, err);
            byte[] frame = File.ReadAllBytes(output);
            frame.Length.Should().Be(Width * Height * 3);
            return frame;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string FfmpegVersion()
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-version");
        using Process p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text.Split('\n')[0].Trim();
    }

    private static string FixturePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "inference")) && Directory.Exists(Path.Combine(dir.FullName, "web"))))
            dir = dir.Parent;
        string root = dir?.FullName ?? throw new InvalidOperationException("could not locate the repo root above " + AppContext.BaseDirectory);
        return Path.Combine(root, "web", "lib", "editing", "color", "__fixtures__", "grade-parity.json");
    }

    // ── the cases ────────────────────────────────────────────────────────────

    private static GradeNode Node(int n, GradePrimaries primaries, string name = "Correction") =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), name, true, primaries);

    private static GradeStack Stack(params GradeNode[] nodes) => new(true, nodes);

    private static List<Case> Cases() =>
    [
        new("identity", Stack(Node(1, new GradePrimaries()))),

        // masters alone
        new("lift-master", Stack(Node(2, new GradePrimaries(Lift: new GradeWheel(Master: 0.3))))),
        new("gamma-master", Stack(Node(3, new GradePrimaries(Gamma: new GradeWheel(Master: 0.3))))),
        new("gain-master", Stack(Node(4, new GradePrimaries(Gain: new GradeWheel(Master: 0.3))))),
        new("offset-master", Stack(Node(5, new GradePrimaries(Offset: new GradeWheel(Master: 0.1))))),
        new("exposure-plus-1", Stack(Node(6, new GradePrimaries(Exposure: 1)))),
        new("exposure-minus-1", Stack(Node(7, new GradePrimaries(Exposure: -1)))),
        new("contrast-1.4-pivot-0.435", Stack(Node(8, new GradePrimaries(Contrast: 1.4, Pivot: 0.435)))),

        // per-channel wheels, then every channel of every wheel, then the extremes
        new("lift-red-plus-0.3", Stack(Node(9, new GradePrimaries(Lift: new GradeWheel(R: 0.3))))),
        new("gamma-green-minus-0.3", Stack(Node(10, new GradePrimaries(Gamma: new GradeWheel(G: -0.3))))),
        new("gain-blue-plus-0.3", Stack(Node(11, new GradePrimaries(Gain: new GradeWheel(B: 0.3))))),
        new("all-wheels-all-channels", Stack(Node(12, new GradePrimaries(
            Lift: new GradeWheel(R: 0.2, G: -0.1, B: 0.05),
            Gamma: new GradeWheel(R: 0.1, G: 0.2, B: -0.15),
            Gain: new GradeWheel(R: -0.2, G: 0.1, B: 0.25))))),
        new("wheels-extremes-plus-1", Stack(Node(13, new GradePrimaries(
            Lift: new GradeWheel(R: 1, G: 1, B: 1),
            Gamma: new GradeWheel(R: 1, G: 1, B: 1),
            Gain: new GradeWheel(R: 1, G: 1, B: 1))))),
        new("wheels-extremes-minus-1", Stack(Node(14, new GradePrimaries(
            Lift: new GradeWheel(R: -1, G: -1, B: -1),
            Gamma: new GradeWheel(R: -1, G: -1, B: -1),
            Gain: new GradeWheel(R: -1, G: -1, B: -1))))),
        new("offset-per-channel", Stack(Node(15, new GradePrimaries(Offset: new GradeWheel(R: 0.1, G: -0.1, B: 0.2))))),

        // tint alone and tint with a gamma-green, which the builder merges into one colorbalance gm
        new("tint-plus-0.5", Stack(Node(16, new GradePrimaries(Tint: 0.5)))),
        new("tint-plus-0.5-gamma-green-plus-0.3", Stack(Node(17, new GradePrimaries(Gamma: new GradeWheel(G: 0.3), Tint: 0.5)))),

        new("temperature-plus-0.5", Stack(Node(18, new GradePrimaries(Temperature: 0.5)))),
        new("saturation-1.5", Stack(Node(19, new GradePrimaries(Saturation: 1.5)))),
        new("saturation-0.5", Stack(Node(20, new GradePrimaries(Saturation: 0.5)))),
        new("hue-30", Stack(Node(21, new GradePrimaries(Hue: 30)))),

        // two nodes apply in order
        new("two-nodes", Stack(
            Node(22, new GradePrimaries(
                Lift: new GradeWheel(Master: -0.05), Gamma: new GradeWheel(Master: 0.2),
                Gain: new GradeWheel(Master: 0.1), Exposure: 0.5)),
            Node(23, new GradePrimaries(Gamma: new GradeWheel(R: 0.1, B: -0.1))))),

        // the Warm, Cool and Filmic look nodes as `lookNode(..., 'Normal')` builds them (web/lib/editing/color/grade.ts)
        new("look-warm", Stack(Node(24, new GradePrimaries(
            Lift: new GradeWheel(R: 0.1, B: -0.1), Gamma: new GradeWheel(R: 0.05, B: -0.05),
            Contrast: 1, Saturation: 1.06), "Warm (Normal)"))),
        new("look-cool", Stack(Node(25, new GradePrimaries(
            Lift: new GradeWheel(R: -0.1, B: 0.1), Gamma: new GradeWheel(R: -0.05, B: 0.05),
            Contrast: 1, Saturation: 1.03), "Cool (Normal)"))),
        new("look-filmic", Stack(Node(26, new GradePrimaries(
            Lift: new GradeWheel(B: 0.04), Gamma: new GradeWheel(Master: -0.03),
            Contrast: 1.1, Saturation: 0.9), "Filmic (Normal)"))),
    ];
}
