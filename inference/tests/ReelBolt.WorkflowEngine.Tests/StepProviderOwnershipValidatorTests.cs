using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP A10, save half: a step config that names an inference provider may only name a row the
/// PROJECT'S organization can see — a platform-managed row or one of that organization's own. A
/// cross-organization id is refused before anything is written, with the D5 message that reports
/// invisibility without saying which of its causes applies.
/// </summary>
public class StepProviderOwnershipValidatorTests : IDisposable
{
    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private readonly InferenceApiDbContext _db;

    public StepProviderOwnershipValidatorTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_talking_heads_provider_must_be_a_LipSync_row_a_VideoGeneration_row_is_refused_under_the_same_message()
    {
        ModelCapabilityView video = Provider("org-a-video", InferenceProviderCapability.VideoGeneration, OrgA);
        ModelCapabilityView lipSync = Provider("org-a-lip", InferenceProviderCapability.LipSync, OrgA);
        _db.AddFlat(video, lipSync);
        await _db.SaveChangesAsync();
        List<CreateWorkflowStepRequest> Step(Guid id) =>
        [
            new(Guid.NewGuid(), 1, null, "Talk", StepType: nameof(StepType.VideoGenerate),
                VideoGenerateConfigJson: $$"""{"planSource":"Inline","inlinePrompt":"x","providerId":"{{id}}","avatarAudio":{"from":"Step","stepOrder":1},"maxSpendCredits":5,"takes":1,"maxClips":1}""")
        ];

        (await ValidateAsync(OrgA, Step(lipSync.Id))).Should().BeEmpty();
        (await ValidateAsync(OrgA, Step(video.Id))).Should().ContainSingle().Which.Should().Contain("not available to this organization");
    }

    // ------------------------------------------------------------------------------------------
    // Save-time ownership
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_owned_by_another_organization_is_refused()
    {
        ModelCapabilityView theirs = Provider("org-b-transcription", InferenceProviderCapability.Transcription, OrgB);
        _db.AddFlat(theirs);
        await _db.SaveChangesAsync();

        IReadOnlyList<string> errors = await ValidateAsync(OrgA, AnalyzeStep(theirs.Id));

        errors.Should().ContainSingle();
        errors[0].Should().Be(
            "Step 1 (VideoAnalyze): transcriptionProviderId references an inference provider "
            + "that is not available to this organization.");
    }

    [Fact]
    public async Task A_platform_managed_provider_is_accepted_by_every_organization()
    {
        ModelCapabilityView platform = Provider("platform-transcription", InferenceProviderCapability.Transcription, organizationId: null);
        _db.AddFlat(platform);
        await _db.SaveChangesAsync();

        (await ValidateAsync(OrgA, AnalyzeStep(platform.Id))).Should().BeEmpty();
        (await ValidateAsync(OrgB, AnalyzeStep(platform.Id))).Should().BeEmpty();
    }

