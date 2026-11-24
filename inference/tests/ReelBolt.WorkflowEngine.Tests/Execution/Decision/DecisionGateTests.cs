using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

public class DecisionGateTests
{
    private readonly Mock<IInferenceProviderResolver> _mockResolver;
    private readonly Mock<IDecisionClientFactory> _mockFactory;
    private readonly Mock<IServiceScopeFactory> _mockScopeFactory;
    private readonly Mock<ILogger<DecisionGate>> _mockLogger;
    private readonly DbContextOptions<WorkflowEngineDbContext> _dbContextOptions;

    public DecisionGateTests()
    {
        _mockResolver = new Mock<IInferenceProviderResolver>();
        _mockFactory = new Mock<IDecisionClientFactory>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockLogger = new Mock<ILogger<DecisionGate>>();

        _dbContextOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"DecisionGateTests_{Guid.NewGuid()}")
            .Options;
    }

    private DecisionGate CreateSut()
    {
        return new DecisionGate(
            _mockResolver.Object,
            _mockFactory.Object,
            _mockScopeFactory.Object,
            _mockLogger.Object);
    }

    private WorkflowEngineDbContext CreateDbContext()
    {
        return new WorkflowEngineDbContext(_dbContextOptions);
    }

    [Fact]
    public async Task RunShadowAsync_WithModeOff_NeverInvokesFactory()
    {
        // Arrange
        var sut = CreateSut();
        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Off);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        _mockResolver.Verify(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockFactory.Verify(f => f.Get(It.IsAny<ResolvedDecisionProvider>()), Times.Never);
    }

    [Fact]
    public async Task RunShadowAsync_WithModeOff_DoesNotPersistRows()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Off);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        ctx.DecisionObservations.Count().Should().Be(0);
    }

    [Fact]
    public async Task RunShadowAsync_WithNullResolvedProvider_DoesNotPersistRows()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);
        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        ctx.DecisionObservations.Count().Should().Be(0);
    }

    [Fact]
    public async Task RunShadowAsync_WithValidProvider_PersistsOneObservationPerQuestion()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    {
                        "q1", new DecisionAnswer(
                            Name: "q1",
                            Kind: DecisionKind.Choice,
                            Choice: "a",
                            Score: null,
                            Noul: null,
                            Confidence: 0.95,
                            Probabilities: new Dictionary<string, double> { { "a", 0.95 }, { "b", 0.05 } })
                    },
                    {
                        "q2", new DecisionAnswer(
                            Name: "q2",
                            Kind: DecisionKind.Choice,
                            Choice: "x",
                            Score: null,
                            Noul: null,
                            Confidence: 0.88,
                            Probabilities: new Dictionary<string, double> { { "x", 0.88 }, { "y", 0.12 } })
                    }
                },
                InputTokens: 100,
                OutputTokens: 50));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a"),
            new DecisionGateQuestion("site2", "q2", DecisionKind.Choice, "inst2", new[] { "x", "y" }, "x")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        ctx.DecisionObservations.Count().Should().Be(2);
        var obs1 = ctx.DecisionObservations.First(o => o.Question.Contains("q1"));
        obs1.ExecutionId.Should().Be(executionId);
        obs1.StepOrder.Should().Be(1);
        obs1.Answer.Should().Be("a");
        obs1.Confidence.Should().Be(0.95);

        var obs2 = ctx.DecisionObservations.First(o => o.Question.Contains("q2"));
        obs2.Answer.Should().Be("x");
        obs2.Confidence.Should().Be(0.88);
    }

    [Fact]
    public async Task RunShadowAsync_StampsTheExecutionsProjectAndOrganizationOnEveryRow()
    {
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);
        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(), Name: "mock-provider", Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test", ModelName: "test-model", ApiKey: "key", TimeoutSeconds: 300);
        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    { "q1", new DecisionAnswer("q1", DecisionKind.Choice, "a", null, null, 0.95,
                        new Dictionary<string, double> { { "a", 0.95 }, { "b", 0.05 } }) }
                },
                InputTokens: 1,
                OutputTokens: 1));
        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        ctx.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            ProjectId = projectId,
            OrganizationId = organizationId
        });
        await ctx.SaveChangesAsync();

        await sut.RunShadowAsync(
            executionId, 1, AgentType.DirectorAgent, new DecisionState("content"),
            new[] { new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a") },
            new DecisionGateConfig(Mode: DecisionGateMode.Shadow), CancellationToken.None);

        var row = ctx.DecisionObservations.Single();
        row.ProjectId.Should().Be(projectId);
        row.OrganizationId.Should().Be(organizationId);
    }

    [Fact]
    public async Task RunShadowAsync_WithLowTopProbability_SetsEscalatedTrue()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    {
                        "q1", new DecisionAnswer(
                            Name: "q1",
                            Kind: DecisionKind.Choice,
                            Choice: "a",
                            Score: null,
                            Noul: null,
                            Confidence: 0.80, // Below AcceptAt=0.85
                            Probabilities: new Dictionary<string, double> { { "a", 0.80 }, { "b", 0.20 } })
                    }
                },
                InputTokens: 100,
                OutputTokens: 50));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow, AcceptAt: 0.85);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        var obs = ctx.DecisionObservations.First();
        obs.Escalated.Should().Be(true);
    }

    [Fact]
    public async Task RunShadowAsync_WithMarginAboveMinMargin_SetsEscalatedFalse()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    {
                        "q1", new DecisionAnswer(
                            Name: "q1",
                            Kind: DecisionKind.Choice,
                            Choice: "a",
                            Score: null,
                            Noul: null,
                            Confidence: 0.9,
                            Probabilities: new Dictionary<string, double> { { "a", 0.65 }, { "b", 0.35 } }) // Margin = 0.30
                    }
                },
                InputTokens: 100,
                OutputTokens: 50));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow, AcceptAt: 0.5, MinMargin: 0.25);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        var obs = ctx.DecisionObservations.First();
        // top 0.65 >= AcceptAt 0.5, margin 0.30 >= MinMargin 0.25 — both conditions satisfied
        obs.Escalated.Should().Be(false);
    }

    [Fact]
    public async Task RunShadowAsync_WithMarginLessThanMinMargin_SetsEscalatedTrue()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    {
                        "q1", new DecisionAnswer(
                            Name: "q1",
                            Kind: DecisionKind.Choice,
                            Choice: "a",
                            Score: null,
                            Noul: null,
                            Confidence: 0.9,
                            Probabilities: new Dictionary<string, double> { { "a", 0.52 }, { "b", 0.48 } }) // Margin = 0.04 < 0.25
                    }
                },
                InputTokens: 100,
                OutputTokens: 50));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow, MinMargin: 0.25);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        var obs = ctx.DecisionObservations.First();
        obs.Escalated.Should().Be(true);
    }

    [Fact]
    public async Task RunShadowAsync_WithMatchingSystemTwoAnswer_SetsAgreedTrue()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    {
                        "q1", new DecisionAnswer(
                            Name: "q1",
                            Kind: DecisionKind.Choice,
                            Choice: "accept",
                            Score: null,
                            Noul: null,
                            Confidence: 0.95,
                            Probabilities: new Dictionary<string, double> { { "accept", 0.95 }, { "reject", 0.05 } })
                    }
                },
                InputTokens: 100,
                OutputTokens: 50));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "accept", "reject" }, "accept")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        var obs = ctx.DecisionObservations.First();
        var outcomeJson = JsonDocument.Parse(obs.DownstreamOutcomeJson ?? "{}");
        outcomeJson.RootElement.GetProperty("agreed").GetBoolean().Should().Be(true);
    }

    [Fact]
    public async Task RunShadowAsync_WithNonMatchingSystemTwoAnswer_SetsAgreedFalse()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    {
                        "q1", new DecisionAnswer(
                            Name: "q1",
                            Kind: DecisionKind.Choice,
                            Choice: "reject",
                            Score: null,
                            Noul: null,
                            Confidence: 0.95,
                            Probabilities: new Dictionary<string, double> { { "accept", 0.05 }, { "reject", 0.95 } })
                    }
                },
                InputTokens: 100,
                OutputTokens: 50));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "accept", "reject" }, "accept")
        };
        var state = new DecisionState("content");

        // Act
        await sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None);

        // Assert
        var obs = ctx.DecisionObservations.First();
        var outcomeJson = JsonDocument.Parse(obs.DownstreamOutcomeJson ?? "{}");
        outcomeJson.RootElement.GetProperty("agreed").GetBoolean().Should().Be(false);
    }

    [Fact]
    public async Task RunShadowAsync_WhenDecisionClientThrows_DoesNotThrow()
    {
        // Arrange
        var sut = CreateSut();
        var ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        var resolvedProvider = new ResolvedDecisionProvider(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedProvider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        _mockFactory.Setup(f => f.Get(resolvedProvider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", new[] { "a", "b" }, "a")
        };
        var state = new DecisionState("content");

        // Act & Assert (should not throw)
        var exception = await Record.ExceptionAsync(() =>
            sut.RunShadowAsync(executionId, 1, AgentType.DirectorAgent, state, questions, config, CancellationToken.None));

        exception.Should().BeNull();
        ctx.DecisionObservations.Count().Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // D-004: explicit modes, the gate-level timeout, and the outcome-returning API.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DecideAsync_WithUnrecognisedMode_PerformsZeroOutboundCalls()
    {
        // Arrange: provider resolution and the client are both wired up to succeed, so the only
        // thing standing between this call and an outbound request is the mode switch itself.
        var sut = CreateSut();
        SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "a", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: (DecisionGateMode)42),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a")]);

        // Act
        DecisionGateOutcome outcome = await sut.DecideAsync(request, CancellationToken.None);

        // Assert: an unrecognised value is "do nothing", not "not Off".
        outcome.HasDecision.Should().BeFalse();
        outcome.Accepted.Should().BeFalse();
        outcome.Result.Should().BeNull();
        mockClient.Invocations.Should().BeEmpty();
        _mockResolver.Verify(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockFactory.Verify(f => f.Get(It.IsAny<ResolvedDecisionProvider>()), Times.Never);
    }

    [Fact]
    public async Task DecideAsync_WithModeGate_RoutesThroughTheSingleProviderCall()
    {
        // Gate is a live arm: it is the future accept/escalate path, so it must reach the same
        // provider-call core Shadow reaches — it is not a second "Off".
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "a", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a")]);

        // Act
        DecisionGateOutcome outcome = await sut.DecideAsync(request, CancellationToken.None);

        // Assert
        mockClient.Invocations.Should().HaveCount(1);
        outcome.HasDecision.Should().BeTrue();
        outcome.Accepted.Should().BeTrue();
        ctx.DecisionObservations.Single().Mode.Should().Be("Gate");
    }

    [Fact]
    public async Task DecideAsync_WhenClientNeverCompletes_ReturnsNoDecisionWithinTheTimeoutBudget()
    {
        // The provider's own HttpClient timeout is up to 300 s; the gate's own budget is what
        // bounds the step. Without it this call would never return.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Returns(async (DecisionState _, IReadOnlyList<DecisionQuestion> _, CancellationToken token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return ConfidentResult("q1", "a", 0.95);
            });
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: 1),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a")]);

        // Act
        var sw = Stopwatch.StartNew();
        DecisionGateOutcome outcome = await sut.DecideAsync(request, CancellationToken.None);
        sw.Stop();

        // Assert
        outcome.HasDecision.Should().BeFalse();
        outcome.Result.Should().BeNull();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        ctx.DecisionObservations.Should().BeEmpty();
    }

    [Fact]
    public async Task DecideAsync_WhenTheCallerCancels_RethrowsInsteadOfReturningNoDecision()
    {
        // A timeout escalates; the workflow's own cancellation must still be observed. Conflating
        // them would let a cancelled workflow keep running its agent.
        var sut = CreateSut();
        SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Returns((DecisionState _, IReadOnlyList<DecisionQuestion> _, CancellationToken token) =>
                Task.FromCanceled<DecisionResult>(token));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: 30),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a")]);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.DecideAsync(request, cts.Token));
    }

    [Fact]
    public async Task DecideAsync_WithAQuestionTheProviderDidNotAnswer_IsNotAccepted()
    {
        // All-or-nothing is over every question, including the ones nobody answered.
        var sut = CreateSut();
        SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "a", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            [
                new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a"),
                new DecisionGateQuestion("site2", "q2", DecisionKind.Choice, "inst2", ["x", "y"], "x"),
            ]);

        // Act
        DecisionGateOutcome outcome = await sut.DecideAsync(request, CancellationToken.None);

        // Assert
        outcome.HasDecision.Should().BeTrue();
        outcome.Accepted.Should().BeFalse();
        outcome.TopTwoProbabilities.Should().ContainKey("q1");
        outcome.TopTwoProbabilities.Should().NotContainKey("q2");
    }

    [Fact]
    public async Task DecideAsync_WhenOneQuestionMissesTheMargin_IsNotAccepted()
    {
        // q1 clears both thresholds; q2's margin (0.08) does not. One failing question escalates
        // the whole step.
        var sut = CreateSut();
        SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: "gpt-4",
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    { "q1", ChoiceAnswer("q1", "a", 0.95, ("a", 0.95), ("b", 0.05)) },
                    { "q2", ChoiceAnswer("q2", "x", 0.70, ("x", 0.70), ("y", 0.62)) },
                },
                InputTokens: 100,
                OutputTokens: 50));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            [
                new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a"),
                new DecisionGateQuestion("site2", "q2", DecisionKind.Choice, "inst2", ["x", "y"], "x"),
            ]);

        // Act
        DecisionGateOutcome outcome = await sut.DecideAsync(request, CancellationToken.None);

        // Assert
        outcome.Accepted.Should().BeFalse();
        outcome.TopTwoProbabilities["q1"].Should().Be((0.95, 0.05));
        outcome.TopTwoProbabilities["q2"].Top.Should().Be(0.70);
    }

    [Fact]
    public async Task RunShadowAsync_WithAnAnsweredQuestion_WritesTheFullObservationRow()
    {
        // The byte-identical claim, checked rather than asserted: every column phase 1 wrote is
        // pinned to its exact expected value. The only additions are D-001's three token columns.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "a", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var executionId = Guid.NewGuid();
        var config = new DecisionGateConfig(Mode: DecisionGateMode.Shadow);
        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a")
        };

        // Act
        await sut.RunShadowAsync(
            executionId, 7, AgentType.Colorist, new DecisionState("content"), questions, config, CancellationToken.None);

        // Assert
        DecisionObservation obs = ctx.DecisionObservations.Single();
        obs.Id.Should().NotBe(Guid.Empty);
        obs.ExecutionId.Should().Be(executionId);
        obs.StepOrder.Should().Be(7);
        obs.AgentType.Should().Be(AgentType.Colorist);
        obs.Site.Should().Be("site1");
        obs.Question.Should().Be("q1");
        obs.OptionsHash.Should().Be("0EAB8A0A3380ABF4C7D1FB0B43B66AAFBB64A4B953E4EB2DCCCA579461912D0C");
        obs.Kind.Should().Be(DecisionKind.Choice);
        obs.Answer.Should().Be("a");
        obs.Confidence.Should().Be(0.95);
        obs.ProbabilitiesJson.Should().Be("{\"a\":0.95,\"b\":0.05}");
        obs.Mode.Should().Be("Shadow");
        obs.Escalated.Should().BeFalse();
        obs.DownstreamOutcomeJson.Should().Be("{\"agreed\":true}");
        obs.ProviderId.Should().Be(provider.ProviderId);
        obs.Model.Should().Be("gpt-4");
        obs.DecisionInputTokens.Should().Be(100);
        obs.DecisionOutputTokens.Should().Be(50);
        obs.AgentTokensUsed.Should().BeNull();
        obs.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RunShadowAsync_AfterANewObservationRow_StillSwallowsCallerCancellation()
    {
        // Phase-1 contract: the shadow observer never throws, not even when the workflow's token is
        // already cancelled — the executor that owns that token observes it itself.
        var sut = CreateSut();
        SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Returns((DecisionState _, IReadOnlyList<DecisionQuestion> _, CancellationToken token) =>
                Task.FromCanceled<DecisionResult>(token));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var questions = new[]
        {
            new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], "a")
        };

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        Exception? exception = await Record.ExceptionAsync(() => sut.RunShadowAsync(
            Guid.NewGuid(), 1, AgentType.Colorist, new DecisionState("content"),
            questions, new DecisionGateConfig(Mode: DecisionGateMode.Shadow), cts.Token));

        // Assert
        exception.Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------
    // T-009: the pre-agent Gate path must not claim a disagreement it never observed.
    //
    // Phase 1's gate was post-hoc — the agent ran first, so SystemTwoAnswer was always a real answer
    // and {"agreed": …} was a real comparison. Phase 2 inverts the order for Gate, so on the
    // pre-agent path no agent has run at all. string.Equals(x, null, Ordinal) is false, so the
    // unconditional comparison claimed a disagreement for every such row — and the calibration
    // view's agreement rate is computed from exactly that column.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task DecideAsync_OnThePreAgentGatePath_RecordsNoAgreementClaim()
    {
        // No counterfactual exists yet, so there is nothing to agree or disagree with. Null
        // DownstreamOutcomeJson is the existing "outcome unavailable" contract the calibration
        // aggregation already understands — not a new sentinel shape.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "a", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], null)]);

        // Act
        DecisionGateOutcome outcome = await sut.DecideAsync(request, CancellationToken.None);

        // Assert: the row is still written — the observation itself is the point of the call — but
        // with no agreement claim attached to it.
        outcome.HasDecision.Should().BeTrue();
        DecisionObservation obs = ctx.DecisionObservations.Single();
        obs.Mode.Should().Be("Gate");
        obs.Answer.Should().Be("a");
        obs.DownstreamOutcomeJson.Should().BeNull();
    }

    [Fact]
    public async Task DecideAsync_OnThePreAgentGatePath_DoesNotDiluteTheCalibrationAgreementRate()
    {
        // The aggregation's rule (DecisionCalibrationService.RowFacts.ParseAgreed): a row whose
        // DownstreamOutcomeJson carries no "agreed" property records NO outcome, and AgreementRate
        // divides by the rows that did. A pre-agent Gate row written as {"agreed":false} therefore
        // counts as a disagreement that never happened; written as null it is excluded entirely.
        //
        // One genuine observed agreement is seeded alongside it so the two rules are separable:
        // excluded ⇒ 1.0, wrongly counted as a disagreement ⇒ 0.5.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "a", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        await sut.DecideAsync(
            CreateRequest(
                new DecisionGateConfig(Mode: DecisionGateMode.Gate),
                [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["a", "b"], null)]),
            CancellationToken.None);

        ctx.DecisionObservations.Add(ObservationRow(
            Guid.NewGuid(), 3, "Shadow", "site2", "q2", "accept", "{\"agreed\":true}"));
        await ctx.SaveChangesAsync();

        // Act
        DecisionCalibrationReport report = await new DecisionCalibrationService(ctx)
            .GetCalibrationAsync(acceptAt: 0.85, minMargin: 0.25, providerId: null, site: null, ct: CancellationToken.None);

        // Assert: both rows are observed, and the rate is computed from the one that recorded an
        // outcome. Before this fix the pre-agent row entered the denominator as a disagreement.
        report.Totals.ObservationCount.Should().Be(2);
        report.Totals.AgreementRate.Should().Be(1.0);
    }

    [Fact]
    public async Task DecideAsync_WithAMatchingSystemTwoAnswer_RecordsAgreedTrue()
    {
        // The non-null branch is unchanged, down to the serialized bytes. Checked on the Gate path
        // too, since Gate is where the inversion happened.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "accept", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["accept", "reject"], "accept")]);

        // Act
        await sut.DecideAsync(request, CancellationToken.None);

        // Assert
        ctx.DecisionObservations.Single().DownstreamOutcomeJson.Should().Be("{\"agreed\":true}");
    }

    [Fact]
    public async Task DecideAsync_WithADifferingSystemTwoAnswer_RecordsAgreedFalse()
    {
        // A real, observed disagreement must still be recorded as one.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ResolvedDecisionProvider provider = CreateProvider();
        SetupProvider(provider);
        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ConfidentResult("q1", "reject", 0.95));
        _mockFactory.Setup(f => f.Get(provider)).Returns(mockClient.Object);

        var request = CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate),
            [new DecisionGateQuestion("site1", "q1", DecisionKind.Choice, "inst1", ["accept", "reject"], "accept")]);

        // Act
        await sut.DecideAsync(request, CancellationToken.None);

        // Assert
        ctx.DecisionObservations.Single().DownstreamOutcomeJson.Should().Be("{\"agreed\":false}");
    }

    [Fact]
    public async Task AttachSystemTwoAnswersAsync_OnAMatchingGateRow_FillsTheAgreementAndTheTokens()
    {
        // D-005's escalated path runs the agent only AFTER the gate escalated, so it learns System
        // Two's answer late and completes the row the pre-agent call already wrote.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        var executionId = Guid.NewGuid();
        ctx.DecisionObservations.Add(ObservationRow(executionId, 7, "Gate", "site1", "q1", "accept", downstreamOutcomeJson: null));
        await ctx.SaveChangesAsync();

        // Act
        await sut.AttachSystemTwoAnswersAsync(
            executionId, 7, new Dictionary<string, string> { ["q1"] = "accept" }, agentTokensUsed: 1234, CancellationToken.None);

        // Assert
        DecisionObservation obs = ctx.DecisionObservations.Single();
        obs.DownstreamOutcomeJson.Should().Be("{\"agreed\":true}");
        obs.AgentTokensUsed.Should().Be(1234);
    }

    [Fact]
    public async Task AttachSystemTwoAnswersAsync_OnAGenuinelyDifferingAnswer_RecordsAgreedFalse()
    {
        // The recomputed agreement is a real comparison against the model's stored answer — an
        // attachment that differs is a disagreement, and must be recorded as one.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        var executionId = Guid.NewGuid();
        ctx.DecisionObservations.Add(ObservationRow(executionId, 7, "Gate", "site1", "q1", "accept", downstreamOutcomeJson: null));
        await ctx.SaveChangesAsync();

        // Act
        await sut.AttachSystemTwoAnswersAsync(
            executionId, 7, new Dictionary<string, string> { ["q1"] = "reject" }, agentTokensUsed: null, CancellationToken.None);

        // Assert
        ctx.DecisionObservations.Single().DownstreamOutcomeJson.Should().Be("{\"agreed\":false}");
    }

    [Fact]
    public async Task AttachSystemTwoAnswersAsync_LeavesAShadowRowUntouched()
    {
        // A Shadow row already had its answer at write time. Rewriting it would break the
        // byte-identical guarantee phase 1's full-row assertion pins, so Attach filters on Mode.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        var executionId = Guid.NewGuid();
        ctx.DecisionObservations.Add(ObservationRow(executionId, 7, "Gate", "site1", "q1", "accept", downstreamOutcomeJson: null));
        ctx.DecisionObservations.Add(ObservationRow(executionId, 7, "Shadow", "site1", "q1", "accept", "{\"agreed\":true}"));
        await ctx.SaveChangesAsync();

        // Act
        await sut.AttachSystemTwoAnswersAsync(
            executionId, 7, new Dictionary<string, string> { ["q1"] = "reject" }, agentTokensUsed: 999, CancellationToken.None);

        // Assert
        DecisionObservation gate = ctx.DecisionObservations.Single(o => o.Mode == "Gate");
        gate.DownstreamOutcomeJson.Should().Be("{\"agreed\":false}");
        gate.AgentTokensUsed.Should().Be(999);

        DecisionObservation shadow = ctx.DecisionObservations.Single(o => o.Mode == "Shadow");
        shadow.DownstreamOutcomeJson.Should().Be("{\"agreed\":true}");
        shadow.AgentTokensUsed.Should().BeNull();
    }

    [Fact]
    public async Task AttachSystemTwoAnswersAsync_WithNoMatchingRows_IsANoOpThatDoesNotThrow()
    {
        // Nothing to complete is a legal outcome, not an error: the gate may have recorded nothing
        // (a time-out, an unrecognised mode) and the caller cannot know that from where it stands.
        var sut = CreateSut();
        WorkflowEngineDbContext ctx = SetupScope();
        ctx.DecisionObservations.Add(ObservationRow(Guid.NewGuid(), 7, "Gate", "site1", "q1", "accept", downstreamOutcomeJson: null));
        await ctx.SaveChangesAsync();

        // Act
        Exception? exception = await Record.ExceptionAsync(() => sut.AttachSystemTwoAnswersAsync(
            Guid.NewGuid(), 99, new Dictionary<string, string> { ["q1"] = "accept" }, agentTokensUsed: 5, CancellationToken.None));

        // Assert
        exception.Should().BeNull();
        DecisionObservation obs = ctx.DecisionObservations.Single();
        obs.DownstreamOutcomeJson.Should().BeNull();
        obs.AgentTokensUsed.Should().BeNull();
    }

    [Fact]
    public async Task AttachSystemTwoAnswersAsync_WhenTheStoreIsUnreachable_DoesNotThrow()
    {
        // Same soft-failure discipline as the rest of the class: completing an observation is
        // bookkeeping about a decision that has already been made, so it can never fail a workflow.
        var sut = CreateSut();
        _mockScopeFactory.Setup(sf => sf.CreateScope()).Throws(new InvalidOperationException("no scope"));

        // Act
        Exception? exception = await Record.ExceptionAsync(() => sut.AttachSystemTwoAnswersAsync(
            Guid.NewGuid(), 7, new Dictionary<string, string> { ["q1"] = "accept" }, agentTokensUsed: 5, CancellationToken.None));

        // Assert
        exception.Should().BeNull();
    }

    private WorkflowEngineDbContext SetupScope()
    {
        WorkflowEngineDbContext ctx = CreateDbContext();
        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _mockScopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);
        return ctx;
    }

    private void SetupProvider(ResolvedDecisionProvider provider)
    {
        _mockResolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
    }

    private static ResolvedDecisionProvider CreateProvider() =>
        new(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

    private static DecisionGateRequest CreateRequest(
        DecisionGateConfig config, IReadOnlyList<DecisionGateQuestion> questions) =>
        new(
            ExecutionId: Guid.NewGuid(),
            StepOrder: 7,
            AgentType: AgentType.Colorist,
            Site: "Colorist.step",
            State: new DecisionState("content"),
            Questions: questions,
            Config: config,
            SystemTwoAnswer: null,
            AgentTokensUsed: null);

    private static DecisionAnswer ChoiceAnswer(
        string name, string choice, double confidence, params (string Option, double Probability)[] probabilities)
    {
        var map = new Dictionary<string, double>();
        foreach ((string option, double probability) in probabilities)
            map[option] = probability;

        return new DecisionAnswer(
            Name: name,
            Kind: DecisionKind.Choice,
            Choice: choice,
            Score: null,
            Noul: null,
            Confidence: confidence,
            Probabilities: map);
    }

    /// <summary>
    /// An already-persisted observation row, for the tests that exercise
    /// <see cref="IDecisionGate.AttachSystemTwoAnswersAsync"/> against rows it did not itself write.
    /// </summary>
    private static DecisionObservation ObservationRow(
        Guid executionId, int stepOrder, string mode, string site, string question, string answer,
        string? downstreamOutcomeJson) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExecutionId = executionId,
            StepOrder = stepOrder,
            AgentType = AgentType.Colorist,
            Site = site,
            Question = question,
            OptionsHash = "HASH",
            Kind = DecisionKind.Choice,
            Answer = answer,
            Confidence = 0.95,
            ProbabilitiesJson = "{\"accept\":0.95,\"reject\":0.05}",
            Mode = mode,
            Escalated = false,
            DownstreamOutcomeJson = downstreamOutcomeJson,
            ProviderId = null,
            Model = "gpt-4",
            DecisionInputTokens = 100,
            DecisionOutputTokens = 50,
            AgentTokensUsed = null,
            CreatedAt = DateTime.UtcNow,
        };

    private static DecisionResult ConfidentResult(string name, string choice, double confidence) =>
        new(
            Model: "gpt-4",
            Answers: new Dictionary<string, DecisionAnswer>
            {
                {
                    name,
                    ChoiceAnswer(
                        name, choice, confidence,
                        (choice, confidence),
                        ("b", Math.Round(1 - confidence, 10)))
                }
            },
            InputTokens: 100,
            OutputTokens: 50);
}
