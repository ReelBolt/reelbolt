namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One in-flight device pairing request — the RFC 8628-style device authorisation flow of
/// <c>docs/design/desktop-runner-adr.md</c> §9 ("Pairing"), specified by WP E4.
/// Table <c>runner_pairing_requests</c>, owned (migrated) by the Inference API; the Go API writes it
/// through GORM and never migrates. Nothing here is a long-lived platform credential: the row is the
/// <em>unapproved</em> half of pairing, and approving it is what creates the
/// <see cref="RunnerDevice"/> row. The 32-byte device code is a bearer secret for this row alone, so
/// only its hash is stored, it expires in ten minutes and it is single-use.
/// </summary>
public class RunnerPairingRequest
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 (hex) of the 32-byte device code; the code itself is never stored.</summary>
    public string DeviceCodeHash { get; set; } = string.Empty;

    /// <summary>
    /// The short code the user types in the dashboard, shown as <c>XXXX-XXXX</c>. Stored normalized
    /// (uppercase, no separator). Unique among <see cref="RunnerPairingStatus.Pending"/> rows — a
    /// partial unique index enforces that, and the Go API sweeps expired rows out of
    /// <see cref="RunnerPairingStatus.Pending"/> before it issues a new one.
    /// </summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Base64 Ed25519 public key the device registered. The private half never leaves the device.</summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Hex SHA-256 of <see cref="PublicKey"/>. Kept so a key that is already paired is refused at
    /// approval time as well, against the unique index on <c>runner_devices.public_key_fingerprint</c>.
    /// </summary>
    public string PublicKeyFingerprint { get; set; } = string.Empty;

    /// <summary>User-supplied device name, shown on the approval screen.</summary>
    public string Name { get; set; } = string.Empty;

    public string? Os { get; set; }
    public string? Arch { get; set; }
    public string? RunnerVersion { get; set; }

    /// <summary>Client address the request came from, shown on the approval screen ("requested from").</summary>
    public string? RequestIp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }

    /// <summary>Last poll of this request, so polling faster than the advertised interval is refused.</summary>
    public DateTime? LastPolledAt { get; set; }

    public RunnerPairingStatus Status { get; set; } = RunnerPairingStatus.Pending;

    public DateTime? ApprovedAt { get; set; }
    public DateTime? DeniedAt { get; set; }

    /// <summary>
    /// The organization the approver chose. Recorded here as well as on the device row so the request
    /// answers on its own which workspace the device joined, including after the device is revoked.
    /// </summary>
    public Guid? ApprovedOrganizationId { get; set; }

    public Guid? ApprovedByUserId { get; set; }

    /// <summary>The device row approval created. Null until then.</summary>
    public Guid? ApprovedDeviceId { get; set; }

    /// <summary>
    /// When the approved result was handed to the device. A pairing code is single-use, so a later
    /// poll of the same device code is refused — the device must start a new pairing.
    /// </summary>
    public DateTime? ConsumedAt { get; set; }
}
