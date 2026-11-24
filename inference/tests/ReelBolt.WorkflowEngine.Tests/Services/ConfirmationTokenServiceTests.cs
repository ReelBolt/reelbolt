using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// Coverage for the generalized propose-then-apply confirmation contract: the token is never
/// trusted as presented, it is recomputed and compared.
///
/// <para>The two halves are deliberately different. The keyed half (every operation except
/// <see cref="ConfirmationTokenService.ApplyWorkflowOperation"/>) is bound to the operation, the
/// payload and the acting user, and is asserted to be unguessable without the key. The legacy half
/// is pinned to the exact bytes <c>WorkflowEditDiffService</c> has always produced, because
/// decision 2 makes that value a compatibility contract — a proposal minted before a deploy must
/// still apply after it.</para>
/// </summary>
public class ConfirmationTokenServiceTests
{
    // --- helpers -------------------------------------------------------------------------------

    private const string Operation = "TestOperation";
    private const string OtherOperation = "AnotherOperation";
    private const string Payload = """{"stepOrder":1,"label":"Step 1"}""";

    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Deterministic stand-in for Data Protection: a round-trip marker, not encryption.</summary>
    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        private const string Marker = "protected:";

        public string Protect(string plaintext) => Marker + plaintext;

        public bool TryUnprotect(string? ciphertext, out string plaintext)
        {
            if (ciphertext is not null && ciphertext.StartsWith(Marker, StringComparison.Ordinal))
            {
                plaintext = ciphertext[Marker.Length..];
                return true;
            }

            plaintext = string.Empty;
            return false;
        }
    }

    private static ConfirmationTokenService CreateService(
        string? protectedKey = null,
        string? keyFilePath = null)
    {
        var settings = new Dictionary<string, string?>();
        if (protectedKey is not null)
        {
            settings["Assistant:ConfirmationTokenKey"] = protectedKey;
        }

        if (keyFilePath is not null)
        {
            settings["Assistant:ConfirmationTokenKeyFile"] = keyFilePath;
        }

        return new ConfirmationTokenService(
            new PassthroughSecretProtector(),
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<ConfirmationTokenService>.Instance);
    }

    /// <summary>A keyed service whose key is a fixed byte pattern, so tokens are reproducible.</summary>
    private static ConfirmationTokenService CreateKeyedService(byte fill)
        => CreateService(protectedKey: ProtectedKey(fill));

    private static string ProtectedKey(byte fill)
        => new PassthroughSecretProtector().Protect(
            Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()));

    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // --- the organization binding (A11) ---------------------------------------------------------

    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void Compute_binds_the_organization_two_organizations_disagree()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string inOrgA = service.Compute(Operation, Payload, UserA, OrgA);
        string inOrgB = service.Compute(Operation, Payload, UserA, OrgB);

        // Same operation, same payload, same user — only the active organization differs, and the
        // token differs with it. Without this, a caller who switched workspace between proposing and
        // applying would carry the proposal across.
        inOrgB.Should().NotBe(inOrgA);
    }

    [Fact]
    public void Verify_rejects_a_token_minted_in_another_organization()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string mintedInA = service.Compute(Operation, Payload, UserA, OrgA);

        service.Verify(Operation, Payload, UserA, OrgA, mintedInA).Should().BeTrue();
        service.Verify(Operation, Payload, UserA, OrgB, mintedInA).Should().BeFalse(
            "a token proposed in one organization must not verify in another");
    }

    [Fact]
    public void Compute_without_an_organization_reproduces_the_three_argument_token_byte_for_byte()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        // The compatibility obligation of adding the overload (A11): every operation that is not
        // organization-bound — and every token minted before this overload existed — stays valid,
        // because a null organization appends nothing to the framed message.
        service.Compute(Operation, Payload, UserA, organizationId: null)
            .Should().Be(service.Compute(Operation, Payload, UserA));

        // ...and the null form is NOT the same as any organization-bound one.
        service.Compute(Operation, Payload, UserA, organizationId: null)
            .Should().NotBe(service.Compute(Operation, Payload, UserA, OrgA));
    }

    [Fact]
    public void Verify_without_an_organization_still_accepts_the_token_the_three_argument_form_minted()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string minted = service.Compute(Operation, Payload, UserA);

        service.Verify(Operation, Payload, UserA, organizationId: null, minted).Should().BeTrue();
        service.Verify(Operation, Payload, UserA, OrgA, minted).Should().BeFalse(
            "an organization-bound check must not accept a token minted without one");
    }

    [Fact]
    public void Compute_for_ApplyWorkflow_ignores_the_organization_too()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        // The pinned carve-out must stay keyless AND organization-less, or the digest that a proposal
        // minted before a deploy relies on would change.
        service.Compute(ConfirmationTokenService.ApplyWorkflowOperation, Payload, UserA, OrgA)
            .Should().Be(service.Compute(ConfirmationTokenService.ApplyWorkflowOperation, Payload, UserA));
    }

    // --- the keyed contract ---------------------------------------------------------------------

    [Fact]
    public void Compute_is_stable_for_the_same_operation_payload_and_user()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string first = service.Compute(Operation, Payload, UserA);
        string second = service.Compute(Operation, Payload, UserA);

        second.Should().Be(first);
        first.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Verify_accepts_the_token_Compute_minted()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string token = service.Compute(Operation, Payload, UserA);

        service.Verify(Operation, Payload, UserA, token).Should().BeTrue();
    }

    [Fact]
    public void Verify_rejects_a_token_minted_for_a_different_user()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string tokenForUserA = service.Compute(Operation, Payload, UserA);

        service.Verify(Operation, Payload, UserA, tokenForUserA).Should().BeTrue();
        service.Verify(Operation, Payload, UserB, tokenForUserA).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_a_token_minted_for_a_different_operation()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string tokenForOtherOperation = service.Compute(OtherOperation, Payload, UserA);

        service.Verify(OtherOperation, Payload, UserA, tokenForOtherOperation).Should().BeTrue();
        service.Verify(Operation, Payload, UserA, tokenForOtherOperation).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_a_token_minted_for_a_different_payload()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        string tokenForOtherPayload = service.Compute(Operation, """{"stepOrder":2,"label":"Step 2"}""", UserA);

        service.Verify(Operation, """{"stepOrder":2,"label":"Step 2"}""", UserA, tokenForOtherPayload).Should().BeTrue();
        service.Verify(Operation, Payload, UserA, tokenForOtherPayload).Should().BeFalse();
    }

    [Fact]
    public void Verify_does_not_let_a_shifted_operation_and_payload_boundary_through()
    {
        // Length-prefixed framing: without it, ("a", "x\ny") and ("a\nx", "y") would frame
        // identically and one token would verify against the other pair.
        ConfirmationTokenService service = CreateKeyedService(7);

        string token = service.Compute("a", "x\ny", UserA);

        service.Verify("a", "x\ny", UserA, token).Should().BeTrue();
        service.Verify("a\nx", "y", UserA, token).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_null_empty_and_garbage_tokens_without_throwing()
    {
        ConfirmationTokenService service = CreateKeyedService(7);

        service.Verify(Operation, Payload, UserA, null!).Should().BeFalse();
        service.Verify(Operation, Payload, UserA, string.Empty).Should().BeFalse();
        service.Verify(Operation, Payload, UserA, "   ").Should().BeFalse();
        service.Verify(Operation, Payload, UserA, "not-a-token").Should().BeFalse();
        service.Verify(Operation, Payload, UserA, "!!!! not hex !!!!").Should().BeFalse();
        service.Verify(Operation, Payload, UserA, new string('f', 64)).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_a_token_differing_only_in_its_final_byte_without_throwing()
    {
        // FixedTimeEquals, not string ==: a same-length near-miss must be compared in full and
        // rejected, not short-circuited.
        ConfirmationTokenService service = CreateKeyedService(7);
        string token = service.Compute(Operation, Payload, UserA);

        char last = token[^1];
        string nearMiss = token[..^1] + (last == '0' ? '1' : '0');

        nearMiss.Should().NotBe(token);
        nearMiss.Length.Should().Be(token.Length);
        service.Verify(Operation, Payload, UserA, nearMiss).Should().BeFalse();
    }

    [Fact]
    public void Verify_rejects_tokens_of_a_different_length_without_throwing()
    {
        ConfirmationTokenService service = CreateKeyedService(7);
        string token = service.Compute(Operation, Payload, UserA);

        service.Verify(Operation, Payload, UserA, token + "0").Should().BeFalse();
        service.Verify(Operation, Payload, UserA, token[..^1]).Should().BeFalse();
        service.Verify(Operation, Payload, UserA, token + token).Should().BeFalse();
    }

    [Fact]
    public void Compute_is_keyed_two_services_with_different_keys_disagree()
    {
        // The generalization's whole point: unlike the legacy digest, a keyed token cannot be
        // recomputed by anyone who merely knows the payload.
        string fromKeyA = CreateKeyedService(1).Compute(Operation, Payload, UserA);
        string fromKeyB = CreateKeyedService(2).Compute(Operation, Payload, UserA);

        fromKeyA.Should().NotBe(fromKeyB);
        fromKeyA.Should().NotBe(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Payload))).ToLowerInvariant());
    }

    // --- key resolution -------------------------------------------------------------------------

    [Fact]
    public void Compute_reuses_a_generated_key_across_instances_by_persisting_it()
    {
        string keyFilePath = Path.Combine(
            Path.GetTempPath(), $"reelbolt-confirmation-token-{Guid.NewGuid():N}", "key");

        try
        {
            string first = CreateService(keyFilePath: keyFilePath).Compute(Operation, Payload, UserA);
            string second = CreateService(keyFilePath: keyFilePath).Compute(Operation, Payload, UserA);

            File.Exists(keyFilePath).Should().BeTrue("the generated key must be persisted for reuse");
            second.Should().Be(first);
        }
        finally
        {
            string? directory = Path.GetDirectoryName(keyFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Compute_prefers_a_configured_key_over_a_generated_one()
    {
        string keyFilePath = Path.Combine(
            Path.GetTempPath(), $"reelbolt-confirmation-token-{Guid.NewGuid():N}", "key");

        try
        {
            string configured = CreateService(protectedKey: ProtectedKey(9), keyFilePath: keyFilePath)
                .Compute(Operation, Payload, UserA);

            configured.Should().Be(CreateKeyedService(9).Compute(Operation, Payload, UserA));
            File.Exists(keyFilePath).Should().BeFalse("a configured key makes generation unnecessary");
        }
        finally
        {
            string? directory = Path.GetDirectoryName(keyFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Compute_falls_back_to_a_generated_key_when_the_configured_one_cannot_be_decrypted()
    {
        // A garbage/undecryptable configured value must degrade, not throw: the worst outcome is
        // that previously minted tokens are rejected (recoverable by re-proposing).
        string keyFilePath = Path.Combine(
            Path.GetTempPath(), $"reelbolt-confirmation-token-{Guid.NewGuid():N}", "key");

        try
        {
            ConfirmationTokenService service = CreateService(
                protectedKey: "not-a-protected-value", keyFilePath: keyFilePath);

            string token = service.Compute(Operation, Payload, UserA);

            token.Should().NotBeNullOrWhiteSpace();
            service.Verify(Operation, Payload, UserA, token).Should().BeTrue();
        }
        finally
        {
            string? directory = Path.GetDirectoryName(keyFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // --- the legacy ApplyWorkflow contract ------------------------------------------------------

    [Fact]
    public void Compute_for_ApplyWorkflow_reproduces_the_legacy_keyless_digest_byte_for_byte()
    {
        string payload = """{"workflowId":"","currentStepsHash":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","proposedSteps":[]}""";

        string token = CreateKeyedService(7)
            .Compute(ConfirmationTokenService.ApplyWorkflowOperation, payload, UserA);

        token.Should().Be("8d79db6e4fc0c6847bde5e6148624b6a7abbcdb84984041c2a1f1dc029bc246e");
    }

    [Fact]
    public void Compute_for_ApplyWorkflow_ignores_the_key_and_the_user()
    {
        string payload = """{"workflowId":"","currentStepsHash":"x","proposedSteps":[]}""";

        string fromKeyOne = CreateKeyedService(1).Compute(ConfirmationTokenService.ApplyWorkflowOperation, payload, UserA);
        string fromKeyTwo = CreateKeyedService(2).Compute(ConfirmationTokenService.ApplyWorkflowOperation, payload, UserB);
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        fromKeyOne.Should().Be(expected);
        fromKeyTwo.Should().Be(expected);
    }

    [Fact]
    public void Verify_accepts_the_token_the_real_diff_service_mints()
    {
        using InferenceApiDbContext db = CreateContext();
        ConfirmationTokenService tokens = CreateKeyedService(7);
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var current = new List<WorkflowStep>
        {
            new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = agentId,
                StepOrder = 1,
                Label = "Step 1 Modified",
                StepType = StepType.Agent
            }
        };
        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1 New", "Agent"),
            new(agentId, 2, null, "Step 2", "Agent")
        };

        string payload = LegacyApplyWorkflowPayload(workflowId, current, proposed);
        string minted = new WorkflowEditDiffService(db, tokens).ComputeDiff(workflowId, current, proposed).ConfirmationToken;

        minted.Should().Be(tokens.Compute(ConfirmationTokenService.ApplyWorkflowOperation, payload, Guid.Empty));
        tokens.Verify(ConfirmationTokenService.ApplyWorkflowOperation, payload, Guid.Empty, minted).Should().BeTrue();
        tokens.Verify(ConfirmationTokenService.ApplyWorkflowOperation, payload, UserA, minted).Should().BeTrue();
    }

    [Fact]
    public void Routing_the_diff_service_through_the_token_service_does_not_change_the_token()
    {
        // The one-argument constructor is what every pre-existing test and any non-DI caller uses;
        // it must keep producing exactly what the injected one produces.
        using InferenceApiDbContext db = CreateContext();
        Guid workflowId = Guid.NewGuid();
        Guid agentId = Guid.NewGuid();

        var proposed = new List<CreateWorkflowStepRequest>
        {
            new(agentId, 1, null, "Step 1", "Agent")
        };

        string viaLegacyConstructor = new WorkflowEditDiffService(db)
            .ComputeDiff(workflowId, [], proposed).ConfirmationToken;
        string viaInjectedService = new WorkflowEditDiffService(db, CreateKeyedService(7))
            .ComputeDiff(workflowId, [], proposed).ConfirmationToken;

        viaInjectedService.Should().Be(viaLegacyConstructor);
    }

    // --- independent re-implementation of the legacy algorithm ----------------------------------

    private static readonly JsonSerializerOptions DiffJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// The pre-generalization algorithm, written out independently of the production code so the
    /// assertion above pins a value rather than restating an implementation.
    /// </summary>
    private static string LegacyApplyWorkflowPayload(
        Guid? workflowId,
        IReadOnlyList<WorkflowStep> current,
        IReadOnlyList<CreateWorkflowStepRequest> proposed)
    {
        string currentStepsHash;
        if (current.Count == 0)
        {
            currentStepsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))).ToLowerInvariant();
        }
        else
        {
            var stepsArray = new JsonArray();
            foreach (WorkflowStep step in current)
            {
                stepsArray.Add(new JsonObject
                {
                    ["stepOrder"] = step.StepOrder,
                    ["agentDefinitionId"] = step.AgentDefinitionId.ToString(),
                    ["stepType"] = step.StepType.ToString(),
                    ["label"] = step.Label
                });
            }

            currentStepsHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(stepsArray.ToJsonString(DiffJsonOptions)))).ToLowerInvariant();
        }

        var tokenObj = new JsonObject
        {
            ["workflowId"] = workflowId?.ToString() ?? string.Empty,
            ["currentStepsHash"] = currentStepsHash,
            ["proposedSteps"] = JsonNode.Parse(JsonSerializer.Serialize(proposed, DiffJsonOptions))
        };

        return tokenObj.ToJsonString(DiffJsonOptions);
    }
}
