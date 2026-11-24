using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Tests.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// saas-quality-followups, per-iteration render keys, end to end: the engine drives a real review loop, the author
/// step renders through the real render tool inside the scope the engine opened for its step-result row, and the keys
/// that end up PERSISTED on the two author rows are distinct, shaped
/// <c>projects/{project}/outputFiles/{executionId}/{stepResultId:N}/{file}</c>, and the first iteration's row still
/// points at its own object after the second has run. (The tool alone is pinned in
/// ReactRemotionSandboxToolsUnchangedRenderTests; the scope hand-over per executor in StepResultIdScopeTests.)
/// </summary>
public class ReviewLoopRenderKeysEndToEndTests
{
    private const string OutputFileName = "promo.mp4";

    [Fact]
    public async Task Two_review_loop_iterations_persist_two_distinct_per_row_render_keys_and_the_first_row_keeps_its_own()
    {
        string dbName = "renderkeys-" + Guid.NewGuid().ToString("N");
        Func<WorkflowEngineDbContext> newDb = () => new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(dbName).Options);
        ExecutionResumeTests.Seeded s;
        using (WorkflowEngineDbContext seed = newDb())
        {
            s = ExecutionResumeTests.Seed(seed,
                new(StepType.Agent, "Author"),
                new(StepType.ReviewLoop, "Review", st =>
                {
                    st.LoopTargetStepOrder = 1;
                    st.MaxIterations = 2;
                    st.MinScore = 9;
                }));
        }

        List<string> uploaded = [];
        Mock<IAmazonS3> s3 = new();
        s3.Setup(x => x.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) =>
            {
                if (request.Key.EndsWith(OutputFileName, StringComparison.Ordinal)) uploaded.Add(request.Key);
            })
            .ReturnsAsync(new PutObjectResponse());

        ExecutionResumeTests.Scopes scopes = new(newDb);
        WorkflowExecutionContextAccessor accessor = new();
        ReactRemotionSandboxTools tool = new(
            Mock.Of<System.Net.Http.IHttpClientFactory>(),
            accessor,
            Mock.Of<IProjectFileWorkspace>(),
            s3.Object,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge" }).Build(),
            NullLogger<ReactRemotionSandboxTools>.Instance,
            scopes,
            mediaProbe: null,
            usageRecorder: null,
            videoToolRunner: null,
            videoEditingOptions: null,
            sandboxSessions: new ReactRemotionSandboxToolsUnchangedRenderTests.FixedSessionFactory(
                new ReactRemotionSandboxToolsUnchangedRenderTests.FileTreeSession(s.ExecutionId, new Dictionary<string, string>
                {
                    ["src/root.tsx"] = "export const Root = () => null;\n"
                })));

        // The author renders through the real tool inside the scope its step executor opens for the row it is
        // writing (BeginScope with the context's StepResultId), and hands the uploaded key on as its output.
        async Task<StepExecutionResult> Author(StepExecutionContext c)
        {
            using IDisposable scope = accessor.BeginScope(s.ExecutionId, s.ProjectId, "corr", stepResultId: c.StepResultId);
            using JsonDocument rendered = JsonDocument.Parse(await tool.RenderVideoAndUploadToStorage("Main", OutputFileName));
            rendered.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
            return ExecutionResumeTests.Done(c, "{\"cut\":" + c.IterationCount + "}", rendered.RootElement.GetProperty("storageKey").GetString());
        }

        StepExecutionResult Reviewed(StepExecutionContext c)
        {
            int next = c.IterationCount + 1;
            bool loop = next < c.Step.MaxIterations;
            return new StepExecutionResult
            {
                Output = "{\"overallScore\":4,\"summary\":\"Tighten the hook.\"}",
                NextStepIndex = loop ? 0 : c.CurrentStepIndex + 1,
                NewIterationCount = next,
                IterationNumber = next,
                Status = StepStatus.Completed
            };
        }

        await ExecutionResumeTests.Service(scopes, null, null, null,
            new ExecutionResumeTests.Executor(StepType.Agent, Author),
            new ExecutionResumeTests.Executor(StepType.ReviewLoop, c => Task.FromResult(Reviewed(c))))
            .ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

        using WorkflowEngineDbContext check = newDb();
        (await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId)).Status
            .Should().Be(ExecutionStatus.Passed);
        List<WorkflowStepResult> authors = (await check.WorkflowStepResults.AsNoTracking().Include(r => r.WorkflowStep)
                .Where(r => r.WorkflowExecutionId == s.ExecutionId && r.WorkflowStep.Label == "Author").ToListAsync())
            .OrderBy(r => r.ExecutedAt).ToList();

        authors.Should().HaveCount(2, "the loop ran the author in two iterations");
        authors.Select(r => r.Id).Distinct().Should().HaveCount(2);
        authors.Select(r => r.OutputStorageKey).Should().OnlyContain(k => !string.IsNullOrEmpty(k));
        authors[0].OutputStorageKey.Should().NotBe(authors[1].OutputStorageKey,
            "iteration 2's render must not overwrite the object iteration 1's row points at");
        foreach (WorkflowStepResult row in authors)
        {
            row.OutputStorageKey.Should().Be($"projects/{s.ProjectId}/outputFiles/{s.ExecutionId}/{row.Id:N}/{OutputFileName}",
                "each row's key is scoped to its own step-result id");
        }

        uploaded.Should().Equal(authors.Select(r => r.OutputStorageKey!), "two uploads, one object per row, none overwritten");
    }
}
