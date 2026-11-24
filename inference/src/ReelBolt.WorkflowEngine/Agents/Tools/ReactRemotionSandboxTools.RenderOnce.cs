using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// T-206 "render once" as a tool default rather than prose: within one step attempt (one
/// <see cref="WorkflowExecutionContext"/> scope — every agent step attempt, review-loop iteration and revision
/// opens its own) a second successful full render of the SAME composition is refused unless the call passes
/// <c>force: true</c> with a <c>forceReason</c>. Stills never count, and different compositions never collide, so a
/// motion-graphics planner rendering one composition per overlay is unaffected.
///
/// <para>
/// T-009 bounds the other direction: failed full renders. The render tool records each failed
/// <c>render</c> command on the step's <see cref="RenderFailureLedger"/> (one per step, across the
/// retry wrapper's attempts, keyed by composition) and, once
/// <c>Compute:Render:MaxFailedRendersPerStep</c> failed renders of the SAME composition have been
/// recorded, refuses every further render of it WITHOUT executing it (<c>terminal:true</c>) so an
/// agent cannot spend its whole tool loop re-running a render that cannot succeed. Failures of
/// other compositions never count against it.
/// </para>
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Per step attempt: composition id → the storage key its successful full render was uploaded to.</summary>
    private static readonly ConditionalWeakTable<WorkflowExecutionContext, Dictionary<string, string>> FullRenders = new();

    /// <summary>Longest <c>forceReason</c> kept in the log.</summary>
    private const int MaxForceReasonChars = 300;

    /// <summary>The shipped cap on failed full renders of ONE composition per step, when <c>Compute:Render:MaxFailedRendersPerStep</c> is unset.</summary>
    internal const int DefaultMaxFailedRendersPerStep = 2;

    /// <summary>Configuration key of the per-step failed-render cap.</summary>
    internal const string MaxFailedRendersPerStepKey = "Compute:Render:MaxFailedRendersPerStep";

    /// <summary>Failed full renders of ONE composition this step may record before every further render of it is refused without executing.</summary>
    private readonly int _maxFailedRendersPerStep;

    /// <summary>`exit status 1`, `exit code 1`, `exited with code 1` — the sandbox driver's own wording.</summary>
    private static readonly Regex ExitCodePattern = new(
        @"\b(?:exit\s+status|exit\s+code|exited\s+with\s+code|exited\s+with\s+status)\s*[:=]?\s*(-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The markers that mean the process was killed rather than exiting on its own.</summary>
    private static readonly string[] KilledMarkers = ["SIGKILL", "Killed", "ENOMEM", "out of memory", "constraint_memcg"];

    /// <summary>
    /// The refusal for a render of <paramref name="compositionId"/> over the per-composition cap, or
    /// null when that composition has not reached it. Called BEFORE the command runs, and never
    /// executes anything. Failures of OTHER compositions never count here, so a planner rendering
    /// several overlays is bounded per overlay rather than by the whole step's failures.
    /// </summary>
    private string? FailedRenderCapRefusal(string compositionId)
    {
        if (RenderFailureLedger.Current is not { } ledger || ledger.FailedCount(compositionId) < _maxFailedRendersPerStep)
            return null;

        ledger.MarkTerminalRefused(compositionId);
        int failed = ledger.FailedCount(compositionId);
        _logger.LogWarning(
            "Refusing render of composition {CompositionId} for execution {ExecutionId}: {Failed} failed renders of it already reached the per-composition cap",
            compositionId,
            RequireContext().ExecutionId,
            failed);

        string message = ledger.LastMessageFor(compositionId) is { Length: > 0 } last
            ? last + " This composition has used up its render attempts, so it will not render again."
            : FailureMessages.UserMessage(FailureMessages.RenderFailedCode) + " This composition has used up its render attempts, so it will not render again.";

        return JsonSerializer.Serialize(new
        {
            ok = false,
            terminal = true,
            code = FailureMessages.RenderFailedCode,
            compositionId,
            failedRenders = failed,
            error = message
        }, ResultJsonOptions);
    }

    /// <summary>Records a failed full render of <paramref name="compositionId"/> on the step's ledger, if one is published for this scope.</summary>
    private void RecordFailedRender(string compositionId, string outputFileName, string? error, string? output, int? timedOutAfterSeconds = null)
    {
        if (!IsVideo(InferContentType(outputFileName)) || RenderFailureLedger.Current is not { } ledger)
            return;

        (int? exitCode, bool killed) = ReadRenderFailureSignals(error, output);
        string message = timedOutAfterSeconds is { } deadline
            ? RenderTimeoutUserMessage(deadline)
            : RenderFailureUserMessage(exitCode, killed);
        ledger.RecordFailure(compositionId, message);
        _logger.LogWarning(
            "Recorded a failed render of composition {CompositionId} ({OutputFileName}) for execution {ExecutionId} ({Failed} of it this step): {Message}",
            compositionId,
            outputFileName,
            RequireContext().ExecutionId,
            ledger.FailedCount(compositionId),
            message);
    }

    /// <summary>
    /// What the failed command's own text shows: the process exit code when the sandbox driver named
    /// it (`execution failed: exit status 1`), and whether the text carries a kill marker.
    /// </summary>
    internal static (int? ExitCode, bool Killed) ReadRenderFailureSignals(string? error, string? output)
    {
        string combined = $"{error}\n{output}";
        Match match = ExitCodePattern.Match(combined);
        int? exitCode = match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)
                ? code
                : null;

        bool killed = KilledMarkers.Any(marker => combined.Contains(marker, StringComparison.OrdinalIgnoreCase));
        return (exitCode, killed);
    }

    /// <summary>The user-facing sentence for a render stopped at its deadline.</summary>
    internal static string RenderTimeoutUserMessage(int deadlineSeconds) =>
        $"The video renderer did not finish within its time limit ({deadlineSeconds.ToString(CultureInfo.InvariantCulture)} seconds) and was stopped. " +
        "This was on our side, so credits for this step were refunded; run it again.";

    /// <summary>
    /// The user-facing sentence for a failed render, naming only what the command's own output shows:
    /// the exit code, and that the renderer was killed (likely out of memory) when a kill marker is
    /// present. Never the generic platform sentence.
    /// </summary>
    internal static string RenderFailureUserMessage(int? exitCode, bool killed)
    {
        string what = exitCode is { } code
            ? $"The video renderer exited with code {code.ToString(CultureInfo.InvariantCulture)}"
            : killed
                ? "The video renderer was killed"
                : "The video renderer failed inside the sandbox";

        if (killed)
            what += exitCode is null
                ? " (likely out of memory)"
                : " and was killed (likely out of memory)";

        return what + ". This was on our side, so credits for this step were refunded; run it again.";
    }

    /// <summary>
    /// The refusal for a repeated full render, or null when this render may go ahead. A forced render is allowed
    /// only with a non-blank reason, which is logged.
    /// </summary>
    internal string? RenderOnceRefusal(WorkflowExecutionContext ctx, string compositionId, string outputFileName, bool force, string? forceReason)
    {
        if (!IsVideo(InferContentType(outputFileName)))
            return null;

        Dictionary<string, string> rendered = FullRenders.GetOrCreateValue(ctx);
        string? previousKey;
        lock (rendered)
        {
            if (!rendered.TryGetValue(compositionId, out previousKey))
                return null;
        }

        if (!force)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = $"already rendered: composition '{compositionId}' was rendered and uploaded in this step as " +
                        $"'{Path.GetFileName(previousKey)}', and that file is already this step's output. Do not render it again. " +
                        "Only if you changed the composition since, call again with force: true and forceReason saying what changed."
            }, ResultJsonOptions);
        }

        if (string.IsNullOrWhiteSpace(forceReason))
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "force: true needs a forceReason: say what changed in the composition since its last render."
            }, ResultJsonOptions);
        }

        string reason = new(forceReason.Where(c => !char.IsControl(c)).Take(MaxForceReasonChars).ToArray());
        _logger.LogWarning(
            "Forced re-render of composition {CompositionId} in execution {ExecutionId} (previous render {PreviousKey}): {Reason}",
            compositionId, ctx.ExecutionId, previousKey, reason);
        return null;
    }

    /// <summary>Records a successful full (video) render of <paramref name="compositionId"/> in this step attempt.</summary>
    internal static void RecordFullRender(WorkflowExecutionContext ctx, string compositionId, string outputFileName, string storageKey)
    {
        if (!IsVideo(InferContentType(outputFileName)))
            return;

        Dictionary<string, string> rendered = FullRenders.GetOrCreateValue(ctx);
        lock (rendered)
            rendered[compositionId] = storageKey;
    }
}
