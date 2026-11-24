using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace ReelBolt.Shared.DataProtection;

/// <summary>Shared envelope format of <see cref="KmsXmlEncryptor"/> / <see cref="KmsXmlDecryptor"/>.</summary>
internal static class KmsEnvelope
{
    public const string ElementName = "kmsWrapped";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // Layout (base64 of): kidLen u16 | kid | dekLen u16 | wrappedDek | nonce(12) | tag(16) | ciphertext
    public static string Seal(IKeyWrapper wrapper, string plaintextXml)
    {
        byte[] dek = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
        byte[] plain = Encoding.UTF8.GetBytes(plaintextXml);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        using (var aes = new AesGcm(dek, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        byte[] kid = Encoding.UTF8.GetBytes(wrapper.KeyId);
        byte[] wrappedDek = wrapper.Wrap(dek);
        CryptographicOperations.ZeroMemory(dek);

        using var ms = new MemoryStream();
        WriteBlock(ms, kid);
        WriteBlock(ms, wrappedDek);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);
        return Convert.ToBase64String(ms.ToArray());
    }

    public static string Open(IKeyWrapper wrapper, string base64)
    {
        byte[] all = Convert.FromBase64String(base64);
        int pos = 0;
        _ = ReadBlock(all, ref pos); // key id: informational (KMS Decrypt resolves the key from the blob)
        byte[] wrappedDek = ReadBlock(all, ref pos);
        byte[] dek = wrapper.Unwrap(wrappedDek);
        try
        {
            ReadOnlySpan<byte> rest = all.AsSpan(pos);
            byte[] plain = new byte[rest.Length - NonceSize - TagSize];
            using var aes = new AesGcm(dek, TagSize);
            aes.Decrypt(rest[..NonceSize], rest[(NonceSize + TagSize)..], rest.Slice(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static void WriteBlock(Stream s, byte[] block)
    {
        s.WriteByte((byte)(block.Length >> 8));
        s.WriteByte((byte)block.Length);
        s.Write(block);
    }

    private static byte[] ReadBlock(byte[] all, ref int pos)
    {
        int len = (all[pos] << 8) | all[pos + 1];
        pos += 2;
        byte[] block = all.AsSpan(pos, len).ToArray();
        pos += len;
        return block;
    }
}

/// <summary>
/// Encrypts newly generated Data Protection keys with an envelope: a random AES-256-GCM data key
/// encrypts the key XML locally and an <see cref="IKeyWrapper"/> (AWS KMS) wraps the data key.
/// Only the API generates keys, so only the API needs <c>Encrypt</c> permission.
/// </summary>
public sealed class KmsXmlEncryptor : IXmlEncryptor
{
    private readonly IKeyWrapper _wrapper;

    public KmsXmlEncryptor(IKeyWrapper wrapper) => _wrapper = wrapper;

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var element = new XElement(
            KmsEnvelope.ElementName,
            KmsEnvelope.Seal(_wrapper, plaintextElement.ToString(SaveOptions.DisableFormatting)));
        return new EncryptedXmlInfo(element, typeof(KmsXmlDecryptor));
    }
}

/// <summary>
/// Decrypts KMS-wrapped key XML. Tolerant by design: an element that is not a <c>kmsWrapped</c>
/// envelope is a legacy, never-wrapped key and is returned unchanged, so a ring that mixes imported
/// plaintext keys with newly wrapped ones keeps working. Constructed by Data Protection with the
/// application's <see cref="IServiceProvider"/>.
/// </summary>
public sealed class KmsXmlDecryptor : IXmlDecryptor
{
    private readonly IServiceProvider _services;

    public KmsXmlDecryptor(IServiceProvider services) => _services = services;

    public XElement Decrypt(XElement encryptedElement)
    {
        if (encryptedElement.Name.LocalName != KmsEnvelope.ElementName)
        {
            return encryptedElement;
        }

        IKeyWrapper wrapper = (IKeyWrapper?)_services.GetService(typeof(IKeyWrapper))
            ?? throw new InvalidOperationException(
                "A Data Protection key is KMS-wrapped but no IKeyWrapper is configured (set DataProtection:Kms:KeyId).");
        return XElement.Parse(KmsEnvelope.Open(wrapper, encryptedElement.Value));
    }
}
