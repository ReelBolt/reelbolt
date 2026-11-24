using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.WorkflowEngine.Services.Styles;

/// <summary>The style and output-format override of the execution currently running on this async flow.</summary>
public sealed class RunStyleState
{
    private readonly IStyleNudgeService _nudges;
    private readonly Dictionary<AgentType, string?> _cache = new();
    private readonly object _gate = new();

    public RunStyleState(RunStyleSnapshot? style, RunOutputFormat? format, IStyleNudgeService nudges, int? outputResolution = null)
    {
        Style = style;
        Format = format is RunOutputFormat.Original ? null : format;
        OutputResolution = outputResolution;
        _nudges = nudges;
    }

    public RunStyleSnapshot? Style { get; }

    public RunOutputFormat? Format { get; }

    /// <summary>The run's chosen output resolution (short side in pixels); null for Auto.</summary>
    public int? OutputResolution { get; }

    /// <summary>The <c>[STYLE INSTRUCTIONS]</c> block for <paramref name="agentType"/>, computed once per run.</summary>
    public string? InstructionsFor(AgentType agentType)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(agentType, out string? block))
            {
                block = OutputResolution is null
                    ? _nudges.BuildInstructions(agentType, Style, Format)
                    : _nudges.BuildInstructions(agentType, Style, Format, OutputResolution);
                _cache[agentType] = block;
            }

            return block;
        }
    }

    /// <summary>The state for <paramref name="execution"/>, or null when it has no style, no format override and no output resolution.</summary>
    public static RunStyleState? From(WorkflowExecution execution, IStyleNudgeService nudges, bool briefSetsLanguage = false)
    {
        RunStyleSnapshot? style = StyleJson.TryParseSnapshot(execution.StyleConfigJson);

        // One language sentence per run: when the run's brief (or its design's locale) names a language it
        // outranks a style-carried one, so the style's language line is dropped here, never contradicted.
        if (briefSetsLanguage && style is not null && !string.IsNullOrWhiteSpace(style.Config.Language))
            style = style with { Config = style.Config with { Language = null } };

        // The brief's pace outranks the style's dynamics (docs/design/dynamic-edit.md): fold it in here, so
        // every agent's nudge says the one pace. A run with no brief pace is untouched.
        if (RunStyleOverrides.BriefPace(execution) is { } briefPace)
        {
            style = style is null
                ? new RunStyleSnapshot(null, null, null, new StyleConfig(Dynamics: briefPace))
                : style with { Config = style.Config with { Dynamics = briefPace } };
        }

        if (style is not null && style.Config.IsEmpty)
            style = null;

        RunOutputFormat? format = execution.OutputFormatOverride is RunOutputFormat.Original ? null : execution.OutputFormatOverride;
        int? resolution = RunStyleOverrides.OutputResolutionOf(execution);
        return style is null && format is null && resolution is null ? null : new RunStyleState(style, format, nudges, resolution);
    }
}

/// <summary>
/// Carries the running execution's <see cref="RunStyleState"/> to the agents it runs, without a new
/// constructor parameter on every one of them. Agents are singletons shared by every concurrent
/// execution, so the style cannot live on the agent: <see cref="Enter"/> is called once at the top
/// of <c>WorkflowExecutorService.ExecuteAsync</c> and the value flows down every <c>await</c> of
/// that execution — including the background tasks a room or a parallel step starts — and no
/// further. Two concurrent executions never see each other's style.
/// </summary>
public static class RunStyleAmbient
{
    private static readonly AsyncLocal<RunStyleState?> CurrentState = new();

    /// <summary>The running execution's style, or null when it has none (or nothing entered one).</summary>
    public static RunStyleState? Current => CurrentState.Value;

    /// <summary>Makes <paramref name="state"/> current until the returned scope is disposed.</summary>
    public static IDisposable Enter(RunStyleState? state)
    {
        RunStyleState? previous = CurrentState.Value;
        CurrentState.Value = state;
        return new Restore(previous);
    }

    /// <summary>
    /// <paramref name="systemPrompt"/> plus the current run's style block for <paramref name="agentType"/>;
    /// returned unchanged — the same string instance — when the run has no style.
    /// </summary>
    public static string Decorate(string systemPrompt, AgentType agentType) =>
        CurrentState.Value is { } state
            ? StyleNudgeService.Append(systemPrompt, state.InstructionsFor(agentType))
            : systemPrompt;

    private sealed class Restore(RunStyleState? previous) : IDisposable
    {
        public void Dispose() => CurrentState.Value = previous;
    }
}
