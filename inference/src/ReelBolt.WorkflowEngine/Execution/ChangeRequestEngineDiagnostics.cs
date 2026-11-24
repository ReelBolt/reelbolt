using System.Diagnostics.Metrics;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>Engine metrics for step change requests. Tags are closed sets only: never ids, text, org or project.</summary>
public static class ChangeRequestEngineDiagnostics
{
    /// <summary>Meter name; the engine's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Engine.ChangeRequests";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Items a change-request run redid or carried over, tagged <c>step_type</c> and <c>kind</c> = redone | carried.</summary>
    public static readonly Counter<long> Items = Meter.CreateCounter<long>(
        "reelbolt.engine.change_request_items", "items",
        "Lines, clips or takes a step change request redid or carried over, by step type and kind");

    /// <summary>Records the redone and carried counts of one step.</summary>
    public static void Record(string stepType, int redone, int carried)
    {
        if (redone > 0) Items.Add(redone, new KeyValuePair<string, object?>("step_type", stepType), new KeyValuePair<string, object?>("kind", "redone"));
        if (carried > 0) Items.Add(carried, new KeyValuePair<string, object?>("step_type", stepType), new KeyValuePair<string, object?>("kind", "carried"));
    }
}