    [Fact]
    public async Task The_organizations_own_provider_is_accepted()
    {
        ModelCapabilityView mine = Provider("org-a-transcription", InferenceProviderCapability.Transcription, OrgA);
        _db.AddFlat(mine);
        await _db.SaveChangesAsync();

        (await ValidateAsync(OrgA, AnalyzeStep(mine.Id))).Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_id_that_does_not_exist_at_all_is_refused_with_the_same_message()
    {
        // D5: the refusal must not distinguish "another tenant's row" from "no such row" — one
        // message, so this endpoint cannot be used to probe which ids exist elsewhere.
        IReadOnlyList<string> unknown = await ValidateAsync(OrgA, AnalyzeStep(Guid.NewGuid()));

        unknown.Should().ContainSingle();
        unknown[0].Should().Contain("is not available to this organization");
    }

    [Fact]
    public async Task Every_referencing_field_of_one_config_is_checked()
    {
        // VideoAnalyze names three independent providers: transcription, shot captioning and object
        // tracking. A check that only looked at the first would leave the other two reachable from
        // another organization, so all three are asserted in one config.
        ModelCapabilityView foreign = Provider("org-b-vision", InferenceProviderCapability.Vision, OrgB);
        _db.AddFlat(foreign);
        await _db.SaveChangesAsync();

        List<CreateWorkflowStepRequest> steps =
        [
            new(Guid.NewGuid(), 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
                VideoAnalyzeConfigJson: $$"""
                {"version":1,"source":{"kind":"ProjectFile"},"visionProviderId":"{{foreign.Id}}","objectVisionProviderId":"{{foreign.Id}}"}
                """)
        ];

        IReadOnlyList<string> errors = await ValidateAsync(OrgA, steps);

        errors.Should().HaveCount(2);
        errors.Should().ContainMatch("*visionProviderId references an inference provider*");
        errors.Should().ContainMatch("*objectVisionProviderId references an inference provider*");
    }

    [Fact]
    public async Task Every_referencing_step_type_is_covered()
    {
        ModelCapabilityView foreign = Provider("org-b-everything", InferenceProviderCapability.Decision, OrgB);
        ModelCapabilityView foreignSpeech = Provider("org-b-speech", InferenceProviderCapability.SpeechSynthesis, OrgB);
        ModelCapabilityView foreignVideo = Provider("org-b-video", InferenceProviderCapability.VideoGeneration, OrgB);
        _db.AddFlat(foreign, foreignSpeech, foreignVideo);
        await _db.SaveChangesAsync();

        List<CreateWorkflowStepRequest> steps =
        [
            new(Guid.NewGuid(), 1, null, "Conditional", StepType: nameof(StepType.Conditional),
                ConditionalConfigJson: $$"""{"version":1,"mode":"Decision","providerId":"{{foreign.Id}}"}"""),
            new(Guid.NewGuid(), 2, null, "ReviewLoop", StepType: nameof(StepType.ReviewLoop),
                ReviewLoopConfigJson: $$"""{"version":1,"enabled":true,"providerId":"{{foreign.Id}}"}"""),
            new(Guid.NewGuid(), 3, null, "Voiceover", StepType: nameof(StepType.Voiceover),
                VoiceoverConfigJson: $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{foreignSpeech.Id}}"}"""),
            new(Guid.NewGuid(), 4, null, "Generate", StepType: nameof(StepType.VideoGenerate),
                VideoGenerateConfigJson: $$"""{"planSource":"Inline","inlinePrompt":"x","providerId":"{{foreignVideo.Id}}","maxSpendCredits":5,"takes":1,"maxClips":1}"""),
            new(Guid.NewGuid(), 5, null, "EditRoom", StepType: nameof(StepType.EditRoom),
                EditRoomConfigJson: $$"""{"version":1,"conveneProviderId":"{{foreign.Id}}"}"""),
            new(Guid.NewGuid(), 6, null, "GraphicsRoom", StepType: nameof(StepType.GraphicsRoom),
                GraphicsRoomConfigJson: $$"""{"version":1,"conveneProviderId":"{{foreign.Id}}"}"""),
            new(Guid.NewGuid(), 7, null, "ColorGradeRoom", StepType: nameof(StepType.ColorGradeRoom),
                ColorGradeRoomConfigJson: $$"""{"version":1,"conveneProviderId":"{{foreign.Id}}"}""")
        ];

        IReadOnlyList<string> errors = await ValidateAsync(OrgA, steps);

        errors.Should().HaveCount(7);
        errors.Should().ContainMatch("Step 1 (Conditional)*");
        errors.Should().ContainMatch("Step 2 (ReviewLoop)*");
        errors.Should().ContainMatch("Step 3 (Voiceover)*");
        errors.Should().ContainMatch("Step 4 (VideoGenerate)*");
        errors.Should().ContainMatch("Step 5 (EditRoom)*");
        errors.Should().ContainMatch("Step 6 (GraphicsRoom)*");
        errors.Should().ContainMatch("Step 7 (ColorGradeRoom)*");
    }

