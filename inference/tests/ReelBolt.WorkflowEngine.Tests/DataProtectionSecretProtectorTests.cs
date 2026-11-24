using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DataProtectionSecretProtectorTests
{
    // The purpose string is part of the key derivation. Every API key stored before the ReelBolt
    // rebrand was protected under this exact value, so it must never change.
    private const string PersistedPurpose = "ReelForge.InferenceProvider.ApiKey";

    [Fact]
    public void Decrypts_keys_protected_under_the_persisted_purpose()
    {
        IDataProtectionProvider provider = new EphemeralDataProtectionProvider();
        string ciphertext = provider.CreateProtector(PersistedPurpose).Protect("sk-test");

        DataProtectionSecretProtector sut = new(provider, NullLogger<DataProtectionSecretProtector>.Instance);

        sut.TryUnprotect(ciphertext, out string plaintext).Should().BeTrue();
        plaintext.Should().Be("sk-test");
    }
}
