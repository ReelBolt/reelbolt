namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// Wraps (encrypts) a small secret under a key held outside the database, so a database dump alone
/// cannot decrypt the Data Protection ring. Kept deliberately tiny so the key service can be swapped
/// (docs/design/cloud-provider-adr.md, KMS decision): AWS KMS in the cloud, a local key for tests
/// and opt-in self-host.
/// </summary>
public interface IKeyWrapper
{
    /// <summary>Identifier recorded next to every wrapped payload (KMS key id/ARN, or a local label).</summary>
    string KeyId { get; }

    byte[] Wrap(byte[] plaintext);

    /// <summary>Reverses <see cref="Wrap"/>. Throws when the payload was not produced by this key.</summary>
    byte[] Unwrap(byte[] wrapped);
}