    /// <summary>
    /// QA-P1-5 (S-78): a review loop's visual check asks its own Vision model
    /// (<c>ReviewVisualCheck</c> resolves <c>visualCheck.visionProviderId</c> through ResolveVisionAsync), so the
    /// field is a provider reference like any other and a foreign row must be refused before the write — one
    /// refusal, naming the field the author has to change. The organization's own Vision row still saves.
    /// </summary>
    [Fact]
    public async Task A_review_loops_visual_check_vision_provider_is_checked_like_any_other_reference()
    {
        ModelCapabilityView foreign = Provider("org-b-vision", InferenceProviderCapability.Vision, OrgB);
        ModelCapabilityView own = Provider("org-a-vision", InferenceProviderCapability.Vision, OrgA);
        _db.AddFlat(foreign, own);
        await _db.SaveChangesAsync();

        List<CreateWorkflowStepRequest> refused =
        [
            new(Guid.NewGuid(), 1, null, "ReviewLoop", StepType: nameof(StepType.ReviewLoop),
                ReviewLoopConfigJson: $$$"""
                {"version":1,"enabled":true,"visualCheck":{"enabled":true,"frames":8,"visionProviderId":"{{{foreign.Id}}}"}}
                """)
        ];
        List<CreateWorkflowStepRequest> accepted =
        [
            new(Guid.NewGuid(), 1, null, "ReviewLoop", StepType: nameof(StepType.ReviewLoop),
                ReviewLoopConfigJson: $$$"""
                {"version":1,"enabled":true,"visualCheck":{"enabled":true,"frames":8,"visionProviderId":"{{{own.Id}}}"}}
                """)
        ];

        IReadOnlyList<string> errors = await ValidateAsync(OrgA, refused);

        errors.Should().ContainSingle("one reference, one refusal, before any write");
        errors[0].Should().Contain("Step 1 (ReviewLoop)").And.Contain("visualCheck.visionProviderId")
            .And.Contain("not available to this organization");
        (await ValidateAsync(OrgA, accepted)).Should().BeEmpty("the organization's own Vision row is usable");
    }

