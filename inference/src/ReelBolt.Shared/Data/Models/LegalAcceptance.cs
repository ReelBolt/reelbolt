namespace ReelBolt.Shared.Data.Models;

/// <summary>Which legal document was accepted. Stored by name.</summary>
public enum LegalDocument
{
    Terms,
    Privacy,
}

/// <summary>Proof that a user accepted a specific version of a legal document.</summary>
public class LegalAcceptance
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public LegalDocument Document { get; set; }

    /// <summary>Version label of the document at acceptance time (e.g. an ISO date).</summary>
    public string Version { get; set; } = string.Empty;

    public DateTime AcceptedAt { get; set; } = DateTime.UtcNow;
    public string? Ip { get; set; }
}
