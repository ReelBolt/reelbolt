using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>WL018 bound to the real manifest validator, and the one consumption table behind WL011 and the app deriver.</summary>
public class WorkflowLintManifestTests
{
    private static InferenceApiDbContext NewContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static readonly AppManifest VoiceInput = new(
        Inputs: [new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Voice, Label: "Narrator")]);

    private static async Task<IReadOnlyList<LintFinding>> LintWithAsync(InferenceApiDbContext db, WorkflowLintRequest request)
    {
        WorkflowLinter linter = new(new FixedFactsSource(new FakeFacts()), new AppManifestOrphanSource(db));
        return (await linter.LintAsync(request, default)).Findings;
    }

    [Fact]
    public async Task A_draft_manifest_input_no_step_reads_is_a_WL018_error()
    {
        using InferenceApiDbContext db = NewContext();
        List<WorkflowStep> steps = [Agent(1, AgentType.CodeStructureAnalyzer)];

        IReadOnlyList<LintFinding> findings = await LintWithAsync(db, new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), steps, Manifest: VoiceInput));

        LintFinding orphan = findings.Rule("WL018").Should().ContainSingle().Subject;
        orphan.Severity.Should().Be(LintSeverity.Error);
        orphan.Message.Should().Contain("Narrator");
    }

    [Fact]
    public async Task The_same_input_passes_when_a_step_reads_the_field()
    {
        using InferenceApiDbContext db = NewContext();
        List<WorkflowStep> steps = [Voiceover(1, """{"source":{"kind":"Inline"}}""")];

        (await LintWithAsync(db, new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), steps, Manifest: VoiceInput))).Rule("WL018").Should().BeEmpty();
    }

    [Fact]
    public async Task The_stored_manifest_of_a_saved_workflow_is_used_and_is_project_scoped()
    {
        using InferenceApiDbContext db = NewContext();
        Guid project = Guid.NewGuid();
        Guid workflow = Guid.NewGuid();
        db.WorkflowDefinitions.Add(new WorkflowDefinition
        {
            Id = workflow,
            ProjectId = project,
            Name = "w",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            AppManifestJson = AppManifestJson.Serialize(VoiceInput)
        });
        await db.SaveChangesAsync();
        List<WorkflowStep> steps = [Agent(1, AgentType.CodeStructureAnalyzer)];

        (await LintWithAsync(db, new WorkflowLintRequest(Guid.NewGuid(), project, steps, null, workflow))).Rule("WL018").Should().ContainSingle();
        (await LintWithAsync(db, new WorkflowLintRequest(Guid.NewGuid(), Guid.NewGuid(), steps, null, workflow))).Rule("WL018")
            .Should().BeEmpty("another project's workflow id reads no manifest");
    }

    [Fact]
    public void WL011_and_the_app_deriver_read_the_same_table()
    {
        (LintBriefField Field, string Key)[] map =
        [
            (LintBriefField.Language, BriefFieldConsumers.Language), (LintBriefField.SourceLanguage, BriefFieldConsumers.SourceLanguage),
            (LintBriefField.LengthSec, BriefFieldConsumers.LengthSec), (LintBriefField.Voice, BriefFieldConsumers.Voice),
            (LintBriefField.Format, BriefFieldConsumers.Format), (LintBriefField.Resolution, BriefFieldConsumers.Resolution),
            (LintBriefField.DesignSystem, BriefFieldConsumers.DesignSystem)
        ];
        List<WorkflowStep> steps =
        [
            .. Enum.GetValues<AgentType>().Select(a => Agent(1, a)),
            Of(1, StepType.VideoAnalyze, """{"version":1}"""), Of(1, StepType.VideoCompile, "{}"), Of(1, StepType.Voiceover, "{}"),
            Of(1, StepType.VideoGenerate, "{}"), Of(1, StepType.EditRoom), Of(1, StepType.ReviewLoop)
        ];
        foreach (WorkflowStep step in steps.Where(s => s.AgentDefinition?.AgentType != AgentType.Custom))
        {
            IReadOnlySet<string> table = BriefFieldConsumers.FieldsReadBy(AppStepFacts.From(step.StepType, step.AgentDefinition?.AgentType, step.VideoAnalyzeConfigJson));
            foreach ((LintBriefField field, string key) in map)
                StepCompatibilityRules.StepReads(step, field).Should().Be(table.Contains(key), $"{step.StepType}/{step.AgentDefinition?.AgentType} {key}");
            StepCompatibilityRules.StepReads(step, LintBriefField.Style)
                .Should().Be(table.Contains(BriefFieldConsumers.StylePreset) || table.Contains(BriefFieldConsumers.Tone));
        }
    }
}
