using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Editing.Color;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Editing.Color;

/// <summary>Anchors, round trips, display targets, determinism and the shared fixture for the camera profile library.</summary>
public sealed class CameraProfilesTests(ITestOutputHelper output)
{
    private static readonly string[] ExpectedIds =
    [
        "appleLog", "djiDLog", "djiDLogM", "sonySLog3", "panasonicVLog", "canonCLog3",
        "arriLogC3", "arriLogC4", "fujiFLog", "fujiFLog2", "nikonNLog", "redLog3G10",
    ];

    // Pinned SHA-256 of the 65^3 float table bytes per profile. Regenerate with UPDATE_CAMERA_PROFILE_HASHES=1.
    private static readonly Dictionary<string, string> PinnedHashes = new()
    {
        ["appleLog"] = "9229e8f26f58926e47069200833228ea82802320184253b94bf992510d6ba9c6",
        ["djiDLog"] = "b5a56258ff990ce1054971cf4c4437227b17be9b6003b2bc06d95539ccddf6c5",
        ["djiDLogM"] = "b5a56258ff990ce1054971cf4c4437227b17be9b6003b2bc06d95539ccddf6c5",
        ["sonySLog3"] = "41732f719813173c4d2c4c5ffcf59e29d47fa65318bf81777af908d027b61dc7",
        ["panasonicVLog"] = "e79782f448ebcf58ff8ef5d0b76cab96c28d3591e5618f48dcc3fd463d588ced",
        ["canonCLog3"] = "b33bf58b3e0f90f81868417593f011ef6f35c2ec12983f7cbb13bb0c7b5751bc",
        ["arriLogC3"] = "dff466f459045ea19b0dff6540f250a4f8992acc29a1bd56e6fcf961391ef732",
        ["arriLogC4"] = "74e8a81a49a6cb8e733bd16e55a6990ec227c877f6946f8af10e055188a64ff8",
        ["fujiFLog"] = "be8dbb11beb736c846da80d9fdd276dfa0f32292bfba14a216a570482d4a1e87",
        ["fujiFLog2"] = "809174bb1cf06814e388e5e4621379542c03b749d3b4303196dfc1fd09d8030f",
        ["nikonNLog"] = "56faa5712c2c0ab794869e3906f5b5e4c9aa109cec9a0636d31a09f1218009bd",
        ["redLog3G10"] = "4480fcce8714b31d49ae394468a6d26e09a7243fff7414a55cfa8628bc36a2ed",
    };

    // Encode (scene linear, 0.18 = grey) -> normalised code, from the published encode formulas.
    private static double Encode(string id, double v)
    {
        switch (id)
        {
            case "appleLog":
                {
                    const double r0 = -0.05641088, rt = 0.01, c = 47.28711236, b = 0.00964052, g = 0.08550479, d = 0.69336945;
                    if (v >= rt) return g * Math.Log2(v + b) + d;
                    return v >= r0 ? c * (v - r0) * (v - r0) : 0;
                }

            case "djiDLog":
            case "djiDLogM":
                return v <= 0.0078 ? 6.025 * v + 0.0929 : Math.Log10(v * 0.9892 + 0.0108) * 0.256663 + 0.584555;
            case "sonySLog3":
                return v >= 0.01125 ? (420.0 + Math.Log10((v + 0.01) / (0.18 + 0.01)) * 261.5) / 1023.0
                                    : (v * (171.2102946929 - 95.0) / 0.01125 + 95.0) / 1023.0;
            case "panasonicVLog":
                return v < 0.01 ? 5.6 * v + 0.125 : 0.241514 * Math.Log10(v + 0.00873) + 0.598206;
            case "canonCLog3":
                {
                    double x = v / 0.9;
                    if (x < -0.014) return -0.36726845 * Math.Log10(1 - 14.98325 * x) + 0.12783901;
                    if (x <= 0.014) return 1.9754798 * x + 0.12512219;
                    return 0.36726845 * Math.Log10(14.98325 * x + 1) + 0.12240537;
                }

            case "arriLogC3":
                return v > 0.010591 ? 0.247190 * Math.Log10(5.555556 * v + 0.052272) + 0.385537 : 5.367655 * v + 0.092809;
            case "arriLogC4":
                {
                    double a = (262144.0 - 16.0) / 117.45, b = (1023.0 - 95.0) / 1023.0, c = 95.0 / 1023.0;
                    double s = 7.0 * Math.Log(2.0) * Math.Pow(2.0, 7.0 - 14.0 * c / b) / (a * b);
                    double t = (Math.Pow(2.0, 14.0 * (-c / b) + 6.0) - 64.0) / a;
                    return v >= t ? (Math.Log2(a * v + 64.0) - 6.0) / 14.0 * b + c : (v - t) / s;
                }

            case "fujiFLog":
                return v >= 0.00089 ? 0.344676 * Math.Log10(0.555556 * v + 0.009468) + 0.790453 : 8.735631 * v + 0.092864;
            case "fujiFLog2":
                return v >= 0.000889 ? 0.245281 * Math.Log10(5.555556 * v + 0.064829) + 0.384316 : 8.799461 * v + 0.092864;
            case "nikonNLog":
                return (v < 0.328 ? 650.0 * Math.Cbrt(v + 0.0075) : 150.0 * Math.Log(v) + 619.0) / 1023.0;
            case "redLog3G10":
                {
                    double x = v + 0.01;
                    return x < 0 ? x * 15.1927 : 0.224282 * Math.Log10(x * 155.975327 + 1);
                }

            default:
                throw new ArgumentException(id);
        }
    }

