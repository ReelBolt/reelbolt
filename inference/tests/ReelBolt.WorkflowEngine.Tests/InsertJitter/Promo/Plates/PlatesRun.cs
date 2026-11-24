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
using System.Threading.Tasks;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates;

/// <summary>Orchestrates one labelled run: the real analyze step on both raw plates, the pseudo-truth meter, scoring, Film A's predicted wobble, cost, files.</summary>
internal static class PlatesRun
{
    /// <summary>Bump when the meter, its decode or its options change; cached measurements carry it.</summary>
    public const string MeterVersion = "m1";

    private static readonly JsonSerializerOptions Json = new()
    {
        IncludeFields = true,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    private static readonly JsonSerializerOptions CacheJson = new() { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static string BuildConfiguration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static async Task<PlatesResults> RunAsync(PlatesEnv env, Action<string> log)
    {
        (string commit, string branch, string[] dirty) = Git();
        log($"commit {commit} on {branch}; production files modified vs HEAD: {dirty.Length}");
        var plateResults = new List<PlateResult>();
        PlatesWobble? wobble = null;
        WobbleDto? filmMeasured = null;
        foreach (PlateCase c in PlateCase.All)
        {
            log($"== {c.Id}: {c.Film}");
            var sw = Stopwatch.StartNew();
            StepRunResult run = await PlatesStepRun.RunAsync(c.PlatePath, PlatesStepRun.StagingConfig(), Path.Combine(env.Scratch, c.Id));
            string artifactPath = Path.Combine(env.OutDir, $"artifact-{c.Id}.json");
            File.WriteAllText(artifactPath, run.ArtifactJson);
            log($"   step {run.Status} in {sw.Elapsed.TotalSeconds:0.0}s");

            ReproResult? repro = null;
            string? trackerError = null;
            List<Quad>? surface = null, corners = null;
            Dictionary<string, double> region = [];
            try
            {
                repro = PlatesRepro.Compare(artifactPath, c.StagingArtifact);
                (surface, corners, _, _) = BenchSupport.LoadArtifact(artifactPath);
                region = RegionFacts(artifactPath);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException or JsonException)
            {
                trackerError = "no usable insert region in the artifact: " + ex.Message;
            }

            (List<Quad> stagingSurface, _, int w, int h) = BenchSupport.LoadArtifact(c.StagingArtifact);
            PlateMeasurement meas = Measure(env, c, stagingSurface, w, h, c.PlatePath, "plate");
            log($"   meter: {meas.Valid}/{meas.N} frames with all four edges clean");

            ScoreDto? shipped = ScoreDto.From(PlateScorer.Score("shipped (staging artifact keyframes, surfaceCorners)", stagingSurface, meas));
            ScoreDto? surfaceScore = surface is null ? null : ScoreDto.From(PlateScorer.Score($"{env.Label} (surfaceCorners)", surface, meas));
            ScoreDto? cornerScore = corners is null ? null : ScoreDto.From(PlateScorer.Score($"{env.Label} (x0..y3, the tracker's own corners)", corners, meas));
            NoTruthDto stagingNoTruth = NoTruthDto.From(JitterMetrics.ComputeNoTruth(stagingSurface.Select((q, i) => q with { T = i / PromoInputs.Fps }).ToList(), PromoInputs.Fps));
            (double meterRes, _, _) = PromoScore.ResidualFloor(meas.Quad);

            List<FloorDto> floors = Floors(env, c, stagingSurface, w, h, log);

            WobbleDto? wobbleShipped = null, wobbleNew = null;
            // The wobble instrument needs the final film and the content proxy; without them (they are not served by any API) the section is left empty rather than failing the run.
            if (c.Id == "filmA" && File.Exists(Path.Combine(PlateCase.Promo, "final-cold-open-ac30a89f.mp4")) && File.Exists(Path.Combine(PlateCase.Promo, "insert-content-chat-window-proxy.mp4")))
            {
                wobble ??= new PlatesWobble(env.CacheDir);
                PredictedWobble ps = wobble.Predict(stagingSurface, meas);
                wobbleShipped = new WobbleDto(RowDto.From(ps.Ideal, 1 / PlateScorer.OutPerSrc), RowDto.From(ps.Corrected, 1 / PlateScorer.OutPerSrc), RowDto.From(ps.VersusPlate, 1 / PlateScorer.OutPerSrc));
                if (surface is not null)
                {
                    PredictedWobble pn = wobble.Predict(surface, meas);
                    wobbleNew = new WobbleDto(RowDto.From(pn.Ideal, 1 / PlateScorer.OutPerSrc), RowDto.From(pn.Corrected, 1 / PlateScorer.OutPerSrc), RowDto.From(pn.VersusPlate, 1 / PlateScorer.OutPerSrc));
                }

                filmMeasured = new WobbleDto(RowDto.From(wobble.FilmMeasured(), 1 / PlateScorer.OutPerSrc), RowDto.From(PromoScore.Row.Empty("-")), RowDto.From(PromoScore.Row.Empty("-")));
            }

            WriteCsvs(env, c, surface ?? stagingSurface, meas);
            InsertPassCost k = run.Cost;
            plateResults.Add(new PlateResult(
                c.Id, c.Film, Path.GetFileName(c.PlatePath), c.StagingArtifactId, run.Status, run.Error,
                repro is null ? new ReproDto(0, 0, double.NaN, double.NaN, double.NaN, double.NaN, 0, ["not compared"], false) : ReproDto.From(repro), trackerError,
                shipped, surfaceScore, cornerScore, stagingNoTruth, meas.Valid, meterRes, floors, wobbleShipped, wobbleNew,
                new CostDto(k.Measured, k.WallSec, k.DecodeWallSec, k.TrackerWallSec, k.SelfCpuSec, k.ChildCpuSec, k.RssBaselineMb, k.RssPeakMb, k.GridMb, BuildConfiguration),
                k.DecodeVf,
                run.SamplerCalls.Select(s => $"{s.Tool}({s.Argv.FirstOrDefault()}) {s.WallSec:0.00}s").ToList(),
                run.Calls.Where(t => t.Tool == "ffmpeg").Select(t => $"{t.WallSec:0.00}s {string.Join(' ', t.Argv.SkipWhile(a => a != "-vf").Take(2))}").ToList(),
                region));
        }

        string rerun = $"cd inference && REELBOLT_JITTER_BENCH=1 REELBOLT_PLATES_LABEL={env.Label} dotnet test tests/ReelBolt.WorkflowEngine.Tests -c Release --filter \"FullyQualifiedName~Promo.Plates.PlatesBench\"";
        var results = new PlatesResults(
            env.Label, commit, dirty.Length > 0, dirty, branch, BuildConfiguration, DateTime.UtcNow, PlatesMeter.DecodeFilter,
            PlatesMeter.Options.ToString(), PlatesStepRun.StagingConfigText(), PlateScorer.OutPerSrc, plateResults, filmMeasured, rerun);
        File.WriteAllText(Path.Combine(env.OutDir, "results.json"), JsonSerializer.Serialize(results, Json));
        File.WriteAllText(Path.Combine(env.OutDir, "report.md"), PlatesReport.Render(results));
        return results;
    }

    // -------------------------------------------------------------------------------------------------- pieces

    private static Dictionary<string, double> RegionFacts(string artifactPath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(artifactPath));
        JsonElement regions = doc.RootElement.GetProperty("insertRegions");
        JsonElement r = regions[0];
        var o = new Dictionary<string, double> { ["regions"] = regions.GetArrayLength(), ["keyframes"] = r.GetProperty("keyframes").GetArrayLength() };
        foreach (string k in new[] { "confidence", "meanAreaRatio", "meanAspectRatio", "meanCurvature" })
        {
            if (r.TryGetProperty(k, out JsonElement v) && v.ValueKind == JsonValueKind.Number)
                o[k] = v.GetDouble();
        }

        if (r.TryGetProperty("tuning", out JsonElement t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("growExcessFraction", out JsonElement g) && g.ValueKind == JsonValueKind.Number)
            o["growExcessFraction"] = g.GetDouble();
        return o;
    }

    private static string CachePath(PlatesEnv env, string name, string clip, IReadOnlyList<Quad> seeds)
    {
        string fp = string.Join('|', MeterVersion, PlatesMeter.DecodeFilter, PlatesMeter.Options.ToString(), new FileInfo(clip).Length.ToString(CultureInfo.InvariantCulture), seeds.Count.ToString(CultureInfo.InvariantCulture),
            seeds[0].X0.ToString("R", CultureInfo.InvariantCulture), seeds[^1].X3.ToString("R", CultureInfo.InvariantCulture));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fp)))[..8].ToLowerInvariant();
        return Path.Combine(env.CacheDir, $"meter-{name}-{hash}.json");
    }

    /// <summary>The pseudo-truth measurement of one clip, cached by (meter version, decode, options, clip size, seeds): it never depends on the code under test.</summary>
    public static PlateMeasurement Measure(PlatesEnv env, PlateCase c, IReadOnlyList<Quad> seeds, int w, int h, string clip, string kind)
    {
        string path = CachePath(env, $"{c.Id}-{kind}-{Path.GetFileNameWithoutExtension(clip)}", clip, seeds);
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

        PlateMeasurement m = PlatesMeter.Measure(clip, seeds, w, h);
        var stripped = new PlateMeasurement(m.N, m.Quad, m.Edges.Select(es => es is null ? null! : es.Select(e => e with { Ts = null, Ds = null, Keep = null }).ToArray()).ToArray());
        File.WriteAllText(path, JsonSerializer.Serialize(stripped, CacheJson));
        return stripped;
    }

    /// <summary>The meter's own floor at this plate's geometry, with known truth, through x264.</summary>
    private static List<FloorDto> Floors(PlatesEnv env, PlateCase c, IReadOnlyList<Quad> stagingSurface, int w, int h, Action<string> log)
    {
        IReadOnlyList<Quad> truth = PlatesMeter.SmoothTruth(stagingSurface);
        var list = new List<FloorDto>();
        (int R, int G, int B) plate = c.Id == "filmA" ? (61, 190, 81) : (0, 236, 1);
        PlatesMeter.SyntheticSpec[] specs =
        [
            new("nominal", "blur sigma 1.2 px, noise 1.0 level, x264 crf 23", 1.2, 1.0, 23, plate),
            new("harsh", "blur sigma 1.8 px, noise 2.5 levels, x264 crf 28", 1.8, 2.5, 28, plate)
        ];
        string Mp4(PlatesMeter.SyntheticSpec spec) => Path.Combine(env.CacheDir, $"synth-{c.Id}-{spec.Id}-{MeterVersion}.mp4");

        // The two renders are independent: run the missing ones side by side (the result is cached, so this is a one-off cost).
        Parallel.ForEach(specs.Where(sp => !File.Exists(Mp4(sp))), spec =>
        {
            log($"   rendering the known-truth plate {spec.Id} ({c.Id})");
            string tmp = Mp4(spec) + ".tmp.mp4";
            PlatesMeter.RenderSynthetic(truth, w, h, spec, tmp);
            File.Move(tmp, Mp4(spec));
        });

        foreach (PlatesMeter.SyntheticSpec spec in specs)
        {
            string mp4 = Mp4(spec);
            PlateMeasurement m = Measure(env, c, stagingSurface, w, h, mp4, "synthetic");
            TrackScore score = PlateScorer.ScoreAgainstTruth($"meter vs truth, {spec.Id}", truth, m);
            (double res, _, _) = PromoScore.ResidualFloor(m.Quad);
            list.Add(new FloorDto(spec.Id, spec.Label, $"{new FileInfo(mp4).Length * 8 / (truth.Count / PromoInputs.Fps) / 1000:0} kbit/s", ScoreDto.From(score), m.Valid, res));
        }

        return list;
    }

    private static void WriteCsvs(PlatesEnv env, PlateCase c, IReadOnlyList<Quad> track, PlateMeasurement m)
    {
        Directory.CreateDirectory(env.OutDir);
        var measured = new List<Quad>();
        for (int i = 0; i < m.N; i++)
        {
            if (m.Quad[i] is { } q)
                measured.Add(q with { T = i / PromoInputs.Fps });
        }

        BenchSupport.WriteCsv(Path.Combine(env.OutDir, $"{c.Id}-surface-vs-plate.csv"), track.Select((q, i) => q with { T = i / PromoInputs.Fps }).ToList(), measured, PromoInputs.Fps);
        var sb = new StringBuilder("frame");
        foreach (string e in PlateScorer.EdgeNames)
            sb.Append(CultureInfo.InvariantCulture, $",{e}_valid,{e}_p0,{e}_p1,{e}_p2,{e}_p3");
        sb.AppendLine();
        for (int i = 0; i < Math.Min(track.Count, m.N); i++)
        {
            sb.Append(i.ToString(CultureInfo.InvariantCulture));
            for (int e = 0; e < 4; e++)
            {
                if (m.Edges[i] is { } es && es[e].Valid)
                {
                    double[] d = PromoScore.MeasuredInsideTrack(track[i], e, es[e]).Select(v => -v).ToArray();
                    sb.Append(CultureInfo.InvariantCulture, $",1,{d[0]:0.####},{d[1]:0.####},{d[2]:0.####},{d[3]:0.####}");
                }
                else
                {
                    sb.Append(",0,,,,");
                }
            }

            sb.AppendLine();
        }

        File.WriteAllText(Path.Combine(env.OutDir, $"{c.Id}-edge-errors.csv"), sb.ToString());
    }

    private static (string Commit, string Branch, string[] Dirty) Git()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo is not null && !Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(Path.Combine(repo, ".git")))
            repo = Path.GetDirectoryName(repo);
        if (repo is null)
            return ("unknown", "unknown", []);
        string Run(params string[] a)
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = repo };
            foreach (string x in a)
                psi.ArgumentList.Add(x);
            using Process p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return o.Trim();
        }

        string[] dirty = Run("status", "--porcelain", "--", "inference/src").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return (Run("rev-parse", "HEAD"), Run("rev-parse", "--abbrev-ref", "HEAD"), dirty);
    }
}
