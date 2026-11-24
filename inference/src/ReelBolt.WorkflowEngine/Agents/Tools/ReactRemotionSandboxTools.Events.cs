using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Amazon.S3.Model;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// T-110: the composition's event manifest (docs/video-editing.md "Event anchors inside a composition").
/// <see cref="DeclareRenderEvents"/> validates and writes <see cref="RenderEventManifest.SandboxPath"/>; a
/// successful VIDEO render uploads that file as the render's non-playable sidecar
/// (<see cref="RenderEventManifest.SidecarKeyFor"/>, under <c>agentFiles/</c>, never an output).
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Most event-manifest findings echoed in a render result; the rest are counted.</summary>
    private const int MaxReportedEventWarnings = 5;

    [Description(
        "Declare timed events inside your composition — a stamp landing, a row appearing, a door slamming — so a sound " +
        "designer can fire a sound effect exactly on them. Each event is {name, frame, label}: name is 1-40 characters of " +
        "a-z, 0-9, '_' or '-'; frame is the composition frame the event happens on, exactly as your code places it; label " +
        "is a few words saying what happens (no times or numbers of seconds). At most 200 events. Validates and writes " +
        "public/events.json (calling it again replaces the file); the next video render uploads it next to the film.")]
    public async Task<string> DeclareRenderEvents(
        [Description("The events, each {name, frame, label}, in any order.")] RenderEvent[] events)
    {
        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Validate(events);
        if (manifest is null)
            return JsonSerializer.Serialize(new { ok = false, errors });

        ISandboxSession session = Sandbox();
        SandboxActionResult result = await session.WriteTextAsync(RenderEventManifest.SandboxPath, manifest.ToJson(), CancellationToken.None);
        _logger.LogInformation(
            "Tool result declare_render_events for execution {ExecutionId}: ok={Ok}, events={Count}",
            session.ExecutionId, result.Ok, manifest.Events.Count);

        return result.Ok
            ? JsonSerializer.Serialize(new { ok = true, events = manifest.Events.Count, path = RenderEventManifest.SandboxPath })
            : JsonSerializer.Serialize(new { ok = false, errors = new[] { "public/events.json could not be written." } });
    }

    /// <summary>
    /// Uploads the sandbox's event manifest as <paramref name="storageKey"/>'s sidecar. Null when there is
    /// no manifest (the render result then carries no event fields at all, exactly as before); otherwise the
    /// number of events uploaded (0 when the file was refused) and what was wrong with it. Never throws
    /// (cancellation aside) and never fails the render, which is already uploaded and recorded.
    /// </summary>
    private async Task<(int Count, IReadOnlyList<string> Warnings)?> WriteRenderEventsSidecarAsync(
        ISandboxSession session, string storageKey, CompositionInfo? composition, int declaredFps)
    {
        string raw = await TryReadSandboxFileRaw(session, RenderEventManifest.SandboxPath);
        if (string.IsNullOrWhiteSpace(raw))
        {
            // QA-P1-19 item 2: a re-render that declares no events must not leave the PREVIOUS render's
            // anchors at the same key; they would point at moments this film no longer has.
            await DeleteStaleSidecarAsync(storageKey);
            return null;
        }

        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Parse(raw);
        if (manifest is null)
        {
            await DeleteStaleSidecarAsync(storageKey);
            return (0, BoundEventWarnings(errors));
        }

        double? fps = composition?.Fps is > 0 ? composition.Fps : declaredFps > 0 ? declaredFps : null;
        if (fps is null)
        {
            await DeleteStaleSidecarAsync(storageKey);
            return (0, ["public/events.json was not uploaded: the composition's frame rate is unknown — pass `fps` to the render."]);
        }

        List<string> warnings = [];
        List<RenderEvent> kept = manifest.Events.ToList();
        if (composition is { DurationInFrames: > 0 } c)
        {
            int beyond = kept.RemoveAll(e => e.Frame >= c.DurationInFrames);
            if (beyond > 0)
                warnings.Add($"{beyond} event(s) are past the composition's last frame ({c.DurationInFrames - 1}) and were left out.");
        }

        if (kept.Count == 0)
        {
            await DeleteStaleSidecarAsync(storageKey);
            return (0, BoundEventWarnings(warnings));
        }

        string? sidecarKey = RenderEventManifest.SidecarKeyFor(storageKey);
        if (sidecarKey is null)
            return (0, ["public/events.json was not uploaded: this render has no output key to attach it to."]);

        try
        {
            RenderEventManifest uploaded = manifest with { Fps = fps, Events = kept };
            await using MemoryStream body = new(Encoding.UTF8.GetBytes(uploaded.ToJson()));
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = sidecarKey,
                InputStream = body,
                ContentType = "application/json"
            }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not write the event manifest for {StorageKey}; the render is uploaded and complete", storageKey);
            // The previous sidecar describes a different film; do not leave it behind.
            await DeleteStaleSidecarAsync(storageKey);
            return (0, ["public/events.json could not be uploaded; the render itself is complete."]);
        }

        _logger.LogInformation("Event manifest for {StorageKey}: {Count} event(s)", storageKey, kept.Count);
        return (kept.Count, BoundEventWarnings(warnings));
    }

    /// <summary>
    /// QA-P1-19 item 2: removes the sidecar a PREVIOUS render left at this key. A render key is stable for
    /// the step result, so a re-render that declares no events would otherwise keep the old <c>e{n}</c>
    /// anchors, pointing at moments the new film does not have. Best effort by design: the render is
    /// already uploaded and complete, so this never fails it — the retention sweep is the backstop.
    /// </summary>
    private async Task DeleteStaleSidecarAsync(string storageKey)
    {
        string? sidecarKey = RenderEventManifest.SidecarKeyFor(storageKey);
        if (sidecarKey is null)
            return;

        try
        {
            await _s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = sidecarKey
            }, CancellationToken.None);
            _logger.LogInformation("Event manifest for {StorageKey}: no events this render, stale sidecar removed", storageKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not remove the stale event manifest for {StorageKey}; the render is uploaded and complete", storageKey);
        }
    }

    private static IReadOnlyList<string> BoundEventWarnings(IReadOnlyList<string> warnings) =>
        warnings.Count <= MaxReportedEventWarnings
            ? warnings
            : [.. warnings.Take(MaxReportedEventWarnings), $"... and {warnings.Count - MaxReportedEventWarnings} more"];
}