    public static IEnumerable<object[]> Ids() => ExpectedIds.Select(i => new object[] { i });

    [Fact]
    public void Registry_HasTheClosedSetInOrder()
    {
        Assert.Equal(ExpectedIds, CameraProfiles.Ids);
        Assert.Equal(ExpectedIds, CameraProfiles.All.Select(p => p.Id));
        Assert.True(CameraProfiles.IsKnown("sonySLog3"));
        Assert.False(CameraProfiles.IsKnown("sonyslog3"));
        Assert.False(CameraProfiles.IsKnown(null));
        Assert.False(CameraProfiles.TryGet("nope", out _));
        Assert.Equal(CameraProfileStatus.Approximate, CameraProfiles.All.Single(p => p.Id == "djiDLogM").Status);
        Assert.All(CameraProfiles.All.Where(p => p.Id != "djiDLogM"), p => Assert.Equal(CameraProfileStatus.Uncalibrated, p.Status));
        Assert.All(CameraProfiles.All, p =>
        {
            Assert.Equal(9, p.GamutToRec709.Length);
            Assert.Contains(p.ExpectedRange, new[] { "full", "legal" });
            Assert.Contains(p.ExpectedYuvMatrix, new[] { "bt709", "bt2020nc" });
        });
    }

    // (id, black10, grey10, white10) from the spec anchor tables; null = not tabulated. Tolerance in 10-bit codes.
    public static IEnumerable<object?[]> Anchors() =>
    [
        ["appleLog", 153.9, 499.5, 697.4, 0.1],
        ["djiDLog", 95.0, 408.0, 586.0, 0.6],
        ["djiDLogM", 95.0, 408.0, 586.0, 0.6],
        ["sonySLog3", 95.0, 420.0, 598.0, 0.6],
        ["panasonicVLog", 128.0, 433.0, 602.0, 0.6],
        ["canonCLog3", 128.0, 351.0, 577.0, 0.6],
        ["arriLogC3", 94.9, 400.0, 572.3, 0.15],
        ["arriLogC4", 95.0, 285.0, null, 0.6],
        ["fujiFLog", 95.0, 470.0, 705.0, 0.6],
        ["fujiFLog2", 95.0, 400.0, 570.0, 0.6],
        ["nikonNLog", 127.2, 372.0, 603.2, 0.15],
        ["redLog3G10", 93.66, 341.0, null, 0.15],
    ];

