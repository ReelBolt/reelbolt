namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Folds TRANSIENT shot spans — a span shorter than a minimum duration — into the shot they
/// belong to, so a flash frame is never offered to an agent as a shot of its own. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ffmpeg's scene score fires on any single frame that differs sharply from its neighbour, so a
/// one-frame bloom flash, a white pop or a dissolve's middle frame each come back as a "shot". On a
/// designed composition this is the common case: a Remotion render puts a short flash on every hard
/// cut, and the detector returned one 33 ms shot per cut plus the real scene behind it.
/// </para>
/// <para>
/// Those slivers were not harmless noise. Near-duplicate grouping put each one in a group with the
/// real scene that follows it (the flash is mostly that scene's own first frame), and marked the
/// sliver or the scene as an alternate TAKE. The editor then did what the view told it: keep the
/// best take, drop the other. That is how a run lost whole capability scenes, and every anchor
/// (overlays, sound effects, generated cutaways) taken from those ids pointed at the wrong moment.
/// </para>
/// <para>
/// A transient span joins the shot AFTER it, because a flash marks the cut INTO that shot; the cut
/// point itself does not move. A transient at the very end joins the shot before it. Runs of
/// consecutive transients collapse forward together. Spans stay contiguous and gapless, and
/// nothing changes when no span is shorter than <c>minShotSec</c> or <c>minShotSec &lt;= 0</c>.
/// </para>
/// </remarks>
public static class ShotSpanConsolidator
{
    /// <summary>Returns <paramref name="spans"/> with every span shorter than <paramref name="minShotSec"/> folded into a neighbour.</summary>
    public static IReadOnlyList<(double StartSec, double EndSec)> AbsorbTransients(
        IReadOnlyList<(double StartSec, double EndSec)> spans, double minShotSec)
    {
        if (minShotSec <= 0 || spans.Count < 2)
            return spans;

        List<(double StartSec, double EndSec)> result = new(spans.Count);
        double? pendingStart = null;

        foreach ((double start, double end) in spans)
        {
            double effectiveStart = pendingStart ?? start;
            if (end - start < minShotSec)
            {
                // Carry this sliver forward into whatever real shot comes next.
                pendingStart = effectiveStart;
                continue;
            }

            result.Add((effectiveStart, end));
            pendingStart = null;
        }

        if (pendingStart is double tailStart)
        {
            double tailEnd = spans[^1].EndSec;
            if (result.Count > 0)
                result[^1] = (result[^1].StartSec, tailEnd);
            else
                result.Add((tailStart, tailEnd)); // every span was a sliver: one shot for the file
        }

        return result;
    }
}
