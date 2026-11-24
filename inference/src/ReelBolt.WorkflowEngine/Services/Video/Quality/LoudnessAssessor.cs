using System.Globalization;

namespace ReelBolt.WorkflowEngine.Services.Video.Quality;

/// <summary>Measured audio facts for one film. Silences are (start, end) seconds.</summary>
public sealed record LoudnessFacts(
    double? IntegratedLufs,
    double? TruePeakDbtp,
    double? RangeLu,
    IReadOnlyList<(double Start, double End)> Silences,
    double DurationSec,
    bool HasAudioTrack,
    bool Normalised);

/// <summary>A loudness target with true-peak ceiling and tolerance, all in LU/LUFS/dBTP.</summary>
public sealed record LoudnessTarget(double Lufs, double TruePeakCeilingDbtp, double RangeLu, double ToleranceLu)
{
    public static LoudnessTarget Web { get; } = new(-16, -1.5, 11, 2);
    public static LoudnessTarget Social { get; } = new(-14, -1.5, 11, 2);
    public static LoudnessTarget Broadcast { get; } = new(-23, -2, 15, 2);
    public static LoudnessTarget Loose { get; } = new(-16, -1.5, 11, 3);
}

/// <summary>
/// Reusable analysis helper: turns an ebur128 measurement plus a target into customer-readable
/// findings. Severity is conservative: high only for clipping or no sound at all.
/// </summary>
public static class LoudnessAssessor
{
    private static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);

    public static IReadOnlyList<QualityFinding> Assess(LoudnessFacts facts, LoudnessTarget? target = null)
    {
        var t = target ?? LoudnessTarget.Loose;
        var list = new List<QualityFinding>();
        if (facts is null || !facts.HasAudioTrack) return list;
        var dur = double.IsFinite(facts.DurationSec) ? facts.DurationSec : 0;
        var silences = facts.Silences ?? [];

        var measured = facts.IntegratedLufs is { } i && double.IsFinite(i) ? i : (double?)null;
        if (measured is null)
        {
            var covered = dur > 0 && CoveredSeconds(silences, dur) >= dur - 0.25;
            if (covered)
            {
                list.Add(new QualityFinding(QualityKinds.NoSound, QualitySeverity.High, 0, dur, null, null,
                    "The film has an audio track but it is silent from start to finish."));
                return list;
            }
        }

        if (measured is { } lufs && !facts.Normalised)
        {
            var delta = lufs - t.Lufs;
            if (Math.Abs(delta) > t.ToleranceLu)
            {
                var word = delta < 0 ? "quieter" : "louder";
                list.Add(new QualityFinding(QualityKinds.LoudnessOffTarget, QualitySeverity.Medium, null, null, lufs, t.Lufs,
                    $"The sound is {word} than the usual online level ({F(lufs)} LUFS, aim for {F(t.Lufs)})."));
            }
        }

        if (facts.TruePeakDbtp is { } tp && double.IsFinite(tp))
        {
            if (tp >= 0)
            {
                list.Add(new QualityFinding(QualityKinds.Clipping, QualitySeverity.High, null, null, tp, 0,
                    $"The sound peaks at {F(tp)} dBTP, which means it is likely distorted (clipping)."));
            }
            else if (tp > t.TruePeakCeilingDbtp + 0.2)
            {
                list.Add(new QualityFinding(QualityKinds.TruePeakHigh, QualitySeverity.Medium, null, null, tp, t.TruePeakCeilingDbtp,
                    $"The loudest peaks are very close to the limit ({F(tp)} dBTP, keep under {F(t.TruePeakCeilingDbtp)}) and may distort on some players."));
            }
        }

        if (!facts.Normalised && facts.RangeLu is { } lra && double.IsFinite(lra) && lra > t.RangeLu + 4)
        {
            list.Add(new QualityFinding(QualityKinds.LoudnessRangeWide, QualitySeverity.Low, null, null, lra, t.RangeLu,
                $"The volume jumps around a lot between quiet and loud parts ({F(lra)} LU, aim for {F(t.RangeLu)} or less)."));
        }

        foreach (var (s, e) in silences)
        {
            if (!double.IsFinite(s) || !double.IsFinite(e)) continue;
            var start = Math.Max(s, 0.5);
            var end = dur > 0 ? Math.Min(e, dur - 2) : e;
            var len = end - start;
            if (len < 3) continue;
            var sev = len >= 6 ? QualitySeverity.Medium : QualitySeverity.Low;
            list.Add(new QualityFinding(QualityKinds.LongSilence, sev, start, end, len, 3,
                $"There is {F(len)} seconds of silence in the middle of the film (from {F(start)} s to {F(end)} s)."));
        }

        return list;
    }

    private static double CoveredSeconds(IReadOnlyList<(double Start, double End)> silences, double dur)
    {
        var spans = silences.Where(x => double.IsFinite(x.Start) && double.IsFinite(x.End))
            .Select(x => (S: Math.Max(0, x.Start), E: Math.Min(dur, x.End))).Where(x => x.E > x.S).OrderBy(x => x.S).ToList();
        double total = 0, curS = 0, curE = -1;
        var open = false;
        foreach (var (s, e) in spans)
        {
            if (!open) { curS = s; curE = e; open = true; }
            else if (s <= curE) curE = Math.Max(curE, e);
            else { total += curE - curS; curS = s; curE = e; }
        }

        if (open) total += curE - curS;
        return total;
    }
}
