namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Puts a track's grid facts where the music really plays (QA-DYN-54). A track's facts are in MUSIC time (seconds from the track's start).
/// The bed starts with the FILM, so a beat at music time <c>t</c> plays at film time <c>t + bed shift</c>, and a cold open (or any pre-roll)
/// of <c>P</c> seconds means the body starts at film time <c>P</c>: the same beat is at body time <c>t + bed shift - P</c>. Every cut,
/// drop-aligned cut and Dynamics moment the compile places is in body time, so it must read the grid through this shift or it lands where
/// the music has a different beat (on the prod film f1e9be7a the body cut on a "drop" at film 16.3 s while the track's drop plays at 6.9 s).
/// </summary>
public static class MusicFilmTime
{
    /// <summary>The shift that takes music time to body time: the bed's own shift minus the pre-roll the body starts after.</summary>
    public static double BodyShiftSec(double preRollSec, double bedShiftSec = 0) => bedShiftSec - preRollSec;

    /// <summary>
    /// The same analysis as the body sees it: the beat and bar phases move with the shift (kept in one beat / one bar), the drop and the
    /// outro move with it and are dropped when they play before the body starts (<paramref name="dropInPreRoll"/>: the drop is in the
    /// cold open). With a zero shift the analysis is returned unchanged.
    /// </summary>
    public static MusicBeatAnalysis ToBodyTime(MusicBeatAnalysis a, double shiftSec, out bool dropInPreRoll)
    {
        dropInPreRoll = false;
        if (shiftSec == 0)
            return a;
        double? drop = null, outro = null;
        if (a.DropSec is { } d)
        {
            if (d + shiftSec >= -1e-6)
                drop = Math.Max(0, d + shiftSec);
            else
                dropInPreRoll = true;
        }

        if (a.OutroSec is { } o && o + shiftSec >= -1e-6)
            outro = Math.Max(0, o + shiftSec);
        return a with
        {
            BeatPhaseSec = Wrap(a.BeatPhaseSec + shiftSec, a.BeatSec),
            BarPhaseSec = Wrap(a.BarPhaseSec + shiftSec, a.BarSec),
            DropSec = drop,
            OutroSec = outro
        };
    }

    /// <summary>Grid lines (music time, already looped over a short track) in body time, the ones that play before the body starts left out.</summary>
    public static List<double> LinesInBodyTime(IEnumerable<double> musicTimeLines, double shiftSec)
    {
        var lines = new List<double>();
        foreach (double l in musicTimeLines)
        {
            double t = Math.Round(l + shiftSec, 4);
            if (t >= -1e-6)
                lines.Add(Math.Max(0, t));
        }

        return lines;
    }

    private static double Wrap(double value, double step)
    {
        if (!(step > 0) || !double.IsFinite(step) || !double.IsFinite(value))
            return value;
        double r = value % step;
        return r < 0 ? r + step : r;
    }
}
