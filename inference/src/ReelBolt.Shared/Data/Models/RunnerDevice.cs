namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A runner the gateway can hand jobs to: a paired desktop machine or a cloud pool worker.
/// Table <c>runner_devices</c>, owned (migrated) by the Inference API; the WorkflowEngine maps it
/// read-only. The Go runner gateway reads and updates it through raw SQL and never migrates.
/// Schema ruling: this table is owned here, including the
/// columns the desktop pairing flow (E4a) needs.
/// </summary>
public class RunnerDevice
{
    public Guid Id { get; set; }

    /// <summary>The owning organization. Null only for <see cref="RunnerKind.CloudPool"/> workers.</summary>
    public Guid? OrganizationId { get; set; }

    public RunnerKind Kind { get; set; } = RunnerKind.Desktop;
    public string Name { get; set; } = string.Empty;

    /// <summary>Base64 Ed25519 public key registered at pairing. Null for pool workers (pool credential instead).</summary>
    public string? PublicKey { get; set; }

    /// <summary>Hex SHA-256 of <see cref="PublicKey"/>; globally unique so a key cannot pair twice.</summary>
    public string? PublicKeyFingerprint { get; set; }

    /// <summary>The capabilities the runner last advertised in <c>hello</c> (jsonb).</summary>
    public string? CapabilitiesJson { get; set; }

    public string? Os { get; set; }
    public string? Arch { get; set; }
    public string? RunnerVersion { get; set; }

    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedByUserId { get; set; }

    /// <summary>The user who created the row (for a desktop: who approved the pairing).</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>The user who completed pairing at the machine.</summary>
    public Guid? PairedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