    [Fact]
    public async Task A_step_that_names_no_provider_and_a_step_type_that_can_name_none_are_untouched()
    {
        // The check is an overlay, never a new requirement: it must not start rejecting configs that
        // save today — a config that leaves the capability to the resolved default, and an Agent
        // step (whose provider comes from its AgentDefinition, not from any step config field).
        List<CreateWorkflowStepRequest> steps =
        [
            new(Guid.NewGuid(), 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
                VideoAnalyzeConfigJson: """{"version":1,"source":{"kind":"ProjectFile"}}"""),
            new(Guid.NewGuid(), 2, null, "Agent", StepType: nameof(StepType.Agent))
        ];

        (await ValidateAsync(OrgA, steps)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_synchronous_twin_reports_the_same_errors()
    {
        // WorkflowEditDiffService.ValidateProposedSteps runs synchronously, and a validate-then-apply
        // round trip must not be able to disagree with the write.
        ModelCapabilityView foreign = Provider("org-b-transcription", InferenceProviderCapability.Transcription, OrgB);
        _db.AddFlat(foreign);
        await _db.SaveChangesAsync();

        List<CreateWorkflowStepRequest> steps = AnalyzeStep(foreign.Id);

        Guid projectId = Guid.NewGuid();
        Validator().Validate(OrgA, projectId, steps)
            .Should().BeEquivalentTo(await Validator().ValidateAsync(OrgA, projectId, steps, CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------
    // Project transfer
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task FindDangling_lists_the_references_a_move_would_strand()
    {
        (Guid projectId, Guid workflowId) = await SeedWorkflowAsync();

        ModelCapabilityView orgOwned = Provider("org-a-voice", InferenceProviderCapability.SpeechSynthesis, OrgA);
        ModelCapabilityView platform = Provider("platform-voice", InferenceProviderCapability.SpeechSynthesis, organizationId: null);
        ModelCapabilityView targetOwned = Provider("org-b-voice", InferenceProviderCapability.SpeechSynthesis, OrgB);
        _db.AddFlat(orgOwned, platform, targetOwned);
        await _db.SaveChangesAsync();

        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{orgOwned.Id}}"}"""
        });
        await _db.SaveChangesAsync();

        IReadOnlyList<DanglingProviderReference> dangling =
            await Validator().FindDanglingAsync(projectId, OrgB, CancellationToken.None);

        dangling.Should().ContainSingle();
        dangling[0].ProviderId.Should().Be(orgOwned.Id);
        dangling[0].ProviderName.Should().Be("org-a-voice");
        dangling[0].Field.Should().Be("providerId");
        dangling[0].StepOrder.Should().Be(1);
        dangling[0].StepType.Should().Be(StepType.Voiceover);
        dangling[0].WorkflowId.Should().Be(workflowId);

        // A platform row is visible to every organization, and a row the TARGET organization owns
        // becomes the project's own after the move — neither is dangling.
        _db.WorkflowSteps.RemoveRange(_db.WorkflowSteps);
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{platform.Id}}"}"""
        });
        _db.WorkflowSteps.Add(new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Voiceover,
            VoiceoverConfigJson = $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{targetOwned.Id}}"}"""
        });
        await _db.SaveChangesAsync();

        (await Validator().FindDanglingAsync(projectId, OrgB, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task FindDangling_reports_one_row_named_by_several_steps_only_once()
    {
        (Guid projectId, Guid workflowId) = await SeedWorkflowAsync();
        ModelCapabilityView shared = Provider("org-a-voice", InferenceProviderCapability.SpeechSynthesis, OrgA);
        _db.AddFlat(shared);
        await _db.SaveChangesAsync();

        for (int order = 1; order <= 3; order++)
        {
            _db.WorkflowSteps.Add(new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = workflowId,
                AgentDefinitionId = Guid.NewGuid(),
                StepOrder = order,
                StepType = StepType.Voiceover,
                VoiceoverConfigJson = $$"""{"version":1,"source":{"kind":"Inline"},"providerId":"{{shared.Id}}"}"""
            });
        }

        await _db.SaveChangesAsync();

        IReadOnlyList<DanglingProviderReference> dangling =
            await Validator().FindDanglingAsync(projectId, OrgB, CancellationToken.None);

        dangling.Should().ContainSingle("one row to repoint is one thing for the author to fix");
        dangling[0].StepOrder.Should().Be(1, "the first step naming it is the one an operator would find");
    }

    // ------------------------------------------------------------------------------------------

    private StepProviderOwnershipValidator Validator() => new(_db);

    // Provider ownership is an organization rule; the project only scopes the cross-run references.
    private async Task<IReadOnlyList<string>> ValidateAsync(Guid organizationId, List<CreateWorkflowStepRequest> steps) =>
        await Validator().ValidateAsync(organizationId, Guid.NewGuid(), steps, CancellationToken.None);

    private static List<CreateWorkflowStepRequest> AnalyzeStep(Guid transcriptionProviderId) =>
    [
        new(Guid.NewGuid(), 1, null, "Analyze", StepType: nameof(StepType.VideoAnalyze),
            VideoAnalyzeConfigJson: $$"""
            {"version":1,"source":{"kind":"ProjectFile"},"transcriptionProviderId":"{{transcriptionProviderId}}"}
            """)
    ];

    private static ModelCapabilityView Provider(string name, InferenceProviderCapability capability, Guid? organizationId) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Kind = InferenceProviderKind.AzureOpenAI,
        Capability = capability,
        OrganizationId = organizationId,
        Endpoint = "https://provider.example",
        ModelName = "m1",
        IsEnabled = true,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private async Task<(Guid ProjectId, Guid WorkflowId)> SeedWorkflowAsync()
    {
        Guid projectId = Guid.NewGuid();
        Guid workflowId = Guid.NewGuid();

        _db.Projects.Add(new Project
        {
            Id = projectId,
            Name = "p",
            OwnerId = Guid.NewGuid(),
            OrganizationId = OrgA,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflowId,
            Name = "wf",
            ProjectId = projectId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return (projectId, workflowId);
    }
}
