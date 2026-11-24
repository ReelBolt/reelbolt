using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The LUT twin of <see cref="GradeParityFixtureTests"/>: each case is a <c>.cube</c> text run through the
/// parser, the grade chain and the REAL ffmpeg (1D via <c>lut1d</c>, shaper + 3D via <c>lut1d,lut3d</c> or one
/// baked <c>lut3d</c>), committed to <c>web/lib/editing/color/__fixtures__/lut-parity.json</c> so the web sampler is
/// checked against the same pixels. Regenerate with <c>UPDATE_LUT_PARITY_FIXTURE=1</c>.
/// </summary>
public class LutParityFixtureTests
{
    private const string UpdateVariable = "UPDATE_LUT_PARITY_FIXTURE";
    private const int PatchSize = 8;
    private const int Columns = 8;
    private const int Width = Columns * PatchSize;
    private const int Height = 8 * PatchSize;
    private static readonly AssetRef LutRef = new(AssetRefKind.ProjectFile, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"), null);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record Case(string Name, string Cube, double Intensity);

    public sealed record RenderedCase(string Name, string Cube, double Intensity, string Chain, List<int[]> Out);

    public sealed record Fixture(string Ffmpeg, string Invocation, List<int[]> Patches, List<RenderedCase> Cases);

    [FfmpegFact]
    public void Every_case_re_renders_to_the_pinned_fixture()
    {
        List<int[]> patches = Patches();
        byte[] input = Frame(patches);
        string ffmpegVersion = FfmpegVersion();
        string invocation = "ffmpeg -v error -f rawvideo -pix_fmt rgb24 -s " + Width + "x" + Height +
            " -i in.rgb -vf format=rgb24,{chain},format=rgb24 -frames:v 1 -f rawvideo -pix_fmt rgb24 out.rgb";
        List<Case> cases = Cases();

        List<RenderedCase> rendered = cases.Select(c => Render(c, patches, input)).ToList();

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(FixturePath(), JsonSerializer.Serialize(new Fixture(ffmpegVersion, invocation, patches, rendered), Json) + "\n");
            return;
        }

        Fixture fixture = JsonSerializer.Deserialize<Fixture>(File.ReadAllText(FixturePath()), Json)!;
        List<string> problems = [];
        fixture.Patches.Count.Should().Be(patches.Count);
        fixture.Cases.Select(c => c.Name).Should().Equal(cases.Select(c => c.Name));
        foreach (RenderedCase actual in rendered)
        {
            RenderedCase pinned = fixture.Cases.Single(c => c.Name == actual.Name);
            if (pinned.Chain != actual.Chain) problems.Add($"{actual.Name}: chain changed (fixture \"{pinned.Chain}\", now \"{actual.Chain}\")");
            if (pinned.Cube != actual.Cube || pinned.Intensity != actual.Intensity) problems.Add($"{actual.Name}: the case input changed");
            for (int i = 0; i < patches.Count && i < pinned.Out.Count; i++)
                for (int ch = 0; ch < 3; ch++)
                    if (Math.Abs(pinned.Out[i][ch] - actual.Out[i][ch]) > 1)
                        problems.Add($"{actual.Name}: patch {i} channel {ch} moved {pinned.Out[i][ch]} -> {actual.Out[i][ch]}");
            if (pinned.Out.Count != patches.Count) problems.Add($"{actual.Name}: fixture has {pinned.Out.Count} outputs, expected {patches.Count}");
        }

        problems.Should().BeEmpty($"the export no longer matches the committed fixture; regenerate with {UpdateVariable}=1 if the change is intended");
    }

    [FfmpegFact]
    public void The_one_d_sampler_agrees_with_ffmpeg_lut1d()
    {
        List<int[]> patches = Patches();
        byte[] input = Frame(patches);
        foreach (Case c in Cases().Where(c => CubeLut.Parse(c.Cube).Kind == CubeLutKind.OneD))
        {
            RenderedCase r = Render(c, patches, input);
            CubeLut lut = CubeLut.Parse(c.Cube).WithIntensity(c.Intensity);
            for (int i = 0; i < patches.Count; i++)
            {
                (float R, float G, float B) = lut.Sample(patches[i][0] / 255f, patches[i][1] / 255f, patches[i][2] / 255f);
                Math.Abs((double)R * 255 - r.Out[i][0]).Should().BeLessThanOrEqualTo(1.5, $"{c.Name} patch {i} red");
                Math.Abs((double)G * 255 - r.Out[i][1]).Should().BeLessThanOrEqualTo(1.5, $"{c.Name} patch {i} green");
                Math.Abs((double)B * 255 - r.Out[i][2]).Should().BeLessThanOrEqualTo(1.5, $"{c.Name} patch {i} blue");
            }
        }
    }

