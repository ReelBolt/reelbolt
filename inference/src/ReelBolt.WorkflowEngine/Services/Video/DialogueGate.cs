using System.Globalization;
using System.Text.Json.Nodes;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// What the compile step does to the SOURCE dialogue under narration and pickup windows (see
/// docs/voiceover.md "Pickups and dubs"): a deterministic, keyframed <c>volume</c> filter applied
/// to the dialogue cut BEFORE any mix, so neither the narration nor the music is ever gated with
/// it. <see cref="DuckWindows"/> are attenuated to <see cref="DuckGainLinear"/>; <see cref="MuteWindows"/>
/// go to silence. Both ramp in and out over <see cref="RampSec"/> so a gate never clicks. Pure —
/// an expression over <c>t</c>, exactly like the music ducking envelope, never a runtime
/// compressor whose behaviour would depend on the waveform.
/// </summary>
public sealed record DialogueGate(
    IReadOnlyList<(double Start, double End)> DuckWindows,
    IReadOnlyList<(double Start, double End)> MuteWindows,
    double DuckGainLinear,
    double RampSec,
    string Mode)
{
    /// <summary>The complete <c>volume=eval=frame:volume='…'</c> filter, ready to be appended to a chain.</summary>
    public string BuildVolumeFilter() => $"volume=eval=frame:volume='{BuildExpression()}'";

    /// <summary>
    /// The gain expression: the product, over every window, of
    /// <c>1-(1-g)*min(1,max(0,(t-a)/r))*min(1,max(0,(b-t)/r))</c> — 1 outside the window, <c>g</c>
    /// inside it, a linear ramp of <c>r</c> seconds at each edge. With <c>r</c> = 0 the ramps become
    /// hard edges (<c>gte(t,a)*lt(t,b)</c>). Windows are sorted and merged per gain first, so two
    /// adjacent narration lines never double-attenuate a shared frame.
    /// </summary>
    public string BuildExpression()
    {
        List<string> factors = [];
        foreach ((double start, double end) in Merge(MuteWindows))
            factors.Add(Factor(start, end, 0.0));
        foreach ((double start, double end) in Merge(DuckWindows))
            factors.Add(Factor(start, end, Math.Clamp(DuckGainLinear, 0.0, 1.0)));

        return factors.Count == 0 ? "1" : string.Join("*", factors);
    }

    private string Factor(double start, double end, double gain)
    {
        string a = N(start);
        string b = N(end);
        string g = N(gain);
        if (RampSec <= 0)
            return $"(1-(1-{g})*gte(t,{a})*lt(t,{b}))";

        // The ramp never exceeds half the window, so a short window still reaches its floor.
        double ramp = Math.Min(RampSec, Math.Max(0.001, (end - start) / 2));
        string r = N(ramp);
        return $"(1-(1-{g})*min(1,max(0,(t-{a})/{r}))*min(1,max(0,({b}-t)/{r})))";
    }

    /// <summary>Sorts and merges overlapping/abutting windows, dropping empty or non-finite ones.</summary>
    internal static List<(double Start, double End)> Merge(IReadOnlyList<(double Start, double End)> windows)
    {
        List<(double Start, double End)> merged = [];
        foreach ((double start, double end) in windows
            .Where(w => double.IsFinite(w.Start) && double.IsFinite(w.End) && w.End > w.Start)
            .Select(w => (Math.Max(0, w.Start), w.End))
            .OrderBy(w => w.Item1))
        {
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }

        return merged;
    }

    public JsonObject ToNode()
    {
        var duck = new JsonArray();
        foreach ((double s, double e) in Merge(DuckWindows))
            duck.Add(new JsonObject { ["startSec"] = Math.Round(s, 3), ["endSec"] = Math.Round(e, 3) });
        var mute = new JsonArray();
        foreach ((double s, double e) in Merge(MuteWindows))
            mute.Add(new JsonObject { ["startSec"] = Math.Round(s, 3), ["endSec"] = Math.Round(e, 3) });

        return new JsonObject
        {
            ["mode"] = Mode,
            ["duckGainDb"] = Math.Round(20 * Math.Log10(Math.Max(1e-6, DuckGainLinear)), 2),
            ["rampMs"] = (int)Math.Round(RampSec * 1000),
            ["duckWindows"] = duck,
            ["muteWindows"] = mute
        };
    }

    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
