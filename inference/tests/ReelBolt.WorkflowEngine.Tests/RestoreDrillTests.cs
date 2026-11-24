using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DataProtection;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The database half of the F13 restore drill (<c>docs/disaster-recovery.md</c>), driven by
/// <c>infra/scripts/restore-drill.sh</c>. The drill restores PostgreSQL to a point in time, restores
/// the object store's copy beside it, and then asks these two facts to prove the restored database
/// is really the database the platform was serving:
/// <list type="number">
/// <item>the Data Protection key ring lives inside the database (the drill's whole premise), and</item>
/// <item>a provider API key protected <i>before</i> the backup still decrypts <i>after</i> the
/// restore, under the same application name and purpose string (D20) and the same KMS wrapper.</item>
/// </list>
/// Both facts are inert unless the drill sets <c>REELBOLT_DRILL_MODE</c>, exactly like the A1
/// backfill test and <see cref="ConfirmationTokenKeyMigrationTests"/>: the normal suite runs them
/// as immediate no-ops, so building and running it needs no PostgreSQL and no credential.
/// <para>
/// The seal half writes the facts it seeded into <c>REELBOLT_DRILL_STATE</c>; the verify half reads
/// them back and, only after every check has passed, writes <c>verifyOk</c> into the same file. The
/// script refuses to report a successful drill without that flag, so a misconfigured drill fails
/// loudly instead of quietly testing nothing.
/// </para>
/// <para>
/// The verify half has two arms. <c>seeded</c> (what the local drill uses) compares against the
/// exact ciphertext and plaintext recorded before the backup, and additionally proves the check can
/// fail: the same ciphertext must NOT decrypt under a different wrapper key. <c>census</c> (what a
/// staging drill uses, where the real keys' plaintexts are unknown to anybody) requires that every
/// encrypted provider key in the restored database decrypts, and that there is at least one. The
/// census arm wraps with <c>REELBOLT_DRILL_KMS_LOCAL_KEY</c> or, for real, with the services' own
/// <c>DataProtection__Kms__KeyId</c> / <c>DataProtection__Kms__Region</c> plus AWS credentials.
/// </para>
/// </summary>
public sealed class RestoreDrillTests
{
    /// <summary><c>seeded</c> (the local drill) or <c>census</c> (a drill against a real environment).</summary>
    public const string ModeVariable = "REELBOLT_DRILL_MODE";

    /// <summary>Connection string of the database the drill seeds, before the backup is taken.</summary>
    public const string SeedDsnVariable = "REELBOLT_DRILL_SEED_DSN";

    /// <summary>Connection string of the database the provider restored, after the backup.</summary>
    public const string VerifyDsnVariable = "REELBOLT_DRILL_VERIFY_DSN";

    /// <summary>JSON file carrying the drilled facts; written by the seed half, stamped by the verify half.</summary>
    public const string StateFileVariable = "REELBOLT_DRILL_STATE";

    /// <summary>Printed by the verify half, so the drill log says which keys were read and from where.</summary>
    public const string VerifyMarker = "reelbolt-restore-drill-verify: ok";

    /// <summary>Name prefix of the provider row the seeded arm writes; the drill updates its model after the backup.</summary>
    private const string DrillProviderPrefix = "drill-restore-";

    [Fact]
    public async Task Seed_writes_the_key_ring_into_the_database_and_one_protected_provider_key()
    {
        string mode = Mode();
        if (mode.Length == 0)
        {
            return;
        }

        mode.Should().Be("seeded", $"the seed half only makes sense in seeded mode, not '{mode}'");
        string dsn = Require(SeedDsnVariable);
        string statePath = Require(StateFileVariable);

        // The wrapper stands in for AWS KMS, through the same IKeyWrapper seam: the drill has no
        // cloud credential, so the wrapping key has to be one the drill itself can hold and pass on.
        string wrapperKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        string plaintext = "sk-drill-" + Guid.NewGuid().ToString("N");
        string providerName = DrillProviderPrefix + Guid.NewGuid().ToString("N")[..8];

        using ServiceProvider provider = Build(dsn, wrapperKey);
        string ciphertext;
        int keysSeeded;
        using (IServiceScope scope = provider.CreateScope())
        {
            InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            // The drill's source database starts empty; the real one holds whatever the release's
            // migrations produce, which is why the schema comes from the migrations and not from SQL.
            await db.Database.MigrateAsync();

            ciphertext = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect(plaintext);
            db.InferenceProviders.Add(new InferenceProvider
            {
                Id = Guid.NewGuid(),
                Name = providerName,
                Kind = InferenceProviderKind.OpenAICompatible,
                Endpoint = "https://drill.invalid/v1",
                ApiKeyEncrypted = ciphertext,
                ApiKeyLastFour = plaintext[^4..],
                IsEnabled = true,
            });
            await db.SaveChangesAsync();
            keysSeeded = await db.DataProtectionKeys.CountAsync();
        }

        keysSeeded.Should().BeGreaterThan(0,
            "the database key-ring store must have persisted a key before the backup, or the drill proves nothing");

        WriteState(statePath, new DrillState
        {
            WrapperKey = wrapperKey,
            ProviderName = providerName,
            ApiKeyPlaintext = plaintext,
            ApiKeyEncrypted = ciphertext,
            KeysSeeded = keysSeeded,
        });
    }

