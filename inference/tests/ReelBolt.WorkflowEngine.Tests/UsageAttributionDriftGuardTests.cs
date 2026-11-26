using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// An ASR or embedding call made outside any <see cref="UsageScope"/> lands as Source=Unattributed on the platform
/// org. This guard fails when a source file calls one without opening a scope, unless it is allowlisted with a reason.
/// </summary>
public class UsageAttributionDriftGuardTests
{
    private static readonly Dictionary<string, string> Allowlist = new()
    {
        ["ReelBolt.Inference.Api/Services/Inference/InferenceProviderTester.cs"] =
            "admin connectivity ping with no organization; accepted as Unattributed",
        ["ReelBolt.Shared/Metering/MeteringMediaClients.cs"] = "the metering wrapper itself",
        ["ReelBolt.Inference.Api/Services/VectorSearch/VectorSearchQueryService.cs"] =
            "its only caller (ProjectFilesController.Search) opens the Indexing scope",
        ["ReelBolt.Inference.Api/Services/VectorSearch/ProviderEmbeddingGenerator.cs"] =
            "the embedding generator adapter; callers own the scope",
        ["ReelBolt.WorkflowEngine/Execution/StepExecutors/AudioGenerateStepExecutor.cs"] =
            "audio synthesis (not ASR/embedding) inside a workflow step, which the engine already scopes",
        ["ReelBolt.WorkflowEngine/Execution/StepExecutors/ImageGenerateStepExecutor.cs"] =
            "image synthesis (not ASR/embedding) inside a workflow step, which the engine already scopes",
        ["ReelBolt.WorkflowEngine/Execution/StepExecutors/VideoAnalyzeStepExecutor.cs"] =
            "runs inside a workflow step, which the engine already scopes",
        ["ReelBolt.WorkflowEngine/Services/Voiceover/VoiceoverAlignmentService.cs"] =
            "called only from VoiceoverStepExecutor, inside the engine's step scope",
        ["ReelBolt.WorkflowEngine/Services/Video/MediaAnalysisService.cs"] =
            "its caller MediaAnalysisConsumer opens an Editor scope",
        ["ReelBolt.Shared/Inference/LocalRunnerEmbeddingGenerator.cs"] = "client decorator; callers own the scope",
    };

    [Fact]
    public void Every_asr_and_embedding_call_site_opens_a_usage_scope_or_is_allowlisted()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ReelBolt.sln"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the repo's inference root must be found");
        string src = Path.Combine(dir!, "src");

        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
            string text = File.ReadAllText(file);
            bool calls = text.Contains(".TranscribeAsync(") || text.Contains(".GenerateAsync(");
            if (!calls) continue;
            if (text.Contains("UsageScope.Begin(") || text.Contains("UsageScope.BeginIfNone(")) continue;
            if (Allowlist.ContainsKey(rel)) continue;
            offenders.Add(rel);
        }

        offenders.Should().BeEmpty("these files call ASR/embedding without opening a UsageScope (add UsageScope.BeginIfNone, or allowlist with a reason)");
    }

    [Fact]
    public void BeginIfNone_opens_a_scope_when_none_and_keeps_the_outer_one()
    {
        Guid org = Guid.NewGuid(), project = Guid.NewGuid(), user = Guid.NewGuid();
        UsageScope.Current.Should().BeNull();

        using (UsageScope.BeginIfNone(org, project, user, UsageSource.Moderation))
        {
            UsageScope.Current!.OrganizationId.Should().Be(org);
            UsageScope.Current.Source.Should().Be(UsageSource.Moderation);

            using (UsageScope.BeginIfNone(Guid.NewGuid(), null, null, UsageSource.Editor))
                UsageScope.Current!.OrganizationId.Should().Be(org);

            UsageScope.Current!.Source.Should().Be(UsageSource.Moderation);
        }

        UsageScope.Current.Should().BeNull();
    }
}
