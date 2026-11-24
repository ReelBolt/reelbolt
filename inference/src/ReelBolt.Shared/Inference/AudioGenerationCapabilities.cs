using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>How a vendor rounds the quantity it bills.</summary>
public enum AudioBillingRounding
{
    /// <summary>Each started minute is a unit (fal ElevenLabs Music: "rounded up to the next minute").</summary>
    UpToWholeMinute,

    /// <summary>Each started second is a unit (the conservative reading of a per-second price).</summary>
    UpToWholeSecond,
}

/// <summary>
/// What a budget gate needs to price an audio take, read from the catalogue entry the request body is built from
/// (so the estimate and the request cannot disagree). <see cref="EstimateUsd"/> FAILS CLOSED: a duration outside
/// the range or a non-positive estimate throws, and the caller treats the model as unofferable
/// (<c>CONFIG_INVALID</c>, no ledger row).
/// </summary>
/// <param name="Model">The endpoint id / model id.</param>
/// <param name="Kind">Music or sound effect.</param>
/// <param name="Unit">The usage kind metered (<see cref="UsageKind.MusicGenMinutes"/> or <see cref="UsageKind.SoundEffectGenSeconds"/>).</param>
/// <param name="UsdPerUnit">The vendor list price of one <paramref name="Unit"/>.</param>
/// <param name="Rounding">How a duration becomes billed units.</param>
/// <param name="MinDurationSec">Shortest duration the platform will request.</param>
/// <param name="MaxDurationSec">Longest duration the platform will request (and the estimate for an unset one).</param>
/// <param name="PriceBasis">Where the price was read, in the vendor's own words.</param>
/// <param name="PriceMustBeMeasured">The billing unit is ambiguous on the vendor's own page and must be measured with a real key.</param>
public sealed record AudioGenerationCapabilities(
    string Model,
    AudioGenerationKind Kind,
    UsageKind Unit,
    decimal UsdPerUnit,
    AudioBillingRounding Rounding,
    double MinDurationSec,
    double MaxDurationSec,
    string PriceBasis,
    bool PriceMustBeMeasured)
{
    /// <summary>The billed quantity for <paramref name="durationSec"/> seconds of output, in <see cref="Unit"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is not positive.</exception>
    public decimal BilledUnits(double durationSec)
    {
        if (!(durationSec > 0) || double.IsInfinity(durationSec))
        {
            throw new ArgumentOutOfRangeException(nameof(durationSec), durationSec, "A billed duration must be positive.");
        }

        // QA-P1-9: the exact probed duration, never a pre-rounded one. Rounding to 3 dp first collapsed the
        // 60.000/60.001 boundary (a 60.029 s probe of a 60 s track billed two minutes) and hid every unit that
        // starts in the last millisecond of a run. A started unit is started once the probe is past it, so a
        // played 60.001 s is two minutes and a played 60.000 s is one; ffprobe's own precision is the
        // quantity's precision, and the caller measures the file it was billed for
        // (AudioGenerateStepExecutor's probe), so no tolerance belongs here.
        decimal seconds = (decimal)durationSec;
        return Rounding switch
        {
            AudioBillingRounding.UpToWholeMinute => Math.Ceiling(seconds / 60m),
            AudioBillingRounding.UpToWholeSecond => Math.Ceiling(seconds),
            _ => throw new NotSupportedException("Unhandled rounding " + Rounding),
        };
    }

    /// <summary>
    /// The USD a take of <paramref name="durationSec"/> seconds costs; an unset duration is priced at
    /// <see cref="MaxDurationSec"/>, because the vendor then chooses the length.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The duration is outside the endpoint's range.</exception>
    /// <exception cref="InvalidOperationException">The price yields a non-positive estimate (fail closed).</exception>
    public decimal EstimateUsd(double? durationSec)
    {
        double seconds = durationSec ?? MaxDurationSec;
        if (seconds < MinDurationSec || seconds > MaxDurationSec)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSec), seconds,
                $"'{Model}' takes a duration from {MinDurationSec} to {MaxDurationSec} seconds.");
        }

        decimal estimate = BilledUnits(seconds) * UsdPerUnit;
        if (estimate <= 0m)
        {
            throw new InvalidOperationException(
                $"The price of '{Model}' yields a non-positive estimate ({estimate}); the model cannot be offered.");
        }

        return estimate;
    }
}
