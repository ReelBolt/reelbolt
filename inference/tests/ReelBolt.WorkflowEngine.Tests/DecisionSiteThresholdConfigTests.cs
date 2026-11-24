using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for the per-site DecisionGate config surface on <see cref="AgentStepExecutor"/>.
///
/// <para>
/// Resolution order for <c>AcceptAt</c>/<c>MinMargin</c>, per question:
/// <c>Agents:&lt;Agent&gt;:DecisionGate:Sites:&lt;QuestionName&gt;:AcceptAt|MinMargin</c> →
/// <c>Agents:&lt;Agent&gt;:DecisionGate:AcceptAt|MinMargin</c> (the pre-existing key, unchanged) →
/// <see cref="DecisionGateConfig"/>'s own phase-1 defaults (0.85 / 0.25).
/// </para>
///
/// <para>
/// The load-bearing property is that resolution is <b>per question</b>: a site override must not
/// leak onto its siblings, which is why these tests always assert the sibling's value in the same
/// call as the overridden one. The defaults (0.85 / 0.25) are the phase-1 fallback and are
/// deliberately <i>not</i> described as calibrated — the install has no Decision provider and no
/// calibration rows, so nothing here could have been tuned (decisions.md §3).
/// </para>
///
/// <para>
/// The last two tests are a guard on the shipped file rather than on the resolver: the whole
/// per-site surface must stay inert — no live <c>DecisionGate</c> key anywhere and every example
/// commented out at <c>Mode: Off</c>.
/// </para>
/// </summary>
public sealed class DecisionSiteThresholdConfigTests
{
    private const string Colorist = "Colorist";
    private const string MusicSupervisor = "MusicSupervisor";

    // The question names exactly as BuildColoristQuestions / BuildMusicSupervisorQuestions pose
    // them to the gate — the site key is keyed on this name, never on the Site string.
    private const string Look = "Look";
    private const string Strength = "Strength";
    private const string Intensity = "Intensity";

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    private static IConfiguration EmptyConfig() => Config();

    private static string AppSettingsPath() =>
        Path.Combine(AppContext.BaseDirectory, "WorkflowEngine.appsettings.json");

    private static IConfigurationRoot LoadRealAppSettings() =>
        new ConfigurationBuilder().AddJsonFile(AppSettingsPath(), optional: false).Build();

    private static IEnumerable<string> AllLeafPaths(IConfigurationSection section) =>
        section.GetChildren().SelectMany(child =>
            child.Value is not null
                ? [child.Path]
                : AllLeafPaths(child));

    private static DecisionGateConfig Resolve(IConfiguration configuration, string agent, string? questionName) =>
        AgentStepExecutor.ResolveDecisionGateConfig(configuration, agent, questionName);

    // ---------------------------------------------------------------------
    // The site layer wins, for its own question only
    // ---------------------------------------------------------------------

    [Fact]
    public void Site_key_overrides_the_agent_key_for_that_question_only()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.60"),
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"));

