namespace ReelBolt.Shared.Inference;

/// <summary>
/// Fail-closed cost estimation for video generation. The estimate is what the budget gate
/// charges against every cap, so an estimate of zero or less would pass all of them and, once
/// persisted, credit the daily ledger. Anything this class cannot price therefore throws;
/// a returned value is always strictly positive.
/// </summary>
public static class VideoGenerationPricing
{
    /// <summary>
    /// Looks a resolution up in a model's price table, ordinally — no case folding, no trimming. A
    /// value the table does not key exactly cannot be priced, so it is not guessed.
    /// <para>
    /// This is the ONE resolution→rate rule in the codebase. It is exposed so that
    /// <see cref="VideoGenerationPriceOracleExtensions.QuoteFromStaticTable"/> can build a per-second
    /// rate for the static-table pricing path without restating the lookup, which is what keeps the
    /// quote and the estimate from ever disagreeing about the same model.
    /// </para>
    /// </summary>
    /// <param name="capabilities">The model's capabilities. Must not be null.</param>
    /// <param name="resolution">The resolution token to look up.</param>
    /// <param name="pricePerSecond">The model's per-second price for that resolution, when found.</param>
    /// <returns><c>true</c> when the table prices <paramref name="resolution"/>; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
    public static bool TryGetPricePerSecond(
        VideoGenerationCapabilities capabilities, string resolution, out decimal pricePerSecond)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        foreach (var (key, price) in capabilities.PricePerSecondByResolution)
        {
            if (string.Equals(key, resolution, StringComparison.Ordinal))
            {
                pricePerSecond = price;
                return true;
            }
        }

        pricePerSecond = 0m;
        return false;
    }

    /// <summary>
    /// Estimates the USD cost of ONE clip: the model's per-second price for
    /// <paramref name="resolution"/> (looked up ordinally — no case folding) times
    /// <paramref name="durationSeconds"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="durationSeconds"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="resolution"/> has no price in the model's table.</exception>
    /// <exception cref="InvalidOperationException">The price table yields a non-positive estimate.</exception>
    public static decimal EstimateClipCostUsd(
        VideoGenerationCapabilities capabilities, string resolution, int durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationSeconds);

        if (!TryGetPricePerSecond(capabilities, resolution, out decimal pricePerSecond))
        {
            throw new ArgumentException(
                $"Resolution '{resolution}' has no price for this model.", nameof(resolution));
        }

        var estimate = pricePerSecond * durationSeconds;
        if (estimate <= 0m)
        {
            throw new InvalidOperationException(
                $"Price table yields a non-positive estimate ({estimate}) for resolution '{resolution}'.");
        }

        return estimate;
    }
}
