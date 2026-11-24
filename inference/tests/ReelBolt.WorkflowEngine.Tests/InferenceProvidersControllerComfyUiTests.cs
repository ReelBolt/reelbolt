using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Issue #93 phase 5: the <c>ComfyUI</c> kind serves <c>VideoGeneration</c> only, and — being
/// self-hosted — is exempt from the private-address endpoint guard the cloud kinds keep, so a
/// docker-service URL such as <c>http://comfyui:8188</c> can be registered.
/// </summary>
public class InferenceProvidersControllerComfyUiTests
{
    private static InferenceApiDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email { get; } = "admin@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = true;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private static InferenceProvidersController Controller(InferenceApiDbContext db) =>
        new(
            db, new FakeCurrentUser(),
            new Mock<IChatClientFactory>().Object, new Mock<ITranscriptionClientFactory>().Object,
            new Mock<IDecisionClientFactory>().Object, new Mock<IVideoGenerationClientFactory>().Object,
            new Mock<ISpeechSynthesisClientFactory>().Object, new Mock<ISecretProtector>().Object,
            new Mock<Microsoft.Extensions.Logging.ILogger<InferenceProvidersController>>().Object);

    private static CreateProviderRequest Request(string capability, string endpoint = "http://comfyui:8188") => ProviderTree.OneModel(
        Name: "Local ComfyUI",
        Kind: "ComfyUI",
        Endpoint: endpoint,
        ModelName: ComfyUiWorkflowTemplates.TextToVideoName,
        ApiKey: "",
        IsDefault: false,
        IsEnabled: true,
        TimeoutSeconds: null,
        Capability: capability);

    [Fact]
    public async Task Create_accepts_ComfyUI_with_VideoGeneration_on_a_private_docker_service_endpoint()
    {
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result = await Controller(db).Create(Request("VideoGeneration"), CancellationToken.None);

        result.Result.Should().BeOfType<CreatedAtActionResult>();
        var response = ((ProviderResponse)((CreatedAtActionResult)result.Result!).Value!).Flat();
        response.Kind.Should().Be("ComfyUI");
        response.Capability.Should().Be("VideoGeneration");
        response.Endpoint.Should().Be("http://comfyui:8188");
    }

    [Theory]
    [InlineData("Chat")]
    [InlineData("Vision")]
    [InlineData("Transcription")]
    [InlineData("Decision")]
    [InlineData("SpeechSynthesis")]
    [InlineData("Embedding")]
    public async Task Create_rejects_ComfyUI_with_any_capability_but_VideoGeneration(string capability)
    {
        using InferenceApiDbContext db = CreateContext();

        ActionResult<ProviderResponse> result = await Controller(db).Create(Request(capability), CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>();
        db.FlatProviders().Should().BeEmpty();
    }

    [Fact]
    public async Task Create_rejects_a_cloud_kind_on_the_same_private_endpoint_so_the_exemption_is_ComfyUI_specific()
    {
        using InferenceApiDbContext db = CreateContext();
        CreateProviderRequest minimax = Request("VideoGeneration") with { Kind = "MiniMax", Name = "MiniMax on a LAN address" };

        ActionResult<ProviderResponse> result = await Controller(db).Create(minimax, CancellationToken.None);

        result.Result.Should().BeOfType<BadRequestObjectResult>("MiniMax keeps the private-address guard; only the self-hosted kinds are exempt");
    }
}
