using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.DesignSystems;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// T-206: the advisory half of the sandbox write tools (<see cref="AuthoringAdvisories"/>): fonts that will not
/// load and colours the compile would key as the run's chroma plate come back as <c>warnings</c> on the write's
/// own result. Advice only: it never blocks, never fails a write, and costs nothing on a write it has nothing to
/// say about (the scan runs on the new text first; the prior file and the run's plates are read only on a hit).
/// </summary>
public partial class ReactRemotionSandboxTools
{
    /// <summary>Most executions whose plates and declared families are remembered; the oldest is dropped past it.</summary>
    private const int MaxAdvisoryExecutions = 256;

    private static readonly ConcurrentDictionary<Guid, AdvisoryState> AdvisoryStates = new();

    private static readonly JsonSerializerOptions AdvisoryArtifactJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Per execution: the analysis artifacts the plates were read from, the plates, and families declared so far.</summary>
    internal sealed class AdvisoryState
    {
        public string PlateSource { get; set; } = string.Empty;
        public IReadOnlyList<PlateKey> Plates { get; set; } = [];
        public HashSet<string> DeclaredFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ColourAdvisedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long Touched { get; set; }
    }

    private static long _advisoryClock;

    /// <summary>
    /// The warnings for writing <paramref name="after"/> to <paramref name="path"/>; <paramref name="before"/> is the
    /// file's prior text when the caller has it, else it is read here only if the new text has a candidate finding.
    /// Never throws (cancellation aside): any failure is no advice.
    /// </summary>
    private async Task<IReadOnlyList<string>> AuthoringWarningsAsync(ISandboxSession session, string path, string? before, string after, bool beforeKnown)
    {
        if (!AuthoringAdvisories.IsScanned(path))
            return [];

        try
        {
            AdvisoryState state = StateFor(session.ExecutionId);
            List<string> known = [.. SandboxFonts.Families.Select(f => f.Family), .. DesignSystemFamilies()];
            lock (state)
                known.AddRange(state.DeclaredFamilies);

            // A first pass over the new text alone (the run's plates only when it has a colour at all): most
            // writes stop here, before any read of the prior file.
            IReadOnlyList<PlateKey> plates = AuthoringAdvisories.HasColourLiteral(after)
                ? await RunPlatesAsync(session.ExecutionId, state)
                : [];
            IReadOnlyList<string> candidates = AuthoringAdvisories.Scan(path, null, after, known, plates);
            RememberDeclarations(state, after);
            string? paletteAdvice = DesignSystemColourWarning(state, path, after, DesignSystemPaletteIfColoured(after));
            if (paletteAdvice is not null)
                candidates = [.. candidates, paletteAdvice];
            if (candidates.Count == 0)
                return [];

            string? prior = beforeKnown ? before : await TryReadSandboxFileRaw(session, path);
            if (string.IsNullOrEmpty(prior))
                return candidates;
            IReadOnlyList<string> refined = AuthoringAdvisories.Scan(path, prior, after, known, plates);
            return paletteAdvice is null ? refined : [.. refined, paletteAdvice];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Authoring advisories skipped for {Path} in execution {ExecutionId}", path, session.ExecutionId);
            return [];
        }
    }

    /// <summary>The design-system colour reminder for this write, at most once per file per run; null when none applies.</summary>
    internal static string? DesignSystemColourWarning(AdvisoryState state, string path, string after, IReadOnlyDictionary<string, string> palette)
    {
        if (palette.Count == 0 || !AuthoringAdvisories.IsScanned(path))
            return null;
        lock (state)
        {
            if (state.ColourAdvisedPaths.Contains(path))
                return null;
        }

        string? advice = AuthoringAdvisories.DesignSystemColourAdvice(path, after, palette);
        if (advice is not null)
        {
            lock (state)
            {
                if (!state.ColourAdvisedPaths.Add(path))
                    return null;
            }
        }

        return advice;
    }

    private static IReadOnlyDictionary<string, string> DesignSystemPaletteIfColoured(string after) =>
        AuthoringAdvisories.HasColourLiteral(after) ? DesignSystemPalette() : new Dictionary<string, string>();

    /// <summary>The colour tokens of the design system pinned to this run: normalised <c>rrggbb</c> to token name.</summary>
    internal static IReadOnlyDictionary<string, string> DesignSystemPalette()
    {
        if (DesignSystemAmbient.Current?.Profile.Design is not JsonObject design)
            return new Dictionary<string, string>();

        Dictionary<string, string> palette = [];
        Walk(design, "", 0);
        return palette;

        void Walk(JsonNode? node, string name, int depth)
        {
            if (depth > 12 || palette.Count >= 64)
                return;
            switch (node)
            {
                case JsonObject obj:
                    if (obj["$type"] is JsonValue type && type.TryGetValue(out string? t) && t == "color")
                    {
                        string? hex = obj["$value"] switch
                        {
                            JsonValue v when v.TryGetValue(out string? sv) => sv,
                            JsonObject o when o["hex"] is JsonValue hv && hv.TryGetValue(out string? h) => h,
                            _ => null
                        };
                        string? norm = NormaliseHex(hex);
                        if (norm is not null)
                            palette.TryAdd(norm, string.IsNullOrEmpty(name) ? "color" : name);
                    }

                    foreach (KeyValuePair<string, JsonNode?> child in obj)
                    {
                        if (child.Key.StartsWith('$'))
                            continue;
                        Walk(child.Value, string.IsNullOrEmpty(name) ? child.Key : $"{name}.{child.Key}", depth + 1);
                    }

                    break;
            }
        }
    }

