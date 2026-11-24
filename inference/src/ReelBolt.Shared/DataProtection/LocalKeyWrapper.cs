using System.Security.Cryptography;

namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// AES-256-GCM wrapper over a locally supplied 32-byte key. For tests and for self-hosters who run
/// <c>DataProtection:Store = Database</c> and want the ring encrypted without a cloud KMS. The key
/// itself must come from a secret, never from the database that holds the ring.
/// </summary>
public sealed class LocalKeyWrapper : IKeyWrapper
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] _key;

    public LocalKeyWrapper(byte[] key, string keyId = "local")
    {
        if (key is null || key.Length != 32)
        {
            throw new ArgumentException("A local wrapping key must be exactly 32 bytes.", nameof(key));
        }

        _key = (byte[])key.Clone();
        KeyId = keyId;
    }

    public string KeyId { get; }

    public byte[] Wrap(byte[] plaintext)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] cipher = new byte[plaintext.Length];
        byte[] tag = new byte[TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unwrap(byte[] wrapped)
    {
        if (wrapped.Length < NonceSize + TagSize)
        {
            throw new CryptographicException("Wrapped payload is too short.");
        }

        ReadOnlySpan<byte> all = wrapped;
        byte[] plain = new byte[wrapped.Length - NonceSize - TagSize];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(all[..NonceSize], all[(NonceSize + TagSize)..], all.Slice(NonceSize, TagSize), plain);
        return plain;
    }
}
