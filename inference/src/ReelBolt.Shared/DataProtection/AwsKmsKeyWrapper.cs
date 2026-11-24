using Amazon;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// <see cref="IKeyWrapper"/> over AWS KMS (<c>Encrypt</c>/<c>Decrypt</c> of the small data key; the
/// bulk XML is encrypted locally by <see cref="KmsXmlEncryptor"/>, so the 4 KB KMS limit never applies).
/// The Inference API needs <c>kms:Encrypt</c> + <c>kms:Decrypt</c>; the WorkflowEngine only <c>kms:Decrypt</c>.
/// Credentials come from the SDK's default chain (instance role, IRSA, env).
/// </summary>
public sealed class AwsKmsKeyWrapper : IKeyWrapper, IDisposable
{
    private static readonly Dictionary<string, string> Context = new() { ["purpose"] = "reelbolt-data-protection-ring" };
    private readonly IAmazonKeyManagementService _kms;

    public AwsKmsKeyWrapper(string keyId, string? region = null)
        : this(keyId, string.IsNullOrWhiteSpace(region)
            ? new AmazonKeyManagementServiceClient()
            : new AmazonKeyManagementServiceClient(RegionEndpoint.GetBySystemName(region)))
    {
    }

    public AwsKmsKeyWrapper(string keyId, IAmazonKeyManagementService kms)
    {
        if (string.IsNullOrWhiteSpace(keyId))
        {
            throw new ArgumentException("A KMS key id is required.", nameof(keyId));
        }

        KeyId = keyId;
        _kms = kms;
    }

    public string KeyId { get; }

    public byte[] Wrap(byte[] plaintext)
    {
        using var input = new MemoryStream(plaintext);
        EncryptResponse response = _kms.EncryptAsync(new EncryptRequest
        {
            KeyId = KeyId,
            Plaintext = input,
            EncryptionContext = Context,
        }).GetAwaiter().GetResult();
        return response.CiphertextBlob.ToArray();
    }

    public byte[] Unwrap(byte[] wrapped)
    {
        using var input = new MemoryStream(wrapped);
        DecryptResponse response = _kms.DecryptAsync(new DecryptRequest
        {
            KeyId = KeyId,
            CiphertextBlob = input,
            EncryptionContext = Context,
        }).GetAwaiter().GetResult();
        return response.Plaintext.ToArray();
    }

    public void Dispose() => _kms.Dispose();
}
