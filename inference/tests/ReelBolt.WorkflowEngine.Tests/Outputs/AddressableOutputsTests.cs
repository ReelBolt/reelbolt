#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// T-024 (QA-P0-1, S-02): a VideoGenerate clip, stored where the engine really stores it
/// (<c>projects/{p}/agentFiles/generated/{exec}/step-N-vI.mp4</c>), is an output exactly like a render —
/// GetOutput describes it, a link streams it and the read models give it an outputId — while anything
/// outside the two addressable prefixes, or in an organization that has been deleted, is still nothing.
/// </summary>
public class AddressableOutputsTests
{
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();

    private OutputLinkService Links(OutputsWorld w) => new(_tokens, new ConfigurationBuilder().Build(), w.Clock);

    private OutputDescriber Describer(OutputsWorld w) =>
        new(w.Db, w.Storage, Links(w), new Mock<IPublishEndpoint>().Object);

    private static WorkflowStepResult AddGeneratedClip(OutputsWorld w, string? key = null)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = w.Step.WorkflowDefinitionId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.VideoGenerate,
            Label = "Plates"
        };
        var clip = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = w.Execution.Id,
            WorkflowStepId = step.Id,
            Status = StepStatus.Completed,
            OutputStorageKey = key ?? $"projects/{w.Project.Id}/agentFiles/generated/{w.Execution.Id}/step-2-v1.mp4",
            OutputJson = "{\"clips\":[]}",
            CompletedAt = OutputsWorld.Now.UtcDateTime
        };
        w.Db.WorkflowSteps.Add(step);
        w.Db.WorkflowStepResults.Add(clip);
        w.Db.SaveChanges();
        w.Storage.Objects[clip.OutputStorageKey!] = OutputsWorld.Bytes(321);
        return clip;
    }

    [Fact]
    public async Task FindRender_accepts_a_generated_clip_under_agentFiles_generated()
    {
        using OutputsWorld w = new();
        WorkflowStepResult clip = AddGeneratedClip(w);

        OutputRender? render = await Describer(w).FindRenderAsync(w.Project.Id, clip.Id, CancellationToken.None);

        render.Should().NotBeNull();
        render!.FileName.Should().Be("step-2-v1.mp4");
        render.MimeType.Should().Be("video/mp4");
        render.SizeBytes.Should().Be(321);
    }

    [Fact]
    public async Task Describe_reports_a_generated_clip_as_a_render_with_its_step()
    {
        using OutputsWorld w = new();
        WorkflowStepResult clip = AddGeneratedClip(w);

        OutputDescription? d = await Describer(w).DescribeAsync(w.Project, clip.Id, w.UserId, CancellationToken.None);

        d.Should().NotBeNull();
        d!.Kind.Should().Be("render");
        d.StepOrder.Should().Be(2);
        d.StepLabel.Should().Be("Plates");
    }

    [Fact]
    public async Task FindRender_refuses_a_key_outside_both_output_prefixes()
    {
        using OutputsWorld w = new();
        WorkflowStepResult clip = AddGeneratedClip(w, $"projects/{w.Project.Id}/agentFiles/workspace/{w.Execution.Id}/raw.mp4");

        (await Describer(w).FindRenderAsync(w.Project.Id, clip.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_link_to_a_generated_clip_resolves_to_the_clip()
    {
        using OutputsWorld w = new();
        WorkflowStepResult clip = AddGeneratedClip(w);
        OutputLinkClaims claims = Claims(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.StepResult, clip.Id, null));

        ResolvedOutputObject? resolved = await new OutputLinkResolver(w.Db, w.Storage).ResolveAsync(claims, CancellationToken.None);

        resolved.Should().NotBeNull();
        resolved!.FileName.Should().Be("step-2-v1.mp4");
        resolved.ContentType.Should().Be("video/mp4");
    }

    [Fact]
    public async Task A_link_to_a_quarantined_project_file_resolves_to_nothing()
    {
        using OutputsWorld w = new();
        w.File.CsamStatus = CsamScanStatus.Quarantined;
        await w.Db.SaveChangesAsync();
        OutputLinkClaims claims = Claims(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.ProjectFile, w.File.Id, null));

        (await new OutputLinkResolver(w.Db, w.Storage).ResolveAsync(claims, CancellationToken.None))
            .Should().BeNull("the anonymous output link must not serve quarantined bytes");
    }

    [Fact]
    public async Task A_link_to_a_generated_key_of_another_project_resolves_to_nothing()
    {
        using OutputsWorld w = new();
        WorkflowStepResult clip = AddGeneratedClip(w, $"projects/{Guid.NewGuid()}/agentFiles/generated/{w.Execution.Id}/step-2-v1.mp4");
        OutputLinkClaims claims = Claims(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.StepResult, clip.Id, null));

        (await new OutputLinkResolver(w.Db, w.Storage).ResolveAsync(claims, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_link_stops_resolving_once_the_organization_is_soft_deleted()
    {
        using OutputsWorld w = new();
        OutputLinkClaims claims = Claims(w, new OutputLinkTarget(w.Project.Id, OutputLinkKind.StepResult, w.Render.Id, null));
        var resolver = new OutputLinkResolver(w.Db, w.Storage);
        (await resolver.ResolveAsync(claims, CancellationToken.None)).Should().NotBeNull("the link is good before the deletion");

        Organization org = w.Db.Organizations.Find(w.OrgId)!;
        org.DeletedAt = OutputsWorld.Now.UtcDateTime;
        w.Db.SaveChanges();

        (await resolver.ResolveAsync(claims, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public void The_run_read_models_give_a_generated_clip_an_output_id()
    {
        Guid projectId = Guid.NewGuid();
        var clip = new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            OutputStorageKey = $"projects/{projectId}/agentFiles/generated/{Guid.NewGuid()}/step-2-v1.mp4"
        };

        AssistantRunShapes.OutputId(projectId, clip).Should().Be(clip.Id);
        AssistantRunShapes.OutputId(Guid.NewGuid(), clip).Should().BeNull("another project's prefix is not this project's output");
    }

    private OutputLinkClaims Claims(OutputsWorld w, OutputLinkTarget target)
    {
        string url = Links(w).Create(target, w.UserId, null).Url;
        string token = url[(url.LastIndexOf('/') + 1)..];
        Links(w).TryVerify(token, w.Clock.GetUtcNow(), out OutputLinkClaims claims).Should().BeTrue();
        return claims;
    }
}
