using System;
using System.Security.Claims;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>C5: an unverified caller cannot start a run through any Execute entry point.</summary>
public class EmailVerificationGateTests
{
    private sealed class FakeUser(bool verified) : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool EmailVerified { get; } = verified;
    }

    private static InferenceApiDbContext Db() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WorkflowsController Workflows(InferenceApiDbContext db, ICurrentUser user) =>
        new(db, user, TestTenancy.Access(db, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);

    private static void AssertGated(ObjectResult? result)
    {
        result.Should().NotBeNull();
        result!.StatusCode.Should().Be(403);
        result.Value!.GetType().GetProperty("code")!.GetValue(result.Value).Should().Be("EMAIL_NOT_VERIFIED");
    }

    [Fact]
    public async Task Execute_Unverified_Returns403EmailNotVerified()
    {
        using InferenceApiDbContext db = Db();
        var result = await Workflows(db, new FakeUser(false)).Execute(Guid.NewGuid(), Guid.NewGuid(), null, default);
        AssertGated(result.Result as ObjectResult);
    }

    [Fact]
    public async Task Retry_Unverified_Returns403EmailNotVerified()
    {
        using InferenceApiDbContext db = Db();
        var result = await Workflows(db, new FakeUser(false)).RetryExecution(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, default);
        AssertGated(result.Result as ObjectResult);
    }

    [Fact]
    public async Task AssistantExecute_Unverified_Returns403EmailNotVerified()
    {
        using InferenceApiDbContext db = Db();
        ICurrentUser unverified = new FakeUser(false);
        var controller = new WorkflowAssistantController(db, unverified, TestTenancy.Access(db, unverified),
            Mock.Of<IWorkflowEditDiffService>(), Mock.Of<IAssistantExecutionGuard>(), Mock.Of<IWorkflowExecutionService>());
        var result = await controller.Execute(Guid.NewGuid(), Guid.NewGuid(), null, default);
        AssertGated(result.Result as ObjectResult);
    }

    [Fact]
    public async Task Execute_Verified_PassesTheGate()
    {
        using InferenceApiDbContext db = Db();
        var result = await Workflows(db, new FakeUser(true)).Execute(Guid.NewGuid(), Guid.NewGuid(), null, default);
        // Reaches the project lookup (no such project) instead of being refused.
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    [InlineData(null, true)]
    [InlineData("garbage", true)]
    public void ReadEmailVerified_OnlyExplicitFalseIsUnverified(string claim, bool expected)
    {
        var identity = new ClaimsIdentity(claim is null ? [] : [new Claim(AuthClaims.EmailVerified, claim)], "test");
        AuthClaims.ReadEmailVerified(new ClaimsPrincipal(identity)).Should().Be(expected);
    }
}
