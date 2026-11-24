using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Shared.DataProtection;

/// <summary>
/// The single Data Protection registration used by both services (docs/design/cloud-provider-adr.md).
/// <list type="bullet">
/// <item><c>DataProtection:Store</c> = <c>FileSystem</c> (default; <c>DataProtection:KeysPath</c>, default
/// <c>/keys</c>) or <c>Database</c> (table <c>data_protection_keys</c>).</item>
/// <item><c>DataProtection:Kms:KeyId</c> (+ optional <c>:Region</c>) selects AWS KMS wrapping;
/// <c>DataProtection:Kms:LocalKey</c> (base64, 32 bytes) selects the local wrapper; neither = keys stay
/// unwrapped, exactly as before. Wrapping applies only to Database mode.</item>
/// </list>
/// <b>Never change the application name below, or the <c>DataProtectionSecretProtector</c> purpose
/// string</b>: both are part of the key derivation (decision D20).
/// </summary>
public static class DataProtectionRegistration
{
    public const string StoreKey = "DataProtection:Store";
    public const string KeysPathKey = "DataProtection:KeysPath";
    public const string DefaultKeysPath = "/keys";

    public static bool IsDatabaseStore(IConfiguration configuration) =>
        string.Equals(configuration[StoreKey], "Database", StringComparison.OrdinalIgnoreCase);

    /// <param name="readOnlyRing">True for the WorkflowEngine: it never generates or revokes keys. In the file-system
    /// store it also never writes a key file, but only when <c>EngineJwt:SigningKey</c> is set (a split engine
    /// host); with the key unset a single-host install keeps the writable ring it always had.</param>
    public static IServiceCollection AddReelBoltDataProtection<TContext>(
        this IServiceCollection services, IConfiguration configuration, bool readOnlyRing)
        where TContext : DbContext
    {
        IDataProtectionBuilder builder = services.AddDataProtection()
            // Pre-rebrand name, kept on purpose: it isolates the key ring, so changing it makes every stored provider API key undecryptable.
            .SetApplicationName("ReelForge");

        if (!IsDatabaseStore(configuration))
        {
            string keysPath = configuration[KeysPathKey] ?? DefaultKeysPath;
            if (!readOnlyRing || string.IsNullOrWhiteSpace(configuration[EngineJwtSettings.SigningKeyKey]))
            {
                builder.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
                return services;
            }

            // The engine only reads the ring the Inference API writes: it never generates a key and never
            // writes a file, so a compromised engine cannot add a key of its own to the shared ring.
            services.AddOptions<KeyManagementOptions>().Configure(options =>
            {
                options.XmlRepository = new ReadOnlyFileKeyRepository(keysPath);
                options.AutoGenerateKeys = false;
            });
            return services;
        }

        IKeyWrapper? wrapper = CreateWrapper(configuration);
        if (wrapper is not null)
        {
            services.AddSingleton(wrapper);
        }

        services.AddOptions<KeyManagementOptions>().Configure<IServiceScopeFactory>((options, scopes) =>
        {
            options.XmlRepository = new DataProtectionKeyStore<TContext>(scopes, readOnlyRing);
            if (readOnlyRing)
            {
                options.AutoGenerateKeys = false;
            }
            else if (wrapper is not null)
            {
                options.XmlEncryptor = new KmsXmlEncryptor(wrapper);
            }
        });

        return services;
    }

    private static IKeyWrapper? CreateWrapper(IConfiguration configuration)
    {
        string? keyId = configuration["DataProtection:Kms:KeyId"];
        if (!string.IsNullOrWhiteSpace(keyId))
        {
            return new AwsKmsKeyWrapper(keyId, configuration["DataProtection:Kms:Region"]);
        }

        string? local = configuration["DataProtection:Kms:LocalKey"];
        if (!string.IsNullOrWhiteSpace(local))
        {
            return new LocalKeyWrapper(Convert.FromBase64String(local));
        }

        return null;
    }
}
