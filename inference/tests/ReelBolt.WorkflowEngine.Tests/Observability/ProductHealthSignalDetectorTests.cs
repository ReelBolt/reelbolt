using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Observability;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Observability;

/// <summary>
/// T-020, the detector itself: a seeded condition of each of the four kinds emits <b>exactly one</b>
/// signal carrying the pinned metadata and <b>no message text</b>, and the detector's own failure is
/// contained. Determinism is pinned by injecting the clock.
/// </summary>
public class ProductHealthSignalDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid User = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Project = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Execution = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>A sentence the detector must never copy anywhere, however it is seeded.</summary>
    private const string Prose = "the user's private prompt: please delete the whole project";

    private sealed class RecordingSink : IProductHealthSignalSink
    {
        private readonly List<ProductHealthSignal> _signals = [];
        public IReadOnlyList<ProductHealthSignal> Signals { get { lock (_signals) return [.. _signals]; } }
        public void Emit(ProductHealthSignal signal) { lock (_signals) _signals.Add(signal); }
    }

    private sealed class ThrowingSink : IProductHealthSignalSink
    {
        public void Emit(ProductHealthSignal signal) => throw new InvalidOperationException("the sink is down");
    }

    private static ProductHealthSignalDetector Detector(IProductHealthSignalSink sink) => new(sink, () => Now);

    private static ToolCallObservation ToolResult(string text) => new("assistant.tool_result", Org, User, Project, Execution, "GetWorkflowExecution", text);

    // ---------------------------------------------------------------------
    // One signal per kind, with the metadata fields
    // ---------------------------------------------------------------------

    [Fact]
    public void A_tool_result_that_reports_failure_emits_exactly_one_tool_call_failed()
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveToolResult(ToolResult(JsonSerializer.Serialize(new { ok = false, error = Prose })));

        ProductHealthSignal signal = sink.Signals.Single();
        signal.Kind.Should().Be(ProductHealthSignalKinds.ToolCallFailed);
        signal.Source.Should().Be("assistant.tool_result");
        signal.OccurredAt.Should().Be(Now);
        signal.OrgId.Should().Be(Org);
        signal.UserId.Should().Be(User);
        signal.ProjectId.Should().Be(Project);
    }

    [Fact]
    public void A_tool_result_that_reports_success_emits_nothing()
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveToolResult(ToolResult(JsonSerializer.Serialize(new { ok = true, message = Prose })));

        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public void A_run_report_naming_a_failed_step_emits_one_workflow_step_failed_carrying_its_order()
    {
        var sink = new RecordingSink();
        string run = JsonSerializer.Serialize(new
        {
            id = Execution,
            status = "Failed",
            steps = new object[]
            {
                new { stepOrder = 1, status = "Completed" },
                new { stepOrder = 2, status = "Failed" }
            }
        });

        Detector(sink).ObserveToolResult(ToolResult(run));

        ProductHealthSignal signal = sink.Signals.Single();
        signal.Kind.Should().Be(ProductHealthSignalKinds.WorkflowStepFailed);
        signal.ExecutionId.Should().Be(Execution);
        signal.StepOrder.Should().Be(2);
    }

    [Theory]
    [InlineData(402)]
    [InlineData(409)]
    [InlineData(503)]
    public void A_refusal_status_emits_exactly_one_request_refused(int status)
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveRefusal(new RefusalObservation("workflow.run_start", Org, User, Project, status, "A_CODE"));

        ProductHealthSignal signal = sink.Signals.Single();
        signal.Kind.Should().Be(ProductHealthSignalKinds.RequestRefused);
        signal.Source.Should().Be("workflow.run_start");
        signal.Code.Should().Be("A_CODE");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)]
    [InlineData(429)]
    public void A_status_outside_the_closed_set_emits_nothing(int status)
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveRefusal(new RefusalObservation("workflow.run_start", Org, User, Project, status, "A_CODE"));

        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public void A_turn_that_ended_on_an_error_without_a_reply_emits_exactly_one_abandonment()
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveTurnOutcome(new TurnOutcomeObservation("assistant.approval_gate", Org, User, Project, EndedInError: true, HadReply: false));

        ProductHealthSignal signal = sink.Signals.Single();
        signal.Kind.Should().Be(ProductHealthSignalKinds.ConversationAbandonedAfterError);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_turn_that_did_not_abandon_after_an_error_emits_nothing(bool endedInError, bool hadReply)
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveTurnOutcome(new TurnOutcomeObservation("assistant.approval_gate", Org, User, Project, endedInError, hadReply));

        sink.Signals.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // Metadata only, never user prose
    // ---------------------------------------------------------------------

    [Fact]
    public void A_signal_carries_only_the_pinned_fields_and_no_message_text()
    {
        var sink = new RecordingSink();

        Detector(sink).ObserveToolResult(ToolResult(JsonSerializer.Serialize(new { ok = false, error = Prose })));

        ProductHealthSignal signal = sink.Signals.Single();
        PropertyInfo[] fields = typeof(ProductHealthSignal).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        fields.Select(f => f.Name).Should().BeEquivalentTo(
            ["Kind", "Source", "OccurredAt", "OrgId", "UserId", "ProjectId", "ExecutionId", "StepOrder", "Code"],
            "the record is the whole payload: a field added here is a field the sink would carry");

        foreach (PropertyInfo field in fields)
        {
            string? value = field.GetValue(signal)?.ToString();
            value.Should().NotContain(Prose, "no field may echo the tool's own text");
        }
    }

    [Fact]
    public void The_otel_payload_is_the_pinned_tags_and_nothing_the_tool_said()
    {
        var captured = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OtelProductHealthSignalSink.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = captured.Add
        };
        ActivitySource.AddActivityListener(listener);

        var sink = new OtelProductHealthSignalSink();
        new ProductHealthSignalDetector(sink, () => Now).ObserveToolResult(
            ToolResult(JsonSerializer.Serialize(new { ok = false, error = Prose })));

        // Other classes run in parallel and the ambient detector shares this source, so pick OUR signal out
        // by the tenant tag the seeded observation carries.
        Activity span = captured.Single(a =>
            a.OperationName == OtelProductHealthSignalSink.ActivityName
            && Equals(a.GetTagItem(OtelProductHealthSignalSink.OrgIdTag), Org.ToString()));
        span.OperationName.Should().Be(OtelProductHealthSignalSink.ActivityName);
        span.GetTagItem(OtelProductHealthSignalSink.KindTag).Should().Be(ProductHealthSignalKinds.ToolCallFailed);
        span.GetTagItem(OtelProductHealthSignalSink.SourceTag).Should().Be("assistant.tool_result");
        span.GetTagItem(OtelProductHealthSignalSink.OrgIdTag).Should().Be(Org.ToString());

        List<string> tagKeys = [.. span.TagObjects.Select(t => t.Key)];
        tagKeys.Should().BeEquivalentTo(
            [OtelProductHealthSignalSink.KindTag, OtelProductHealthSignalSink.SourceTag,
             OtelProductHealthSignalSink.OrgIdTag, OtelProductHealthSignalSink.UserIdTag,
             OtelProductHealthSignalSink.ProjectIdTag, OtelProductHealthSignalSink.ExecutionIdTag],
            "the span carries the signal's fields and nothing else");
        foreach (KeyValuePair<string, object?> tag in span.TagObjects)
            tag.Value?.ToString().Should().NotContain(Prose);
    }

    // ---------------------------------------------------------------------
    // Determinism and containment
    // ---------------------------------------------------------------------

    [Fact]
    public void The_same_seeded_condition_yields_the_identical_signal_twice()
    {
        var first = new RecordingSink();
        var second = new RecordingSink();
        string payload = JsonSerializer.Serialize(new { ok = false, error = Prose });

        Detector(first).ObserveToolResult(ToolResult(payload));
        Detector(second).ObserveToolResult(ToolResult(payload));

        second.Signals.Single().Should().Be(first.Signals.Single(), "with the clock pinned the detection is pure");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"steps\": 7}")]
    public void A_payload_the_detector_cannot_read_emits_nothing_and_never_throws(string payload)
    {
        var sink = new RecordingSink();

        Action act = () => Detector(sink).ObserveToolResult(ToolResult(payload));

        act.Should().NotThrow();
        sink.Signals.Should().BeEmpty();
    }

    [Fact]
    public void A_sink_that_throws_is_contained_by_every_detector_method()
    {
        var detector = Detector(new ThrowingSink());

        Action act = () =>
        {
            detector.ObserveToolResult(ToolResult(JsonSerializer.Serialize(new { ok = false })));
            detector.ObserveRefusal(new RefusalObservation("workflow.run_start", Org, User, Project, 402, null));
            detector.ObserveTurnOutcome(new TurnOutcomeObservation("assistant.approval_gate", Org, User, Project, EndedInError: true, HadReply: false));
        };

        act.Should().NotThrow("a detector's own failure must never reach the request it observes");
    }
}
