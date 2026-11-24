#nullable enable
using System.Text;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Archives;

/// <summary>
/// Whether this test host really has Unicode normalisation data (ICU). Probed by behaviour, not by switch:
/// the <c>DOTNET_SYSTEM_GLOBALIZATION_INVARIANT</c> environment variable does not set the AppContext switch.
/// </summary>
public static class TestGlobalization
{
    public static readonly bool NormalizationWorks = "é".Normalize(NormalizationForm.FormC) == "é";
}

/// <summary>A fact that only runs on a host with normalisation data; skipped (not failed) under invariant globalization.</summary>
public sealed class IcuFactAttribute : FactAttribute
{
    public IcuFactAttribute()
    {
        if (!TestGlobalization.NormalizationWorks)
            Skip = "needs ICU normalisation data; this host runs in invariant globalization mode";
    }
}

/// <summary>A fact that only runs when normalisation is NOT available (the fail-closed path).</summary>
public sealed class InvariantFactAttribute : FactAttribute
{
    public InvariantFactAttribute()
    {
        if (TestGlobalization.NormalizationWorks)
            Skip = "only meaningful when the host runs in invariant globalization mode";
    }
}
