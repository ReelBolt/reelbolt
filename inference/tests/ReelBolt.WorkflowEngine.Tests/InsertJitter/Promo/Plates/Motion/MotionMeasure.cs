#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>The three independent-of-chroma things measured on a clip (real plate or synthetic): the plate's luma edges and the scene ring's motion.</summary>
internal static class MotionMeasure
{
    private static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>
    /// Meter options on LUMA: the same integrating sub-pixel meter as the chroma one (<see cref="PlatesMeter.Options"/>) on the clip's Y plane at native
    /// resolution (no chroma at all). The green plate against the dark bezel is 100+ levels of contrast in Y.
    /// </summary>
    public static readonly PromoEdgeOptions LumaOptions = new(Reach1: 5, Reach2: 3.5, MinSpan: 20, MaxResidualRms: 0.9, MinInlier: 0.85);

    /// <summary>The chroma meter's own reaches on luma (sensitivity check; fails on Film B where the bezel is thinner than the reach).</summary>
    public static readonly PromoEdgeOptions LumaOptionsWide = new(Reach1: 9, Reach2: 6, MinSpan: 20, MaxResidualRms: 0.6, MinInlier: 0.85);

    public static string Fingerprint(string clip) => string.Join('|', new FileInfo(clip).Length.ToString(CultureInfo.InvariantCulture), File.GetLastWriteTimeUtc(clip).Ticks.ToString(CultureInfo.InvariantCulture));

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    // ------------------------------------------------------------------------------------------------ luma edge meter

    public static PlateMeasurement Luma(string name, string clip, IReadOnlyList<Quad> seeds, int w, int h, PromoEdgeOptions? options = null)
    {
        PromoEdgeOptions o = options ?? LumaOptions;
        string fp = string.Join('|', "luma-v2", o.ToString(), Fingerprint(clip), seeds.Count.ToString(CultureInfo.InvariantCulture), seeds[0].X0.ToString("R", CultureInfo.InvariantCulture), seeds[^1].X3.ToString("R", CultureInfo.InvariantCulture));
        string path = Path.Combine(MotionEnv.Cache, $"luma-{name}-{Hash(fp)}.json");
        if (File.Exists(path))
        {
            try
            {
                PlateMeasurement? hit = JsonSerializer.Deserialize<PlateMeasurement>(File.ReadAllText(path), CacheJson);
                if (hit is not null && hit.N == seeds.Count)
                    return hit;
            }
            catch (JsonException)
            {
            }
        }

        int n = seeds.Count;
        var quads = new Quad?[n];
        var edges = new PromoEdge[n][];
        foreach ((int index, float[] luma) in PromoVideo.Luma(clip, w, h, n))
        {
            if (index >= n)
                break;
            PromoQuadFit fit = PromoEdgeFit.FitQuad(luma, w, h, seeds[index], o);
            quads[index] = fit.Quad;
            edges[index] = fit.Edges.Select(e => e with { Ts = null, Ds = null, Keep = null }).ToArray();
        }

        var m = new PlateMeasurement(n, quads, edges);
        File.WriteAllText(path, JsonSerializer.Serialize(m, CacheJson));
        return m;
    }

    // ------------------------------------------------------------------------------------------------ ring motion

    public static IReadOnlyList<RingPair> Ring(string name, string clip, IReadOnlyList<Quad> geometry, int w, int h, RingOptions o, Action<string>? log = null, int from = 0, int to = int.MaxValue)
    {
        to = Math.Min(to, geometry.Count - 1);
        string fp = string.Join('|', "ring-v1", o.ToString(), Fingerprint(clip), from.ToString(CultureInfo.InvariantCulture), to.ToString(CultureInfo.InvariantCulture), geometry.Count.ToString(CultureInfo.InvariantCulture), geometry[0].X0.ToString("R", CultureInfo.InvariantCulture), geometry[^1].X3.ToString("R", CultureInfo.InvariantCulture));
        string path = Path.Combine(MotionEnv.Cache, $"ring-{name}-{Hash(fp)}.json");
        if (File.Exists(path))
        {
            try
            {
                RingPair[]? hit = JsonSerializer.Deserialize<RingPair[]>(File.ReadAllText(path), CacheJson);
                if (hit is not null && hit.Length == to - from)
                    return hit;
            }
            catch (JsonException)
            {
            }
        }

        var pairs = new List<RingPair>();
        Pyramid? prev = null;
        var sw = Stopwatch.StartNew();
        var buf = new byte[w * h];
        foreach ((int index, float[] luma) in PromoVideo.Luma(clip, w, h, to + 1))
        {
            if (index < from)
                continue;
            for (int i = 0; i < buf.Length; i++)
                buf[i] = (byte)luma[i];
            Pyramid cur = Pyramid.Build(buf, w, h, o.Levels, o.BlurSigma);
            if (prev is not null)
                pairs.Add(RingMotion.Estimate(prev, cur, geometry[index - 1], o, index - 1));
            prev = cur;
            if (index % 60 == 0)
                log?.Invoke($"   ring {name}: frame {index} ({sw.Elapsed.TotalSeconds:0}s)");
        }

        File.WriteAllText(path, JsonSerializer.Serialize(pairs.ToArray(), CacheJson));
        return pairs;
    }

    /// <summary>
    /// Integrates a ring's frame-to-frame homographies into one quad per frame, starting from <paramref name="start"/> at frame 0; a failed pair carries the quad over (and is
    /// reported invalid at that frame, which the scorer then skips along with its neighbour).
    /// </summary>
    public static (Quad?[] Quads, bool[] StepOk) Integrate(IReadOnlyList<RingPair> pairs, Quad start, int n)
    {
        var q = new Quad?[n];
        var ok = new bool[n];
        Quad cur = start;
        q[0] = cur;
        ok[0] = true;
        for (int i = 0; i < n - 1 && i < pairs.Count; i++)
        {
            RingPair p = pairs[i];
            if (p.Ok)
                cur = RingMotion.PredictCorners(cur, p.H) with { T = (i + 1) / PromoInputs.Fps };
            else
                cur = cur with { T = (i + 1) / PromoInputs.Fps };
            q[i + 1] = cur;
            ok[i + 1] = p.Ok;
        }

        return (q, ok);
    }
}
