using System.Collections.Concurrent;
using System.ComponentModel;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// The <see cref="ReelBolt.Shared.Agents.ToolGroup.FootageRead"/> tools, part of <see cref="VideoEventsAgentTools"/> so they
/// share its artifact loader: <c>ReadVideoSection</c>, <c>ReadVideoTranscript</c>, <c>ListVideoSfxClips</c>,
/// <c>ListVideoMusic</c>. Free and read-only, scoped to THIS execution's analysis (no argument names a project or file),
/// at most <see cref="MaxCallsPerStep"/> calls per step, at most 2,000 tokens a call, cut between entries with a
/// <c>next</c> cursor. Ids and words only; speech is fenced as untrusted data.
/// </summary>
public partial class VideoEventsAgentTools
{
    /// <summary>More than the event tools: reading a long film section by section takes several calls.</summary>
    internal const int MaxCallsPerStep = 24;

    private readonly StepKeyedState<StepCallCounter> _footageCalls = new(MaxTrackedSteps);

    [Description(
        "Read the speech of one outline section (an id like c2): runs of speech as {ids: \"t12-t30\", text}. A keep may start " +
        "or end on any listed id. Returns about 2,000 tokens at most; when cut it says truncated and gives next, to pass as " +
        "cursor. Free and read-only; no times.")]
    public Task<string> ReadVideoSection(
        [Description("The outline section id, e.g. c2.")] string sectionId,
        [Description("Optional cursor from a previous call's next.")] string? cursor = null) =>
        FootageAsync($"section|{sectionId}", cursor, (artifact, c) => VideoFootageReader.ReadSection(artifact, sectionId, c));

    [Description(
        "Read the speech between two ids (transcript ids like t12 and t90, or outline section ids like c1 and c3) as runs " +
        "{ids: \"t12-t30\", text}. Returns about 2,000 tokens at most; when cut it says truncated and gives next, to pass as " +
        "cursor. Free and read-only; no times.")]
    public Task<string> ReadVideoTranscript(
        [Description("The first id, e.g. t12 or c1.")] string fromId,
        [Description("The last id, e.g. t90 or c3.")] string toId,
        [Description("Optional cursor from a previous call's next.")] string? cursor = null) =>
        FootageAsync($"transcript|{fromId}|{toId}", cursor, (artifact, c) => VideoFootageReader.ReadTranscript(artifact, fromId, toId, c));

    [Description("List the sound-effect clips that can be placed (ids like x1 with a file name). About 2,000 tokens at most; pass next as cursor for more. Free and read-only.")]
    public Task<string> ListVideoSfxClips([Description("Optional cursor from a previous call's next.")] string? cursor = null) =>
        FootageAsync("sfx", cursor, (artifact, c) => VideoFootageReader.ListSfxClips(artifact, c));

    [Description("List the music tracks that can be placed (ids like m1 with a file name). About 2,000 tokens at most; pass next as cursor for more. Free and read-only.")]
    public Task<string> ListVideoMusic([Description("Optional cursor from a previous call's next.")] string? cursor = null) =>
        FootageAsync("music", cursor, (artifact, c) => VideoFootageReader.ListMusic(artifact, c));

    private async Task<string> FootageAsync(string readKey, string? cursor, Func<ReelBolt.Shared.Workflows.VideoAnalysisArtifact, string?, string> read)
    {
        WorkflowExecutionContext context = _executionContextAccessor.Current
            ?? throw new InvalidOperationException("Video footage tools can only be used inside a running workflow.");
        StepCallCounter counter = _footageCalls.GetOrAdd(context.StepResultId ?? context.ExecutionId, () => new StepCallCounter());
        (bool run, string? usableCursor) = counter.Admit(readKey, cursor);
        if (!run)
            return new System.Text.Json.Nodes.JsonObject
            {
                ["alreadyReturned"] = true,
                ["message"] = "This was already answered in this step (a complete answer has no further pages). Use that answer and carry on; do not repeat it."
            }.ToJsonString();
        int calls = counter.Increment();
        if (calls > MaxCallsPerStep)
            return new System.Text.Json.Nodes.JsonObject
            {
                ["error"] = "read limit reached",
                ["message"] = $"At most {MaxCallsPerStep} footage reads are allowed in one step. Work from what you have already read and give your answer now."
            }.ToJsonString();
        string answer = await ReadAsync(artifact => read(artifact, usableCursor), requireEvents: false, countCall: false);
        counter.Record(readKey, NextCursorOf(answer));
        return answer;
    }

    private static string? NextCursorOf(string answer)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(answer) is System.Text.Json.Nodes.JsonObject o && o["next"] is { } n ? n.GetValue<string>() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
