using Microsoft.AspNetCore.DataProtection;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>
/// Protects webhook signing secrets with a data-protection purpose of their own, so a provider API key blob can
/// never be passed off as a webhook secret (or the reverse). Registered as itself, not as the shared
/// <see cref="ISecretProtector"/>.
/// </summary>
public sealed class WebhookSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    public const string Purpose = "ReelBolt.Webhooks.EndpointSecret";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public bool TryUnprotect(string? ciphertext, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(ciphertext)) return false;
        try
        {
            plaintext = _protector.Unprotect(ciphertext);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }
}
