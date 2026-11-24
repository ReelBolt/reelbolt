using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Briefs;

/// <summary>
/// Run state that is chosen when a run starts must survive every place a run is copied, re-queued, shown or
/// exported (docs/run-brief.md, "Copy sites"). A new <see cref="WorkflowExecution"/> property forces a decision
/// here: either it is run-start state (and must appear at each copy site that carries it) or it is runtime state
/// (and is listed as such). Pattern: the other drift guards in this project.
/// </summary>
public class RunStateCopySiteDriftGuardTests
{
    /// <summary>What a run is started with; each must reach the engine through the row, the event and the copy sites below.</summary>
    internal static readonly string[] RunStartState =
    [
        nameof(WorkflowExecution.UserRequest),
        nameof(WorkflowExecution.StyleConfigJson),
        nameof(WorkflowExecution.DesignSystemPinJson),
        nameof(WorkflowExecution.OutputFormatOverride),
        nameof(WorkflowExecution.OutputResolution),
        nameof(WorkflowExecution.RunBriefJson),
        nameof(WorkflowExecution.AdmittedRunRateBucket),
    ];

    /// <summary>Identity, ownership, lifecycle, lease and snapshot state: written by the engine or the queue, never a run choice.</summary>
    private static readonly string[] RuntimeState =
    [
        nameof(WorkflowExecution.Id), nameof(WorkflowExecution.WorkflowDefinitionId), nameof(WorkflowExecution.ProjectId),
        nameof(WorkflowExecution.OrganizationId), nameof(WorkflowExecution.Status), nameof(WorkflowExecution.StartedAt),
        nameof(WorkflowExecution.CompletedAt), nameof(WorkflowExecution.CurrentStepId), nameof(WorkflowExecution.IterationCount),
        nameof(WorkflowExecution.ResultJson), nameof(WorkflowExecution.CorrelationId), nameof(WorkflowExecution.InitiatedByUserId),
        nameof(WorkflowExecution.ErrorMessage), nameof(WorkflowExecution.Initiator), nameof(WorkflowExecution.QueuedAt),
        nameof(WorkflowExecution.LeaseOwner), nameof(WorkflowExecution.LeaseExpiresAt), nameof(WorkflowExecution.HeartbeatAt),
        nameof(WorkflowExecution.SnapshotDefinitionId), nameof(WorkflowExecution.AgentSnapshotJson), nameof(WorkflowExecution.PendingResume),
        nameof(WorkflowExecution.ResumeCount), nameof(WorkflowExecution.AutoResumeCount), nameof(WorkflowExecution.WorkflowDefinition),
        nameof(WorkflowExecution.Project), nameof(WorkflowExecution.CurrentStep), nameof(WorkflowExecution.StepResults),
        nameof(WorkflowExecution.ReviewScores), nameof(WorkflowExecution.AttentionJson), nameof(WorkflowExecution.FirstStartedAt),
        nameof(WorkflowExecution.ApiKeyId),
    ];

    private static string SrcRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReelBolt.sln"))) return Path.Combine(directory.FullName, "src");
            directory = directory.Parent;
        }

        throw new InvalidOperationException("inference/ReelBolt.sln not found above the test output directory.");
    }

    [Fact]
    public void Every_run_property_is_classified_as_run_start_or_runtime_state()
    {
        string[] all = typeof(WorkflowExecution).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).ToArray();

        all.Except(RunStartState).Except(RuntimeState).Should().BeEmpty(
            "a new WorkflowExecution property must be classified: run-start state is copied by retry, resume, the sweeper, "
            + "composition revisions, the DTOs, the export and the event; list it in RunStartState and its copy sites, or in RuntimeState");
        RunStartState.Concat(RuntimeState).Except(all).Should().BeEmpty("the lists name properties that no longer exist");
    }

    /// <summary>Files that construct a run row or a run-start event. A new one must be reviewed and added here.</summary>
    private static readonly string[] Writers =
    [
        "ReelBolt.Inference.Api/Services/Workflows/WorkflowExecutionService.cs",   // queue: row + event
        "ReelBolt.Inference.Api/Controllers/WorkflowsController.cs",               // retry/resume event
        "ReelBolt.WorkflowEngine/Services/ExecutionRecoverySweeper.cs",            // sweeper event
        "ReelBolt.Inference.Api/Services/Account/AccountPurgeService.cs",          // id-only stub for a delete
    ];

    [Fact]
    public void Every_writer_of_a_run_row_or_run_start_event_is_a_known_copy_site()
    {
        var writer = new System.Text.RegularExpressions.Regex(
            @"new\s+WorkflowExecution(Requested)?\b|WorkflowExecution(Requested)?\s+\w+\s*=\s*new\s*\(\s*\)");
        string root = SrcRoot();
        string[] found = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"))
            .Where(f => writer.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        found.Should().Equal(Writers.OrderBy(f => f, StringComparer.Ordinal),
            "a new place that creates a run row or the run-start event must copy every run-start property (docs/run-brief.md); "
            + "add it to Writers and give it a test that fills every property with a sentinel and checks the event mirrors it");
    }

    /// <summary>Gives every run-start property of the row a distinctive value.</summary>
    internal static void FillRunStartState(WorkflowExecution row)
    {
        foreach (string name in RunStartState)
        {
            PropertyInfo p = typeof(WorkflowExecution).GetProperty(name)!;
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            object value = t == typeof(string) ? $"sentinel-{name}"
                : t == typeof(int) ? 720
                : t.IsEnum ? Enum.GetValues(t).GetValue(1)!
                : throw new InvalidOperationException($"Teach FillRunStartState about {name} ({t.Name}).");
            p.SetValue(row, value);
        }
    }

    /// <summary>Fails for every run-start property whose value did not reach the published event.</summary>
    internal static void AssertEventMirrors(WorkflowExecution row, WorkflowExecutionRequested sent)
    {
        foreach (string name in RunStartState)
        {
            object? expected = typeof(WorkflowExecution).GetProperty(name)!.GetValue(row);
            string eventName = name == nameof(WorkflowExecution.OutputFormatOverride) ? "OutputFormat" : name;
            object? actual = typeof(WorkflowExecutionRequested).GetProperty(eventName)!.GetValue(sent);
            (actual?.ToString()).Should().Be(expected?.ToString(), "the published request must carry {0}", name);
        }
    }

    [Fact]
    public void The_requested_event_mirrors_each_run_start_property_it_can()
    {
        // OutputFormatOverride travels as OutputFormat, the enum name; everything else under its own name.
        HashSet<string> eventProps = typeof(WorkflowExecutionRequested).GetProperties().Select(p => p.Name).ToHashSet();

        foreach (string name in RunStartState)
        {
            string eventName = name == nameof(WorkflowExecution.OutputFormatOverride) ? "OutputFormat" : name;
            eventProps.Should().Contain(eventName, "the published request carries {0}", name);
        }
    }
}
