namespace ReelBolt.WorkflowEngine.Services.Marketplace;

/// <summary>
/// Engine side of the recipe-marketplace preview job. <see cref="Enabled"/> mirrors <c>Marketplace:Enabled</c>; the rest is
/// bound from <c>Marketplace:Preview</c>. No value here ever comes from a message.
/// </summary>
public sealed class MarketplacePreviewOptions
{
    public const string SectionName = "Marketplace:Preview";

    /// <summary>Master switch (<c>Marketplace:Enabled</c>). Off: the consumer answers every request with a failure and does no work.</summary>
    public bool Enabled { get; set; }

    /// <summary>A source longer than this is refused.</summary>
    public double MaxSourceSeconds { get; set; } = 90;

    /// <summary>A source larger than this is refused before it is read.</summary>
    public long MaxSourceBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Length of the cut.</summary>
    public double PreviewSeconds { get; set; } = 30;

    public int EncodeTimeoutSeconds { get; set; } = 300;
}