    private static RenderedCase Render(Case c, List<int[]> patches, byte[] input)
    {
        string dir = Path.Combine(Path.GetTempPath(), "lut-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            CubeLut lut = CubeLut.Parse(c.Cube);
            GradeStack stack = new(true, [new GradeNode(Guid.NewGuid(), "Correction", true, Lut: new GradeLut(LutRef, c.Intensity))]);
            List<GradeFilterBuilder.LutRequest> requests = [];
            int index = 0;
            string chain = GradeFilterBuilder.Build(stack, n => Path.Combine(dir, $"lut-{n}.cube"), requests, ref index,
                luts: new Dictionary<AssetRef, CubeLut> { [LutRef] = lut });
            GradeFilterBuilder.LutRequest request = requests.Single();
            CubeLut atIntensity = lut.WithIntensity(c.Intensity);
            if (request.CubePath is not null)
            {
                File.WriteAllText(request.Path, atIntensity.ToShaperCubeText());
                File.WriteAllText(request.CubePath, atIntensity.ToCubeText());
            }
            else
            {
                File.WriteAllText(request.Path, atIntensity.ToCubeText());
            }

            byte[] output = RunFfmpeg(dir, input, chain);
            return new RenderedCase(c.Name, c.Cube, c.Intensity, chain.Replace(dir, "{dir}"), ReadPatches(output, patches.Count));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── the cases ────────────────────────────────────────────────────────────

    private static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static string OneD(int size, string header, Func<double, (double R, double G, double B)> f)
    {
        StringBuilder sb = new();
        sb.Append("TITLE \"parity\"\nLUT_1D_SIZE ").Append(size).Append('\n').Append(header);
        for (int i = 0; i < size; i++)
        {
            (double r, double g, double b) = f((double)i / (size - 1));
            sb.Append(F(r)).Append(' ').Append(F(g)).Append(' ').Append(F(b)).Append('\n');
        }

        return sb.ToString();
    }

    private static (double R, double G, double B) Grade3D(double r, double g, double b)
    {
        double luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        double sr = luma + (1.3 * (r - luma)), sg = luma + (1.3 * (g - luma)), sb = luma + (1.3 * (b - luma));
        return (Math.Clamp(sr + 0.03, 0, 1), Math.Clamp(sg, 0, 1), Math.Clamp((sb * 0.92) + 0.04, 0, 1));
    }

    private static string ThreeD(int n, Func<double, double, double, (double R, double G, double B)> f, StringBuilder? sb = null)
    {
        sb ??= new StringBuilder("TITLE \"parity\"\n");
        sb.Append("LUT_3D_SIZE ").Append(n).Append('\n');
        for (int b = 0; b < n; b++)
            for (int g = 0; g < n; g++)
                for (int r = 0; r < n; r++)
                {
                    (double mr, double mg, double mb) = f((double)r / (n - 1), (double)g / (n - 1), (double)b / (n - 1));
                    sb.Append(F(mr)).Append(' ').Append(F(mg)).Append(' ').Append(F(mb)).Append('\n');
                }

        return sb.ToString();
    }

    private static string Shaper3D()
    {
        StringBuilder sb = new("TITLE \"parity\"\nLUT_1D_SIZE 256\n");
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0;
            double s = Math.Log2(1 + (15 * x)) / 4;
            sb.Append(F(s)).Append(' ').Append(F(s)).Append(' ').Append(F(s)).Append('\n');
        }

        return ThreeD(9, Grade3D, sb);
    }

    private static List<Case> Cases()
    {
        string gamma = OneD(256, "DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n", x => (Math.Pow(x, 0.8), Math.Pow(x, 0.5), Math.Pow(x, 0.65)));
        string ranged = OneD(64, "LUT_1D_INPUT_RANGE -0.1 1.2\n", x => (Math.Pow(x, 0.7), Math.Pow(x, 1.3), 1 - x));
        string shaper = Shaper3D();
        return
        [
            new("one-d-gamma", gamma, 1),
            new("one-d-input-range", ranged, 1),
            new("one-d-intensity-half", gamma, 0.5),
            new("shaper-3d", shaper, 1),
            new("shaper-3d-intensity-half-baked", shaper, 0.5),
            new("three-d-control", ThreeD(5, Grade3D), 1),
        ];
    }

    // ── the frame (same patches and invocation as the grade fixture) ─────────

    private static List<int[]> Patches()
    {
        List<int[]> patches = [];
        for (int v = 0; v <= 248; v += 8) patches.Add([v, v, v]);
        patches.Add([255, 255, 255]);
        patches.AddRange(
        [
            [255, 0, 0], [0, 255, 0], [0, 0, 255],
            [255, 255, 0], [0, 255, 255], [255, 0, 255],
            [230, 180, 150], [200, 140, 110], [140, 90, 70],
            [64, 0, 0], [0, 64, 0], [0, 0, 64], [40, 30, 50],
            [255, 128, 0], [128, 0, 255], [0, 200, 180], [255, 240, 200],
            [2, 0, 0], [0, 2, 0], [0, 0, 2], [4, 3, 2],
            [253, 255, 255], [255, 253, 255], [255, 255, 253],
        ]);
        return patches;
    }

    private static byte[] Frame(List<int[]> patches)
    {
        byte[] rgb = new byte[Width * Height * 3];
        for (int y = 0; y < Height; y++)
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

    private static byte[] RunFfmpeg(string dir, byte[] rgb, string chain)
    {
        string inputPath = Path.Combine(dir, "in.rgb"), output = Path.Combine(dir, "out.rgb");
        File.WriteAllBytes(inputPath, rgb);
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[]
                 {
                     "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{Width}x{Height}", "-i", inputPath,
                     "-vf", $"format=rgb24,{chain},format=rgb24", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", output
                 })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        System.Threading.Tasks.Task<string> stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(120_000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("ffmpeg did not finish within 120 s");
        }

        p.ExitCode.Should().Be(0, stderr.GetAwaiter().GetResult());
        byte[] frame = File.ReadAllBytes(output);
        frame.Length.Should().Be(Width * Height * 3);
        return frame;
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
        return Path.Combine(root, "web", "lib", "editing", "color", "__fixtures__", "lut-parity.json");
    }
}