    private static string? NormaliseHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#')
            return null;
        string v = value[1..].Trim().ToLowerInvariant();
        if (v.Length is 3 or 4)
            v = string.Concat(v.Select(c => new string(c, 2)));
        if (v.Length == 8)
            v = v[..6];
        return v.Length == 6 && v.All(Uri.IsHexDigit) ? v : null;
    }

    private static AdvisoryState StateFor(Guid executionId)
    {
        if (AdvisoryStates.Count >= MaxAdvisoryExecutions && !AdvisoryStates.ContainsKey(executionId))
        {
            Guid oldest = AdvisoryStates.MinBy(p => p.Value.Touched).Key;
            AdvisoryStates.TryRemove(oldest, out _);
        }

        AdvisoryState state = AdvisoryStates.GetOrAdd(executionId, _ => new AdvisoryState());
        state.Touched = Interlocked.Increment(ref _advisoryClock);
        return state;
    }

    private static void RememberDeclarations(AdvisoryState state, string text)
    {
        List<string> declared = AuthoringAdvisories.DeclaredFamilies(text).ToList();
        if (declared.Count == 0)
            return;
        lock (state)
            state.DeclaredFamilies.UnionWith(declared.Take(64));
    }

    /// <summary>The font families of the design system pinned to this run (<c>$type: fontFamily</c> tokens).</summary>
    private static IEnumerable<string> DesignSystemFamilies()
    {
        if (DesignSystemAmbient.Current?.Profile.Design is not JsonObject design)
            return [];

        List<string> families = [];
        Walk(design, 0);
        return families;

        void Walk(JsonNode? node, int depth)
        {
            if (depth > 12 || families.Count > 32)
                return;
            switch (node)
            {
                case JsonObject obj:
                    if (obj["$type"] is JsonValue type && type.TryGetValue(out string? t) && t == "fontFamily")
                    {
                        foreach (JsonNode? v in obj["$value"] is JsonArray array ? array : [obj["$value"]])
                        {
                            if (v is JsonValue value && value.TryGetValue(out string? family) && !string.IsNullOrWhiteSpace(family))
                                families.Add(family.Trim());
                        }
                    }

                    foreach (KeyValuePair<string, JsonNode?> child in obj)
                        Walk(child.Value, depth + 1);
                    break;
                case JsonArray arr:
                    foreach (JsonNode? child in arr)
                        Walk(child, depth + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// The chroma plates this run's VideoAnalyze steps tracked, read once per set of analysis artifacts. Empty when
    /// the run has no analysis, or the analysis offered no chroma screen.
    /// </summary>
    private async Task<IReadOnlyList<PlateKey>> RunPlatesAsync(Guid executionId, AdvisoryState state)
    {
        List<string> keys;
        try
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            keys = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.WorkflowExecutionId == executionId
                            && r.Status == StepStatus.Completed
                            && r.ArtifactStorageKey != null
                            && r.WorkflowStep.StepType == StepType.VideoAnalyze)
                .Select(r => r.ArtifactStorageKey!)
                .ToListAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No plates is no colour advice; the font advice still runs.
            _logger.LogDebug(ex, "Could not list the analysis artifacts of execution {ExecutionId} for authoring advisories", executionId);
            return [];
        }

        string source = string.Join("|", keys.Order(StringComparer.Ordinal));
        lock (state)
        {
            if (source == state.PlateSource)
                return state.Plates;
        }

        List<PlateKey> plates = [];
        WorkflowExecutionContext ctx = RequireContext();
        foreach (string key in keys.Take(4))
        {
            string local = Path.Combine(Path.GetTempPath(), $"reelbolt-advisory-{Guid.NewGuid():N}.json");
            try
            {
                await _projectFileWorkspace.DownloadStorageKeyToFileAsync(ctx.ProjectId, key, local, CancellationToken.None);
                VideoAnalysisArtifact? artifact = JsonSerializer.Deserialize<VideoAnalysisArtifact>(
                    (await File.ReadAllTextAsync(local)).TrimStart('﻿'), AdvisoryArtifactJsonOptions);
                HashSet<string> offered = new(artifact?.OfferedInsertRegionIds ?? [], StringComparer.Ordinal);
                foreach (VideoInsertRegionTrack region in artifact?.InsertRegions ?? [])
                {
                    if (region.Method != "chroma" || !offered.Contains(region.Id) || string.IsNullOrWhiteSpace(region.ColorName))
                        continue;
                    VideoInsertChromaKey? k = region.ChromaKey;
                    plates.Add(new PlateKey(region.Id, region.ColorName, k?.R ?? 0, k?.G ?? 0, k?.B ?? 0, k?.Similarity, k?.PlateDominance));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Could not read analysis artifact {Key} for authoring advisories", key);
            }
            finally
            {
                try { File.Delete(local); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        lock (state)
        {
            state.PlateSource = source;
            state.Plates = plates;
        }

        return plates;
    }
}
