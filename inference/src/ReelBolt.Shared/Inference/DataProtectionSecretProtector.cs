using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// <see cref="ISecretProtector"/> backed by <see cref="IDataProtectionProvider"/>. Both services
/// must register Data Protection with the same application name and a shared, persisted key
/// ring directory, or the WorkflowEngine cannot decrypt keys the Inference API wrote (Risk R3).
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    // Pre-rebrand value, kept on purpose: it is part of the key derivation, so changing it makes
    // every stored provider API key undecryptable (pinned by DataProtectionSecretProtectorTests).
    private const string Purpose = "ReelForge.InferenceProvider.ApiKey";

    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionSecretProtector> _logger;

    public DataProtectionSecretProtector(
        IDataProtectionProvider dataProtectionProvider,
        ILogger<DataProtectionSecretProtector> logger)
    {
        _protector = dataProtectionProvider.CreateProtector(Purpose);
        _logger = logger;
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext ?? string.Empty);

    public bool TryUnprotect(string? ciphertext, out string plaintext)
    {
        plaintext = string.Empty;

        if (string.IsNullOrEmpty(ciphertext))
        {
            return false;
        }

        try
        {
            plaintext = _protector.Unprotect(ciphertext);
            return true;
        }
        catch (Exception ex)
        {
            // Data Protection can throw a variety of exception types when the key ring is
            // lost, mismatched, or corrupted. None of that may ever propagate out of here and
            // take down an inference call — surface it as "no usable key" instead (Risk R3).
            _logger.LogWarning(ex, "Failed to unprotect a secret; the Data Protection key ring may be lost or mismatched.");
            return false;
        }
    }
}
