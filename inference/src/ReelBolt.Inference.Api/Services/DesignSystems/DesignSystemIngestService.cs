using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.Shared.IO;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>
/// The Phase-2 ingest path (SPEC 9, 8.1): reads the Compose step's build artifact and turns it into
/// Draft design-system versions. The engine calls it with the on-behalf-of token of the execution's
/// initiator, so the ordinary <c>[Authorize]</c> + Edit project check is the whole auth story; this
/// service only verifies that the execution belongs to the project and that the key is under the
/// execution's own prefix.
/// </summary>
public sealed class DesignSystemIngestService(
    InferenceApiDbContext db,
    IDesignSystemService designSystems,
    IFileStorageService storage,
    ILogger<DesignSystemIngestService> logger) : IDesignSystemIngestService
{
    /// <summary>SPEC 8.1: the largest artifact accepted, in bytes.</summary>
    internal const int MaxArtifactBytes = 2 * 1024 * 1024;

    /// <summary>The artifact schema this ingest understands.</summary>
    private const string ArtifactSchema = "reelbolt.design-system-build/1";

    private static readonly JsonSerializerOptions StepConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The one key prefix an artifact may live under (SPEC 8).</summary>
    internal static bool HasValidPrefix(string key, Guid projectId, Guid executionId) =>
        key is not null
        && key.StartsWith(
            $"projects/{projectId}/agentFiles/design-system/{executionId}/",
            StringComparison.Ordinal)
        && !key.Contains("..", StringComparison.Ordinal)
        && !key.Contains('\\');

    /// <summary>The server-built artifact key: the caller supplies no part of it but the step order.</summary>
    internal static string BuildArtifactKey(Guid projectId, Guid executionId, int stepOrder) =>
        $"projects/{projectId}/agentFiles/design-system/{executionId}/step-{stepOrder}-profile.json";

    /// <summary>The step orders of the workflow's DesignSystem steps whose op is Compose, ascending.</summary>
    private async Task<List<int>> ComposeStepOrdersAsync(Guid workflowDefinitionId, CancellationToken ct)
    {
        var steps = await db.WorkflowSteps
            .AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == workflowDefinitionId && s.StepType == StepType.DesignSystem)
            .OrderBy(s => s.StepOrder)
            .Select(s => new { s.StepOrder, s.DesignSystemConfigJson })
            .ToListAsync(ct);

        var orders = new List<int>();
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.DesignSystemConfigJson))
                continue;

            try
            {
                DesignSystemStepConfig? config = JsonSerializer.Deserialize<DesignSystemStepConfig>(
                    step.DesignSystemConfigJson, StepConfigOptions);
                if (config?.Op == DesignSystemOp.Compose)
                    orders.Add(step.StepOrder);
            }
            catch (JsonException)
            {
            }
        }

        return orders;
    }

    public async Task<DesignSystemIngestResult> IngestAsync(
        Guid projectId, Guid userId, Guid executionId, int stepOrder, CancellationToken ct)
    {
        WorkflowExecution? execution = await db.WorkflowExecutions
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.Id == executionId, ct);
        if (execution is null || execution.ProjectId != projectId)
            return new DesignSystemIngestResult(DesignSystemIngestStatus.NotFound);

        // stepOrder 0 (omitted by the caller) means "the execution's Compose step".
        List<int> composeOrders = await ComposeStepOrdersAsync(execution.WorkflowDefinitionId, ct);
        if (stepOrder <= 0)
        {
            if (composeOrders.Count == 0)
                return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidArtifactKey);

            stepOrder = composeOrders[0];
        }
        else if (!composeOrders.Contains(stepOrder))
        {
            return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidArtifactKey);
        }

        string key = BuildArtifactKey(projectId, executionId, stepOrder);
        if (!HasValidPrefix(key, projectId, executionId))
            return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidArtifactKey);

        // Size cap BEFORE the read.
        StoredObjectMetadata? metadata = await storage.GetMetadataAsync(projectId, key, ct);
        if (metadata is null)
            return new DesignSystemIngestResult(DesignSystemIngestStatus.ArtifactNotFound);
        if (metadata.SizeBytes > MaxArtifactBytes)
            return new DesignSystemIngestResult(DesignSystemIngestStatus.ArtifactTooLarge);

        await using Stream stream = await storage.DownloadAsync(projectId, key, ct);
        BoundedText bounded = await BoundedTextReader.ReadAsync(stream, MaxArtifactBytes, ct);
        if (bounded.Truncated)
            return new DesignSystemIngestResult(DesignSystemIngestStatus.ArtifactTooLarge);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bounded.Text);
        }
        catch
        {
            node = null;
        }

        if (node is not JsonObject root)
            return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

        if (!string.Equals(GetString(root, "schema"), ArtifactSchema, StringComparison.Ordinal))
            return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

        JsonObject? design = root["design"] as JsonObject;
        JsonObject? productMap = root["productMap"] as JsonObject;
        JsonArray? sources = root["sources"] as JsonArray;
        JsonObject? coverage = root["coverage"] as JsonObject;

        // Idempotent per workflow_execution_id: a second call answers the same ids and writes nothing.
        List<DesignSystemVersion> existing = await db.DesignSystemVersions
            .Where(v => v.WorkflowExecutionId == executionId)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            return new DesignSystemIngestResult(
                DesignSystemIngestStatus.Ok,
                existing.Select(v => v.Id).ToList(),
                existing.Select(ToSummary).ToList());
        }

        if (design is null && productMap is null)
            return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

        // Pre-validate BOTH documents before any write, so an invalid artifact stores NOTHING.
        List<Guid> projectFileIds = await db.ProjectFiles
            .Where(f => f.ProjectId == projectId && f.CsamStatus != CsamScanStatus.Quarantined)
            .Select(f => f.Id)
            .ToListAsync(ct);
        HashSet<Guid> projectFileSet = projectFileIds.ToHashSet();
        var context = new DesignValidationContext(projectFileSet.Contains, DesignSystemService.BundledFontNames);

        // The build is a candidate, not a replacement: tokens the current approved version locked
        // survive it, and the draft is based on that version so approving it is not a stale base.
        DesignSystemVersion? currentDesign = await designSystems.GetCurrentApprovedAsync(
            projectId, DesignSystemKinds.Design, ct);
        DesignSystemVersion? currentProductMap = await designSystems.GetCurrentApprovedAsync(
            projectId, DesignSystemKinds.ProductMap, ct);

        if (design is not null)
        {
            if (currentDesign is not null && JsonNode.Parse(currentDesign.DocumentJson) is JsonObject currentDocument)
                design = DesignDocumentMerger.Merge(currentDocument, [design]);

            DesignValidationResult validation = DesignDocumentValidator.Validate(design, context);
            if (!validation.IsValid)
                return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);
        }

        ProductMapDocument? map = null;
        if (productMap is not null)
        {
            map = ProductMapJson.Parse(productMap.ToJsonString());
            if (map is null)
                return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

            DesignValidationResult validation = ProductMapValidator.Validate(map);
            if (!validation.IsValid)
                return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);
        }

        // Sources are checked with the documents, before any write: a bad one stores nothing.
        // The rows hang off the design version when both kinds exist, else off the single version.
        var inputs = new List<DesignSourceInput>();
        if (sources is not null)
        {
            foreach (JsonNode? item in sources)
            {
                if (item is not JsonObject source)
                    continue;

                string? path = GetString(source, "path");
                string? importer = GetString(source, "importer");
                string? sha256 = GetString(source, "sha256");
                if (path is null || importer is null || sha256 is null)
                    continue;

                Guid? fileId = GetGuid(source, "projectFileId");
                bool namesFile = source["projectFileId"] is JsonValue named && named.GetValueKind() != JsonValueKind.Null;
                if (namesFile && (fileId is null || !projectFileSet.Contains(fileId.Value)))
                    return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

                long size = 0;
                if (source["sizeBytes"] is JsonValue sizeValue
                    && sizeValue.GetValueKind() != JsonValueKind.Null
                    && (!sizeValue.TryGetValue(out size) || size < 0))
                {
                    return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);
                }

                inputs.Add(new DesignSourceInput(
                    path, importer, sha256, fileId, size, GetString(source, "sourceRevision")));
            }
        }

        DesignVersionOrigin designOrigin = OriginOfDesign(design);
        DesignVersionOrigin productMapOrigin = OriginOfProductMap(productMap);
        string? coverageJson = coverage?.ToJsonString();

        var versionIds = new List<Guid>();
        var summaries = new List<DesignVersionSummaryResponse>();
        Guid? designSystemId = null;
        Guid? designVersionId = null;
        Guid? productMapSystemId = null;
        Guid? productMapVersionId = null;

        if (design is not null)
        {
            DesignSystemWriteResult write = await designSystems.CreateDraftAsync(
                projectId, userId, DesignSystemKinds.Design, design, designOrigin,
                workflowExecutionId: executionId, coverageJson: coverageJson,
                sources: [], baseVersionId: currentDesign?.Id, ct);
            if (write.Status == DesignSystemWriteStatus.ValidationFailed || write.Version is null)
                return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

            designVersionId = write.Version.Id;
            designSystemId = write.Version.DesignSystemId;
            versionIds.Add(write.Version.Id);
            summaries.Add(ToSummary(write.Version));
        }

        if (productMap is not null && map is not null)
        {
            string mapJson = ProductMapJson.Serialize(map);
            JsonObject mapNode = JsonNode.Parse(mapJson)!.AsObject();

            DesignSystemWriteResult write = await designSystems.CreateDraftAsync(
                projectId, userId, DesignSystemKinds.ProductMap, mapNode, productMapOrigin,
                workflowExecutionId: executionId, coverageJson: coverageJson,
                sources: [], baseVersionId: currentProductMap?.Id, ct);
            if (write.Status == DesignSystemWriteStatus.ValidationFailed || write.Version is null)
                return new DesignSystemIngestResult(DesignSystemIngestStatus.InvalidDocument);

            productMapVersionId = write.Version.Id;
            productMapSystemId = write.Version.DesignSystemId;
            versionIds.Add(write.Version.Id);
            summaries.Add(ToSummary(write.Version));
        }

        if (inputs.Count > 0 && (designSystemId ?? productMapSystemId) is { } systemId
            && (designVersionId ?? productMapVersionId) is { } versionId)
        {
            await DesignSystemService.PersistSourcesAsync(db, systemId, versionId, inputs, ct);
        }

        logger.LogInformation(
            "design_system.ingest {ExecutionId} {UserId} {VersionIds}",
            executionId, userId, string.Join(',', versionIds));

        return new DesignSystemIngestResult(DesignSystemIngestStatus.Ok, versionIds, summaries);
    }

    /// <summary>
    /// The design origin from every token's <c>$extensions.com.reelbolt.prov.method</c>: both static
    /// and llm means a merge, llm alone an llm extract, anything else a static extract.
    /// </summary>
    private static DesignVersionOrigin OriginOfDesign(JsonObject? document)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (document is not null)
            CollectDesignMethods(document, methods);

        return Classify(methods);
    }

    private static void CollectDesignMethods(JsonNode? node, HashSet<string> methods)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["$extensions"] is JsonObject extensions
                    && extensions["com.reelbolt"] is JsonObject reelbolt
                    && reelbolt["prov"] is JsonObject provenance
                    && GetString(provenance, "method") is { } method)
                {
                    methods.Add(method);
                }

                foreach (KeyValuePair<string, JsonNode?> entry in obj)
                    CollectDesignMethods(entry.Value, methods);
                break;

            case JsonArray array:
                foreach (JsonNode? item in array)
                    CollectDesignMethods(item, methods);
                break;
        }
    }

    /// <summary>
    /// The product-map origin from every route/component <c>source</c> and every
    /// <c>provenance.*.method</c>.
    /// </summary>
    private static DesignVersionOrigin OriginOfProductMap(JsonObject? document)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (document is null)
            return Classify(methods);

        CollectSourceMethods(document["routes"] as JsonArray, methods);
        CollectSourceMethods(document["components"] as JsonArray, methods);

        if (document["provenance"] is JsonObject provenance)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in provenance)
            {
                if (entry.Value is JsonObject item && GetString(item, "method") is { } method)
                    methods.Add(method);
            }
        }

        return Classify(methods);
    }

    private static void CollectSourceMethods(JsonArray? array, HashSet<string> methods)
    {
        if (array is null)
            return;

        foreach (JsonNode? item in array)
        {
            if (item is JsonObject obj && GetString(obj, "source") is { } source)
                methods.Add(source);
        }
    }

    private static DesignVersionOrigin Classify(IReadOnlySet<string> methods)
    {
        bool hasLlm = methods.Contains("llm");
        bool hasStatic = methods.Contains("static");

        if (hasLlm && hasStatic)
            return DesignVersionOrigin.Merge;
        if (hasLlm)
            return DesignVersionOrigin.LlmExtract;
        return DesignVersionOrigin.StaticExtract;
    }

    private static string? GetString(JsonObject? obj, string name)
    {
        if (obj?[name] is JsonValue value && value.TryGetValue<string>(out string? text))
            return text;

        return null;
    }

    private static Guid? GetGuid(JsonObject obj, string name)
    {
        if (obj[name] is not JsonValue value)
            return null;

        if (value.TryGetValue<Guid>(out Guid guid))
            return guid;

        if (value.TryGetValue<string>(out string? text) && Guid.TryParse(text, out Guid parsed))
            return parsed;

        return null;
    }

    private static DesignVersionSummaryResponse ToSummary(DesignSystemVersion version) => new(
        version.Id,
        version.DesignSystemId,
        version.VersionNumber,
        version.State.ToString(),
        version.Origin.ToString(),
        version.DocumentDigest,
        version.BaseVersionId,
        version.WorkflowExecutionId,
        version.CreatedByUserId,
        version.ApprovedByUserId,
        version.ApprovedAt,
        version.CreatedAt);

    private static DesignVersionSummaryResponse ToSummary(DesignVersionResponse version) => new(
        version.Id,
        version.DesignSystemId,
        version.VersionNumber,
        version.State,
        version.Origin,
        version.DocumentDigest,
        version.BaseVersionId,
        version.WorkflowExecutionId,
        version.CreatedByUserId,
        version.ApprovedByUserId,
        version.ApprovedAt,
        version.CreatedAt);
}
