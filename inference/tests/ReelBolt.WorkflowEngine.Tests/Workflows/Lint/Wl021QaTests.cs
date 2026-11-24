using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>QA probes (voiceover-doubled): WL021 on the owner's full 10-step shape and on camera derush shapes.</summary>
public class Wl021QaTests
{
    private const string Voice = """{"source":{"kind":"Inline","lines":[{"text":"a","startSec":0},{"text":"b","startSec":3}]}}""";

    private static string CompileJson(string extra = "") =>
        "{\"version\":1,\"keepWholeSources\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":7,\"voiceoverMode\":\"OverMusic\","
        + "\"analysisStepOrder\":8,\"enableMusic\":true,\"musicDuckingMode\":\"SpeechEnvelope\"" + extra + "}";

    private static List<WorkflowStep> CeoTenSteps(string compileExtra = "") =>
    [
        Of(1, StepType.DesignSystem),
        Agent(2, AgentType.AuthorAgent), Agent(3, AgentType.AuthorAgent), Agent(4, AgentType.AuthorAgent), Agent(5, AgentType.AuthorAgent),
        Of(6, StepType.Revise, agent: AgentType.CompositionReviser),
        Voiceover(7, Voice),
        Of(8, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"StepOutput","stepOrder":6},"transcription":"Off"}"""),
        Compile(9, CompileJson(compileExtra)),
        ReviewLoopAt(10, 9),
    ];

    [Fact]
    public async Task WL021_fires_once_on_the_ceo_ten_step_shape_and_clears_with_mute()
    {
        IReadOnlyList<LintFinding> findings = await LintAsync(CeoTenSteps());
        findings.Rule("WL021").Should().ContainSingle().Which.StepOrder.Should().Be(9);

        (await LintAsync(CeoTenSteps(",\"muteSourceAudio\":true"))).Rule("WL021").Should().BeEmpty();
        (await LintAsync(CeoTenSteps().Select(x => x.StepOrder == 9 ? Compile(9, CompileJson().Replace("\"OverMusic\"", "2")) : x).ToList())).Rule("WL021").Should().BeEmpty("ordinal 2 = ReplaceDialogue");
        (await LintAsync(CeoTenSteps().Select(s => s).ToList())).Rule("WL021").Should().ContainSingle();
    }

    [Fact]
    public async Task WL021_is_quiet_on_camera_derush_voiceover_shapes()
    {
        // Camera upload -> analyze -> decision agent -> voiceover -> compile OverMusic: the camera sound is the user's.
        List<WorkflowStep> derush =
        [
            Of(1, StepType.VideoAnalyze, """{"version":1,"source":{"kind":"ProjectFile","projectFileId":"00000000-0000-0000-0000-000000000001"}}"""),
            Agent(2, AgentType.AuthorAgent),
            Voiceover(3, Voice),
            Compile(4, "{\"version\":1,\"keepWholeSources\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":3,\"analysisStepOrder\":1}"),
        ];
        (await LintAsync(derush)).Rule("WL021").Should().BeEmpty();

        List<WorkflowStep> brief =
        [
            Voiceover(1, Voice),
            Of(2, StepType.VideoAnalyze, """{"version":1,"sources":[{"kind":"BriefFiles"}]}"""),
            Compile(3, "{\"version\":1,\"keepWholeSources\":true,\"enableVoiceover\":true,\"voiceoverStepOrder\":1,\"analysisStepOrder\":2}"),
        ];
        (await LintAsync(brief)).Rule("WL021").Should().BeEmpty();
    }
}