    [Theory]
    [MemberData(nameof(Anchors))]
    public void Encode_MatchesPublishedAnchors(string id, double black, double grey, double? white, double tol)
    {
        Assert.InRange(Encode(id, 0.0) * 1023.0, black - tol, black + tol);
        Assert.InRange(Encode(id, 0.18) * 1023.0, grey - tol, grey + tol);
        if (white is double w) Assert.InRange(Encode(id, 0.90) * 1023.0, w - tol, w + tol);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Decode_InvertsEncode(string id)
    {
        CameraProfiles.TryGet(id, out CameraProfileDefinition? p);
        foreach (double v in new[] { 0.0, 0.01, 0.05, 0.18, 0.5, 0.9, 2.0 })
        {
            double back = p!.DecodeToLinear(Encode(id, v));
            Assert.True(Math.Abs(back - v) <= 1e-5 + 1e-6 * Math.Abs(v), $"{id}: decode(encode({v})) = {back}");
        }
    }

    [Fact]
    public void Decode_PublishedPoints()
    {
        CameraProfiles.TryGet("arriLogC4", out CameraProfileDefinition? c4);
        Assert.Equal(469.80, c4!.DecodeToLinear(1.0), 2);
        Assert.Equal(-0.0181, c4.DecodeToLinear(0.0), 4);
        CameraProfiles.TryGet("redLog3G10", out CameraProfileDefinition? red);
        Assert.Equal(184.322, red!.DecodeToLinear(1.0), 2);
        Assert.Equal(0.0, red.DecodeToLinear(0.091551), 5);
        Assert.Equal(0.18, red.DecodeToLinear(1.0 / 3.0), 4);
        CameraProfiles.TryGet("appleLog", out CameraProfileDefinition? apple);
        Assert.Equal(12.0, apple!.DecodeToLinear(1.0), 3);
        Assert.Equal(-0.05641088, apple.DecodeToLinear(0.0), 8);
        CameraProfiles.TryGet("djiDLog", out CameraProfileDefinition? dji);
        Assert.Equal(41.9994, dji!.DecodeToLinear(1.0), 2);
        CameraProfiles.TryGet("sonySLog3", out CameraProfileDefinition? sony);
        Assert.Equal(38.42, sony!.DecodeToLinear(1.0), 2);
        CameraProfiles.TryGet("canonCLog3", out CameraProfileDefinition? canon);
        Assert.Equal(14.67, canon!.DecodeToLinear(1.0), 2);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void DisplayTransform_HitsGreyAndWhiteTargets(string id)
    {
        CameraProfiles.TryGet(id, out CameraProfileDefinition? p);
        double grey = Encode(id, 0.18);
        double white = Encode(id, 0.90);
        (double r, double g, double b) = CameraDisplayTransform.ToRec709(p!, grey, grey, grey);
        foreach (double v in new[] { r, g, b }) Assert.InRange(v, 0.409 - 5e-3, 0.409 + 5e-3);
        (r, g, b) = CameraDisplayTransform.ToRec709(p!, white, white, white);
        foreach (double v in new[] { r, g, b }) Assert.InRange(v, 0.906 - 5e-3, 0.906 + 5e-3);
        (r, g, b) = CameraDisplayTransform.ToRec709(p!, 0, 0, 0);
        Assert.True(r >= 0 && g >= 0 && b >= 0 && r < 0.01);
    }

    [Fact]
    public void DisplayTransform_ShoulderIsMonotoneAndBounded()
    {
        CameraProfiles.TryGet("sonySLog3", out CameraProfileDefinition? p);
        double prev = -1;
        for (int i = 0; i <= 1000; i++)
        {
            double c = i / 1000.0;
            (double r, _, _) = CameraDisplayTransform.ToRec709(p!, c, c, c);
            Assert.InRange(r, 0.0, 1.0);
            Assert.True(r >= prev - 1e-12);
            prev = r;
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Cube_RoundTripsThroughTheEngineParser(string id)
    {
        string text = CameraProfileCube.Write(id);
        CubeLut lut = CubeLut.Parse(text);
        Assert.Equal(65, lut.Size);
        Assert.Equal(65 * 65 * 65, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.Length > 0 && (char.IsDigit(l[0]) || l[0] == '-')));
        CameraProfiles.TryGet(id, out CameraProfileDefinition? p);
        double grey = Encode(id, 0.18);
        (float r, float g, float b) = lut.Sample((float)grey, (float)grey, (float)grey);
        // Log3G10 puts grey at code 1/3 on a very steep part of the curve; the 65-point grid interpolates it less tightly.
        double tol = id == "redLog3G10" ? 4e-3 : 1e-3;
        foreach (float v in new[] { r, g, b }) Assert.InRange(v, 0.409 - tol, 0.409 + tol);
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void Table_IsDeterministicAndPinned(string id)
    {
        float[] a = CameraProfileTable.Generate(id);
        float[] b = CameraProfileTable.Generate(id);
        Assert.Equal(65 * 65 * 65 * 3, a.Length);
        string ha = Hash(a);
        Assert.Equal(ha, Hash(b));
        if (Environment.GetEnvironmentVariable("UPDATE_CAMERA_PROFILE_HASHES") == "1")
        {
            output.WriteLine($"HASH {id} {ha}");
            Console.WriteLine($"HASH {id} {ha}");
            return;
        }

        Assert.Equal(PinnedHashes[id], ha);
    }

    [Fact]
    public void Table_IsInCubeOrder()
    {
        float[] t = CameraProfileTable.Generate("sonySLog3", 5);
        CameraProfiles.TryGet("sonySLog3", out CameraProfileDefinition? p);
        // index of (r=1,g=0,b=0) is the second entry: red is fastest.
        (double r, double g, double b) = CameraDisplayTransform.ToRec709(p!, 0.25, 0, 0);
        Assert.Equal((float)r, t[3]);
        Assert.Equal((float)g, t[4]);
        Assert.Equal((float)b, t[5]);
        (r, g, b) = CameraDisplayTransform.ToRec709(p!, 0, 0, 0.25);
        int blueStep = 5 * 5 * 3;
        Assert.Equal((float)r, t[blueStep]);
        Assert.Equal((float)b, t[blueStep + 2]);
    }

    private static string Hash(float[] table) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(table.AsSpan()))).ToLowerInvariant();

    // ---- shared fixture for the TypeScript mirror ----

    private static readonly double[] GridLevels = [0.0, 0.2, 0.55, 1.0];

    private static string FixturePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "web", "lib", "editing", "color"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) throw new DirectoryNotFoundException("repo root (web/lib/editing/color) not found above the test output directory");
        return Path.Combine(dir, "web", "lib", "editing", "color", "__fixtures__", "camera-profile-samples.json");
    }

