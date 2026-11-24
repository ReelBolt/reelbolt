using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InferenceApiReliability;

/// <summary>
/// Finding: the default speech provider (<c>local-fish-tts</c>) had FAILED its connection test
/// while a working one (<c>qa-local-fish</c>) sat unused, and nothing told the customer their
/// narration would probably fail. Speech resolution is now health-aware, the voiceover step says
/// what happened, and any signed-in user can read the status (without secrets).
/// </summary>
public class ProviderHealthTests
{
    private static ModelCapabilityView Speech(string name, bool isDefault = false, bool? lastTestOk = null, DateTime? lastTestAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = InferenceProviderKind.FishAudio,
        Capability = InferenceProviderCapability.SpeechSynthesis,
        Endpoint = "https://secret-endpoint.internal",
        ModelName = "secret-model",
        ApiKeyEncrypted = "ENCRYPTED-SECRET",
        ApiKeyLastFour = "abcd",
        IsDefault = isDefault,
        IsEnabled = true,
        LastTestOk = lastTestOk,
        LastTestAt = lastTestAt,
        LastTestError = lastTestOk == false ? "401 Unauthorized from https://secret-endpoint.internal" : null
    };

    // ---------------------------------------------------------------- selection precedence

    [Fact]
    public void A_healthy_or_untested_default_is_used()
    {
        ModelCapabilityView def = Speech("default", isDefault: true, lastTestOk: null);
        ModelCapabilityView other = Speech("other", lastTestOk: true, lastTestAt: DateTime.UtcNow);

        ProviderSelection selection = ProviderHealthSelector.Select([def, other], InferenceProviderCapability.SpeechSynthesis, null);

        selection.Provider.Should().BeSameAs(def);
        selection.Reason.Should().Be(ProviderSelectionReason.Default);
        selection.UsedFallback.Should().BeFalse();
    }

    [Fact]
    public void A_failed_default_is_passed_over_for_the_most_recently_passing_row()
    {
        ModelCapabilityView def = Speech("local-fish-tts", isDefault: true, lastTestOk: false);
        ModelCapabilityView older = Speech("older-ok", lastTestOk: true, lastTestAt: DateTime.UtcNow.AddDays(-3));
        ModelCapabilityView newer = Speech("qa-local-fish", lastTestOk: true, lastTestAt: DateTime.UtcNow.AddHours(-1));
        ModelCapabilityView failing = Speech("also-broken", lastTestOk: false, lastTestAt: DateTime.UtcNow);

        ProviderSelection selection = ProviderHealthSelector.Select(
            [def, older, newer, failing], InferenceProviderCapability.SpeechSynthesis, null);

        selection.Provider.Should().BeSameAs(newer);
        selection.Reason.Should().Be(ProviderSelectionReason.HealthyFallback);
        selection.ConfiguredDefault.Should().BeSameAs(def);
    }

    [Fact]
    public void An_explicit_provider_always_wins_even_when_it_failed_its_test()
    {
        ModelCapabilityView def = Speech("default", isDefault: true, lastTestOk: true);
        ModelCapabilityView pinned = Speech("pinned", lastTestOk: false);

        ProviderSelection selection = ProviderHealthSelector.Select([def, pinned], InferenceProviderCapability.SpeechSynthesis, pinned.Id);

        selection.Provider.Should().BeSameAs(pinned);
        selection.Reason.Should().Be(ProviderSelectionReason.Explicit);
        selection.ChosenProviderFailedLastTest.Should().BeTrue();
    }

    [Fact]
    public void A_failed_default_with_no_healthy_alternative_is_used_anyway()
    {
        ModelCapabilityView def = Speech("default", isDefault: true, lastTestOk: false);
        ModelCapabilityView untested = Speech("untested", lastTestOk: null);

        ProviderSelection selection = ProviderHealthSelector.Select([def, untested], InferenceProviderCapability.SpeechSynthesis, null);

        selection.Provider.Should().BeSameAs(def);
        selection.Reason.Should().Be(ProviderSelectionReason.UnhealthyDefault);
    }

