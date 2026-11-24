using System.Diagnostics.Metrics;

namespace ReelBolt.Shared.Inference;

/// <summary>Metrics for org-scoped provider resolution.</summary>
public static class InferenceTenancyDiagnostics
{
    /// <summary>Meter name; services that export metrics add it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.Tenancy";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>
    /// An explicit provider id that is not visible to the caller's organization (another org's row,
    /// a disabled row, or a BYO row after a downgrade). Resolution falls through as if missing.
    /// </summary>
    public static readonly Counter<long> ForeignProviderRef =
        Meter.CreateCounter<long>(
            "reelbolt.inference.foreign_provider_ref", "refs",
            "Explicit inference provider references not visible to the resolving organization");
}
