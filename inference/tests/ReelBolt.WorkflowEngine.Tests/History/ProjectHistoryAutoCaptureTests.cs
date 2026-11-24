using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>Contract section 9: the automatic-version triggers.</summary>
public sealed class ProjectHistoryAutoCaptureTests
{
    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"auto-capture-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public void Every_listed_write_tool_is_real_and_every_other_apply_tool_is_left_out_with_a_reason()
    {
        using InferenceApiDbContext db = NewDb();
        Mock<ICurrentUser> user = new();
        Guid userId = Guid.NewGuid();
        user.SetupGet(u => u.UserId).Returns(userId);
        HashSet<string> real = AssistantToolTestFactory.CreateProvider(db, user.Object)
            .GetTools(new AssistantToolContext(userId, Guid.NewGuid()))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        ProjectHistoryAutoCapture.AssistantWriteTools.Where(n => !real.Contains(n)).Should().BeEmpty();
        ProjectHistoryAutoCapture.AssistantApplyToolsLeftOut.Keys.Where(n => !real.Contains(n)).Should().BeEmpty();
        ProjectHistoryAutoCapture.AssistantApplyToolsLeftOut.Values.Should().OnlyContain(v => !string.IsNullOrWhiteSpace(v));
        ProjectHistoryAutoCapture.AssistantWriteTools
            .Intersect(ProjectHistoryAutoCapture.AssistantApplyToolsLeftOut.Keys).Should().BeEmpty();

        real.Where(n => n.StartsWith("Apply", StringComparison.Ordinal))
            .Where(n => !ProjectHistoryAutoCapture.AssistantWriteTools.Contains(n)
                        && !ProjectHistoryAutoCapture.AssistantApplyToolsLeftOut.ContainsKey(n)
                        // The history tools take their own versions (a named save, a beforeRestore safety version).
                        && n is not ("ApplySaveProjectVersion" or "ApplyRestoreProjectVersion"))
            .Should().BeEmpty("every Apply tool is either captured before or deliberately left out");
    }

    [Fact]
    public async Task The_interceptor_captures_once_before_a_write_tool_and_a_run_tool_and_not_for_others()
    {
        Guid projectId = Guid.NewGuid();
        Mock<IProjectHistoryService> history = new();
        List<string> calls = [];
        history.Setup(h => h.TryCaptureAutoAsync(projectId, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, Guid?, CancellationToken>((_, cause, _, _) => calls.Add(cause))
            .Returns(Task.CompletedTask);

        using IDisposable scope = ProjectHistoryAutoCapture.Use(history.Object, null, projectId, Guid.NewGuid());

        await Invoke("ApplyWriteProjectFile");
        await Invoke("ExecuteWorkflow");
        await Invoke("ListProjectFiles");

        calls.Should().Equal(ProjectVersionCause.BeforeAssistant, ProjectVersionCause.BeforeRun);
    }

    [Fact]
    public async Task An_explicit_project_argument_wins_over_the_page_project()
    {
        Guid page = Guid.NewGuid();
        Guid explicitId = Guid.NewGuid();
        Mock<IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using IDisposable scope = ProjectHistoryAutoCapture.Use(history.Object, null, page, null);

        await Invoke("ApplyEditProjectFile", explicitId);

        history.Verify(h => h.TryCaptureAutoAsync(explicitId, ProjectVersionCause.BeforeAssistant, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        history.Verify(h => h.TryCaptureAutoAsync(page, It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_capture_failure_never_fails_the_tool()
    {
        Mock<IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        using IDisposable scope = ProjectHistoryAutoCapture.Use(history.Object, null, Guid.NewGuid(), null);

        object? result = await Invoke("ApplyWriteProjectFile");

        result!.ToString().Should().Be("tool-result");
    }

    [Fact]
    public async Task No_project_in_scope_captures_nothing()
    {
        Mock<IProjectHistoryService> history = new();
        using IDisposable scope = ProjectHistoryAutoCapture.Use(history.Object);

        await Invoke("ApplyWriteProjectFile");

        history.Verify(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Switch_off_makes_the_sweep_capture_nothing()
    {
        using InferenceApiDbContext db = NewDb();
        Project project = new()
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-30)
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        ProjectHistoryOptions off = new() { AutoCapture = false };
        ProjectHistoryIdleCaptureService sweeper = NewSweeper(db, Mock.Of<IProjectHistoryService>(), off);
        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_idle_sweep_captures_a_quietly_changed_project_once_and_skips_recent_and_already_versioned_ones()
    {
        using InferenceApiDbContext db = NewDb();
        Project idle = NewProject(DateTime.UtcNow.AddMinutes(-30));
        Project recent = NewProject(DateTime.UtcNow.AddMinutes(-1));
        Project versioned = NewProject(DateTime.UtcNow.AddMinutes(-30));
        db.Projects.AddRange(idle, recent, versioned);
        db.ProjectVersions.Add(new ProjectVersion
        {
            Id = Guid.NewGuid(),
            ProjectId = versioned.Id,
            Number = 1,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        Mock<IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        ProjectHistoryIdleCaptureService sweeper = NewSweeper(db, history.Object, new ProjectHistoryOptions());

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        history.Verify(h => h.TryCaptureAutoAsync(idle.Id, ProjectVersionCause.Idle, null, It.IsAny<CancellationToken>()), Times.Once);
        history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_idle_sweep_survives_a_failing_capture()
    {
        using InferenceApiDbContext db = NewDb();
        db.Projects.Add(NewProject(DateTime.UtcNow.AddMinutes(-30)));
        await db.SaveChangesAsync();
        Mock<IProjectHistoryService> history = new();
        history.Setup(h => h.TryCaptureAutoAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        Func<Task> act = () => NewSweeper(db, history.Object, new ProjectHistoryOptions()).SweepOnceAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    private static Project NewProject(DateTime updatedAt) => new()
    {
        Id = Guid.NewGuid(),
        Name = "P",
        OwnerId = Guid.NewGuid(),
        OrganizationId = Guid.NewGuid(),
        UpdatedAt = updatedAt
    };

    private static ProjectHistoryIdleCaptureService NewSweeper(
        InferenceApiDbContext db, IProjectHistoryService history, ProjectHistoryOptions options)
    {
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(history);
        IServiceScopeFactory factory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        Mock<IOptionsMonitor<ProjectHistoryOptions>> monitor = new();
        monitor.SetupGet(m => m.CurrentValue).Returns(options);
        return new ProjectHistoryIdleCaptureService(factory, monitor.Object, NullLogger<ProjectHistoryIdleCaptureService>.Instance);
    }

    private static async Task<object?> Invoke(string toolName, Guid? projectId = null)
    {
        AIFunction inner = AIFunctionFactory.Create(() => "tool-result", toolName);
        AssistantToolInterceptor interceptor = new(inner, gate: null, onResult: null);
        AIFunctionArguments args = new();
        if (projectId is { } id)
            args["projectId"] = id;
        return await interceptor.InvokeAsync(args, CancellationToken.None);
    }
}
