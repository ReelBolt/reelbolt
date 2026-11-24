using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;
using ModelStepType = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.StepType;

/// <summary>
/// <see cref="DesignSystemStepConfig.Validate"/> coverage: every rule returns ALL its errors at once
/// and never throws, so a save-time 400 body can list them together.
/// </summary>
public sealed class DesignSystemStepConfigTests
{
    [Theory]
    [MemberData(nameof(ValidConfigs))]
    public void A_valid_config_for_each_op_returns_no_errors(DesignSystemStepConfig config)
    {
        config.Validate().Should().BeEmpty();
    }

    public static TheoryData<DesignSystemStepConfig> ValidConfigs() =>
    [
        new DesignSystemStepConfig(DesignSystemOp.Parse, Parse: new()),
        new DesignSystemStepConfig(DesignSystemOp.Compose, Compose: new(ParseStepOrder: 1)),
        new DesignSystemStepConfig(DesignSystemOp.Require, Require: new())
    ];

    [Fact]
    public void A_version_other_than_the_current_one_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(DesignSystemOp.Parse, Version: 2, Parse: new()).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("version");
    }

    [Fact]
    public void An_unknown_op_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig((DesignSystemOp)999).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("op");
    }

    [Fact]
    public void A_Parse_op_without_parse_options_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(DesignSystemOp.Parse).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("parse");
    }

    [Fact]
    public void A_Parse_op_carrying_compose_options_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(), Compose: new(ParseStepOrder: 1)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("compose");
    }

    [Fact]
    public void A_Compose_op_without_compose_options_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(DesignSystemOp.Compose).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("compose");
    }

    [Fact]
    public void A_Require_op_that_requires_nothing_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Require, Require: new(RequireDesign: false, RequireProductMap: false)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("requireDesign");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public void Parse_maxFiles_outside_its_range_is_rejected(int maxFiles)
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(MaxFiles: maxFiles)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("maxFiles");
    }

    [Fact]
    public void Parse_maxBytesPerFile_below_its_floor_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(MaxBytesPerFile: 1023)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("maxBytesPerFile");
    }

    [Fact]
    public void Parse_maxTotalBytes_above_its_ceiling_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(MaxTotalBytes: 67108865)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("maxTotalBytes");
    }

    [Fact]
    public void Parse_extraGlobs_with_too_many_entries_is_rejected()
    {
        List<string> globs = [.. System.Linq.Enumerable.Repeat("src/**", 21)];

        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(ExtraGlobs: globs)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("extraGlobs");
    }

    [Fact]
    public void Parse_extraGlobs_with_an_over_long_entry_is_rejected()
    {
        List<string> globs = [new string('a', 101)];

        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(ExtraGlobs: globs)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("extraGlobs");
    }

    [Fact]
    public void Parse_extraGlobs_with_an_illegal_character_is_rejected()
    {
        List<string> globs = ["bad!"];

        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Parse, Parse: new(ExtraGlobs: globs)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("extraGlobs");
    }

    [Fact]
    public void Compose_parseStepOrder_below_one_is_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Compose, Compose: new(ParseStepOrder: 0)).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("parseStepOrder");
    }

    [Fact]
    public void Compose_fallback_step_orders_below_one_are_rejected()
    {
        List<string> errors = new DesignSystemStepConfig(
            DesignSystemOp.Compose,
            Compose: new(ParseStepOrder: 1, DesignFallbackStepOrders: [0])).Validate();

        errors.Should().ContainSingle();
        errors[0].Should().Contain("designFallbackStepOrders");
    }

    [Fact]
    public void The_schema_exporter_discovers_the_DesignSystem_config_by_its_record_name()
    {
        var result = new StepConfigSchemaExporter().GetSchema(ModelStepType.DesignSystem);

        result.HasConfig.Should().BeTrue();
        result.ConfigTypeName.Should().Be("DesignSystemStepConfig");
    }
}
