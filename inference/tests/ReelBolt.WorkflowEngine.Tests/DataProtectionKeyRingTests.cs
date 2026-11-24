using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Security.Cryptography;
using System.Xml.Linq;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DataProtection;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The safety net for plan decision D20: a provider API key protected under the file-system key ring
/// must still unprotect after the ring moves into <c>data_protection_keys</c>, with or without KMS
/// wrapping, and the WorkflowEngine's read-only mapping must read what the API wrote.
/// </summary>
public sealed class DataProtectionKeyRingTests : IDisposable
{
    private readonly string _keysDir = Path.Combine(Path.GetTempPath(), "rb-dp-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbName = "dp-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (Directory.Exists(_keysDir))
        {
            Directory.Delete(_keysDir, recursive: true);
        }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private ServiceProvider Build(IConfiguration configuration, bool engine)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (engine)
        {
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(_dbName), ServiceLifetime.Scoped);
            services.AddReelBoltDataProtection<WorkflowEngineDbContext>(configuration, readOnlyRing: true);
        }
        else
        {
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(_dbName), ServiceLifetime.Scoped);
            services.AddReelBoltDataProtection<InferenceApiDbContext>(configuration, readOnlyRing: false);
        }

        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildFileRing() =>
        Build(Config(("DataProtection:KeysPath", _keysDir)), engine: false);

    [Fact]
    public async Task File_ring_protected_secret_unprotects_after_switching_to_the_database_store_and_importing()
    {
        string ciphertext;
        using (ServiceProvider fileRing = BuildFileRing())
        {
            ciphertext = fileRing.GetRequiredService<ISecretProtector>().Protect("sk-live-123");
        }

        Directory.GetFiles(_keysDir, "key-*.xml").Should().NotBeEmpty();

        IConfiguration dbConfig = Config(
            ("DataProtection:Store", "Database"),
            ("DataProtection:KeysPath", _keysDir));
        using ServiceProvider dbRing = Build(dbConfig, engine: false);

        using (IServiceScope scope = dbRing.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
            (await DataProtectionKeyImporter.ImportAsync(db, _keysDir, NullLogger.Instance)).Should().BeGreaterThan(0);
            // Idempotent: a second run with a populated table imports nothing.
            (await DataProtectionKeyImporter.ImportAsync(db, _keysDir, NullLogger.Instance)).Should().Be(0);
        }

        dbRing.GetRequiredService<ISecretProtector>().TryUnprotect(ciphertext, out string plaintext).Should().BeTrue();
        plaintext.Should().Be("sk-live-123");

        // Imported verbatim: the stored XML is byte-identical to the file.
        using IServiceScope check = dbRing.CreateScope();
        var rows = check.ServiceProvider.GetRequiredService<InferenceApiDbContext>().DataProtectionKeys.ToList();
        string file = Directory.GetFiles(_keysDir, "key-*.xml").Single();
        rows.Should().ContainSingle(r => r.Xml == File.ReadAllText(file));
    }

    [Fact]
    public async Task Engine_read_only_mapping_unprotects_what_the_api_ring_wrote_and_never_generates_keys()
    {
        string ciphertext;
        using (ServiceProvider fileRing = BuildFileRing())
        {
            ciphertext = fileRing.GetRequiredService<ISecretProtector>().Protect("engine-secret");
        }

        IConfiguration dbConfig = Config(("DataProtection:Store", "Database"));
        using (ServiceProvider api = Build(dbConfig, engine: false))
        using (IServiceScope scope = api.CreateScope())
        {
            await DataProtectionKeyImporter.ImportAsync(
                scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), _keysDir, NullLogger.Instance);
        }

        using ServiceProvider engine = Build(dbConfig, engine: true);
        engine.GetRequiredService<ISecretProtector>().TryUnprotect(ciphertext, out string plaintext).Should().BeTrue();
        plaintext.Should().Be("engine-secret");

        var store = new DataProtectionKeyStore<WorkflowEngineDbContext>(engine.GetRequiredService<IServiceScopeFactory>(), readOnly: true);
        Action write = () => store.StoreElement(new XElement("key"), "x");
        write.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Wrapped_new_keys_round_trip_alongside_imported_plaintext_keys_and_are_not_stored_in_the_clear()
    {
        string legacyCipher;
        using (ServiceProvider fileRing = BuildFileRing())
        {
            legacyCipher = fileRing.GetRequiredService<ISecretProtector>().Protect("legacy");
        }

        string kmsKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        IConfiguration dbConfig = Config(("DataProtection:Store", "Database"), ("DataProtection:Kms:LocalKey", kmsKey));

        string newCipher;
        using (ServiceProvider api = Build(dbConfig, engine: false))
        {
            using (IServiceScope scope = api.CreateScope())
            {
                await DataProtectionKeyImporter.ImportAsync(
                    scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>(), _keysDir, NullLogger.Instance);
            }

            // Force a brand-new (wrapped) key into the ring next to the imported one.
            var manager = api.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
            manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));
            newCipher = api.GetRequiredService<ISecretProtector>().Protect("fresh");

            using IServiceScope check = api.CreateScope();
            List<string> xml = check.ServiceProvider.GetRequiredService<InferenceApiDbContext>()
                .DataProtectionKeys.Select(k => k.Xml).ToList();
            xml.Should().HaveCount(2);
            xml.Count(x => x.Contains("kmsWrapped")).Should().Be(1);
            xml.Count(x => !x.Contains("kmsWrapped")).Should().Be(1);
        }

        // A second process (the engine) with the same wrapper reads both generations.
        using ServiceProvider engine = Build(dbConfig, engine: true);
        ISecretProtector protector = engine.GetRequiredService<ISecretProtector>();
        protector.TryUnprotect(legacyCipher, out string legacy).Should().BeTrue();
        legacy.Should().Be("legacy");
        protector.TryUnprotect(newCipher, out string fresh).Should().BeTrue();
        fresh.Should().Be("fresh");

        // Without the wrapper the wrapped key cannot be read: the new ciphertext fails closed.
        using ServiceProvider noWrapper = Build(Config(("DataProtection:Store", "Database")), engine: true);
        noWrapper.GetRequiredService<ISecretProtector>().TryUnprotect(newCipher, out _).Should().BeFalse();
    }

    [Fact]
    public void Decryptor_returns_a_legacy_unwrapped_element_unchanged()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var decryptor = new KmsXmlDecryptor(services);
        var plain = new XElement("masterKey", new XElement("value", "abc"));

        decryptor.Decrypt(plain).Should().BeSameAs(plain);
    }

    [Fact]
    public void Local_wrapper_rejects_a_tampered_payload()
    {
        var wrapper = new LocalKeyWrapper(RandomNumberGenerator.GetBytes(32));
        byte[] wrapped = wrapper.Wrap([1, 2, 3, 4]);
        wrapper.Unwrap(wrapped).Should().Equal(1, 2, 3, 4);

        wrapped[^1] ^= 0xFF;
        Action act = () => wrapper.Unwrap(wrapped);
        act.Should().Throw<CryptographicException>();
    }
}
