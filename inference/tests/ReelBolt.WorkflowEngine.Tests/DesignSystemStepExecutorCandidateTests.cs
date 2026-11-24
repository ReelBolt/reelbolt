using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// The reported customer run failed at step 10 because step 9 (a DesignSystem <c>Parse</c>) never saw
/// the project's <c>.scss</c>: a stylesheet is not a candidate, so the Parse loop never reads it, so
/// it never reaches the extractor. These tests drive the real Parse executor's candidate-selection
/// path — a candidate is exactly the set of files the Parse loop reads — and pin both the new
/// stylesheet candidates and the pre-existing ones.
/// </summary>
public sealed class DesignSystemStepExecutorCandidateTests
{
    [Fact]
    public async Task Parse_selects_stylesheet_sources_alongside_the_existing_candidates()
    {
        Guid projectId = Guid.NewGuid();

        (ProjectWorkspaceFile File, string Text)[] candidates =
        [
            (WorkspaceFile(projectId, "_icons.scss", "src/styles", "$brand: #5b4bff;\n.btn { color: $brand; }\n"),
                "$brand: #5b4bff;\n.btn { color: $brand; }\n"),
            (WorkspaceFile(projectId, "theme.sass", "src/styles", "$brand: #5b4bff\n.btn\n  color: $brand\n"),
                "$brand: #5b4bff\n.btn\n  color: $brand\n"),
            (WorkspaceFile(projectId, "vars.less", "src/styles", "@brand: #5b4bff;\n.btn { color: @brand; }\n"),
                "@brand: #5b4bff;\n.btn { color: @brand; }\n"),
            (WorkspaceFile(projectId, "page.tsx", "src/app", "export default function Page() { return <main>hi</main>; }"),
                "export default function Page() { return <main>hi</main>; }")
        ];

        // A file that is neither a design source nor an extractor candidate: the selection must stay
        // discriminating, not simply read everything the workspace lists.
        ProjectWorkspaceFile nonCandidate =
            WorkspaceFile(projectId, "README.md", null, "# not a design source\n");

        var texts = candidates.ToDictionary(entry => entry.File.Id, entry => entry.Text);
        texts[nonCandidate.Id] = "# not a design source\n";

        var workspace = new Mock<IProjectFileWorkspace>();
        workspace.Setup(w => w.ListFilesAsync(projectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(candidates.Select(entry => entry.File).Append(nonCandidate).ToList());
        workspace.Setup(w => w.ReadFileAsync(projectId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, string reference, CancellationToken _) => Task.FromResult(texts[Guid.Parse(reference)]));
        workspace.Setup(w => w.UploadArtifactAsync(
                projectId, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync("projects/p/agentFiles/design-system/exec/step-1-candidates.json");

        StepExecutionResult result = await CreateExecutor(workspace.Object).ExecuteAsync(Context(projectId));

        result.Status.Should().Be(StepStatus.Completed, result.Output);

        foreach ((ProjectWorkspaceFile file, string _) in candidates)
        {
            workspace.Verify(
                w => w.ReadFileAsync(projectId, file.Id.ToString(), It.IsAny<CancellationToken>()),
                Times.Once,
                $"{file.OriginalFileName} must be a candidate so the Parse step reads it into the extractor");
        }

        workspace.Verify(
            w => w.ReadFileAsync(projectId, nonCandidate.Id.ToString(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a non-design file must not become a candidate");
    }

    private static DesignSystemStepExecutor CreateExecutor(IProjectFileWorkspace workspace) =>
        new(
            workspace,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Mock.Of<IHttpClientFactory>(),
            NullLogger<DesignSystemStepExecutor>.Instance);

    private static StepExecutionContext Context(Guid projectId)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 9,
            StepType = ReelBolt.Shared.Data.Models.StepType.DesignSystem,
            DesignSystemConfigJson = """{"op":"Parse","version":1,"parse":{}}"""
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "design-system-candidate-test",
            CancellationToken = CancellationToken.None
        };
    }

    private static ProjectWorkspaceFile WorkspaceFile(
        Guid projectId, string fileName, string? originalPath, string content) =>
        new(
            Guid.NewGuid(),
            projectId,
            fileName,
            originalPath,
            "sourceFiles",
            $"projects/{projectId:D}/sourceFiles/{fileName}",
            "text/plain",
            Encoding.UTF8.GetByteCount(content),
            DateTime.UtcNow,
            null);
}