    [Fact]
    public async Task Verify_restored_database_still_decrypts_provider_keys()
    {
        string mode = Mode();
        if (mode.Length == 0)
        {
            return;
        }

        string dsn = Require(VerifyDsnVariable);
        string statePath = Require(StateFileVariable);
        DrillState state = mode == "seeded" ? ReadState(statePath) : new DrillState();

        using ServiceProvider provider = Build(dsn, state.WrapperKey);
        using IServiceScope scope = provider.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        ISecretProtector protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        int keysInRestore = await db.DataProtectionKeys.CountAsync();
        keysInRestore.Should().BeGreaterThan(0,
            "the restored database must carry the Data Protection key ring; without it no provider key is readable");

        string detail;
        if (mode == "seeded")
        {
            keysInRestore.Should().BeGreaterThanOrEqualTo(state.KeysSeeded,
                "the restored key ring must hold at least the keys the backup held");

            InferenceProvider row = await db.InferenceProviders.SingleAsync(p => p.Name == state.ProviderName);
            row.ApiKeyEncrypted.Should().Be(state.ApiKeyEncrypted,
                "a point-in-time restore must carry the exact ciphertext the backup held");

            protector.TryUnprotect(row.ApiKeyEncrypted, out string plaintext).Should().BeTrue(
                "a provider key protected before the backup must decrypt after the restore");
            plaintext.Should().Be(state.ApiKeyPlaintext);

            // Negative control: under a different wrapper the same ciphertext must fail, or the
            // assertion above would pass for a reason that has nothing to do with the restored ring.
            using ServiceProvider wrongWrapper = Build(dsn, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            using IServiceScope wrongScope = wrongWrapper.CreateScope();
            wrongScope.ServiceProvider.GetRequiredService<ISecretProtector>()
                .TryUnprotect(row.ApiKeyEncrypted, out _)
                .Should().BeFalse("the KMS wrapper is part of the answer, not a detail");

            detail = $"seeded arm: {keysInRestore} key(s), provider {row.Name} decrypted, wrong-wrapper control refused";
        }
        else
        {
            mode.Should().Be("census", $"unknown drill mode '{mode}'");
            List<InferenceProvider> rows = await db.InferenceProviders
                .Where(p => p.ApiKeyEncrypted != null && p.ApiKeyEncrypted != "")
                .ToListAsync();
            rows.Should().NotBeEmpty(
                "a census over a database with no encrypted provider keys proves nothing; point the drill at the environment it is meant to check");

            foreach (InferenceProvider row in rows)
            {
                protector.TryUnprotect(row.ApiKeyEncrypted, out _).Should().BeTrue(
                    $"provider '{row.Name}' carries a key the restored key ring cannot read");
            }

            detail = $"census arm: {keysInRestore} key(s), {rows.Count} provider key(s) all decrypted";
        }

        state.VerifyOk = true;
        state.VerifyDetail = detail;
        WriteState(statePath, state);
        Console.WriteLine($"{VerifyMarker} ({detail})");
    }

    private static ServiceProvider Build(string dsn, string base64WrapperKey)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(dsn));

        Dictionary<string, string?> settings = new()
        {
            [DataProtectionRegistration.StoreKey] = "Database",
        };

        // Which wrapper the restored ring needs, in the order the drill can supply it:
        //  1. the local wrapper the seed half generated (the local drill's stand-in for KMS);
        //  2. REELBOLT_DRILL_KMS_LOCAL_KEY, for a census over a database wrapped that way;
        //  3. the real KMS configuration the services themselves read, with the AWS SDK taking its
        //     credentials from the environment exactly as it does in the containers. A staging
        //     census needs these: both names are accepted, because the .env the services get spells
        //     them DATA_PROTECTION_KMS_KEY_ID / AWS_REGION and compose maps them to the first pair.
        string? localWrapper = base64WrapperKey.Length > 0
            ? base64WrapperKey
            : Environment.GetEnvironmentVariable("REELBOLT_DRILL_KMS_LOCAL_KEY");
        string? kmsKeyId = Environment.GetEnvironmentVariable("DataProtection__Kms__KeyId")
            ?? Environment.GetEnvironmentVariable("DATA_PROTECTION_KMS_KEY_ID");
        if (!string.IsNullOrWhiteSpace(localWrapper))
        {
            settings["DataProtection:Kms:LocalKey"] = localWrapper;
        }
        else if (!string.IsNullOrWhiteSpace(kmsKeyId))
        {
            settings["DataProtection:Kms:KeyId"] = kmsKeyId;
            settings["DataProtection:Kms:Region"] =
                Environment.GetEnvironmentVariable("DataProtection__Kms__Region")
                ?? Environment.GetEnvironmentVariable("AWS_REGION");
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddReelBoltDataProtection<InferenceApiDbContext>(configuration, readOnlyRing: false);
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services.BuildServiceProvider();
    }

    private static string Mode() => (Environment.GetEnvironmentVariable(ModeVariable) ?? string.Empty).Trim();

    private static string Require(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{variable} must be set for this half of the drill.");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static DrillState ReadState(string path)
    {
        File.Exists(path).Should().BeTrue(
            $"{path} carries the facts the seed half wrote; a seeded verify with no state proves nothing");
        return JsonSerializer.Deserialize<DrillState>(File.ReadAllText(path), Json)
            ?? throw new InvalidOperationException($"{path} is not a drill state file.");
    }

    private static void WriteState(string path, DrillState state)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(state, Json));
    }

    /// <summary>Everything the two halves have to agree on; travels as JSON through the drill's workspace.</summary>
    private sealed class DrillState
    {
        public string WrapperKey { get; set; } = string.Empty;
        public string ProviderName { get; set; } = string.Empty;
        public string ApiKeyPlaintext { get; set; } = string.Empty;
        public string ApiKeyEncrypted { get; set; } = string.Empty;
        public int KeysSeeded { get; set; }
        public bool VerifyOk { get; set; }
        public string VerifyDetail { get; set; } = string.Empty;
    }
}
