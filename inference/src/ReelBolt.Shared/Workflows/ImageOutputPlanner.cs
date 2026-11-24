using System.Globalization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// What an ImageGenerate step asks the vendor for, and the exact size it must end at.
/// </summary>
/// <param name="Tier">The resolution tier sent and billed (1K, 2K, 4K), or null when the endpoint has no resolution tier or none was chosen (the vendor default applies).</param>
/// <param name="AspectRatio">The aspect ratio word sent, or null.</param>
/// <param name="RequestWidth">The explicit size sent for an endpoint that takes a fixed size list, or null.</param>
/// <param name="RequestHeight">See <paramref name="RequestWidth"/>.</param>
/// <param name="TargetWidth">The exact final width, or null when no output size was asked for.</param>
/// <param name="TargetHeight">The exact final height, or null.</param>
public sealed record ImageOutputPlan(string? Tier, string? AspectRatio, int? RequestWidth, int? RequestHeight, int? TargetWidth, int? TargetHeight)
{
    /// <summary>True when the delivered picture must be brought to an exact size.</summary>
    public bool HasTarget => TargetWidth is not null && TargetHeight is not null;
}

/// <summary>
/// Pure planner: step config + the chosen endpoint in, the request shape out. No exact-size API exists on the supported
/// models, so the plan asks for the cheapest tier and nearest shape that cover the target; the executor then resizes and
/// crops to the exact size.
/// </summary>
public static class ImageOutputPlanner
{
    /// <summary>The tier words in ascending order.</summary>
    private static readonly string[] Tiers = ["1K", "2K", "4K"];

    /// <summary>Plans for the endpoint a fal model id names (a non-fal or unknown model plans as a pass-through).</summary>
    public static ImageOutputPlan PlanFor(ImageGenerateStepConfig config, InferenceProviderKind kind, string model) =>
        Plan(config, kind == InferenceProviderKind.FalAi ? FalImageCatalog.Instance.Find(model) : null);

    public static ImageOutputPlan Plan(ImageGenerateStepConfig config, FalImageEndpoint? endpoint)
    {
        ArgumentNullException.ThrowIfNull(config);

        int? tw = config.OutputWidth;
        int? th = config.OutputHeight;
        bool target = tw is > 0 && th is > 0;
        if (!target)
        {
            tw = null;
            th = null;
        }

        string? tier = PlanTier(config, endpoint, tw, th);
        if (endpoint is null)
            return new ImageOutputPlan(null, config.AspectRatio, config.Width, config.Height, tw, th);

        if (!target)
            return new ImageOutputPlan(tier, config.AspectRatio, config.Width, config.Height, null, null);

        double ratio = (double)tw!.Value / th!.Value;
        bool generate = config.Mode == ImageGenerateMode.Generate;
        if (endpoint.SizeField is not null && endpoint.SupportedSizes is { Count: > 0 } sizes)
        {
            if (generate && NearestSize(sizes, tw.Value, th.Value) is { } size)
                return new ImageOutputPlan(tier, null, size.W, size.H, tw, th);
            return new ImageOutputPlan(tier, null, null, null, tw, th);
        }

        string? aspect = null;
        if (endpoint.AspectRatioField is { } aspectField && endpoint.FieldNamed(aspectField) is { Values: { } words })
            aspect = NearestAspect(words, ratio);
        return new ImageOutputPlan(tier, aspect, null, null, tw, th);
    }

    private static string? PlanTier(ImageGenerateStepConfig config, FalImageEndpoint? endpoint, int? tw, int? th)
    {
        if (endpoint?.BillingTierField is not { } tierField
            || endpoint.FieldNamed(tierField) is not { Values: { } values } field
            || !string.Equals(field.Name, "resolution", StringComparison.Ordinal))
            return null;

        string? asked = config.ResolutionTier;
        if (asked is not null)
            return values.Contains(asked, StringComparer.Ordinal) ? asked : null;
        if (tw is null || th is null)
            return null;

        int longEdge = Math.Max(tw.Value, th.Value);
        foreach (string t in Tiers)
        {
            if (values.Contains(t, StringComparer.Ordinal) && ImageGenerateStepConfig.TierLongEdges[t] >= longEdge)
                return t;
        }

        return values.Contains("4K", StringComparer.Ordinal) ? "4K" : null;
    }

    /// <summary>The listed ratio word nearest to <paramref name="ratio"/> (log distance; exact when listed). "auto" is never chosen.</summary>
    public static string? NearestAspect(IEnumerable<string> words, double ratio)
    {
        string? best = null;
        double bestDistance = double.MaxValue;
        foreach (string word in words)
        {
            if (!TryParseRatio(word, out double r))
                continue;
            double distance = Math.Abs(Math.Log(r / ratio));
            if (distance < bestDistance - 1e-12)
            {
                bestDistance = distance;
                best = word;
            }
        }

        return best;
    }

    /// <summary>
    /// Among the listed sizes at the nearest aspect ratio, the smallest that covers both target sides; when none covers, the
    /// largest at that aspect.
    /// </summary>
    public static (int W, int H)? NearestSize(IEnumerable<string> sizes, int targetWidth, int targetHeight)
    {
        List<(int W, int H)> parsed = [];
        foreach (string s in sizes)
        {
            string[] parts = s.Split('x');
            if (parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int w)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int h)
                && w > 0 && h > 0)
                parsed.Add((w, h));
        }

        if (parsed.Count == 0)
            return null;

        double ratio = (double)targetWidth / targetHeight;
        double nearest = parsed.Min(p => Math.Abs(Math.Log((double)p.W / p.H / ratio)));
        List<(int W, int H)> group = [.. parsed.Where(p => Math.Abs(Math.Log((double)p.W / p.H / ratio)) <= nearest + 1e-9)];
        List<(int W, int H)> covering = [.. group.Where(p => p.W >= targetWidth && p.H >= targetHeight)];
        return covering.Count > 0
            ? covering.OrderBy(p => (long)p.W * p.H).First()
            : group.OrderByDescending(p => (long)p.W * p.H).First();
    }

    private static bool TryParseRatio(string word, out double ratio)
    {
        ratio = 0;
        string[] parts = word.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
            && a > 0 && b > 0)
        {
            ratio = a / b;
            return true;
        }

        return false;
    }
}
