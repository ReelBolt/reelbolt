#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>How closely one analysis artifact's first insert region reproduces another's (the harness's against the staging one), in plate source px.</summary>
internal sealed record ReproResult(
    int FramesA, int FramesB, int Compared,
    double CornersMax, double CornersRms, double SurfaceMax, double SurfaceRms, int FramesOverTolerance,
    string[] FieldDiffs, bool Identical);

internal static class PlatesRepro
{
    /// <summary>Corner tolerance (source px) under which two runs are called the same track: well below the 0.001 px a changed code path would still leave unmoved.</summary>
    public const double Tolerance = 1e-6;

    private static readonly string[] Keys = ["x0", "y0", "x1", "y1", "x2", "y2", "x3", "y3"];

    public static ReproResult Compare(string artifactA, string artifactB)
    {
        using JsonDocument da = JsonDocument.Parse(File.ReadAllText(artifactA));
        using JsonDocument db = JsonDocument.Parse(File.ReadAllText(artifactB));
        int w = da.RootElement.GetProperty("media").GetProperty("width").GetInt32();
        int h = da.RootElement.GetProperty("media").GetProperty("height").GetInt32();
        JsonElement ra = da.RootElement.GetProperty("insertRegions")[0], rb = db.RootElement.GetProperty("insertRegions")[0];
        JsonElement ka = ra.GetProperty("keyframes"), kb = rb.GetProperty("keyframes");
        int n = Math.Min(ka.GetArrayLength(), kb.GetArrayLength());
        double cMax = 0, cSq = 0, sMax = 0, sSq = 0;
        int cN = 0, sN = 0, over = 0;
        for (int i = 0; i < n; i++)
        {
            double frameMax = 0;
            for (int k = 0; k < 8; k++)
            {
                double scale = k % 2 == 0 ? w : h;
                double d = (ka[i].GetProperty(Keys[k]).GetDouble() - kb[i].GetProperty(Keys[k]).GetDouble()) * scale;
                cMax = Math.Max(cMax, Math.Abs(d));
                cSq += d * d;
                cN++;
                frameMax = Math.Max(frameMax, Math.Abs(d));
                if (ka[i].TryGetProperty("surfaceCorners", out JsonElement sa) && sa.ValueKind == JsonValueKind.Object
                    && kb[i].TryGetProperty("surfaceCorners", out JsonElement sb) && sb.ValueKind == JsonValueKind.Object)
                {
                    double ds = (sa.GetProperty(Keys[k]).GetDouble() - sb.GetProperty(Keys[k]).GetDouble()) * scale;
                    sMax = Math.Max(sMax, Math.Abs(ds));
                    sSq += ds * ds;
                    sN++;
                    frameMax = Math.Max(frameMax, Math.Abs(ds));
                }
            }

            if (frameMax > Tolerance)
                over++;
        }

        var diffs = new List<string>();
        foreach (string f in new[] { "confidence", "meanAreaRatio", "meanAspectRatio", "motion", "startSec", "endSec", "meanCurvature", "label", "method" })
            Diff(f, ra, rb, diffs);
        DiffObject("chromaKey", ra, rb, diffs);
        DiffObject("tuning", ra, rb, diffs);
        if (ka.GetArrayLength() != kb.GetArrayLength())
            diffs.Add($"keyframe count {ka.GetArrayLength()} vs {kb.GetArrayLength()}");
        bool identical = diffs.Count == 0 && over == 0;
        return new ReproResult(ka.GetArrayLength(), kb.GetArrayLength(), n, cMax, cN == 0 ? 0 : Math.Sqrt(cSq / cN), sMax, sN == 0 ? 0 : Math.Sqrt(sSq / sN), over, [.. diffs], identical);
    }

    private static void Diff(string name, JsonElement a, JsonElement b, List<string> diffs)
    {
        bool ha = a.TryGetProperty(name, out JsonElement va), hb = b.TryGetProperty(name, out JsonElement vb);
        if (!ha && !hb)
            return;
        string sa = ha ? va.ToString() : "<absent>", sb = hb ? vb.ToString() : "<absent>";
        if (ha && hb && va.ValueKind == JsonValueKind.Number && vb.ValueKind == JsonValueKind.Number)
        {
            if (Math.Abs(va.GetDouble() - vb.GetDouble()) > 1e-9)
                diffs.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {va.GetDouble():0.#######} vs {vb.GetDouble():0.#######}"));
            return;
        }

        if (sa != sb)
            diffs.Add($"{name}: {Trunc(sa)} vs {Trunc(sb)}");
    }

    private static void DiffObject(string name, JsonElement a, JsonElement b, List<string> diffs)
    {
        bool ha = a.TryGetProperty(name, out JsonElement va) && va.ValueKind == JsonValueKind.Object;
        bool hb = b.TryGetProperty(name, out JsonElement vb) && vb.ValueKind == JsonValueKind.Object;
        if (!ha && !hb)
            return;
        if (ha != hb)
        {
            diffs.Add($"{name}: present {ha} vs {hb}");
            return;
        }

        foreach (JsonProperty p in va.EnumerateObject())
        {
            if (vb.TryGetProperty(p.Name, out JsonElement q) && p.Value.ValueKind == JsonValueKind.Number && q.ValueKind == JsonValueKind.Number)
            {
                if (Math.Abs(p.Value.GetDouble() - q.GetDouble()) > 1e-9)
                    diffs.Add(string.Create(CultureInfo.InvariantCulture, $"{name}.{p.Name}: {p.Value.GetDouble():0.#######} vs {q.GetDouble():0.#######}"));
            }
            else if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                // Nested candidate lists (tuning): compared as text.
                if (!vb.TryGetProperty(p.Name, out JsonElement q2) || p.Value.GetRawText() != q2.GetRawText())
                    diffs.Add($"{name}.{p.Name}: differs");
            }
            else if (!vb.TryGetProperty(p.Name, out q) || p.Value.ToString() != q.ToString())
            {
                diffs.Add($"{name}.{p.Name}: {Trunc(p.Value.ToString())} vs {(vb.TryGetProperty(p.Name, out q) ? Trunc(q.ToString()) : "<absent>")}");
            }
        }
    }

    private static string Trunc(string s) => s.Length <= 60 ? s : s[..57] + "...";
}
