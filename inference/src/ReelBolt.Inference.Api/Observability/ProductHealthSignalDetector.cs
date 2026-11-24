using System.Text.Json;

namespace ReelBolt.Inference.Api.Observability;

/// <summary>
/// A tool result the assistant produced, as the detector sees it. <see cref="ResultText"/> is the tool's
/// own JSON payload; the detector reads it only to recognise the condition and never copies it anywhere.
/// </summary>
public sealed record ToolCallObservation(
    string Source,
    Guid OrgId,
    Guid UserId,
    Guid? ProjectId,
    Guid? ExecutionId,
    string? ToolName,
    string? ResultText);

/// <summary>A request the API is about to answer with a refusal status.</summary>
public sealed record RefusalObservation(
    string Source,
    Guid OrgId,
    Guid UserId,
    Guid? ProjectId,
    int StatusCode,
    string? Code);

/// <summary>How an assistant turn ended. <c>EndedInError &amp;&amp; !HadReply</c> is the abandonment condition.</summary>
public sealed record TurnOutcomeObservation(
    string Source,
    Guid OrgId,
    Guid UserId,
    Guid? ProjectId,
    bool EndedInError,
    bool HadReply);

/// <summary>
/// Observes the API's existing surfaces and emits a <see cref="ProductHealthSignal"/> when one of the four
/// pinned conditions is present.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method returns <c>void</c>.</b> That is the property, not a style: a detector that returned a
/// value could be branched on, and a detector that can change a response is a defect. The observing seams
/// call these methods and ignore the result because there is none.
/// </para>
/// <para>
/// <b>Deterministic.</b> Each method is a closed-set test over data the caller already has — a status
/// code, a JSON <c>ok:false</c>, a persisted <c>Failed</c> status. There is no model call, no
/// summarisation, no prompt, and nothing that could put user prose into the sink.
/// </para>
/// <para>
/// <b>Never throws.</b> A detector's own failure (a malformed payload, a hostile string, a sink that
/// throws) is swallowed and reported as nothing, because the alternative is turning telemetry into a
/// failed user request.
/// </para>
/// </remarks>
public interface IProductHealthSignalDetector
{
    /// <summary>Emits <see cref="ProductHealthSignalKinds.ToolCallFailed"/> and/or <c>workflow_step_failed</c> for this result.</summary>
    void ObserveToolResult(ToolCallObservation observation);

    /// <summary>Emits <see cref="ProductHealthSignalKinds.RequestRefused"/> for a 402, 409 or 503.</summary>
    void ObserveRefusal(RefusalObservation observation);

    /// <summary>Emits <see cref="ProductHealthSignalKinds.ConversationAbandonedAfterError"/> for a turn that ended in error with no reply.</summary>
    void ObserveTurnOutcome(TurnOutcomeObservation observation);
}

/// <inheritdoc />
public sealed class ProductHealthSignalDetector(IProductHealthSignalSink sink, Func<DateTimeOffset>? clock = null) : IProductHealthSignalDetector
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public void ObserveToolResult(ToolCallObservation observation)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(observation.ResultText))
                return;

            using JsonDocument document = JsonDocument.Parse(observation.ResultText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return;

            JsonElement root = document.RootElement;

            if (root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.False)
            {
                Emit(ProductHealthSignalKinds.ToolCallFailed, observation, stepOrder: null, executionId: observation.ExecutionId, code: null);
            }

            // A workflow-run report names each step's persisted status. Each step that ended Failed is one
            // signal, carrying its own order and the run's id — this is the API's own view of a failed step.
            if (root.TryGetProperty("steps", out JsonElement steps) && steps.ValueKind == JsonValueKind.Array)
            {
                Guid? executionId = observation.ExecutionId ?? ReadGuid(root, "id");
                foreach (JsonElement step in steps.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object
                        || !step.TryGetProperty("status", out JsonElement status)
                        || status.ValueKind != JsonValueKind.String
                        || !string.Equals(status.GetString(), "Failed", StringComparison.Ordinal))
                        continue;

                    Emit(ProductHealthSignalKinds.WorkflowStepFailed, observation, ReadInt(step, "stepOrder"), executionId, code: null);
                }
            }
        }
        catch
        {
            // Reporting only: a detector's own failure never fails the request.
        }
    }

    /// <inheritdoc />
    public void ObserveRefusal(RefusalObservation observation)
    {
        try
        {
            if (observation.StatusCode is not (402 or 409 or 503))
                return;

            Emit(ProductHealthSignalKinds.RequestRefused, observation, stepOrder: null, executionId: null, observation.Code);
        }
        catch
        {
            // Reporting only.
        }
    }

    /// <inheritdoc />
    public void ObserveTurnOutcome(TurnOutcomeObservation observation)
    {
        try
        {
            if (!observation.EndedInError || observation.HadReply)
                return;

            Emit(ProductHealthSignalKinds.ConversationAbandonedAfterError, observation, stepOrder: null, executionId: null, code: null);
        }
        catch
        {
            // Reporting only.
        }
    }

    private void Emit(string kind, ToolCallObservation observation, int? stepOrder, Guid? executionId, string? code) =>
        Emit(new ProductHealthSignal
        {
            Kind = kind,
            Source = observation.Source,
            OccurredAt = _clock(),
            OrgId = observation.OrgId,
            UserId = observation.UserId,
            ProjectId = observation.ProjectId,
            ExecutionId = executionId,
            StepOrder = stepOrder,
            Code = code
        });

    private void Emit(string kind, RefusalObservation observation, int? stepOrder, Guid? executionId, string? code) =>
        Emit(new ProductHealthSignal
        {
            Kind = kind,
            Source = observation.Source,
            OccurredAt = _clock(),
            OrgId = observation.OrgId,
            UserId = observation.UserId,
            ProjectId = observation.ProjectId,
            ExecutionId = executionId,
            StepOrder = stepOrder,
            Code = code
        });

    private void Emit(string kind, TurnOutcomeObservation observation, int? stepOrder, Guid? executionId, string? code) =>
        Emit(new ProductHealthSignal
        {
            Kind = kind,
            Source = observation.Source,
            OccurredAt = _clock(),
            OrgId = observation.OrgId,
            UserId = observation.UserId,
            ProjectId = observation.ProjectId,
            ExecutionId = executionId,
            StepOrder = stepOrder,
            Code = code
        });

    private void Emit(ProductHealthSignal signal) => sink.Emit(signal);

    private static Guid? ReadGuid(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && Guid.TryParse(value.GetString(), out Guid parsed)
            ? parsed
            : null;

    private static int? ReadInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;
}

/// <summary>
/// The ambient detector the observing seams use when nothing is injected. A process-wide singleton with no
/// mutable state: it emits to <see cref="OtelProductHealthSignalSink"/>, and a test injects its own detector
/// through the seam's constructor instead of reaching in here.
/// </summary>
public static class ProductHealthSignals
{
    /// <summary>The production detector. Reporting only; nothing ever reads it back.</summary>
    public static IProductHealthSignalDetector Ambient { get; } = new ProductHealthSignalDetector(new OtelProductHealthSignalSink());
}
