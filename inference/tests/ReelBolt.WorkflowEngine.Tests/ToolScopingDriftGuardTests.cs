using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Skills;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Tests.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression guard for the exact bug this repo has already hit twice: the WorkflowEngine's real,
/// load-bearing tool grant (<see cref="AgentToolProvider.GetTools"/>) and the Inference API's
/// display-only "Available Tools" metadata (<c>DatabaseSeeder.GetAvailableToolsJson</c>, private —
/// invoked here via reflection, the same pattern <see cref="VideoStoryEditorPromptConsistencyTests"/>
/// uses) used to be two hand-written, independently maintained mappings. They drifted apart twice:
/// once <c>FailWorkflow</c> was silently missing from the display metadata entirely (found by e2e
/// QA), and once <c>MotionGraphicsPlanner</c> was widened in the real tool provider without the
/// corresponding display-metadata update. Both now derive their answer from the single
/// <see cref="ToolGroupCatalog"/> (see <c>inference/src/ReelBolt.Shared/Agents/</c>), so this test
/// asserts — for every <see cref="AgentType"/> — that the two sets of tool names are exactly equal.
/// If anyone ever again edits one side's scoping without going through the shared catalog, this
/// test fails immediately instead of silently shipping stale display metadata (or, worse, an
/// under/over-scoped agent) a third time.
/// </summary>
public class ToolScopingDriftGuardTests
{
    public static IEnumerable<object[]> AllAgentTypes() =>
        Enum.GetValues<AgentType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllAgentTypes))]
    public void Real_tool_grant_and_display_metadata_agree_on_tool_names(AgentType agentType)
    {
        IReadOnlyList<AIFunction> realTools = CreateAgentToolProvider().GetTools(agentType);
        HashSet<string> realToolNames = realTools.Select(f => f.Name).ToHashSet();

        // UseSkill/ReadSkillResource are a deliberate, documented exception: they are bound
        // per agent-run in ReelBoltAgentBase from SkillCatalog.DefaultsFor, never registered as
        // static ToolGroup-based tools by AgentToolProvider, but ARE included in the display
        // metadata (sourced from the same SkillCatalog) so the "Available Tools" UI card shows
        // them. Add them to the expected set here rather than to AgentToolProvider's real tools —
        // the two are intentionally different for this one case, and this addition itself is
        // still derived from SkillCatalog, so it cannot silently drift from SkillCatalog.cs.
        HashSet<string> expectedToolNames = new(realToolNames);
        if (SkillCatalog.DefaultsFor(agentType).Count > 0)
        {
            expectedToolNames.Add("UseSkill");
            expectedToolNames.Add("ReadSkillResource");
        }

        HashSet<string> displayToolNames = InvokeGetAvailableToolsJson(agentType);

        displayToolNames.Should().BeEquivalentTo(expectedToolNames,
            $"AgentToolProvider.GetTools({agentType}) plus SkillCatalog.DefaultsFor's UseSkill/ReadSkillResource " +
            $"(when non-empty) and DatabaseSeeder.GetAvailableToolsJson({agentType}) " +
            "must derive from the same ToolGroupCatalog/SkillCatalog and therefore agree exactly — a mismatch " +
            "here means one side was edited without the other, which is precisely the drift this catalog exists to prevent");
    }

    /// <summary>
    /// Sanity check that this test actually exercises real tool names, not two empty sets agreeing
    /// vacuously, for every agent type that is genuinely expected to receive tools.
    /// </summary>
    [Fact]
    public void Every_LLM_agent_type_is_granted_at_least_FailWorkflow()
    {
        AgentToolProvider provider = CreateAgentToolProvider();

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            if (agentType is AgentType.ExtractTransform or AgentType.VideoTransform)
                continue;

            IReadOnlyList<AIFunction> tools = provider.GetTools(agentType);
            tools.Select(f => f.Name).Should().Contain("FailWorkflow",
                $"{agentType} is a real (non-deterministic) agent type and must always be able to abort the workflow");
        }
    }

    /// <summary>
    /// <see cref="AgentType.ShotDirector"/> plans generated shots that a separate deterministic
    /// <c>StepType.VideoGenerate</c> step then PAYS a third-party provider to produce. The agent
    /// itself must therefore be structurally unable to touch media, the sandbox, or anything that
    /// can reach a provider — and its grant is asserted as an exact SET, not merely as
    /// "contains no tool from this blocklist": an accidentally ADDED tool is as much a regression
    /// here as a removed one, and a blocklist silently stops protecting the moment a new
    /// spend/render-capable tool is introduced under a name nobody listed.
    /// </summary>
    [Fact]
    public void ShotDirector_is_granted_no_sandbox_render_or_spending_tool()
    {
        HashSet<string> realToolNames = CreateAgentToolProvider()
            .GetTools(AgentType.ShotDirector)
            .Select(f => f.Name)
            .ToHashSet();

        HashSet<string> forbiddenToolNames = new()
        {
            "WriteProjectFile", "EditProjectFile", "EnsureSandbox", "WriteSandboxFile", "EditSandboxFile",
            "ApplySandboxFileEdits", "InsertSandboxFileLines", "StageProjectFile", "StageFont", "DeleteSandboxPath", "InstallNpmPackages",
            "RunSandboxNpmScript", "RunSandboxRemotionCommand", "RenderVideoAndUploadToStorage",
            "GetCompositionInfo", "RenderPreviewStills"
        };

        realToolNames.Intersect(forbiddenToolNames).Should().BeEmpty(
            "ShotDirector only plans; the deterministic VideoGenerate step does the generating and the spending");

        realToolNames.Should().BeEquivalentTo(
            new[]
            {
                "ListProjectFiles", "ReadProjectFile", "ReadProjectFileLines", "GetProjectFileOutline",
                "SearchProjectFiles", "GetDeterministicContextFiles", "FailWorkflow", "WebSearch"
            },
            "ShotDirector's grant must stay exactly read-only project context + FailWorkflow + WebSearch");
    }

    /// <summary>
    /// <see cref="AgentType.NarrationWriter"/> only writes the WORDS a voice speaks — an offered
    /// cut-anchor id plus the line's own prose — and never produces or touches media: the
    /// deterministic <c>StepType.Voiceover</c> step synthesizes the audio and the deterministic
    /// narration fit solver measures the result.
    ///
    /// <para>
    /// <b>Why this Fact exists when the membership theory above already covers the type.</b> That
    /// theory is driven by <c>Enum.GetValues&lt;AgentType&gt;()</c>, so a new enum member needed no
    /// test edit to appear — but what it enforces is <b>agreement between two consumers</b>
    /// (<c>ToolGroupCatalog</c> and the provider reading it), not the grant's CONTENT: both sides
    /// read the same arm, so a future
    /// <c>AgentType.NarrationWriter =&gt; [ProjectRead, SandboxAuthoring, WorkflowControl]</c> would
    /// ship green. This Fact pins the names instead, exactly as the <c>ShotDirector</c> one above
    /// does, so widening the arm with any sandbox, render, write or spend group fails here.
    /// </para>
    /// </summary>
    [Fact]
    public void NarrationWriter_is_granted_no_sandbox_render_or_spending_tool()
    {
        HashSet<string> realToolNames = CreateAgentToolProvider()
            .GetTools(AgentType.NarrationWriter)
            .Select(f => f.Name)
            .ToHashSet();

        HashSet<string> forbiddenToolNames = new()
        {
            "WriteProjectFile", "EditProjectFile", "EnsureSandbox", "WriteSandboxFile", "EditSandboxFile",
            "ApplySandboxFileEdits", "InsertSandboxFileLines", "StageProjectFile", "StageFont", "DeleteSandboxPath", "InstallNpmPackages",
            "RunSandboxNpmScript", "RunSandboxRemotionCommand", "RenderVideoAndUploadToStorage",
            "GetCompositionInfo", "RenderPreviewStills"
        };

        realToolNames.Intersect(forbiddenToolNames).Should().BeEmpty(
            "NarrationWriter only writes the words a voice speaks; the deterministic Voiceover "
            + "step synthesizes the audio, and nothing on this agent's path can render or spend");

        realToolNames.Should().BeEquivalentTo(
            new[]
            {
                "ListProjectFiles", "ReadProjectFile", "ReadProjectFileLines", "GetProjectFileOutline",
                "SearchProjectFiles", "GetDeterministicContextFiles", "FailWorkflow", "WebSearch"
            },
            "NarrationWriter's grant must stay exactly read-only project context + FailWorkflow + WebSearch");
    }

    /// <summary>
    /// CompositionReviser (docs/design/composition-revisions.md) edits and renders a restored film:
    /// the Author's sandbox+render grant, but never ProjectWrite — a revision changes the film, not
    /// the project's files — pinned by name so neither half drifts.
    /// </summary>
    [Fact]
    public void CompositionReviser_is_granted_the_author_sandbox_pipeline_without_project_write()
    {
        HashSet<string> reviser = CreateAgentToolProvider()
            .GetTools(AgentType.CompositionReviser)
            .Select(f => f.Name)
            .ToHashSet();
        HashSet<string> author = CreateAgentToolProvider()
            .GetTools(AgentType.AuthorAgent)
            .Select(f => f.Name)
            .ToHashSet();

        reviser.Should().Contain(["EditSandboxFile", "ApplySandboxFileEdits", "RenderPreviewStills", "GetCompositionInfo", "RenderVideoAndUploadToStorage"]);
        reviser.Should().NotContain(["WriteProjectFile", "EditProjectFile", "CompleteSandbox"]);
        reviser.Should().BeEquivalentTo(author.Except(["WriteProjectFile", "EditProjectFile"]));
    }

    /// <summary>
    /// GraphicDesigner (docs/editor.md "Designed graphics") writes one transparent Remotion Still and
    /// checks it with the preview tools: the CompositionReviser's sandbox+render grant — the Author's
    /// sandbox pipeline without ProjectWrite — pinned by name. The final PNG is rendered by the
    /// platform, so the render-and-upload tool it holds (it lives in the same group as the preview
    /// tools) is not how a graphic is delivered; the prompt test pins that it is told so.
    ///
    /// <para>
    /// The one deliberate difference from <see cref="AgentType.CompositionReviser"/> is
    /// <see cref="ToolGroup.DesignSystemRead"/>: SPEC §12 grants the two read tools to exactly the
    /// seven agent types listed there, and GraphicDesigner is not one of them, so the comparison
    /// excludes them explicitly rather than silently widening the grant to an eighth agent.
    /// </para>
    /// </summary>
    [Fact]
    public void GraphicDesigner_is_granted_the_reviser_sandbox_pipeline_without_project_write()
    {
        HashSet<string> designer = CreateAgentToolProvider()
            .GetTools(AgentType.GraphicDesigner)
            .Select(f => f.Name)
            .ToHashSet();
        HashSet<string> reviser = CreateAgentToolProvider()
            .GetTools(AgentType.CompositionReviser)
            .Select(f => f.Name)
            .ToHashSet();

        designer.Should().Contain(["WriteSandboxFile", "EditSandboxFile", "ApplySandboxFileEdits", "RenderPreviewStills", "GetCompositionInfo", "CheckLintAndTypeErrors", "FailWorkflow"]);
        designer.Should().NotContain(["WriteProjectFile", "EditProjectFile", "CompleteSandbox"]);
        designer.Should().BeEquivalentTo(reviser.Except(["ReadDesignSystem", "ReadProductProfile"]));
    }

    /// <summary>
    /// SPEC §12 grants <see cref="ToolGroup.DesignSystemRead"/> to exactly seven agent types
    /// (RemotionComponentTranslator, AnimationStrategyAgent, ScriptwriterAgent, DirectorAgent,
    /// AuthorAgent, ReviewAgent and CompositionReviser). Pinned as an exact set, not a "contains":
    /// an accidentally added eighth consumer is as much a regression as a missing one, and the
    /// analyzers (which inspect source only) must stay off it.
    /// </summary>
    [Fact]
    public void DesignSystemRead_is_granted_to_exactly_the_seven_specified_agent_types()
    {
        AgentToolProvider provider = CreateAgentToolProvider();
        string[] designSystemTools = ["ReadDesignSystem", "ReadProductProfile"];

        HashSet<AgentType> granted =
        [
            AgentType.RemotionComponentTranslator,
            AgentType.AnimationStrategyAgent,
            AgentType.ScriptwriterAgent,
            AgentType.DirectorAgent,
            AgentType.AuthorAgent,
            AgentType.ReviewAgent,
            AgentType.CompositionReviser,
        ];

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            HashSet<string> names = provider.GetTools(agentType).Select(f => f.Name).ToHashSet();

            foreach (string tool in designSystemTools)
            {
                names.Contains(tool).Should().Be(
                    granted.Contains(agentType),
                    $"{tool} must be granted to {agentType} if and only if it is one of the seven "
                    + "agent types SPEC §12 names for ToolGroup.DesignSystemRead");
            }
        }

        provider.GetTools(AgentType.CodeStructureAnalyzer).Select(f => f.Name).Should().NotContain(designSystemTools);
    }

    /// <summary>
    /// The WebSearch tool spends (a metered provider call) and returns untrusted text, so it is granted to exactly
    /// four agent types: ScriptwriterAgent, DirectorAgent, ShotDirector and NarrationWriter. Pinned as an exact set
    /// (an accidental fifth consumer is as much a regression as a missing one); a custom agent type is never in it.
    /// </summary>
    [Fact]
    public void WebSearch_is_granted_to_exactly_the_specified_agent_types()
    {
        AgentToolProvider provider = CreateAgentToolProvider();

        HashSet<AgentType> granted =
        [
            AgentType.ScriptwriterAgent,
            AgentType.DirectorAgent,
            AgentType.ShotDirector,
            AgentType.NarrationWriter,
        ];

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            provider.GetTools(agentType).Select(f => f.Name).Contains("WebSearch").Should().Be(
                granted.Contains(agentType),
                $"WebSearch must be granted to {agentType} if and only if it is one of the four specified agent types");
            ToolGroupCatalog.GroupsFor(agentType).Contains(ToolGroup.WebSearch).Should().Be(granted.Contains(agentType));
        }

        InvokeGetAvailableToolsJson(AgentType.Custom).Should().NotContain("WebSearch");
        InvokeGetAvailableToolsJson(AgentType.ScriptwriterAgent).Should().Contain("WebSearch");
    }

    /// <summary>
    /// <see cref="ToolGroup.EventsRead"/> (progressive context) is read-only and free; it goes to exactly the planning
    /// agents that read an analysis, in the catalog, the runtime provider and the seeded display metadata alike.
    /// </summary>
    [Fact]
    public void EventsRead_is_granted_to_exactly_the_three_planning_agents_that_read_an_analysis()
    {
        AgentToolProvider provider = CreateAgentToolProvider();
        AgentType[] granted = [AgentType.VideoStoryEditor, AgentType.SoundDesigner, AgentType.MotionGraphicsPlanner, AgentType.MotionGraphicsDirector];

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            ToolGroupCatalog.GroupsFor(agentType).Contains(ToolGroup.EventsRead).Should().Be(granted.Contains(agentType), agentType.ToString());
            HashSet<string> names = provider.GetTools(agentType).Select(f => f.Name).ToHashSet();
            names.Contains("ListVideoEvents").Should().Be(granted.Contains(agentType), agentType.ToString());
            names.Contains("DescribeVideoWindow").Should().Be(granted.Contains(agentType), agentType.ToString());
        }

        ToolGroupCatalog.FunctionNamesFor(ToolGroup.EventsRead).Should().Equal("ListVideoEvents", "DescribeVideoWindow");
        InvokeGetAvailableToolsJson(AgentType.SoundDesigner).Should().Contain("ListVideoEvents");
        InvokeGetAvailableToolsJson(AgentType.Custom).Should().NotContain("ListVideoEvents");
    }

    /// <summary>
    /// <see cref="ToolGroup.MomentLook"/> spends (one small Vision call per look), so it goes to exactly the three agents
    /// that decide on moments, in the catalog, the runtime provider and the seeded display metadata alike, and to no
    /// custom agent. Each of their prompts says when to use it, in both copies.
    /// </summary>
    [Fact]
    public void MomentLook_is_granted_to_exactly_the_three_agents_that_decide_on_moments()
    {
        AgentToolProvider provider = CreateAgentToolProvider();
        AgentType[] granted = [AgentType.SoundDesigner, AgentType.VideoStoryEditor, AgentType.MotionDirector];
        FieldInfo builtInField = typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!;
        var builtIn = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)builtInField.GetValue(null)!;

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            ToolGroupCatalog.GroupsFor(agentType).Contains(ToolGroup.MomentLook).Should().Be(granted.Contains(agentType), agentType.ToString());
            provider.GetTools(agentType).Select(f => f.Name).Contains("LookAtMoment").Should().Be(granted.Contains(agentType), agentType.ToString());
            if (builtIn.TryGetValue(agentType, out var seeded))
                seeded.SystemPrompt.Contains("LookAtMoment").Should().Be(granted.Contains(agentType), agentType + " seeded prompt");
        }

        ToolGroupCatalog.FunctionNamesFor(ToolGroup.MomentLook).Should().Equal("LookAtMoment");
        InvokeGetAvailableToolsJson(AgentType.SoundDesigner).Should().Contain("LookAtMoment");
        InvokeGetAvailableToolsJson(AgentType.Custom).Should().NotContain("LookAtMoment");
        // Not a screened tool: it is an input to the model, and the screened set stays exactly six.
        AgentToolProvider.ScreenedToolNames.Should().NotContain("LookAtMoment");
    }

    /// <summary>
    /// Every agent type is classified: either it consumes a VideoAnalyze view (and then has the tool groups and the outline
    /// paragraph in BOTH its fallback and its seeded prompt) or it is a named non-consumer with the reason. A new agent type fails
    /// this test until somebody decides which.
    /// </summary>
    private static readonly Dictionary<AgentType, (ToolGroup[] Groups, bool ReadOnlyTurns)> ViewConsumers = new()
    {
        [AgentType.VideoStoryEditor] = ([ToolGroup.FootageRead, ToolGroup.EventsRead], true),
        [AgentType.VideoEditDirector] = ([ToolGroup.FootageRead], true),
        [AgentType.SoundDesigner] = ([ToolGroup.OfferListRead, ToolGroup.EventsRead], true),
        [AgentType.MusicSupervisor] = ([ToolGroup.OfferListRead], true),
        [AgentType.MotionGraphicsPlanner] = ([ToolGroup.FootageRead, ToolGroup.EventsRead], true),
        [AgentType.MotionGraphicsDirector] = ([ToolGroup.FootageRead, ToolGroup.EventsRead], true),
        [AgentType.NarrationWriter] = ([], true),
        [AgentType.ColorGradeDirector] = ([], true),
        [AgentType.Colorist] = ([], true),
        [AgentType.ShotDirector] = ([], true),
        [AgentType.OverlayPlanner] = ([], true),
        [AgentType.MotionDirector] = ([], true),
    };

    private static readonly Dictionary<AgentType, string> NonConsumers = new()
    {
        [AgentType.CodeStructureAnalyzer] = "reads project source code, not a video analysis",
        [AgentType.DependencyAnalyzer] = "reads project source code, not a video analysis",
        [AgentType.ComponentInventoryAnalyzer] = "reads project source code, not a video analysis",
        [AgentType.RouteAndApiAnalyzer] = "reads project source code, not a video analysis",
        [AgentType.DynamicsPlanner] = "is handed a purpose-built view (DynamicsPlannerViewBuilder), not the VideoAnalyze view: no outline, no footage tools",
        [AgentType.StyleAndThemeExtractor] = "reads project source code, not a video analysis",
        [AgentType.RemotionComponentTranslator] = "translates components; no video view",
        [AgentType.AnimationStrategyAgent] = "plans animation from a brief; no video view",
        [AgentType.DirectorAgent] = "script direction from words; no video view",
        [AgentType.ScriptwriterAgent] = "writes scripts from a brief; no video view",
        [AgentType.AuthorAgent] = "authors compositions from a brief; no video view",
        [AgentType.ReviewAgent] = "reviews written output; no video view",
        [AgentType.FileSummarizerAgent] = "summarises project files",
        [AgentType.Custom] = "user-defined; its tools are the user's choice",
        [AgentType.ExtractTransform] = "transforms extracted text",
        [AgentType.VideoTransform] = "deterministic transform wrapper; no model view",
        [AgentType.VideoReviewAgent] = "reads the compile's measured evidence (speechCoverage, sfx facts), not the analysis view",
        [AgentType.Assistant] = "chat assistant; reads facts straight from the artifact through its own tools",
        [AgentType.PickupPlanner] = "works on the narration script's words, not the footage view",
        [AgentType.NarrationTranslator] = "translates the narration script's words, not the footage view",
        [AgentType.CompositionReviser] = "revises a composition's code; no analysis view",
        [AgentType.GraphicDesigner] = "designs a graphic from a brief; no analysis view",
        [AgentType.TakeSelector] = "reads generated takes (sources[].clipId, measured shots); sources[] is never thinned and a take is seconds long",
        [AgentType.DailiesDirector] = "reads generated takes (sources[].clipId); sources[] is never thinned",
    };

    [Fact]
    public void Every_agent_type_is_classified_as_a_view_consumer_or_a_named_non_consumer()
    {
        foreach (AgentType agentType in Enum.GetValues<AgentType>())
            (ViewConsumers.ContainsKey(agentType) ^ NonConsumers.ContainsKey(agentType)).Should().BeTrue(
                $"{agentType} must be listed in exactly one of ViewConsumers / NonConsumers");
    }

    [Fact]
    public void Every_view_consumer_has_the_outline_tools_and_the_outline_paragraph_in_both_prompt_copies()
    {
        AgentToolProvider provider = CreateAgentToolProvider();
        FieldInfo builtInField = typeof(DatabaseSeeder).GetField("BuiltInAgents", BindingFlags.NonPublic | BindingFlags.Static)!;
        var builtIn = (Dictionary<AgentType, (string Name, string Description, string SystemPrompt, string Color)>)builtInField.GetValue(null)!;
        Assembly engine = typeof(AgentToolProvider).Assembly;

        foreach ((AgentType agentType, (ToolGroup[] groups, _)) in ViewConsumers)
        {
            foreach (ToolGroup group in groups)
            {
                ToolGroupCatalog.GroupsFor(agentType).Should().Contain(group, agentType.ToString());
                HashSet<string> names = provider.GetTools(agentType).Select(f => f.Name).ToHashSet();
                names.Should().Contain(ToolGroupCatalog.FunctionNamesFor(group), agentType.ToString());
                InvokeGetAvailableToolsJson(agentType).Should().Contain(ToolGroupCatalog.FunctionNamesFor(group), agentType + " (seeded display)");
            }

            // Read-only: no consumer's outline tools can write.
            foreach (ToolGroup group in groups)
                ToolGroupCatalog.FunctionNamesFor(group).Should().NotContain(n => n.Contains("Write") || n.Contains("Render"));

            string seeded = builtIn[agentType].SystemPrompt;
            seeded.Should().Contain("\"outline\"", agentType + " seeded prompt");
            Type? agentClass = engine.GetTypes().FirstOrDefault(t => (t.Name == agentType + "Agent" || t.Name == agentType + "AgentImpl")
                && t.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static) is not null);
            agentClass.Should().NotBeNull(agentType + " must have a fallback prompt class");
            ((string)agentClass!.GetField("DefaultPrompt", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)
                .Should().Be(seeded, agentType + " fallback and seeded prompts are identical");
        }
    }

    [Fact]
    public void Room_charters_carry_the_outline_paragraph_and_the_graphics_room_turns_stay_read_only_with_footage_reads()
    {
        ReelBolt.WorkflowEngine.Execution.StepExecutors.EditRoomStepExecutor.EditRoomCharterPrompt.Should().Contain("\"outline\"");
        foreach (string room in new[] { "GraphicsRoomCharterPrompt", "ColorGradeRoomCharterPrompt" })
        {
            Type executor = room.StartsWith("Graphics") ? typeof(ReelBolt.WorkflowEngine.Execution.StepExecutors.GraphicsRoomStepExecutor)
                : typeof(ReelBolt.WorkflowEngine.Execution.StepExecutors.ColorGradeRoomStepExecutor);
            ((string)executor.GetField(room, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!).Should().Contain("\"outline\"", room);
        }

        FieldInfo allow = typeof(ReelBolt.WorkflowEngine.Execution.StepExecutors.GraphicsRoomStepExecutor)
            .GetField("RoomTurnToolAllowlist", BindingFlags.NonPublic | BindingFlags.Static)!;
        var names = (HashSet<string>)allow.GetValue(null)!;
        names.Should().Contain(ToolGroupCatalog.FunctionNamesFor(ToolGroup.FootageRead)).And.Contain(ToolGroupCatalog.FunctionNamesFor(ToolGroup.EventsRead));
        names.Should().NotContain(n => n.Contains("Render") || n.Contains("Write") || n.Contains("Sandbox"));
    }

    /// <summary>The sound designer reads the clip and track lists itself (and a section), never the transcript.</summary>
    [Fact]
    public void SoundDesigner_gets_the_offer_list_tools_and_not_the_transcript_reader()
    {
        AgentToolProvider provider = CreateAgentToolProvider();
        HashSet<string> names = provider.GetTools(AgentType.SoundDesigner).Select(f => f.Name).ToHashSet();

        names.Should().BeEquivalentTo(new[]
        {
            "ListProjectFiles", "ReadProjectFile", "ReadProjectFileLines", "GetProjectFileOutline", "SearchProjectFiles",
            "GetDeterministicContextFiles", "ListVideoEvents", "DescribeVideoWindow",
            "ListVideoSfxClips", "ListVideoMusic", "ReadVideoSection", "LookAtMoment", "FailWorkflow"
        });
        names.Should().NotContain("ReadVideoTranscript");
        InvokeGetAvailableToolsJson(AgentType.SoundDesigner).Should().Contain("ListVideoSfxClips");
        foreach (AgentType other in Enum.GetValues<AgentType>().Where(a => a != AgentType.SoundDesigner && a != AgentType.MusicSupervisor))
            ToolGroupCatalog.GroupsFor(other).Should().NotContain(ToolGroup.OfferListRead, other.ToString());
    }

    /// <summary>
    /// The two phase-4 words-only agents (docs/voiceover.md "Pickups" / "Dubs") carry exactly the
    /// NarrationWriter grant, pinned by name for the same reason that Fact pins it.
    /// </summary>
    [Theory]
    [InlineData(AgentType.PickupPlanner)]
    [InlineData(AgentType.NarrationTranslator)]
    public void Pickup_and_translation_agents_are_granted_exactly_read_only_project_context_and_FailWorkflow(AgentType agentType)
    {
        HashSet<string> realToolNames = CreateAgentToolProvider()
            .GetTools(agentType)
            .Select(f => f.Name)
            .ToHashSet();

        realToolNames.Should().BeEquivalentTo(
            new[]
            {
                "ListProjectFiles", "ReadProjectFile", "ReadProjectFileLines", "GetProjectFileOutline",
                "SearchProjectFiles", "GetDeterministicContextFiles", "FailWorkflow"
            },
            $"{agentType} only writes words; the deterministic Voiceover step synthesizes the audio");
    }

    /// <summary>
    /// The render-hygiene tools (<c>GetCompositionInfo</c>, <c>RenderPreviewStills</c>) execute a
    /// composition. They ride with <see cref="ToolGroup.SandboxRender"/> to the agents that may render,
    /// and with <see cref="ToolGroup.SandboxPreview"/> to exactly one more: the ReviewAgent, which looks
    /// at the Author's frames (read-only stills, no upload) — and nowhere else. An agent holding the
    /// preview tools without the render group must hold no tool that writes, renders or uploads.
    /// </summary>
    [Fact]
    public void Composition_preview_tools_are_granted_where_rendering_is_plus_the_reviewer_and_nowhere_else()
    {
        AgentToolProvider provider = CreateAgentToolProvider();
        string[] previewTools = ["GetCompositionInfo", "RenderPreviewStills"];

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            HashSet<string> names = provider.GetTools(agentType).Select(f => f.Name).ToHashSet();
            bool mayRender = names.Contains("RenderVideoAndUploadToStorage");
            bool previewsOnly = agentType == AgentType.ReviewAgent;

            foreach (string tool in previewTools)
            {
                names.Contains(tool).Should().Be(mayRender || previewsOnly,
                    $"{tool} must be granted to {agentType} if and only if it may render " +
                    "(ToolGroup.SandboxRender) or it is the ReviewAgent (ToolGroup.SandboxPreview)");
            }
        }

        provider.GetTools(AgentType.AuthorAgent).Select(f => f.Name).Should().Contain(previewTools);

        HashSet<string> reviewer = provider.GetTools(AgentType.ReviewAgent).Select(f => f.Name).ToHashSet();
        reviewer.Should().Contain(previewTools);
        reviewer.Intersect(
        [
            "RunSandboxRemotionCommand", "RenderVideoAndUploadToStorage", "CompleteSandbox", "EnsureSandbox",
            "WriteSandboxFile", "EditSandboxFile", "ApplySandboxFileEdits", "InsertSandboxFileLines",
            "DeleteSandboxPath", "StageProjectFile", "StageFont", "InstallNpmPackages", "RunSandboxNpmScript",
            "WriteProjectFile", "EditProjectFile"
        ]).Should().BeEmpty("the reviewer looks at frames; it never writes, renders a deliverable or uploads");

        ToolGroupCatalog.FunctionNamesFor(ToolGroup.SandboxPreview).Should().BeEquivalentTo(previewTools);
        Enum.GetValues<AgentType>().Where(t => ToolGroupCatalog.GroupsFor(t).Contains(ToolGroup.SandboxPreview))
            .Should().Equal(AgentType.ReviewAgent);
    }

    /// <summary>
    /// Granting the reviewer <see cref="ToolGroup.SandboxPreview"/> rather than
    /// <see cref="ToolGroup.SandboxRender"/> is what keeps everything keyed on the render group
    /// unchanged for it: its credit hold does not scale with the output resolution, and the replay
    /// graph does not treat it as writing the sandbox (nor, since that is the same SandboxRender
    /// check, as producing a video).
    /// </summary>
    [Fact]
    public void The_reviewer_preview_grant_changes_no_hold_and_no_replay_dependency()
    {
        ToolGroupCatalog.GroupsFor(AgentType.ReviewAgent).Should().NotContain(ToolGroup.SandboxRender);
        ReelBolt.Shared.Metering.ResolutionHoldScaling.RendersOutput(StepType.Agent, AgentType.ReviewAgent).Should().BeFalse();
        ReelBolt.Shared.Metering.ResolutionHoldScaling.RendersOutput(StepType.Agent, AgentType.AuthorAgent).Should().BeTrue();

        WorkflowStep Agent(int order, AgentType type) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = order,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { AgentType = type }
        };

        // Step 4 reads its previous step (3) and the sandbox every earlier WRITER left. The Author (1)
        // is one; a reviewer (2) must not be, or step 4 would depend on it.
        ReelBolt.Shared.Workflows.Replay.WorkflowStepDependencyGraph graph = ReelBolt.Shared.Workflows.Replay.WorkflowStepDependencyGraph.Build(
        [
            Agent(1, AgentType.AuthorAgent),
            Agent(2, AgentType.ReviewAgent),
            Agent(3, AgentType.ReviewAgent),
            Agent(4, AgentType.ReviewAgent)
        ]);

        graph.DependsOn(4).Keys.Should().BeEquivalentTo([1, 3],
            "the reviewer reads the sandbox the Author left, and leaves no sandbox state another step reads");
    }

    /// <summary>
    /// <c>EditProjectFile</c> mutates the project, so it is <see cref="ToolGroup.ProjectWrite"/> and
    /// goes where <c>WriteProjectFile</c> goes; the two read tools are <see cref="ToolGroup.ProjectRead"/>
    /// and go to every real agent.
    /// </summary>
    [Fact]
    public void Targeted_project_file_tools_follow_their_read_and_write_groups()
    {
        AgentToolProvider provider = CreateAgentToolProvider();

        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            HashSet<string> names = provider.GetTools(agentType).Select(f => f.Name).ToHashSet();

            names.Contains("EditProjectFile").Should().Be(names.Contains("WriteProjectFile"),
                $"EditProjectFile and WriteProjectFile are both ToolGroup.ProjectWrite ({agentType})");

            names.Contains("ReadProjectFileLines").Should().Be(names.Contains("ReadProjectFile"),
                $"ReadProjectFileLines and ReadProjectFile are both ToolGroup.ProjectRead ({agentType})");
            names.Contains("GetProjectFileOutline").Should().Be(names.Contains("ReadProjectFile"),
                $"GetProjectFileOutline and ReadProjectFile are both ToolGroup.ProjectRead ({agentType})");
        }

        HashSet<string> author = provider.GetTools(AgentType.AuthorAgent).Select(f => f.Name).ToHashSet();
        author.Should().Contain(["EditProjectFile", "ReadProjectFileLines", "GetProjectFileOutline"]);
    }

    private static HashSet<string> InvokeGetAvailableToolsJson(AgentType agentType)
    {
        MethodInfo? method = typeof(DatabaseSeeder)
            .GetMethod("GetAvailableToolsJson", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("DatabaseSeeder must expose a private static GetAvailableToolsJson(AgentType) method");

        string json = (string)method!.Invoke(null, [agentType])!;
        string[] names = JsonSerializer.Deserialize<string[]>(json)!;
        return names.ToHashSet();
    }

    private static AgentToolProvider CreateAgentToolProvider()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        ProjectFileAgentTools projectFileTools = new(
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            Mock.Of<IHttpClientFactory>(),
            configuration,
            NullLogger<ProjectFileAgentTools>.Instance);

        ReactRemotionSandboxTools sandboxTools = new(
            Mock.Of<IHttpClientFactory>(),
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            Mock.Of<IProjectFileWorkspace>(),
            Mock.Of<IAmazonS3>(),
            configuration,
            NullLogger<ReactRemotionSandboxTools>.Instance,
            Mock.Of<IServiceScopeFactory>());

        WorkflowControlAgentTools workflowControlTools = new(
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            NullLogger<WorkflowControlAgentTools>.Instance);

        DesignSystemAgentTools designSystemTools = new(
            Mock.Of<IWorkflowExecutionContextAccessor>(),
            NullLogger<DesignSystemAgentTools>.Instance);

        return new AgentToolProvider(
            projectFileTools, sandboxTools, workflowControlTools, designSystemTools, WebSearchAgentToolsTests.CreateInert(),
            BlockAgentToolsTests.CreateInert(),
            new VideoEventsAgentTools(Mock.Of<IWorkflowExecutionContextAccessor>(), Mock.Of<IServiceScopeFactory>(), Mock.Of<IProjectFileWorkspace>()),
            MomentLookAgentToolsTests.CreateInert());
    }
}
