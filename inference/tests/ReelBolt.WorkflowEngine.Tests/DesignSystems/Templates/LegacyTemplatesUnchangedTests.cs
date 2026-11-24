using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Templates;

/// <summary>
/// Pins the byte-level serialization of the three legacy promo templates. Phase 2 adds new
/// template steps and a trailing record member to <see cref="WorkflowTemplateStepDefinition"/>,
/// so this test proves the old templates did not change.
/// </summary>
public class LegacyTemplatesUnchangedTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // Re-pinned deliberately when every shipped review loop gained WorkflowTemplateCatalog.ShippedReviewLoopConfigJson
    // (docs/design/review-loop-adjustments.md): the review step's config is the only change to these three templates.
    // Re-pinned again when the shipped config gained visualCheck.qualityChecks (render quality checks): same single change.
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["quick-win-promo"] = "b5d53715ca57e4899645ee591fbef9081242be10f327e53f0d19e74d888ed7a6",
        ["quick-win-promo-plates"] = "bdabbfb37052d0a506d5a908920bddfb9eb5377822cdb8a52c9eb559a6143e23",
        ["lean-context-promo"] = "005a1fb3575689d2f8fcd2ec71ac74c6aa49acc9117cc5d0d86f70b2c27f583c"
    };

    private static string Hash(string key)
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.BaseByKey(key);
        template.Should().NotBeNull($"template '{key}' must be registered in WorkflowTemplateCatalog");
        // The curated app manifest is presentation only (docs/workflow-apps.md): it never changes the steps or the
        // version, so the pin is over the definition without it.
        string json = JsonSerializer.Serialize(template! with { AppManifest = null }, Options);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("quick-win-promo")]
    [InlineData("quick-win-promo-plates")]
    [InlineData("lean-context-promo")]
    public void Legacy_template_definition_is_byte_stable(string key)
    {
        string actual = Hash(key);
        actual.Should().Be(Expected[key], $"template {key} changed; actual hash is {actual}");
    }

    [Fact]
    public void Legacy_templates_keep_their_shape()
    {
        WorkflowTemplateCatalog.BaseByKey("quick-win-promo")!.Steps.Should().HaveCount(11);
        WorkflowTemplateCatalog.BaseByKey("quick-win-promo")!.AutoCreateOnProject.Should().BeTrue();
        WorkflowTemplateCatalog.BaseByKey("quick-win-promo-plates")!.Steps.Should().HaveCount(12);
        WorkflowTemplateCatalog.BaseByKey("lean-context-promo")!.AutoCreateOnProject.Should().BeFalse();
    }
}
