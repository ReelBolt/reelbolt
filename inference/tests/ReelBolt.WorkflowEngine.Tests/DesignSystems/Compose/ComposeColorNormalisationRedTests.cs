using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose;

public class ComposeColorNormalisationRedTests
{
    [Fact]
    public void Mapper_output_for_a_staging_shaped_style_validates_as_a_design_document()
    {
        var design = LegacyAnalyzerMapper.DesignFrom(StagingLikeStyleFixture.Build())!;

        DesignValidationResult result = DesignDocumentValidator.Validate(
            design, new DesignValidationContext(_ => true, new System.Collections.Generic.HashSet<string>()));

        result.Violations.Select(v => v.Code + " " + v.Path).Should().BeEmpty();
    }
}
