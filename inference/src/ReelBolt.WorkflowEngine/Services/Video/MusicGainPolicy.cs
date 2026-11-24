using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>Where the music bed's level came from and what it resolved to (see <see cref="MusicGainPolicy"/>).</summary>
/// <param name="BedDb">The bed level in dB (the un-ducked level).</param>
/// <param name="Limit">Whether the branch gets the sole-soundtrack peak limiter.</param>
/// <param name="Source"><c>absolute</c> (a configured <c>MusicGainDb</c>) or <c>intensity</c>.</param>
/// <param name="Basis">The bed basis reported as <c>music.bedBasis</c>.</param>
/// <param name="ClampedFromDb">The intensity level before the -40..-6 dB clamp moved it; null when nothing moved.</param>
public sealed record MusicBedLevel(double BedDb, bool Limit, string Source, string Basis, double? ClampedFromDb);

/// <summary>
/// The music bed level: <c>VideoCompileStepConfig.MusicGainDb</c> as an absolute track gain, or the
/// intensity-driven bed the compile has always used (clamped to -40..-6 dB, replaced by the
/// sole-soundtrack level when music is the whole programme, lifted in narration gaps). <b>Pure.</b>
/// </summary>
public static class MusicGainPolicy
{
    public const double MinDb = -40;
    public const double MaxDb = 6;

    /// <summary>Null (unset) or a finite gain inside <see cref="MinDb"/>..<see cref="MaxDb"/>.</summary>
    public static bool IsValid(double? db) =>
        db is null || (double.IsFinite(db.Value) && db.Value >= MinDb && db.Value <= MaxDb);

    public static MusicBedLevel Resolve(
        double? absoluteDb, int intensityDb, bool soleSoundtrack, int soleSoundtrackDb, bool narrationLift, int narrationLiftDb)
    {
        if (absoluteDb is { } absolute)
            return new MusicBedLevel(absolute, false, "absolute", "absolute", null);

        int bed = Math.Clamp(intensityDb, -40, -6);
        double? clampedFrom = bed != intensityDb ? intensityDb : null;
        if (soleSoundtrack)
            return new MusicBedLevel(soleSoundtrackDb, true, "intensity", "sole_soundtrack", null);

        if (narrationLift)
            return new MusicBedLevel(Math.Min(-6, bed + narrationLiftDb), false, "intensity", "narration_gaps", clampedFrom);

        return new MusicBedLevel(bed, false, "intensity", "intensity", clampedFrom);
    }

    /// <summary>The <c>music.gain</c> report: <c>{gainDb, source, clampedFromDb?}</c>.</summary>
    public static JsonObject ToNode(MusicBedLevel level)
    {
        var node = new JsonObject { ["gainDb"] = level.BedDb, ["source"] = level.Source };
        if (level.ClampedFromDb is { } from)
            node["clampedFromDb"] = from;
        return node;
    }
}
