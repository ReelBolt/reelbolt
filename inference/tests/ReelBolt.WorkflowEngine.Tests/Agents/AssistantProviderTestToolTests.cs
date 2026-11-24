using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The assistant's <c>TestInferenceProvider</c> tool runs the admin page's Test button — same tester,
/// same persisted <c>LastTest*</c> columns — and returns the error detail instead of a generic sentence.
/// </summary>
public class AssistantProviderTestToolTests
{
    private const string Key = "sk-secret-key-123456";

    // A public IP literal: the endpoint guard resolves host names, which a sandboxed test must not need.
    private const string PublicEndpoint = "https://93.184.216.34";

    private sealed class AdminUser(bool admin) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin { get; } = admin;
        public System.Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static InferenceApiDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static ModelCapabilityView Seed(InferenceApiDbContext db, InferenceProviderKind kind, InferenceProviderCapability capability)
    {
        var p = new ModelCapabilityView
        {
            Id = Guid.NewGuid(),
            Name = "MiniMax",
            Kind = kind,
            Capability = capability,
            Endpoint = PublicEndpoint,
            ModelName = "MiniMax-H3-Max",
            ApiKeyEncrypted = "ciphertext-of-" + Key,
            IsEnabled = true
        };
        db.AddFlat(p);
        db.SaveChanges();
        return p;
    }

    private static InferenceProviderTester Tester(
        InferenceApiDbContext db,
        IVideoGenerationClientFactory? video = null,
        IChatClientFactory? chat = null) =>
        new(db, new FakeSecretProtector(),
            chat ?? Mock.Of<IChatClientFactory>(),
            Mock.Of<ITranscriptionClientFactory>(),
            Mock.Of<IEmbeddingClientFactory>(),
            Mock.Of<IDecisionClientFactory>(),
            video ?? Mock.Of<IVideoGenerationClientFactory>(),
            Mock.Of<ISpeechSynthesisClientFactory>(),
            NullLogger<InferenceProviderTester>.Instance);

    private static async Task<JsonElement> RunToolAsync(AssistantAdminTools tools, Guid providerId)
    {
        AIFunction tool = tools.GetTools(new AssistantToolContext(Guid.NewGuid())).Single(t => t.Name == "TestInferenceProvider");
        object? raw = await tool.InvokeAsync(new AIFunctionArguments { ["providerId"] = providerId });
        string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw!.ToString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Admin_gets_the_probe_result_with_vendor_detail_and_it_is_persisted_like_the_button()
    {
        using InferenceApiDbContext db = CreateDb();
        ModelCapabilityView provider = Seed(db, InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration);

        var client = new Mock<IVideoGenerationClient>();
        client.Setup(c => c.ProbeAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoGenerationProbeResult(false, false, "Provider returned HTTP 500 (api_error: boom), request_id r1."));
        var factory = new Mock<IVideoGenerationClientFactory>();
        factory.Setup(f => f.Get(It.Is<ResolvedVideoGenerationProvider>(r => r.ApiKey == Key))).Returns(client.Object);

        var tools = new AssistantAdminTools(db, ConfirmationTokenService.CreateEphemeral(), new AdminUser(true),
            new FakeSecretProtector(), tester: Tester(db, video: factory.Object));

        JsonElement result = await RunToolAsync(tools, provider.Id);

        result.GetProperty("tested").GetBoolean().Should().BeTrue();
        result.GetProperty("ok").GetBoolean().Should().BeFalse();
        result.GetProperty("error").GetString().Should().Contain("api_error: boom").And.Contain("request_id r1");
        result.GetRawText().Should().NotContain(Key);

        ModelCapabilityView reloaded = db.FlatProviders().AsNoTracking().Single();
        reloaded.LastTestOk.Should().BeFalse();
        reloaded.LastTestAt.Should().NotBeNull();
        reloaded.LastTestError.Should().Contain("api_error: boom");
    }

    [Fact]
    public async Task A_thrown_provider_error_is_explained_with_the_key_scrubbed()
    {
        using InferenceApiDbContext db = CreateDb();
        ModelCapabilityView provider = Seed(db, InferenceProviderKind.OpenAICompatible, InferenceProviderCapability.Chat);

        var chat = new Mock<IChatClientFactory>();
        chat.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>()))
            .Throws(new HttpRequestException($"Connection refused (192.0.2.10:8000) using {Key}"));

        var tools = new AssistantAdminTools(db, ConfirmationTokenService.CreateEphemeral(), new AdminUser(true),
            new FakeSecretProtector(), tester: Tester(db, chat: chat.Object));

        JsonElement result = await RunToolAsync(tools, provider.Id);

        string error = result.GetProperty("error").GetString()!;
        error.Should().StartWith("Provider test failed: HttpRequestException: Connection refused (192.0.2.10:8000)");
        error.Should().NotContain(Key).And.Contain("***");
    }

    [Fact]
    public async Task Non_admin_is_refused_and_nothing_is_tested_or_written()
    {
        using InferenceApiDbContext db = CreateDb();
        ModelCapabilityView provider = Seed(db, InferenceProviderKind.MiniMax, InferenceProviderCapability.VideoGeneration);
        var factory = new Mock<IVideoGenerationClientFactory>(MockBehavior.Strict);

        var tools = new AssistantAdminTools(db, ConfirmationTokenService.CreateEphemeral(), new AdminUser(false),
            new FakeSecretProtector(), tester: Tester(db, video: factory.Object));

        JsonElement result = await RunToolAsync(tools, provider.Id);

        result.GetProperty("error").GetString().Should().Be(AssistantAdminTools.AdminRequiredMessage);
        db.FlatProviders().AsNoTracking().Single().LastTestAt.Should().BeNull();
    }

    [Fact]
    public async Task Unknown_provider_is_not_found()
    {
        using InferenceApiDbContext db = CreateDb();
        var tools = new AssistantAdminTools(db, ConfirmationTokenService.CreateEphemeral(), new AdminUser(true),
            new FakeSecretProtector(), tester: Tester(db));

        JsonElement result = await RunToolAsync(tools, Guid.NewGuid());

        result.GetProperty("error").GetString().Should().Be(AssistantAdminTools.ProviderNotFoundMessage);
    }
}
