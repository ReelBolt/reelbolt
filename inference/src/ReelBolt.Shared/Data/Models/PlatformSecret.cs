namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A named, Data-Protection-protected platform secret (table <c>platform_secrets</c>). Holds the
/// assistant confirmation-token HMAC key when <c>DataProtection:Store = Database</c>, so replicas share
/// it without a shared <c>/keys</c> volume. Inference API owned.
/// </summary>
public class PlatformSecret
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Output of <c>ISecretProtector.Protect</c>; never plaintext.</summary>
    public string Value { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
