using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.Lint.LintKit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows.Lint;

/// <summary>WL022: a censor label tracked only with Segmentation silently matches nothing (run 04ef3b0a).</summary>
public class Wl022CensorTests
{
    private static List<WorkflowStep> Steps(string tracks, string compileExtra) =>
    [
        Of(1, StepType.VideoAnalyze, "{\"version\":1,\"source\":{\"kind\":\"ProjectFile\"},\"transcription\":\"Off\",\"trackObjects\":" + tracks + "}"),
        Compile(2, "{\"version\":1,\"analysisStepOrder\":1" + compileExtra + "}"),
    ];

    private const string Seg = """[{"label":"digital speedometer","method":"Segmentation"}]""";
    private const string Censor = ",\"censorLabels\":[\"digital speedometer\"]";

    [Fact]
    public async Task Fires_for_a_segmentation_only_label()
    {
        LintFinding f = (await LintAsync(Steps(Seg, Censor))).Rule("WL022").Should().ContainSingle().Which;
        f.Severity.Should().Be(LintSeverity.Warning);
        f.StepOrder.Should().Be(2);
        f.Message.Should().Contain("digital speedometer").And.Contain("won't apply").And.Contain("region effect");
    }

    [Fact]
    public async Task Is_silent_when_a_feature_track_carries_the_label()
    {
        string tracks = """[{"label":"digital speedometer","method":"Segmentation"},{"label":"Digital Speedometer","method":"Feature"}]""";
        (await LintAsync(Steps(tracks, Censor))).Rule("WL022").Should().BeEmpty();
        (await LintAsync(Steps("""[{"label":"digital speedometer"}]""", Censor))).Rule("WL022").Should().BeEmpty();
    }

    [Fact]
    public async Task Is_silent_without_censor_labels()
    {
        (await LintAsync(Steps(Seg, ""))).Rule("WL022").Should().BeEmpty();
        (await LintAsync(Steps(Seg, ",\"censorLabels\":[]"))).Rule("WL022").Should().BeEmpty();
    }
}
