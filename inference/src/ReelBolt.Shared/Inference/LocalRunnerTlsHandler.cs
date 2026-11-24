using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The TLS trust for the runner gateway relay, built from <see cref="LocalRunnerGatewayOptions"/>. One instance lives
/// on each options snapshot; its <see cref="SocketsHttpHandler"/> is built once and reused by the chat and the
/// embedding paths. Certificate files are read lazily on first use. Rotating a certificate or CA needs a restart
/// (the options are bound once and not watched). There is no accept-any-certificate path: with a CA file the
/// server chain must build to exactly that CA (system roots ignored, revocation off for private CAs) and the name
/// is still checked against the URL host.
/// </summary>
internal sealed class LocalRunnerTlsHandler(LocalRunnerGatewayOptions options)
{
    private readonly object _gate = new();
    private SocketsHttpHandler? _handler;

    /// <summary>Throws the typed "not set up" failure when TLS is required and the address is not https.</summary>
    public void EnsureTransportAllowed()
    {
        // Defence in depth: the same rules the startup validator enforces.
        if (LocalRunnerGatewayOptionsValidator.Check(options).Count == 0) return;
        throw new LocalRunnerUnavailableException(
            "Local models are not set up yet: the connection to local models is not secured.", "tls_required");
    }

    /// <summary>The shared handler, or null when no trust setting is configured (default transport).</summary>
    public HttpMessageHandler? GetHandler()
    {
        bool ca = !string.IsNullOrWhiteSpace(options.GatewayCaFile);
        bool cert = !string.IsNullOrWhiteSpace(options.GatewayClientCertFile) || !string.IsNullOrWhiteSpace(options.GatewayClientKeyFile);
        if (!ca && !cert) return null;
        lock (_gate)
        {
            return _handler ??= Build(ca, cert);
        }
    }

    private SocketsHttpHandler Build(bool ca, bool cert)
    {
        SslClientAuthenticationOptions ssl = new();
        if (ca)
        {
            X509Certificate2Collection roots = [];
            try
            {
                roots.ImportFromPemFile(options.GatewayCaFile.Trim());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                throw NotSetUp("the trusted certificate authority file could not be read", ex);
            }

            if (roots.Count == 0) throw NotSetUp("the trusted certificate authority file holds no certificate", null);
            X509ChainPolicy policy = new()
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck
            };
            policy.CustomTrustStore.AddRange(roots);
            ssl.CertificateChainPolicy = policy;
        }

        if (cert)
        {
            if (string.IsNullOrWhiteSpace(options.GatewayClientCertFile) || string.IsNullOrWhiteSpace(options.GatewayClientKeyFile))
                throw NotSetUp("the client certificate needs both a certificate file and a key file", null);
            try
            {
                string? pw = string.IsNullOrEmpty(options.GatewayClientKeyPassword) ? null : options.GatewayClientKeyPassword;
                using X509Certificate2 pem = pw is null
                    ? X509Certificate2.CreateFromPemFile(options.GatewayClientCertFile.Trim(), options.GatewayClientKeyFile.Trim())
                    : X509Certificate2.CreateFromEncryptedPemFile(options.GatewayClientCertFile.Trim(), pw, options.GatewayClientKeyFile.Trim());
                // Re-import so the private key is usable by SslStream on every platform.
                ssl.ClientCertificates = [X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null)];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                throw NotSetUp("the client certificate could not be read", ex);
            }
        }

        return new SocketsHttpHandler { SslOptions = ssl, PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
    }

    private static LocalRunnerUnavailableException NotSetUp(string why, Exception? inner) =>
        new($"Local models are not set up yet: {why}.", "tls_config_invalid", inner: inner is null ? null : new InvalidOperationException("TLS file could not be loaded.", null));
}