    private static string G9(double v) => v.ToString("G9", CultureInfo.InvariantCulture);

    private static List<(double[] In, double[] Out)> Samples(CameraProfileDefinition p)
    {
        List<(double[], double[])> list = [];
        foreach (double b in GridLevels)
            foreach (double g in GridLevels)
                foreach (double r in GridLevels)
                {
                    (double oR, double oG, double oB) = CameraDisplayTransform.ToRec709(p, r, g, b);
                    list.Add(([r, g, b], [oR, oG, oB]));
                }

        return list;
    }

    [Fact]
    public void Fixture_MatchesTheCheckedInFile()
    {
        string path = FixturePath();
        if (Environment.GetEnvironmentVariable("UPDATE_CAMERA_PROFILE_FIXTURE") == "1")
        {
            StringBuilder sb = new("{\n");
            for (int i = 0; i < CameraProfiles.All.Count; i++)
            {
                CameraProfileDefinition p = CameraProfiles.All[i];
                sb.Append("  \"").Append(p.Id).Append("\": [\n");
                List<(double[] In, double[] Out)> s = Samples(p);
                for (int j = 0; j < s.Count; j++)
                {
                    sb.Append("    {\"rgbIn\": [").Append(string.Join(", ", s[j].In.Select(G9)))
                      .Append("], \"rgbOut\": [").Append(string.Join(", ", s[j].Out.Select(G9))).Append("]}")
                      .Append(j < s.Count - 1 ? ",\n" : "\n");
                }

                sb.Append("  ]").Append(i < CameraProfiles.All.Count - 1 ? ",\n" : "\n");
            }

            sb.Append("}\n");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sb.ToString());
            return;
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(ExpectedIds, doc.RootElement.EnumerateObject().Select(o => o.Name));
        foreach (CameraProfileDefinition p in CameraProfiles.All)
        {
            JsonElement[] rows = doc.RootElement.GetProperty(p.Id).EnumerateArray().ToArray();
            List<(double[] In, double[] Out)> expected = Samples(p);
            Assert.Equal(expected.Count, rows.Length);
            for (int i = 0; i < rows.Length; i++)
            {
                double[] rin = rows[i].GetProperty("rgbIn").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                double[] rout = rows[i].GetProperty("rgbOut").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                for (int k = 0; k < 3; k++)
                {
                    Assert.True(Math.Abs(rin[k] - expected[i].In[k]) <= 1e-6, $"{p.Id}[{i}] in {k}");
                    Assert.True(Math.Abs(rout[k] - expected[i].Out[k]) <= 1e-6, $"{p.Id}[{i}] out {k}: {rout[k]} vs {expected[i].Out[k]}");
                }
            }
        }
    }
}
