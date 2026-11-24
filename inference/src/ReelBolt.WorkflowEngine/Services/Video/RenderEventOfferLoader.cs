using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// T-110: finds the render event sidecars an analysis can offer as <c>e{n}</c> anchors (docs/video-editing.md
/// "Event anchors inside a composition"):
/// <list type="bullet">
/// <item>every analysed source whose resolved object is a render output with a sidecar (its events on the
/// analysed window, trim-adjusted) — a render COPY saved into <c>userFiles/</c> keeps its own sidecar
/// (QA-P1-12), so it offers the same events as the render it came from;</item>
/// <item>the insert content each downstream <c>VideoCompile</c> that consumes THIS analysis
/// (<c>AnalysisStepOrder</c>) with <c>EnableSfx</c> and <c>EnableInserts</c> pins on its deterministic path —
/// <c>InsertContentRef</c>, <c>InsertContentProjectFileId</c>, every <c>InsertContents</c> entry — resolved
/// exactly as the compile resolves it (<see cref="InsertContentRefResolver"/>, the project's own file list).</item>
/// </list>
/// Fail-open: anything unresolvable, absent or unreadable contributes nothing and never fails the analysis.
/// A <c>Step</c> reference to a step that has not run yet resolves to nothing at analysis time.
/// </summary>
internal static class RenderEventOfferLoader
{
    /// <summary>The bytes a sidecar may be; a validated manifest of 200 events is far below this.</summary>
    private const int MaxSidecarBytes = 256 * 1024;

    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>One analysed source: the object it resolved to and the analysed window of it.</summary>
    public sealed record SourceWindow(int SourceIndex, string ResolvedStorageKey, double? TrimInSec, double DurationSec);

    public static async Task<List<RenderEventAnchors.EventSource>> LoadAsync(
        IReadOnlyList<SourceWindow> sources,
        IReadOnlyList<WorkflowStep> allSteps,
        int analyzeStepOrder,
        Guid projectId,
        IReadOnlyList<StepOutputHistoryEntry> history,
        IProjectFileWorkspace projectFiles,
        IServiceScopeFactory? scopes,
        IMediaWorkspace workspace,
        CancellationToken ct)
    {
        List<RenderEventAnchors.EventSource> found = [];
        int staged = 0;

        foreach (SourceWindow source in sources)
        {
            if (await TryReadAsync(source.ResolvedStorageKey) is { } manifest)
                found.Add(RenderEventAnchors.ForSource(manifest, source.SourceIndex, source.TrimInSec ?? 0, source.DurationSec));
        }

        foreach (string contentKey in await InsertContentKeysAsync(allSteps, analyzeStepOrder, projectId, history, projectFiles, scopes, ct))
        {
            if (await TryReadAsync(contentKey) is { } manifest)
                found.Add(RenderEventAnchors.ForInsertContent(manifest, contentKey));
        }

        return found;

        async Task<RenderEventManifest?> TryReadAsync(string renderKey)
        {
            string? sidecarKey = RenderEventManifest.SidecarKeyFor(renderKey);
            if (sidecarKey is null || !sidecarKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal))
                return null;

            try
            {
                string virtualPath = $"render-events-{staged++}.json";
                await workspace.StageAsync(sidecarKey, virtualPath, ct);
                byte[] bytes = await workspace.ReadAsync(virtualPath, MaxSidecarBytes, ct);
                (RenderEventManifest? manifest, _) = RenderEventManifest.Parse(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
                return manifest is { Fps: > 0 } ? manifest : null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // No sidecar (the usual case) or an unreadable one: nothing to offer.
                return null;
            }
        }
    }

    /// <summary>The distinct storage keys of the insert content downstream SFX+inserts compiles of this analysis pin.</summary>
    internal static async Task<IReadOnlyList<string>> InsertContentKeysAsync(
        IReadOnlyList<WorkflowStep> allSteps,
        int analyzeStepOrder,
        Guid projectId,
        IReadOnlyList<StepOutputHistoryEntry> history,
        IProjectFileWorkspace projectFiles,
        IServiceScopeFactory? scopes,
        CancellationToken ct)
    {
        List<(InsertContentRef? Ref, Guid? FileId)> requests = [];
        foreach (WorkflowStep step in allSteps.Where(s => s.StepType == StepType.VideoCompile && s.StepOrder > analyzeStepOrder))
        {
            VideoCompileStepConfig? config;
            try
            {
                config = string.IsNullOrWhiteSpace(step.VideoCompileConfigJson)
                    ? null
                    : JsonSerializer.Deserialize<VideoCompileStepConfig>(step.VideoCompileConfigJson, ConfigJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (config is null || config.AnalysisStepOrder != analyzeStepOrder || !config.EnableSfx || !config.EnableInserts)
                continue;

            foreach (VideoInsertContentAssignment entry in config.InsertContents ?? [])
                requests.Add(entry.InsertContentRef is { } r ? (r, null) : (null, entry.ProjectFileId));
            if (config.InsertContentRef is { } configRef)
                requests.Add((configRef, null));
            if (config.InsertContentProjectFileId is { } fileId)
                requests.Add((null, fileId));
        }

        if (requests.Count == 0)
            return [];

        List<string> keys = [];
        IReadOnlyList<ProjectWorkspaceFile>? files = null;
        foreach ((InsertContentRef? reference, Guid? fileId) in requests)
        {
            string? key = null;
            try
            {
                Guid? projectFileId = reference is { Kind: InsertContentRef.ProjectFileKind } ? reference.ProjectFileId : fileId;
                if (reference is not null && reference.Kind != InsertContentRef.ProjectFileKind)
                {
                    key = (await InsertContentRefResolver.ResolveAsync(reference, projectId, history, scopes, ct))?.StorageKey;
                }
                else if (projectFileId is { } id && id != Guid.Empty)
                {
                    files ??= await projectFiles.ListFilesAsync(projectId, ct);
                    key = files.FirstOrDefault(f => f.Id == id)?.StorageKey;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                key = null;
            }

            if (key is not null && !keys.Contains(key, StringComparer.Ordinal))
                keys.Add(key);
        }

        return keys;
    }
}