        Resolve(config, Colorist, Look).AcceptAt.Should().Be(0.95);
        Resolve(config, Colorist, Strength).AcceptAt.Should().Be(0.60,
            "a site override is scoped to its own question and must not leak onto its siblings");
    }

    [Fact]
    public void Site_keys_for_two_questions_resolve_independently()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.60"),
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"),
            ("Agents:Colorist:DecisionGate:Sites:Strength:AcceptAt", "0.70"));

        Resolve(config, Colorist, Look).AcceptAt.Should().Be(0.95);
        Resolve(config, Colorist, Strength).AcceptAt.Should().Be(0.70);
        Resolve(config, Colorist, "ShadowTone").AcceptAt.Should().Be(0.60);
    }

    [Fact]
    public void Site_key_is_namespaced_by_agent()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.60"),
            ("Agents:MusicSupervisor:DecisionGate:Sites:Look:AcceptAt", "0.95"));

        Resolve(config, Colorist, Look).AcceptAt.Should().Be(0.60,
            "the site key lives under its own agent's section");
        Resolve(config, MusicSupervisor, Look).AcceptAt.Should().Be(0.95);
    }

    // ---------------------------------------------------------------------
    // Fallback 1: an absent site key is not an error
    // ---------------------------------------------------------------------

    [Fact]
    public void Absent_site_key_falls_back_to_the_agent_level_key()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.60"),
            ("Agents:Colorist:DecisionGate:MinMargin", "0.10"),
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"));

        DecisionGateConfig resolved = Resolve(config, Colorist, Strength);

        resolved.AcceptAt.Should().Be(0.60);
        resolved.MinMargin.Should().Be(0.10);
    }

    [Fact]
    public void Site_key_for_one_threshold_leaves_the_other_on_the_agent_level_key()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.60"),
            ("Agents:Colorist:DecisionGate:MinMargin", "0.10"),
            ("Agents:Colorist:DecisionGate:Sites:Look:MinMargin", "0.40"));

        DecisionGateConfig resolved = Resolve(config, Colorist, Look);

        resolved.AcceptAt.Should().Be(0.60, "the site layer is per key, not per question");
        resolved.MinMargin.Should().Be(0.40);
    }

    // ---------------------------------------------------------------------
    // Fallback 2: both absent lands on the phase-1 defaults
    // ---------------------------------------------------------------------

    [Fact]
    public void Site_and_agent_keys_both_absent_fall_back_to_the_phase_one_defaults()
    {
        DecisionGateConfig defaults = new();

        DecisionGateConfig resolved = Resolve(EmptyConfig(), Colorist, Look);

        resolved.AcceptAt.Should().Be(0.85);
        resolved.MinMargin.Should().Be(0.25);
        resolved.AcceptAt.Should().Be(defaults.AcceptAt);
        resolved.MinMargin.Should().Be(defaults.MinMargin);
    }

    [Fact]
    public void A_site_key_on_another_question_does_not_count_as_present_for_this_one()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"));

        DecisionGateConfig resolved = Resolve(config, Colorist, Strength);

        resolved.AcceptAt.Should().Be(0.85);
        resolved.MinMargin.Should().Be(0.25);
    }

    [Fact]
    public void Null_question_name_resolves_the_agent_level_config_with_no_site_layer()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:AcceptAt", "0.60"),
            ("Agents:Colorist:DecisionGate:Sites:Look:AcceptAt", "0.95"));

        Resolve(config, Colorist, null).AcceptAt.Should().Be(0.60,
            "one config covers every question on the call, so no single site's override may be applied to it");
    }

    // ---------------------------------------------------------------------
    // ProviderId
    // ---------------------------------------------------------------------

    [Fact]
    public void ProviderId_is_read_from_the_agent_level_key_when_present()
    {
        var providerId = Guid.NewGuid();
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:ProviderId", providerId.ToString()));

        Resolve(config, Colorist, Look).ProviderId.Should().Be(providerId);
    }

    [Fact]
    public void ProviderId_is_null_when_absent_so_the_capability_default_still_resolves()
    {
        IConfiguration config = Config(("Agents:Colorist:DecisionGate:Mode", "Shadow"));

        Resolve(config, Colorist, Look).ProviderId.Should().BeNull();
    }

    [Fact]
    public void ProviderId_is_namespaced_by_agent_and_not_inherited_across_agents()
    {
        var providerId = Guid.NewGuid();
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:ProviderId", providerId.ToString()));

        Resolve(config, MusicSupervisor, Intensity).ProviderId.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // Mode stays agent-level, and is read exactly as before
    // ---------------------------------------------------------------------

    [Fact]
    public void Mode_is_the_agent_level_key_and_has_no_site_layer()
    {
        IConfiguration config = Config(
            ("Agents:Colorist:DecisionGate:Mode", "Shadow"),
            ("Agents:Colorist:DecisionGate:Sites:Look:Mode", "Gate"));

        Resolve(config, Colorist, Look).Mode.Should().Be(DecisionGateMode.Shadow);
        Resolve(config, Colorist, Strength).Mode.Should().Be(DecisionGateMode.Shadow);
    }

    [Fact]
    public void Mode_defaults_to_off_when_nothing_is_configured()
    {
        Resolve(EmptyConfig(), Colorist, Look).Mode.Should().Be(DecisionGateMode.Off);
    }

    // ---------------------------------------------------------------------
    // The shipped file stays inert
    // ---------------------------------------------------------------------

    [Fact]
    public void Shipped_appsettings_has_no_live_DecisionGate_key()
    {
        IConfigurationRoot config = LoadRealAppSettings();

        AllLeafPaths(config.GetSection("Agents"))
            .Where(path => path.Contains("DecisionGate", StringComparison.Ordinal))
            .Should().BeEmpty(
                "phase 2 ships the DecisionGate surface commented out only — every live example would enable the gate");
    }

    [Fact]
    public void Shipped_appsettings_examples_are_commented_out_and_every_Mode_example_is_Off()
    {
        string raw = File.ReadAllText(AppSettingsPath());

        string[] liveDecisionGateLines = raw
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Contains("DecisionGate", StringComparison.Ordinal))
            .Where(line => !line.StartsWith("//", StringComparison.Ordinal))
            .ToArray();

        liveDecisionGateLines.Should().BeEmpty(
            "the new keys ship commented out; a live DecisionGate key in appsettings.json is a shipped enablement");

        string[] modeLines = raw
            .Split('\n')
            .Where(line => line.Contains("\"Mode\":", StringComparison.Ordinal))
            .ToArray();

        modeLines.Should().NotBeEmpty("the commented example block must actually document Mode");
        modeLines.Should().OnlyContain(
            line => line.Contains("\"Off\"", StringComparison.Ordinal),
            "every shipped DecisionGate example reads Mode: Off");
    }
}
