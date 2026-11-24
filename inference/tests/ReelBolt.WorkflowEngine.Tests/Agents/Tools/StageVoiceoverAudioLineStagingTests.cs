using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

/// <summary>
/// Issue 16 (ig-reel 2026-11-03): <see cref="ReactRemotionSandboxTools.StageVoiceoverAudio"/> only
/// staged lines by mapping them onto a ScriptwriterAgent step's scenes, so an <c>Inline</c> voiceover
/// with no Scriptwriter step answered <c>{"staged":[],"failed":[]}</c> and the Author rendered a mute
/// film. These pin the by-line staging path, the reasons an empty answer now carries, and that the
/// scene-sourced path is unchanged.
/// </summary>
public class StageVoiceoverAudioLineStagingTests
{
    private const string InlineConfig = """{"source":{"kind":"Inline","lines":[{"text":"a","startSec":0}]}}""";
    private const string ScriptScenesConfig = """{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1}}""";

    private sealed record Line(string Id, string Status, string? StorageKey, double StartSec, double DurationSec, string? Text);

    /// <summary>Records every sandbox write path (the <c>path</c> query value) and answers 200.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> WrittenPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string query = request.RequestUri?.Query ?? "";
            const string marker = "path=";
            int at = query.IndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                string raw = query[(at + marker.Length)..];
                int amp = raw.IndexOf('&');
                WrittenPaths.Add(Uri.UnescapeDataString(amp >= 0 ? raw[..amp] : raw));
            }
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static ServiceProvider Seed(
        Guid executionId,
        Guid projectId,
        string? voiceoverConfigJson,
        IReadOnlyList<Line>? voiceoverLines,
        IReadOnlyList<string>? sceneVoiceovers)
    {
        string dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        ServiceProvider provider = services.BuildServiceProvider();

        using IServiceScope scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        db.WorkflowExecutions.Add(new WorkflowExecution { Id = executionId, ProjectId = projectId });

        if (sceneVoiceovers is not null)
        {
            var agent = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.ScriptwriterAgent, GeneratesOutput = true };
            db.AgentDefinitions.Add(agent);
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Agent,
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                StepOrder = 1
            };
            db.WorkflowSteps.Add(step);
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = step.Id,
                WorkflowStep = step,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(new { scenes = sceneVoiceovers.Select(v => new { voiceover = v }).ToArray() })
            });
        }

        if (voiceoverLines is not null)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepType = StepType.Voiceover,
                StepOrder = 2,
                VoiceoverConfigJson = voiceoverConfigJson
            };
            db.WorkflowSteps.Add(step);

            // Hand-built so a null member is OMITTED, as the producer's JsonIgnore(WhenWritingNull) does.
            var lines = voiceoverLines.Select(l =>
            {
                var o = new Dictionary<string, object> { ["id"] = l.Id, ["startSec"] = l.StartSec, ["status"] = l.Status, ["durationSec"] = l.DurationSec };
                if (l.StorageKey is not null)
                    o["storageKey"] = l.StorageKey;
                if (l.Text is not null)
                    o["text"] = l.Text;
                return o;
            }).ToArray();

            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = step.Id,
                WorkflowStep = step,
                Status = StepStatus.Completed,
                OutputJson = JsonSerializer.Serialize(new { view = new { lines } })
            });
        }

        db.SaveChanges();
        return provider;
    }

    private static Mock<Amazon.S3.IAmazonS3> S3(List<string> requestedKeys, ISet<string>? missingKeys = null)
    {
        var s3 = new Mock<Amazon.S3.IAmazonS3>();
        s3.Setup(s => s.GetObjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string key, CancellationToken _) =>
            {
                requestedKeys.Add(key);
                if (missingKeys is not null && missingKeys.Contains(key))
                {
                    throw new Amazon.S3.AmazonS3Exception("NoSuchKey")
                    {
                        StatusCode = System.Net.HttpStatusCode.NotFound,
                        ErrorCode = "NoSuchKey"
                    };
                }

                return Task.FromResult(new Amazon.S3.Model.GetObjectResponse
                {
                    ResponseStream = new MemoryStream([0x52, 0x49, 0x46, 0x46, 0x28, 0x00, 0x00, 0x00, 0x57, 0x41, 0x56, 0x45]),
                    HttpStatusCode = System.Net.HttpStatusCode.OK
                });
            });
        return s3;
    }

    private static ReactRemotionSandboxTools Tool(
        Guid executionId, Guid projectId, ServiceProvider provider, Amazon.S3.IAmazonS3 s3, CapturingHandler handler)
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.Current).Returns(new WorkflowExecutionContext
        {
            ExecutionId = executionId,
            ProjectId = projectId,
            CorrelationId = "test"
        });

        var http = new Mock<IHttpClientFactory>();
        http.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, disposeHandler: false));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sandbox:BaseUrl"] = "http://sandbox-executor:8080",
                ["Sandbox:ApiToken"] = "test-token",
                ["MinIO:BucketName"] = "reelforge"
            })
            .Build();

        return new ReactRemotionSandboxTools(
            http.Object,
            accessor.Object,
            Mock.Of<IProjectFileWorkspace>(),
            s3,
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    private static string Key(Guid projectId, char c) => $"projects/{projectId}/agentFiles/voiceover/lines/{new string(c, 64)}.wav";

    [Fact]
    public async Task InlineVoiceover_WithoutScriptwriter_StagesEveryOkLineByIdWithTextAndTimings()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, InlineConfig,
            [
                new("vo0", "ok", Key(projectId, 'a'), 0.0, 2.5, "Meet Caffè Lume."),
                new("vo1", "skipped", null, 2.5, 0, ""),
                new("vo2", "ok", Key(projectId, 'b'), 4.25, 3.1234, "Order ahead."),
                new("vo3", "failed", null, 8.0, 0, null)
            ],
            sceneVoiceovers: null);

        List<string> keys = [];
        var handler = new CapturingHandler();
        string result = await Tool(executionId, projectId, provider, S3(keys).Object, handler).StageVoiceoverAudio();

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("staged").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(["voiceover/vo0.wav", "voiceover/vo2.wav"], result);
        json.GetProperty("failed").EnumerateArray().Should().BeEmpty();
        json.TryGetProperty("reason", out _).Should().BeFalse("a run that staged narration is a plain success");

        List<JsonElement> lines = json.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        lines[0].GetProperty("path").GetString().Should().Be("voiceover/vo0.wav");
        lines[0].GetProperty("lineId").GetString().Should().Be("vo0");
        lines[0].GetProperty("text").GetString().Should().Be("Meet Caffè Lume.");
        lines[0].GetProperty("startSec").GetDouble().Should().Be(0.0);
        lines[0].GetProperty("durationSec").GetDouble().Should().Be(2.5);
        lines[0].GetProperty("durationInFrames").GetInt32().Should().Be(75);
        lines[1].GetProperty("lineId").GetString().Should().Be("vo2");
        lines[1].GetProperty("text").GetString().Should().Be("Order ahead.");
        lines[1].GetProperty("startSec").GetDouble().Should().Be(4.25);
        lines[1].GetProperty("durationSec").GetDouble().Should().Be(3.123);

        keys.Should().Equal(Key(projectId, 'a'), Key(projectId, 'b'));
        handler.WrittenPaths.Should().Equal("public/voiceover/vo0.wav", "public/voiceover/vo2.wav");
    }

    [Fact]
    public async Task InlineVoiceover_WithAScriptwriterStepPresent_IsStillStagedByLineIdNotByScene()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, InlineConfig,
            [new("vo0", "ok", Key(projectId, 'a'), 1.0, 2.0, "Hello.")],
            sceneVoiceovers: ["", "Scene 1 voiceover"]);

        List<string> keys = [];
        string result = await Tool(executionId, projectId, provider, S3(keys).Object, new CapturingHandler()).StageVoiceoverAudio();

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("staged").EnumerateArray().Select(e => e.GetString()).Should().Equal(["voiceover/vo0.wav"], result);
    }

    [Fact]
    public async Task NoVoiceoverStep_AnswersWithAReasonNotASilentEmptySuccess()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, null, voiceoverLines: null, sceneVoiceovers: ["Scene 0"]);

        string result = await Tool(executionId, projectId, provider, S3([]).Object, new CapturingHandler()).StageVoiceoverAudio();

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("staged").EnumerateArray().Should().BeEmpty();
        json.GetProperty("reason").GetString().Should().Be("no_voiceover_step");
        json.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InlineVoiceover_WhoseObjectsAreAllMissing_ReportsNothingStagedWithFailures()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, InlineConfig,
            [new("vo0", "ok", Key(projectId, 'a'), 0, 1.0, "One."), new("vo1", "ok", Key(projectId, 'b'), 1.0, 1.0, "Two.")],
            sceneVoiceovers: null);

        List<string> keys = [];
        var missing = new HashSet<string> { Key(projectId, 'a'), Key(projectId, 'b') };
        string result = await Tool(executionId, projectId, provider, S3(keys, missing).Object, new CapturingHandler()).StageVoiceoverAudio();

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("staged").EnumerateArray().Should().BeEmpty();
        json.GetProperty("reason").GetString().Should().Be("nothing_staged");
        json.GetProperty("failed").EnumerateArray().Select(f => f.GetProperty("lineId").GetString()).Should().Equal("vo0", "vo1");
        json.GetProperty("failed").EnumerateArray().Select(f => f.GetProperty("reason").GetString())
            .Should().AllBe("object_not_found");
    }

    [Fact]
    public async Task VoiceoverWithNoSpokenLine_AnswersNoSpokenLines()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, InlineConfig,
            [new("vo0", "skipped", null, 0, 0, ""), new("vo1", "failed", null, 1.0, 0, null)],
            sceneVoiceovers: null);

        string result = await Tool(executionId, projectId, provider, S3([]).Object, new CapturingHandler()).StageVoiceoverAudio();

        JsonSerializer.Deserialize<JsonElement>(result).GetProperty("reason").GetString().Should().Be("no_spoken_lines");
    }

    [Fact]
    public async Task InlineVoiceover_LineIdThatIsNotAFileName_IsReportedNotWritten()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, InlineConfig,
            [new("../evil", "ok", Key(projectId, 'a'), 0, 1.0, "x"), new("vo1", "ok", Key(projectId, 'b'), 1.0, 1.0, "y")],
            sceneVoiceovers: null);

        var handler = new CapturingHandler();
        string result = await Tool(executionId, projectId, provider, S3([]).Object, handler).StageVoiceoverAudio();

        JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
        json.GetProperty("staged").EnumerateArray().Select(e => e.GetString()).Should().Equal(["voiceover/vo1.wav"], result);
        json.GetProperty("failed").EnumerateArray().Single().GetProperty("reason").GetString().Should().Be("invalid_line_id");
        handler.WrittenPaths.Should().Equal("public/voiceover/vo1.wav");
    }

    [Fact]
    public async Task ScriptScenesVoiceover_IsStagedBySceneExactlyAsBefore()
    {
        Guid executionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        using ServiceProvider provider = Seed(executionId, projectId, ScriptScenesConfig,
            [new("vo0", "ok", Key(projectId, 'a'), 0, 2.0, "Scene 0 voiceover"), new("vo1", "ok", Key(projectId, 'b'), 3.0, 1.5, "Scene 2 voiceover")],
            sceneVoiceovers: ["Scene 0 voiceover", "", "Scene 2 voiceover"]);

        List<string> keys = [];
        var handler = new CapturingHandler();
        string result = await Tool(executionId, projectId, provider, S3(keys).Object, handler).StageVoiceoverAudio();

        // The exact pre-fix answer: staged/failed/timing, no `lines`, no `reason`.
        result.Should().Be(
            """{"staged":["voiceover/scene-0.wav","voiceover/scene-2.wav"],"failed":[],"timing":[""" +
            """{"scenePosition":0,"path":"voiceover/scene-0.wav","durationSec":2,"durationInFrames":60,"fps":30},""" +
            """{"scenePosition":2,"path":"voiceover/scene-2.wav","durationSec":1.5,"durationInFrames":45,"fps":30}]}""");
        keys.Should().Equal(Key(projectId, 'a'), Key(projectId, 'b'));
        handler.WrittenPaths.Should().Equal("public/voiceover/scene-0.wav", "public/voiceover/scene-2.wav");
    }
}
