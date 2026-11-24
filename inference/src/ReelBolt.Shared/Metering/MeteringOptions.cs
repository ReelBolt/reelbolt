namespace ReelBolt.Shared.Metering;

/// <summary>The <c>Metering</c> configuration section.</summary>
public sealed class MeteringOptions
{
    public const string SectionName = "Metering";

    /// <summary>
    /// When true, a metered call that runs outside any <see cref="UsageScope"/> (and passes no explicit scope)
    /// throws <see cref="UnattributedUsageException"/> instead of being written as an <c>Unattributed</c> row.
    /// A tripwire for tests and development: a new model call that forgot its scope fails loudly. Off by
    /// default, so production keeps recording such calls against the platform organization.
    /// </summary>
    public bool FailOnUnattributed { get; set; }
}

/// <summary>
/// A metered call ran outside any <see cref="UsageScope"/> while <see cref="MeteringOptions.FailOnUnattributed"/>
/// is on. The metering wrappers let it through instead of logging it, so the call that forgot its scope fails.
/// </summary>
public sealed class UnattributedUsageException : InvalidOperationException
{
    public UnattributedUsageException(string message)
        : base(message)
    {
    }
}
