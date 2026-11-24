using System.Diagnostics;

namespace ReelBolt.Inference.Api.Observability;

/// <summary>Where a <see cref="ProductHealthSignal"/> goes. One call, no return value: a sink cannot answer back.</summary>
public interface IProductHealthSignalSink
{
    /// <summary>Emits one signal. Must never throw — a sink failure is the emitter's to contain, not the request's.</summary>
    void Emit(ProductHealthSignal signal);
}

/// <summary>
/// The sink T-020 chose: <b>one OTLP span per signal</b>, on the Inference API's own
/// <see cref="ActivitySource"/> (<c>ReelBolt.Inference.Api</c>), whose name
/// <c>AddReelBoltTelemetry</c> already registers with the tracer provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a span, not a row.</b> A row would need a table and a migration (owned by another
/// department) for data that is pure telemetry; the OTLP pipeline is already wired, already
/// watched, and is where <c>docs/observability.md</c> says a trace's high-cardinality identifiers
/// belong. A span also inherits the export switch for free: with no collector configured the
/// process builds no exporter and the span is created and dropped exactly as today.
/// </para>
/// <para>
/// <b>Reporting only, structurally.</b> With no <see cref="ActivityListener"/> attached,
/// <see cref="ActivitySource.StartActivity(string, ActivityKind, ActivityContext, IEnumerable{KeyValuePair{string, object?}}, IEnumerable{ActivityLink}, DateTimeOffset)"/>
/// returns <c>null</c> and nothing is recorded — so a host with telemetry off behaves identically
/// to one without this code, and nothing here can hand a value back to a caller.
/// </para>
/// <para>
/// <b>Metadata only.</b> The tags are the <see cref="ProductHealthSignal"/> fields and nothing else:
/// no message, prompt or tool-output text is read, so no user prose can reach the span.
/// </para>
/// </remarks>
public sealed class OtelProductHealthSignalSink : IProductHealthSignalSink
{
    /// <summary>The service's own source name; <c>AddReelBoltTelemetry</c> registers it via <c>AddSource</c>.</summary>
    public const string ActivitySourceName = "ReelBolt.Inference.Api";

    /// <summary>The one span name every signal uses; the kind is a tag, so a query is a tag filter.</summary>
    public const string ActivityName = "product_health.signal";

    public const string KindTag = "product_health.kind";
    public const string SourceTag = "product_health.source";
    public const string StepOrderTag = "product_health.step_order";
    public const string CodeTag = "product_health.code";
    public const string OrgIdTag = "reelbolt.org_id";
    public const string UserIdTag = "reelbolt.user_id";
    public const string ProjectIdTag = "reelbolt.project_id";
    public const string ExecutionIdTag = "reelbolt.execution_id";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    public void Emit(ProductHealthSignal signal)
    {
        List<KeyValuePair<string, object?>> tags =
        [
            new(KindTag, signal.Kind),
            new(SourceTag, signal.Source),
            new(OrgIdTag, signal.OrgId.ToString()),
            new(UserIdTag, signal.UserId.ToString())
        ];

        if (signal.ProjectId is { } projectId) tags.Add(new(ProjectIdTag, projectId.ToString()));
        if (signal.ExecutionId is { } executionId) tags.Add(new(ExecutionIdTag, executionId.ToString()));
        if (signal.StepOrder is { } stepOrder) tags.Add(new(StepOrderTag, stepOrder));
        if (signal.Code is { Length: > 0 } code) tags.Add(new(CodeTag, code));

        using Activity? activity = Source.StartActivity(
            ActivityName, ActivityKind.Internal, default(ActivityContext), tags, links: null, startTime: signal.OccurredAt);
    }
}
