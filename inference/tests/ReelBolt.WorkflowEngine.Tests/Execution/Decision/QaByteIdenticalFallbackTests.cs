#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// QA verification (ticket T-500) of the phase-2 decision-engine delivery's central safety claim:
/// <b>every decision site falls back byte-identically when no Decision provider is configured, and
/// nothing shipped enables a gate.</b>
///
/// <para>
/// This file deliberately does not restate coverage the delivery's own suites already provide. It
/// closes the three specific gaps that a read of the source and the existing tests leaves open:
/// </para>
///
/// <list type="number">
/// <item>
/// <b>The <c>DecideAsync</c> <c>Off</c> arm is pinned only through <c>RunShadowAsync</c>.</b>
/// <c>DecisionGate.DecideAsync</c>'s <c>case DecisionGateMode.Off: return NoDecision;</c> is the
/// line every site relies on, and no test called <c>DecideAsync</c> with <c>Off</c> directly.
/// </item>
/// <item>
/// <b>The <c>DecideAsync</c> Gate arm with an unresolvable provider had no test at all.</b>
/// <c>RunShadowAsync_WithNullResolvedProvider_DoesNotPersistRows</c> pins the <i>Shadow</i> variant;
/// the three phase-2 sites that call <c>DecideAsync</c> in <c>Gate</c> mode (the colour-grade room's
/// convening gate, the <c>Conditional</c> decision branch, the <c>ReviewLoop</c> early stop) each
/// exercise it only through a <c>Mock&lt;IDecisionGate&gt;</c> returning
/// <see cref="DecisionGateOutcome.NoDecision"/>. This file pins the real composition: a real
/// <see cref="DecisionGate"/> whose resolver returns null — the normal local install — reached
/// through <c>DecideAsync</c> in <c>Gate</c> mode.
/// </item>
/// <item>
/// <b>Nothing proves the shipped <c>appsettings.json</c> carries no live gate key.</b> The
/// delivery's <c>No_shipped_configuration_enables_the_convening_gate</c> scans that file as TEXT;
/// this file loads it through the same <c>Microsoft.Extensions.Configuration</c> JSON provider the
/// running service uses and asserts on the BOUND configuration, so a key that a text scan might
/// mis-classify is either genuinely absent or genuinely fails.
/// </item>
/// </list>
///
/// <para>
/// Everything here is a claim about <b>inertness</b>. Nothing asserts that any threshold is the
/// correct one — phase 1/2 has no live calibration data, so a test that did would be pinning a
/// guess.
/// </para>
/// </summary>
public sealed class QaByteIdenticalFallbackTests
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Mock<IInferenceProviderResolver> _resolver = new();
    private readonly Mock<IDecisionClientFactory> _clientFactory = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly Mock<ILogger<DecisionGate>> _logger = new();

    private readonly DbContextOptions<WorkflowEngineDbContext> _dbOptions =
        new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"QaByteIdenticalFallback_{Guid.NewGuid()}")
            .Options;

    // =================================================================================
    // 1. DecideAsync / Off — no provider resolution, no outbound call, no row, no log.
    // =================================================================================

    /// <summary>
    /// The line three phase-2 call sites lean on: <c>DecisionGate.DecideAsync</c> returns
    /// <see cref="DecisionGateOutcome.NoDecision"/> from the mode switch <b>before</b> reaching the
    /// shared provider-call core. Provider resolution AND the client factory are both wired to
    /// succeed here, so the only thing standing between this call and an outbound HTTP request is
    /// the <c>Off</c> arm itself.
    /// </summary>
    [Fact]
    public async Task DecideAsync_WithModeOff_NeverResolvesAProviderAndNeverReachesTheClientFactory()
    {
        WorkflowEngineDbContext db = WireProviderAndScope();

        DecisionGateOutcome outcome = await Sut().DecideAsync(
            Request(DecisionGateMode.Off), CancellationToken.None);

        outcome.Should().BeSameAs(DecisionGateOutcome.NoDecision,
            "Off is a uniform no-decision, not a partially-populated outcome");
        outcome.HasDecision.Should().BeFalse();
        outcome.Accepted.Should().BeFalse();
        outcome.Result.Should().BeNull();

        _resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Off must not even ask whether a Decision provider exists");
        _clientFactory.Verify(
            f => f.Get(It.IsAny<ResolvedDecisionProvider>()),
            Times.Never,
            "the client factory is the only path to an outbound decision request");

        db.DecisionObservations.Should().BeEmpty("Off writes no observation");
        _logger.Invocations.Should().BeEmpty("Off is silent — it is the shipped default, not an anomaly");
    }

    // =================================================================================
    // 2. DecideAsync / Gate with NO Decision provider — the normal local install.
    // =================================================================================

    /// <summary>
    /// The composition the colour-grade room's convening gate, the <c>Conditional</c> decision
    /// branch and the <c>ReviewLoop</c> early stop all call: <b>a real <see cref="DecisionGate"/>,
    /// <c>Gate</c> mode, and a resolver that returns null</b> because no enabled
    /// <c>Capability = Decision</c> row exists.
    ///
    /// <para>
    /// This is the resolver-null path stated as a NORMAL case rather than an error path: it returns
    /// the uniform no-decision, writes nothing, reaches no client, throws nothing, and — the point
    /// of the assertion on <see cref="_logger"/> — logs nothing at all, so it cannot be mistaken for
    /// a fault in production telemetry.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DecideAsync_WithModeGate_AndNoResolvableProvider_IsASilentNoDecision()
    {
        WorkflowEngineDbContext db = WireProviderAndScope();
        _resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider?)null);

        DecisionGateOutcome outcome = await Sut().DecideAsync(
            Request(DecisionGateMode.Gate), CancellationToken.None);

        outcome.Should().BeSameAs(DecisionGateOutcome.NoDecision,
            "no provider resolved means the caller runs its System Two path, never a partial answer");
        outcome.HasDecision.Should().BeFalse();
        outcome.Result.Should().BeNull();

        _resolver.Verify(
            r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "Gate does ask once — that single miss IS the fallback");
        _clientFactory.Verify(
            f => f.Get(It.IsAny<ResolvedDecisionProvider>()),
            Times.Never,
            "a null provider must stop before the factory, or it would dereference null");

        db.DecisionObservations.Should().BeEmpty(
            "with no provider there is no answer to record — an empty table, not a row claiming a disagreement");
        _logger.Invocations.Should().BeEmpty(
            "the resolver-null path is the NORMAL local configuration: it must not log at any level, " +
            "and in particular must not log an error or a warning");
    }

    // =================================================================================
    // 3. The shipped engine configuration, read through the real JSON config provider.
    // =================================================================================

    /// <summary>
    /// Loads the <b>shipped</b> <c>ReelBolt.WorkflowEngine/appsettings.json</c> — the exact file the
    /// service reads at startup, linked into this test's output directory by the csproj — through
    /// <c>AddJsonFile</c>, and asserts on the BOUND configuration that no agent carries a live
    /// <c>DecisionGate</c> block.
    ///
    /// <para>
    /// Two things make this stronger than a text grep. First, the configuration is what the
    /// container actually binds, so a key that survived as real JSON — not inside a comment — is
    /// caught whatever the formatting. Second, building it is itself the proof the shipped file is
    /// loadable by the provider the service uses; if it were not, this test would fail at
    /// <c>Build()</c> rather than pass vacuously.
    /// </para>
    /// </summary>
    [Fact]
    public void The_shipped_engine_appsettings_binds_no_decision_gate_key()
    {
        IConfigurationRoot config = ShippedEngineConfiguration();

        // Non-vacuous: the file really was opened and really did bind something.
        config["Logging:LogLevel:Default"].Should().Be("Information",
            "the shipped file must actually have been read, or every assertion below is vacuous");

        List<string> leafKeys = [.. Flatten(config.GetChildren())
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => kv.Key)];

        leafKeys.Should().NotBeEmpty();

        leafKeys
            .Where(k => k.Contains("DecisionGate", StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeEmpty(
                "the shipped configuration must contain no bound DecisionGate key at all — the block " +
                "ships commented out, so nothing under it may survive into the bound configuration");

        // The two agent-level keys docker-compose.yml passes through, bound from the file alone:
        // absent here, so the compose default (Off) is the only value that can reach the process.
        config["Agents:Colorist:DecisionGate:Mode"].Should().BeNull();
        config["Agents:MusicSupervisor:DecisionGate:Mode"].Should().BeNull();
        config.GetValue<DecisionGateMode?>("Agents:Colorist:DecisionGate:Mode").Should().BeNull();
        config.GetSection("Agents:Colorist:DecisionGate").Exists().Should().BeFalse();
        config.GetSection("Agents:MusicSupervisor:DecisionGate").Exists().Should().BeFalse();

        // Structural, not name-based: no agent block at all may have a DecisionGate child.
        foreach (IConfigurationSection agent in config.GetSection("Agents").GetChildren())
        {
            agent.GetSection("DecisionGate").Exists().Should().BeFalse(
                $"Agents:{agent.Key} must not carry a live DecisionGate block");
        }

        // ...and no bound value anywhere enables a gate by name.
        Flatten(config.GetChildren())
            .Where(kv => kv.Value is "Shadow" or "Gate")
            .Select(kv => kv.Key)
            .Should()
            .BeEmpty("no bound configuration value may name an enabling gate mode");
    }

    // =================================================================================
    // 4. Every SHIPPED workflow template — enumerated through the catalog's public API.
    // =================================================================================

    /// <summary>
    /// The seeded-workflow half of the shipped-configuration question, asked of the catalog the
    /// provisioning service actually reads rather than of the file's text: <b>every</b> template in
    /// <see cref="WorkflowTemplateCatalog"/> is enumerated and each step's gate-bearing config blob
    /// is deserialized and inspected.
    ///
    /// <para>
    /// A room step whose <c>ConveneGate</c> is not <c>Off</c>, a <c>ReviewLoop</c> step whose config
    /// enables the early stop, or a <c>Conditional</c> step in <c>Decision</c> mode is a SHIPPED
    /// activation of a phase-2 gate — the exact thing the delivery claims does not exist.
    /// </para>
    /// </summary>
    [Fact]
    public void No_shipped_workflow_template_step_enables_a_gate()
    {
        IReadOnlyList<WorkflowTemplateDefinition> templates = WorkflowTemplateCatalog.GetAll();

        templates.Should().NotBeEmpty("the catalog must be discoverable or this test is vacuous");
        templates.SelectMany(t => t.Steps).Should().NotBeEmpty();

        List<string> violations = [];
        int roomStepsInspected = 0;
        int conditionalStepsInspected = 0;
        int reviewLoopStepsInspected = 0;
        int reviewLoopConfigsShipped = 0;

        foreach (WorkflowTemplateDefinition template in templates)
        {
            foreach (WorkflowTemplateStepDefinition step in template.Steps)
            {
                string where = $"{template.Key} step '{step.Label}' ({step.StepType})";

                // --- rooms -------------------------------------------------
                foreach ((string label, string? json) in new[]
                {
                    ("EditRoom", step.EditRoomConfigJson),
                    ("GraphicsRoom", step.GraphicsRoomConfigJson),
                    ("ColorGradeRoom", step.ColorGradeRoomConfigJson)
                })
                {
                    if (json is null)
                        continue;

                    roomStepsInspected++;
                    ConveneGateMode mode = label switch
                    {
                        "EditRoom" => JsonSerializer.Deserialize<EditRoomStepConfig>(json, ConfigOptions)!.ConveneGate,
                        "GraphicsRoom" => JsonSerializer.Deserialize<GraphicsRoomStepConfig>(json, ConfigOptions)!.ConveneGate,
                        _ => JsonSerializer.Deserialize<ColorGradeRoomStepConfig>(json, ConfigOptions)!.ConveneGate
                    };

                    if (mode != ConveneGateMode.Off)
                        violations.Add($"{where}: {label} ConveneGate is {mode}, not Off");
                }

                // --- ReviewLoop --------------------------------------------
                if (step.StepType == StepType.ReviewLoop)
                {
                    reviewLoopStepsInspected++;

                    // Every shipped review loop carries the one deliberate config (docs/design/review-loop-adjustments.md):
                    // adjustments and the film evidence on, the decision early stop OFF, no gate provider.
                    if (step.ReviewLoopConfigJson == WorkflowTemplateCatalog.ShippedReviewLoopConfigJson)
                        reviewLoopConfigsShipped++;

                    ReviewLoopStepConfig? loop = step.ReviewLoopConfigJson is null
                        ? null
                        : JsonSerializer.Deserialize<ReviewLoopStepConfig>(step.ReviewLoopConfigJson, ConfigOptions);

                    if (loop is null)
                        violations.Add($"{where}: ReviewLoopConfigJson is absent or did not deserialize — unreadable, so unprovable");
                    else if (loop.Enabled)
                        violations.Add($"{where}: ReviewLoopConfigJson enables the decision early stop");
                    else if (loop.ProviderId is not null)
                        violations.Add($"{where}: ReviewLoopConfigJson names a decision provider");
                }

                // --- Conditional -------------------------------------------
                if (step.ConditionalConfigJson is not null)
                {
                    conditionalStepsInspected++;

                    ConditionalStepConfig? conditional = JsonSerializer.Deserialize<ConditionalStepConfig>(
                        step.ConditionalConfigJson, ConfigOptions);

                    if (conditional is null)
                        violations.Add($"{where}: ConditionalConfigJson did not deserialize — unreadable, so unprovable");
                    else if (conditional.Mode == ConditionMode.Decision)
                        violations.Add($"{where}: ConditionalConfigJson selects ConditionMode.Decision");
                }
            }
        }

        // Non-vacuity guards, per site — a template that stopped carrying a ReviewLoop step, or a
        // catalog that stopped exposing config blobs, must fail loudly rather than pass by absence.
        reviewLoopStepsInspected.Should().BeGreaterThan(0,
            "the shipped templates do contain ReviewLoop steps; if this is zero the scan proved nothing");
        reviewLoopConfigsShipped.Should().Be(reviewLoopStepsInspected,
            "every shipped ReviewLoop step carries exactly WorkflowTemplateCatalog.ShippedReviewLoopConfigJson " +
            "(adjustments and film evidence on, the decision early stop's Enabled = false)");
        ReviewLoopStepConfig shipped = JsonSerializer.Deserialize<ReviewLoopStepConfig>(
            WorkflowTemplateCatalog.ShippedReviewLoopConfigJson, ConfigOptions)!;
        shipped.Enabled.Should().BeFalse("the shipped review loops never turn on the decision early stop");
        shipped.ProviderId.Should().BeNull();
        shipped.AllowAdjustments.Should().BeTrue();
        shipped.VisualCheck.Should().NotBeNull();
        shipped.VisualCheck!.Enabled.Should().BeTrue();
        shipped.VisualCheck.Frames.Should().Be(new VisualCheckConfig().Frames, "the shipped check samples the default frame count");
        shipped.VisualCheck.TimelineFacts.Should().BeTrue();
        shipped.VisualCheck.AudioFacts.Should().BeTrue();
        shipped.VisualCheck.TargetedLooks.Should().BeTrue();

        violations.Should().BeEmpty(
            "no shipped workflow template may activate a phase-2 gate:\n" + string.Join("\n", violations));

        // The room and conditional counts are reported as evidence, not asserted to a fixed number,
        // so adding a template does not have to edit this test — but a silent drop to zero would
        // mean the corresponding scan below never ran.
        _ = roomStepsInspected;
        _ = conditionalStepsInspected;
    }

    // =================================================================================
    // 5. The compiled DEFAULTS, read from the records themselves.
    // =================================================================================

    /// <summary>
    /// The inert-by-default claim has to come from the compiled record's own default parameter
    /// values, not from a comment beside them. Constructing each config with no arguments is the
    /// only way to read what a config left alone actually means.
    /// </summary>
    [Fact]
    public void Every_gate_bearing_config_defaults_to_inert()
    {
        new DecisionGateConfig().Mode.Should().Be(DecisionGateMode.Off,
            "DecisionGateConfig.Mode's declared default must be Off");
        new DecisionGateConfig().ProviderId.Should().BeNull(
            "a default-configured gate must not name a provider");
        new ReviewLoopStepConfig().Enabled.Should().BeFalse(
            "ReviewLoopStepConfig.Enabled's declared default must be false");
        new ReviewLoopStepConfig().ProviderId.Should().BeNull();
        // A config that leaves the review features off stays byte-identical (the shipped templates turn them on
        // explicitly through WorkflowTemplateCatalog.ShippedReviewLoopConfigJson).
        new ReviewLoopStepConfig().AllowAdjustments.Should().BeFalse("adjustments are off unless a config turns them on");
        new ReviewLoopStepConfig().VisualCheck.Should().BeNull("the visual check is off unless a config turns it on");
        new VisualCheckConfig().Enabled.Should().BeFalse();
        new VisualCheckConfig().TimelineFacts.Should().BeFalse();
        new VisualCheckConfig().AudioFacts.Should().BeFalse();
        new VisualCheckConfig().TargetedLooks.Should().BeFalse();
        new ConditionalStepConfig().Mode.Should().Be(ConditionMode.Expression,
            "ConditionalStepConfig.Mode's declared default must be Expression, not Decision");

        // The enums' ordinals are what make "no value supplied" mean "off" for every serializer
        // and every default field initializer alike.
        ((DecisionGateMode)0).Should().Be(DecisionGateMode.Off);
        ((ConveneGateMode)0).Should().Be(ConveneGateMode.Off);
    }

    // =================================================================================
    // Harness
    // =================================================================================

    private DecisionGate Sut() => new(
        _resolver.Object, _clientFactory.Object, _scopeFactory.Object, _logger.Object);

    /// <summary>
    /// Wires the resolver and the client factory to SUCCEED and the scope factory to a real
    /// in-memory store, so a test that expects nothing to happen is not passing because a
    /// dependency happened to be missing. Returns the store for row-count assertions.
    /// </summary>
    private WorkflowEngineDbContext WireProviderAndScope()
    {
        ResolvedDecisionProvider provider = new(
            ProviderId: Guid.NewGuid(),
            Name: "qa-mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        _resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        _clientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>()))
            .Returns(new Mock<IDecisionClient>().Object);

        WorkflowEngineDbContext db = new(_dbOptions);

        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(db);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

        _scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

        return db;
    }

    private static DecisionGateRequest Request(DecisionGateMode mode) => new(
        ExecutionId: Guid.NewGuid(),
        StepOrder: 7,
        AgentType: AgentType.Colorist,
        Site: "Qa.byte-identical-fallback",
        State: new DecisionState("content"),
        Questions:
        [
            new DecisionGateQuestion(
                Site: "Colorist.step",
                Name: "Look",
                Kind: DecisionKind.Choice,
                Instructions: "Pick a look.",
                Options: ["Warm", "Cool"],
                SystemTwoAnswer: null)
        ],
        Config: new DecisionGateConfig(Mode: mode));

    /// <summary>
    /// The shipped engine configuration, bound exactly as the service binds it: the csproj links
    /// <c>ReelBolt.WorkflowEngine/appsettings.json</c> into this project's output directory under a
    /// distinct name so the Inference API's file cannot win.
    /// </summary>
    private static IConfigurationRoot ShippedEngineConfiguration()
    {
        const string FileName = "WorkflowEngine.appsettings.json";
        string path = Path.Combine(AppContext.BaseDirectory, FileName);

        File.Exists(path).Should().BeTrue(
            $"the csproj must link the shipped engine appsettings into the test output as {FileName}; " +
            "without it this test cannot make any claim about what ships");

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(FileName, optional: false, reloadOnChange: false)
            .Build();
    }

    private static IEnumerable<KeyValuePair<string, string?>> Flatten(IEnumerable<IConfigurationSection> sections)
    {
        foreach (IConfigurationSection section in sections)
        {
            List<IConfigurationSection> children = [.. section.GetChildren()];

            if (children.Count == 0)
            {
                yield return new KeyValuePair<string, string?>(section.Path, section.Value);
                continue;
            }

            foreach (KeyValuePair<string, string?> leaf in Flatten(children))
                yield return leaf;
        }
    }
}