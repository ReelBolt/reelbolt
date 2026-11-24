using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;

public sealed class DesignImportGuardTests
{
    private static JsonObject Design(params string[] colorNames)
    {
        JsonObject document = DesignDocument.New("Test");
        foreach (string name in colorNames)
        {
            DesignDocument.SetToken(
                document, "color." + name, "color",
                new JsonObject { ["colorSpace"] = "srgb", ["hex"] = "#112233" },
                new TokenProvenance("manual", null, null, null), 1.0);
        }

        return document;
    }

    [Fact]
    public void An_import_with_no_tokens_warns_even_without_a_current_design()
    {
        var warnings = DesignImportGuard.Assess(DesignDocument.New("Empty"), current: null, currentVersionNumber: null);

        ImportDiagnostic warning = warnings.Should().ContainSingle().Subject;
        warning.Code.Should().Be(DesignImportGuard.ZeroTokensCode);
        warning.Severity.Should().Be(ImportDiagnosticSeverity.Warn);
        warning.Message.Should().Contain("no colours, fonts or other design tokens");
    }

    [Fact]
    public void A_voice_only_import_counts_as_no_tokens()
    {
        JsonObject voiceOnly = DesignDocument.New("Prose");
        voiceOnly["voice"] = new JsonObject { ["summary"] = "Friendly." };

        DesignImportGuard.Assess(voiceOnly, null, null).Should().ContainSingle(d => d.Code == DesignImportGuard.ZeroTokensCode);
    }

    [Fact]
    public void A_zero_token_import_over_a_good_design_names_the_version_it_would_replace()
    {
        var warnings = DesignImportGuard.Assess(DesignDocument.New(), Design("a", "b"), currentVersionNumber: 3);

        // One warning, not two: the loss is already the headline.
        ImportDiagnostic warning = warnings.Should().ContainSingle().Subject;
        warning.Code.Should().Be(DesignImportGuard.ZeroTokensCode);
        warning.Message.Should().Contain("Version 3 has 2");
    }

    [Fact]
    public void An_import_that_loses_tokens_flags_how_many_of_how_many()
    {
        var warnings = DesignImportGuard.Assess(Design("a"), Design("a", "b", "c"), currentVersionNumber: 2);

        ImportDiagnostic warning = warnings.Should().ContainSingle().Subject;
        warning.Code.Should().Be(DesignImportGuard.LosesTokensCode);
        warning.Message.Should().Contain("2 of the 3 tokens in version 2");
    }

    [Fact]
    public void An_import_that_keeps_or_adds_tokens_is_quiet()
    {
        DesignImportGuard.Assess(Design("a", "b", "z"), Design("a", "b"), 1).Should().BeEmpty();
        DesignImportGuard.Assess(Design("a"), null, null).Should().BeEmpty();
    }

    [Fact]
    public void A_null_import_never_throws()
    {
        DesignImportGuard.Assess(null, Design("a"), 1).Select(d => d.Code)
            .Should().Equal(DesignImportGuard.ZeroTokensCode);
    }
}
