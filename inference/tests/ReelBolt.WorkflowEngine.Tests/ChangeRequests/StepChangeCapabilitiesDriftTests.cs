using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// Drift guard for the change-request registry, in the style of RunRateClassifierTests: a new StepType
/// cannot ship without a deliberate decision (docs/design/change-requests.md).
/// </summary>
public class StepChangeCapabilitiesDriftTests
{
    /// <summary>The types that must be Supported. T-004 and later slices extend this list together with the executor.</summary>
    private static readonly StepType[] ExpectedSupported = [StepType.Voiceover, StepType.VideoGenerate, StepType.AudioGenerate, StepType.ThumbnailGenerate];

    public static IEnumerable<object[]> AllStepTypes() =>
        Enum.GetValues<StepType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Every_step_type_has_a_deliberate_entry(StepType type)
    {
        StepChangeCapability cap = StepChangeCapabilities.For(type);
        cap.Should().NotBeNull();
        cap.Supported.Should().Be(ExpectedSupported.Contains(type),
            "a type flips to Supported together with its executor and this expectation");
        if (!cap.Supported)
            cap.NotSupportedReason.Should().NotBeNullOrWhiteSpace("a NotSupported type states why");
        else
            cap.Overrides.Should().NotBeEmpty();
    }

    [Fact]
    public void Revise_points_at_the_revision_route() =>
        StepChangeCapabilities.For(StepType.Revise).Route.Should().Be("composition-revision");

    [Fact]
    public void Supported_types_have_a_registered_IChangeRequestAwareStepExecutor()
    {
        Assembly engine = typeof(IChangeRequestAwareStepExecutor).Assembly;
        HashSet<StepType> implemented = engine.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IChangeRequestAwareStepExecutor).IsAssignableFrom(t))
            .Where(t => typeof(IStepExecutor).IsAssignableFrom(t)) // registered in DI as IStepExecutor (AddSingleton<IStepExecutor, T>)
            .Select(t => ((IChangeRequestAwareStepExecutor)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t)).StepType)
            .ToHashSet();

        foreach (StepType type in Enum.GetValues<StepType>().Where(t => StepChangeCapabilities.For(t).Supported))
            implemented.Should().Contain(type, $"{type} is Supported but no IChangeRequestAwareStepExecutor handles it");
    }

    /// <summary>
    /// A request whose stored JSON cannot be read reaches the executor as a scope flagged <c>Malformed</c>; every executor
    /// that applies change requests must refuse it before any side effect (otherwise a corrupt row would redo a whole step).
    /// </summary>
    [Fact]
    public void Every_change_request_executor_refuses_a_malformed_scope()
    {
        string executors = System.IO.Path.Combine(SrcRoot(), "ReelBolt.WorkflowEngine", "Execution", "StepExecutors");
        string[] files = System.IO.Directory.GetFiles(executors, "*.cs")
            .Where(f => System.IO.File.ReadAllText(f).Contains(": IStepExecutor, IChangeRequestAwareStepExecutor", StringComparison.Ordinal)
                || System.IO.File.ReadAllText(f).Contains("IChangeRequestAwareStepExecutor", StringComparison.Ordinal)
                    && System.IO.Path.GetFileName(f) != "IChangeRequestAwareStepExecutor.cs")
            .ToArray();

        files.Should().NotBeEmpty();
        foreach (string file in files)
            System.IO.File.ReadAllText(file).Should().Contain(".Malformed", $"{System.IO.Path.GetFileName(file)} applies change requests and must refuse a malformed scope");
    }

    private static string SrcRoot()
    {
        System.IO.DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "ReelBolt.sln")))
                return System.IO.Path.Combine(directory.FullName, "src");
            directory = directory.Parent;
        }

        throw new InvalidOperationException("inference/ReelBolt.sln not found above the test output directory.");
    }
}
