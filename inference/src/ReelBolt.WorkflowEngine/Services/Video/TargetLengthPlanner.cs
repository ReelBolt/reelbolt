namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// <c>VideoCompileStepConfig.TargetDurationSec</c>: brings a cut that runs LONG down to the asked-for
/// length by shortening span tails, longest spans first (one common ceiling, "water-filling"), so
/// every span keeps its opening — the frames the editor chose it for — and short beats are left
/// alone. A POV teaser asked to run 15 s came out at 25 s when the length was only reported.
/// </summary>
/// <remarks>
/// Never lengthens a cut (there is no footage to add), never shortens a span below its floor
/// (the narration anchored in it plus a tail, or <see cref="MinSpanSec"/>), and never touches the
/// LAST span, which is the end card or the closing beat the editor placed there on purpose. A cut
/// that cannot reach the target within those floors comes as close as they allow.
/// </remarks>
public static class TargetLengthPlanner
{
    /// <summary>The shortest a trimmed span may become.</summary>
    public const double MinSpanSec = 1.0;

    /// <summary>
    /// The new duration of every span. Identical to <paramref name="durations"/> when the cut
    /// already fits <paramref name="targetSec"/> or the target is not positive.
    /// </summary>
    public static List<double> Plan(IReadOnlyList<double> durations, IReadOnlyList<double>? floors, double targetSec)
    {
        ArgumentNullException.ThrowIfNull(durations);
        List<double> result = durations.ToList();
        double total = result.Sum();
        if (targetSec <= 0 || total <= targetSec + 1e-6 || result.Count < 2)
            return result;

        int last = result.Count - 1;
        double[] lower = new double[result.Count];
        for (int i = 0; i < result.Count; i++)
        {
            double floor = floors is not null && i < floors.Count ? Math.Max(0, floors[i]) : 0;
            lower[i] = i == last ? result[i] : Math.Min(result[i], Math.Max(floor, MinSpanSec));
        }

        double Total(double ceiling)
        {
            double sum = 0;
            for (int i = 0; i < result.Count; i++)
                sum += Math.Max(lower[i], Math.Min(result[i], ceiling));
            return sum;
        }

        // The smallest total the floors allow: aim there when the target is out of reach.
        double goal = Math.Max(targetSec, Total(0));
        double lo = 0, hi = result.Max();
        for (int iteration = 0; iteration < 60; iteration++)
        {
            double mid = (lo + hi) / 2;
            if (Total(mid) > goal)
                hi = mid;
            else
                lo = mid;
        }

        for (int i = 0; i < result.Count; i++)
            result[i] = Math.Round(Math.Max(lower[i], Math.Min(result[i], lo)), 4);
        return result;
    }
}