    [Fact]
    public void Rows_of_another_capability_are_never_chosen()
    {
        ModelCapabilityView def = Speech("default", isDefault: true, lastTestOk: false);
        ModelCapabilityView chat = Speech("chat", lastTestOk: true, lastTestAt: DateTime.UtcNow);
        chat.Capability = InferenceProviderCapability.Chat;

        ProviderSelection selection = ProviderHealthSelector.Select([def, chat], InferenceProviderCapability.SpeechSynthesis, chat.Id);

        selection.Provider.Should().BeSameAs(def);
        selection.Reason.Should().Be(ProviderSelectionReason.UnhealthyDefault);
    }

    [Fact]
    public async Task The_resolver_hands_the_voiceover_step_the_healthy_row_and_says_why()
    {
        ModelCapabilityView def = Speech("local-fish-tts", isDefault: true, lastTestOk: false);
        ModelCapabilityView healthy = Speech("qa-local-fish", lastTestOk: true, lastTestAt: DateTime.UtcNow);
        def.ApiKeyEncrypted = null;
        healthy.ApiKeyEncrypted = null;

        InferenceProviderResolver resolver = Resolver(def, healthy);

        ResolvedSpeechSynthesisProvider? resolved = await resolver.ResolveSpeechSynthesisAsync(null, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.ProviderId.Should().Be(healthy.Id);
        resolved.SelectionReason.Should().Be(ProviderSelectionReason.HealthyFallback);
        resolved.PassedOverDefaultName.Should().Be("local-fish-tts");
        resolved.LastTestOk.Should().BeTrue();

        // An explicit step ProviderId is still honoured verbatim.
        ResolvedSpeechSynthesisProvider? pinned = await resolver.ResolveSpeechSynthesisAsync(def.Id, CancellationToken.None);
        pinned!.ProviderId.Should().Be(def.Id);
        pinned.SelectionReason.Should().Be(ProviderSelectionReason.Explicit);
    }

    // ---------------------------------------------------------------- voiceover warnings (C1)

    [Fact]
    public void A_fallback_and_an_unhealthy_provider_produce_plain_language_warnings()
    {
        var fallback = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "qa", InferenceProviderKind.FishAudio, "e", "m", "k", 30,
            ProviderSelectionReason.HealthyFallback, LastTestOk: true, PassedOverDefaultName: "local");
        VoiceoverStepExecutor.ProviderHealthWarnings(fallback).Select(w => w.Code)
            .Should().Equal(VoiceoverStepExecutor.VoiceProviderFallbackCode);

        var unhealthy = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "local", InferenceProviderKind.FishAudio, "e", "m", "k", 30,
            ProviderSelectionReason.UnhealthyDefault, LastTestOk: false);
        var warnings = VoiceoverStepExecutor.ProviderHealthWarnings(unhealthy);
        warnings.Select(w => w.Code).Should().Equal(VoiceoverStepExecutor.VoiceProviderUnhealthyCode);
        warnings[0].Message.Should().NotContain("local").And.NotContain("Fish").And.EndWith(".");
    }

    [Fact]
    public void A_healthy_provider_leaves_the_output_byte_identical()
    {
        var healthy = new ResolvedSpeechSynthesisProvider(Guid.NewGuid(), "ok", InferenceProviderKind.FishAudio, "e", "m", "k", 30);
        var warnings = VoiceoverStepExecutor.ProviderHealthWarnings(healthy);
        warnings.Should().BeEmpty();

        const string output = """{"view":{"lines":[]},"meta":{"totalLines":0}}""";
        VoiceoverStepExecutor.WithWarnings(output, warnings).Should().Be(output);
    }

    [Fact]
    public void Warnings_are_added_as_a_top_level_array_of_code_and_message()
    {
        string json = VoiceoverStepExecutor.WithWarnings(
            """{"view":{},"meta":{}}""",
            [new VoiceoverStepExecutor.StepOutputWarning("voice_provider_unhealthy", "Something.")]);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement warning = doc.RootElement.GetProperty("warnings")[0];
        warning.GetProperty("code").GetString().Should().Be("voice_provider_unhealthy");
        warning.GetProperty("message").GetString().Should().Be("Something.");
        doc.RootElement.GetProperty("view").ValueKind.Should().Be(JsonValueKind.Object);
    }

    // ---------------------------------------------------------------- status endpoint (C3)

    [Fact]
    public async Task Status_is_readable_by_a_non_admin_and_reports_the_fallback()
    {
        using InferenceApiDbContext db = Db();
        DateTime testedAt = DateTime.UtcNow.AddMinutes(-5);
        db.AddFlat(
            Speech("local-fish-tts", isDefault: true, lastTestOk: false, lastTestAt: DateTime.UtcNow),
            Speech("qa-local-fish", lastTestOk: true, lastTestAt: testedAt));
        await db.SaveChangesAsync();

        ActionResult<InferenceProviderStatusResponse> result = await Controller(db, isAdmin: false)
            .Status("SpeechSynthesis", CancellationToken.None);

        var body = (InferenceProviderStatusResponse)((OkObjectResult)result.Result!).Value!;
        body.Capability.Should().Be("SpeechSynthesis");
        body.ProviderName.Should().Be("qa-local-fish");
        body.LastTestOk.Should().BeTrue();
        body.LastTestAt.Should().Be(testedAt);
        body.Healthy.Should().BeTrue();
        body.UsingFallback.Should().BeTrue();
        body.Message.Should().Contain("failed its last connection check");
    }

    [Fact]
    public async Task Status_never_carries_an_endpoint_model_key_or_raw_error()
    {
        using InferenceApiDbContext db = Db();
        db.AddFlat(Speech("local-fish-tts", isDefault: true, lastTestOk: false, lastTestAt: DateTime.UtcNow));
        await db.SaveChangesAsync();

        ActionResult<InferenceProviderStatusResponse> result = await Controller(db, isAdmin: false)
            .Status("speechsynthesis", CancellationToken.None);

        var body = (InferenceProviderStatusResponse)((OkObjectResult)result.Result!).Value!;
        body.Healthy.Should().BeFalse();
        body.UsingFallback.Should().BeFalse();

        string json = JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // Each value is checked as the specific secret it is, so a timestamp in the status can never match.
        json.Should().NotContain("https://secret-endpoint.internal").And.NotContain("\"secret-model\"")
            .And.NotContain("ENCRYPTED-SECRET").And.NotContain("\"abcd\"")
            .And.NotContain("401 Unauthorized");
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["capability", "providerName", "lastTestOk", "lastTestAt", "healthy", "usingFallback", "message"]);
    }

    [Fact]
    public async Task Status_says_so_when_nothing_is_configured_and_rejects_an_unknown_capability()
    {
        using InferenceApiDbContext db = Db();
        InferenceProvidersController controller = Controller(db, isAdmin: false);

        var none = (InferenceProviderStatusResponse)((OkObjectResult)(await controller.Status("SpeechSynthesis", CancellationToken.None)).Result!).Value!;
        none.ProviderName.Should().BeNull();
        none.Healthy.Should().BeFalse();
        none.Message.Should().Contain("No voice service is set up");

        (await controller.Status("Teleportation", CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
        (await controller.Status(null, CancellationToken.None)).Result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ---------------------------------------------------------------- helpers

    private static InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static InferenceProvidersController Controller(InferenceApiDbContext db, bool isAdmin) => new(
        db, new User(isAdmin),
        Mock.Of<IChatClientFactory>(), Mock.Of<ITranscriptionClientFactory>(),
        Mock.Of<IDecisionClientFactory>(), Mock.Of<IVideoGenerationClientFactory>(),
        Mock.Of<ISpeechSynthesisClientFactory>(), Mock.Of<ISecretProtector>(),
        NullLogger<InferenceProvidersController>.Instance);

    private static InferenceProviderResolver Resolver(params ModelCapabilityView[] providers)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IInferenceProviderStore>(new Store(providers));
        return new InferenceProviderResolver(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<ISecretProtector>(),
            NullLogger<InferenceProviderResolver>.Instance);
    }

    private sealed class Store(IReadOnlyList<ModelCapabilityView> providers) : IInferenceProviderStore
    {
        public Task<IReadOnlyList<ModelCapabilityView>> LoadEnabledAsync(Guid? organizationId, CancellationToken ct) => Task.FromResult(providers);

        public Task<IReadOnlyDictionary<Guid, Guid?>> LoadAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Guid?>>(new Dictionary<Guid, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadOrgAgentOverridesAsync(Guid? organizationId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());

        public Task<IReadOnlyDictionary<AgentType, Guid?>> LoadBuiltInAgentOverridesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<AgentType, Guid?>>(new Dictionary<AgentType, Guid?>());
    }

    private sealed class User(bool isAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "customer@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin { get; } = isAdmin;
        public System.Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }
}
