using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Coverage;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.Merge;
using ReelBolt.Shared.DesignSystems.ProductMap;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.DesignSystem"/> (SPEC §8): the deterministic, non-LLM
/// Parse/Compose/Require operations that read a project's design sources into a static candidate
/// artifact, merge optional LLM analyzer fallbacks into the run's design system, and enforce the
/// run's pinned design-system versions. It never throws — every failure is a
/// <see cref="StepStatus.Failed"/> result whose Output is valid JSON naming the reason
/// (<c>output_json</c> is <c>jsonb</c>) — and it holds no chat-client dependency: every model-shaped
/// input arrives as an already-produced prior step output.
/// </summary>
public sealed class DesignSystemStepExecutor : IStepExecutor
{
    /// <summary>The artifact schema identifier written by both Parse and Compose.</summary>
    public const string ArtifactSchema = "reelbolt.design-system-build/1";

    /// <summary>The largest serialized artifact allowed; trailing sources/diagnostics are dropped beyond it.</summary>
    public const int ArtifactMaxBytes = 2 * 1024 * 1024;

    public static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.Ordinal)
    {
        "css", "scss", "sass", "less",
        "json", "md", "mdx", "ts", "tsx", "js", "jsx", "vue", "svelte", "astro", "yaml", "yml"
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.Ordinal)
    {
        "tsx", "jsx", "ts", "js", "vue", "svelte", "astro", "mdx"
    };

    private static readonly HashSet<string> LockfileNames = new(StringComparer.Ordinal)
    {
        "package-lock.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb", "bun.lock"
    };

    private static readonly string[] DesignGlobs =
    [
        "**/*.css", "**/*.scss", "**/*.sass", "**/*.less",
        "**/tailwind.config.*", "**/*.tokens.json", "**/tokens/**/*.json",
        "**/DESIGN.md", "**/brand/**"
    ];

    private static readonly string[] ExtractorDirectoryNames =
    [
        "app", "pages", "src/app", "src/pages", "routes", "src/routes",
        "components", "src/components", "ui", "lib/components"
    ];

    private static readonly string[] ExtractorFileGlobs =
    [
        "tsconfig*.json", "next.config.*", "vite.config.*", "remix.config.*",
        "nuxt.config.*", "svelte.config.*"
    ];

    private readonly IProjectFileWorkspace _workspace;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DesignSystemStepExecutor> _logger;

    public DesignSystemStepExecutor(
        IProjectFileWorkspace workspace,
        IServiceScopeFactory scopeFactory,
        IHttpClientFactory httpClientFactory,
        ILogger<DesignSystemStepExecutor> logger)
    {
        _workspace = workspace;
        _scopeFactory = scopeFactory;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public StepType StepType => StepType.DesignSystem;

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        WorkflowStep step = context.Step;
        CancellationToken ct = context.CancellationToken;
        string op = "parse";

        try
        {
            DesignSystemStepConfig? config = null;
            List<string> errors = [];
            if (string.IsNullOrWhiteSpace(step.DesignSystemConfigJson))
            {
                errors.Add("DesignSystemConfigJson is required.");
            }
            else
            {
                try
                {
                    config = JsonSerializer.Deserialize<DesignSystemStepConfig>(step.DesignSystemConfigJson, ConfigJsonOptions);
                    if (config is null)
                        errors.Add("DesignSystemConfigJson must be a JSON object.");
                    else
                        errors.AddRange(config.Validate());
                }
                catch (JsonException ex)
                {
                    errors.Add($"DesignSystemConfigJson is not valid JSON: {ex.Message}");
                }
            }

            if (config is null || errors.Count > 0)
                return Failure(context, "DESIGN_CONFIG_INVALID", string.Join(" ", errors), op);

            op = config.Op switch
            {
                DesignSystemOp.Compose => "compose",
                DesignSystemOp.Require => "require",
                _ => "parse"
            };

            return config.Op switch
            {
                DesignSystemOp.Compose => await ComposeAsync(context, config, ct),
                DesignSystemOp.Require => RequireAsync(context, config),
                _ => await ParseAsync(context, config, ct)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DesignSystem step {StepOrder} failed unexpectedly.", step.StepOrder);
            return Failure(context, "DESIGN_PARSE_FAILED", ex.Message, op);
        }
    }

    // -----------------------------------------------------------------
    // Parse
    // -----------------------------------------------------------------

    private async Task<StepExecutionResult> ParseAsync(
        StepExecutionContext context, DesignSystemStepConfig config, CancellationToken ct)
    {
        DesignSystemParseOptions parse = config.Parse ?? new DesignSystemParseOptions();
        Guid projectId = context.Execution.ProjectId;
        WorkflowStep step = context.Step;

        IReadOnlyList<ProjectWorkspaceFile> allFiles = await _workspace.ListFilesAsync(projectId, ct);

        var ownDiagnostics = new List<ImportDiagnostic>();
        List<Candidate> candidates = SelectCandidates(allFiles, parse, ownDiagnostics);

        var importFiles = new List<ImportFile>();
        var sizeById = new Dictionary<Guid, long>();
        long totalBytes = 0;
        bool totalCapReported = false;

        foreach (Candidate candidate in candidates)
        {
            sizeById[candidate.File.Id] = candidate.File.SizeBytes;

            if (candidate.IsLockfile)
            {
                // Name-only: the extractor's package-manager detection needs the path, never the bytes.
                importFiles.Add(new ImportFile(
                    candidate.Path, ReadOnlyMemory<byte>.Empty, Sha256Hex(ReadOnlySpan<byte>.Empty), candidate.File.Id));
                continue;
            }

            if (candidate.File.SizeBytes > parse.MaxBytesPerFile)
            {
                ownDiagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "FILE_TOO_LARGE",
                    candidate.Path,
                    $"file is {candidate.File.SizeBytes} bytes, over the {parse.MaxBytesPerFile}-byte per-file cap."));
                continue;
            }

            if (totalBytes + candidate.File.SizeBytes > parse.MaxTotalBytes)
            {
                if (!totalCapReported)
                {
                    ownDiagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        "TRUNCATED",
                        string.Empty,
                        $"the {parse.MaxTotalBytes}-byte total cap was reached; remaining candidates were not read."));
                    totalCapReported = true;
                }

                break;
            }

            totalBytes += candidate.File.SizeBytes;

            string text;
            try
            {
                text = await _workspace.ReadFileAsync(projectId, candidate.File.Id.ToString(), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DesignSystem parse could not read file {FileId}.", candidate.File.Id);
                ownDiagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn, "FILE_READ_FAILED", candidate.Path, ex.Message));
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
            importFiles.Add(new ImportFile(candidate.Path, bytes, Sha256Hex(bytes), candidate.File.Id));
        }

        DesignImportResult designImport = DesignImportRegistry.ImportAll(importFiles);
        ProductMapExtraction mapExtraction = ProductMapExtractor.Extract(importFiles);
        CoverageReport coverage = DesignCoverage.Compute(designImport.Design, mapExtraction.Map);

        var allDiagnostics = new List<ImportDiagnostic>();
        allDiagnostics.AddRange(designImport.Diagnostics);
        allDiagnostics.AddRange(mapExtraction.Diagnostics);
        allDiagnostics.AddRange(ownDiagnostics);

        JsonArray sourcesNode = BuildSourcesNode(designImport.Sources, sizeById);
        JsonArray diagnosticsNode = BuildDiagnosticsNode(allDiagnostics);
        JsonObject artifact = BuildArtifactJson(
            designImport.Design, ProductMapJson.ToNode(mapExtraction.Map), sourcesNode, diagnosticsNode, coverage);

        string artifactKey = await UploadArtifactAsync(
            projectId, artifact, $"design-system/{context.Execution.Id:D}/step-{step.StepOrder}-candidates.json", ct);

        // each flag asks for the LLM fallback of its own part only, so a run is charged for
        // the style extractor only when the design is actually missing. (Before the template could
        // re-check the design flag after the product analyzers, a missing product map forced the design
        // fallback on too; the template's step 8 Conditional now reads this view directly.)
        bool fallbackProduct = coverage.ProductMap.FallbackRecommended;
        bool fallbackDesign = coverage.Design.FallbackRecommended;

        JsonObject view = new()
        {
            ["coverage"] = JsonSerializer.SerializeToNode(coverage, WebOptions),
            ["designTokenCount"] = coverage.Design.TokenCount,
            ["routeCount"] = coverage.ProductMap.Routes,
            ["componentCount"] = coverage.ProductMap.Components,
            ["fallbackProduct"] = fallbackProduct,
            ["fallbackDesign"] = fallbackDesign
        };
        JsonObject meta = new()
        {
            ["artifactStorageKey"] = artifactKey,
            ["sources"] = sourcesNode.Count,
            ["diagnostics"] = diagnosticsNode.Count
        };

        return Success(context, view, meta, artifactKey);
    }

    // -----------------------------------------------------------------
    // Compose
    // -----------------------------------------------------------------

    private async Task<StepExecutionResult> ComposeAsync(
        StepExecutionContext context, DesignSystemStepConfig config, CancellationToken ct)
    {
        if (config.Compose is not { } compose)
            return Failure(context, "DESIGN_COMPOSE_INVALID", "compose options are required.", "compose");

        Guid projectId = context.Execution.ProjectId;
        WorkflowStep step = context.Step;

        StepOutputHistoryEntry? parseEntry = context.StepOutputHistory
            .FirstOrDefault(h => h.StepOrder == compose.ParseStepOrder);
        if (parseEntry is null || string.IsNullOrWhiteSpace(parseEntry.ArtifactStorageKey))
        {
            return Failure(
                context,
                "DESIGN_COMPOSE_INVALID",
                $"no Parse artifact is available at step order {compose.ParseStepOrder}.",
                "compose");
        }

        string localPath = Path.GetTempFileName();
        JsonObject parsed;
        try
        {
            await _workspace.DownloadStorageKeyToFileAsync(projectId, parseEntry.ArtifactStorageKey, localPath, ct);
            string text = await File.ReadAllTextAsync(localPath, ct);
            parsed = JsonNode.Parse(text) as JsonObject
                ?? throw new JsonException("the Parse artifact is not a JSON object.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DesignSystem compose could not read the Parse artifact.");
            return Failure(context, "DESIGN_COMPOSE_INVALID", ex.Message, "compose");
        }
        finally
        {
            TryDelete(localPath);
        }

        JsonObject? staticDesign = parsed["design"] as JsonObject;
        ProductMapDocument? staticMap = parsed["productMap"] is JsonNode productMapNode
            ? ProductMapJson.Parse(productMapNode.ToJsonString())
            : null;

        var fallbackOrders = new List<int>();
        fallbackOrders.AddRange(compose.DesignFallbackStepOrders ?? []);
        fallbackOrders.AddRange(compose.ProductFallbackStepOrders ?? []);
        fallbackOrders = fallbackOrders.Distinct().ToList();
        Dictionary<int, AgentType> agentTypes = await ResolveAgentTypesAsync(context, fallbackOrders, ct);

        var llmDesigns = new List<JsonObject>();
        var composeDiagnostics = new List<ImportDiagnostic>();
        var composeValidationContext = new DesignValidationContext(_ => true, new HashSet<string>());
        foreach (int order in compose.DesignFallbackStepOrders ?? [])
        {
            StepOutputHistoryEntry? entry = context.StepOutputHistory.FirstOrDefault(h => h.StepOrder == order);
            if (entry is null)
                continue;
            if (!agentTypes.TryGetValue(order, out AgentType agentType) || agentType != AgentType.StyleAndThemeExtractor)
                continue;

            StyleAndThemeOutput? style = TryDeserializeAnalyzer<StyleAndThemeOutput>(entry.Output);
            if (style is null)
            {
                return Failure(
                    context,
                    "DESIGN_COMPOSE_INVALID",
                    $"step {order} did not yield a StyleAndThemeOutput.",
                    "compose");
            }

            // The model's tokens are untrusted text: bad ones are dropped with a warning, never fatal.
            JsonObject? mapped = LegacyAnalyzerMapper.DesignFrom(style, composeDiagnostics);
            if (mapped is not null)
            {
                DesignTokenSanitizer.Sanitize(mapped, composeValidationContext, composeDiagnostics);
                llmDesigns.Add(mapped);
            }
        }

        // Every compose diagnostic is a token that was omitted (unreadable input or failed validation).
        int droppedTokens = composeDiagnostics.Count;

        CodeStructureOutput? codeStructure = null;
        DependencyAnalysisOutput? dependencyAnalysis = null;
        ComponentInventoryOutput? componentInventory = null;
        RouteAndApiOutput? routeAndApi = null;

        foreach (int order in compose.ProductFallbackStepOrders ?? [])
        {
            StepOutputHistoryEntry? entry = context.StepOutputHistory.FirstOrDefault(h => h.StepOrder == order);
            if (entry is null)
                continue;
            if (!agentTypes.TryGetValue(order, out AgentType agentType))
                continue;

            switch (agentType)
            {
                case AgentType.CodeStructureAnalyzer:
                    codeStructure = TryDeserializeAnalyzer<CodeStructureOutput>(entry.Output);
                    if (codeStructure is null)
                        return Failure(context, "DESIGN_COMPOSE_INVALID", $"step {order} did not yield a CodeStructureOutput.", "compose");
                    break;
                case AgentType.DependencyAnalyzer:
                    dependencyAnalysis = TryDeserializeAnalyzer<DependencyAnalysisOutput>(entry.Output);
                    if (dependencyAnalysis is null)
                        return Failure(context, "DESIGN_COMPOSE_INVALID", $"step {order} did not yield a DependencyAnalysisOutput.", "compose");
                    break;
                case AgentType.ComponentInventoryAnalyzer:
                    componentInventory = TryDeserializeAnalyzer<ComponentInventoryOutput>(entry.Output);
                    if (componentInventory is null)
                        return Failure(context, "DESIGN_COMPOSE_INVALID", $"step {order} did not yield a ComponentInventoryOutput.", "compose");
                    break;
                case AgentType.RouteAndApiAnalyzer:
                    routeAndApi = TryDeserializeAnalyzer<RouteAndApiOutput>(entry.Output);
                    if (routeAndApi is null)
                        return Failure(context, "DESIGN_COMPOSE_INVALID", $"step {order} did not yield a RouteAndApiOutput.", "compose");
                    break;
                default:
                    break;
            }
        }

        ProductMapDocument llmMap = LegacyAnalyzerMapper.ProductMapFrom(
            codeStructure, dependencyAnalysis, componentInventory, routeAndApi);

        var designCandidates = new List<JsonObject>();
        if (staticDesign is not null)
            designCandidates.Add(staticDesign);
        designCandidates.AddRange(llmDesigns);
        JsonObject mergedDesign = DesignDocumentMerger.Merge(null, designCandidates);
        ProductMapDocument mergedMap = ProductMapMerger.Merge(staticMap, llmMap);

        // Model prose a little over its soft cap is trimmed with a warning, never fatal; structural
        // problems stay hard failures in the validators below.
        var trimDiagnostics = new List<ImportDiagnostic>();
        mergedMap = ProseClamp.Normalize(mergedMap, trimDiagnostics);
        ProseClamp.NormalizeVoice(mergedDesign, trimDiagnostics);
        int trimmedFields = trimDiagnostics.Count;
        // Trim warnings go first so the 200-entry bound cuts dropped-token noise, not them.
        trimDiagnostics.AddRange(composeDiagnostics);
        composeDiagnostics = DesignTokenSanitizer.CapDiagnostics(trimDiagnostics);

        DesignValidationResult mapValidation = ProductMapValidator.Validate(mergedMap);
        if (!mapValidation.IsValid)
            return Failure(context, "DESIGN_COMPOSE_INVALID", DescribeViolations(mapValidation), "compose");

        DesignValidationResult designValidation = DesignDocumentValidator.Validate(mergedDesign, composeValidationContext);
        if (!designValidation.IsValid)
            return Failure(context, "DESIGN_COMPOSE_INVALID", DescribeViolations(designValidation), "compose");

        CoverageReport coverage = DesignCoverage.Compute(mergedDesign, mergedMap);
        JsonArray sourcesNode = parsed["sources"]?.DeepClone() as JsonArray ?? new JsonArray();
        JsonArray diagnosticsNode = parsed["diagnostics"]?.DeepClone() as JsonArray ?? new JsonArray();
        foreach (JsonNode? added in BuildDiagnosticsNode(composeDiagnostics))
            diagnosticsNode.Add(added!.DeepClone());
        JsonObject artifact = BuildArtifactJson(
            mergedDesign, ProductMapJson.ToNode(mergedMap), sourcesNode, diagnosticsNode, coverage);

        string artifactKey;
        try
        {
            artifactKey = await UploadArtifactAsync(
                projectId, artifact, $"design-system/{context.Execution.Id:D}/step-{step.StepOrder}-profile.json", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DesignSystem compose could not write the profile artifact.");
            return Failure(context, "DESIGN_ARTIFACT_WRITE_FAILED", ex.Message, "compose");
        }

        JsonObject meta = new()
        {
            ["artifactStorageKey"] = artifactKey,
            ["diagnostics"] = composeDiagnostics.Count,
            ["droppedTokens"] = droppedTokens,
            ["trimmedFields"] = trimmedFields
        };
        if (compose.Ingest)
            await TryIngestAsync(context, projectId, step, meta, ct);

        JsonObject view = new()
        {
            ["coverage"] = JsonSerializer.SerializeToNode(coverage, WebOptions),
            ["designTokenCount"] = coverage.Design.TokenCount,
            ["routeCount"] = coverage.ProductMap.Routes,
            ["componentCount"] = coverage.ProductMap.Components,
            ["droppedTokens"] = droppedTokens
        };

        return Success(context, view, meta, artifactKey);
    }

    private async Task TryIngestAsync(
        StepExecutionContext context, Guid projectId, WorkflowStep step, JsonObject meta, CancellationToken ct)
    {
        meta["ingest"] = "pending";
        try
        {
            // The named client owns the base address AND the on-behalf-of bearer; the executor must
            // never set an Authorization header itself (InferenceApiAuthHandler does).
            HttpClient client = _httpClientFactory.CreateClient(InferenceApiClient.Name);
            string body = JsonSerializer.Serialize(
                new { executionId = context.Execution.Id.ToString("D"), stepOrder = step.StepOrder }, WebOptions);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(
                $"api/v1/projects/{projectId:D}/design-system/ingest", content, ct);

            if (response.IsSuccessStatusCode)
            {
                meta["ingest"] = "done";
                try
                {
                    string responseBody = await response.Content.ReadAsStringAsync(ct);
                    if (JsonNode.Parse(responseBody) is JsonObject responseObject
                        && responseObject["versionIds"] is JsonNode versionIds)
                    {
                        meta["versionIds"] = versionIds.DeepClone();
                    }
                }
                catch
                {
                    // Best effort: a 2xx already means the ingest happened.
                }
            }
            else
            {
                _logger.LogWarning(
                    "DesignSystem compose ingest returned {StatusCode} for execution {ExecutionId}; the step completes as pending.",
                    (int)response.StatusCode,
                    context.Execution.Id);
                meta["ingest"] = "pending";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "DesignSystem compose ingest failed for execution {ExecutionId}; the step completes as pending.",
                context.Execution.Id);
            meta["ingest"] = "pending";
        }
    }

    // -----------------------------------------------------------------
    // Require
    // -----------------------------------------------------------------

    /// <summary>
    /// Passes only for members the run can actually USE: the pin names them AND the run's ambient state
    /// (<see cref="DesignSystemAmbient.Current"/>, loaded by the executor service with the digest
    /// verified and the ambient enabled) resolved them. A pin that parses but whose document is gone,
    /// edited or unverifiable — or an ambient that is switched off — fails closed with
    /// <c>PROFILE_MISSING</c> instead of letting the run proceed with no blocks.
    /// </summary>
    private static StepExecutionResult RequireAsync(StepExecutionContext context, DesignSystemStepConfig config)
    {
        DesignSystemRequireOptions require = config.Require ?? new DesignSystemRequireOptions();

        JsonObject? pin = null;
        if (!string.IsNullOrWhiteSpace(context.Execution.DesignSystemPinJson))
        {
            try
            {
                pin = JsonNode.Parse(context.Execution.DesignSystemPinJson) as JsonObject;
            }
            catch (JsonException)
            {
                pin = null;
            }
        }

        JsonObject? design = pin?["design"] as JsonObject;
        JsonObject? productMap = pin?["productMap"] as JsonObject;

        if (require.RequireDesign && design is null)
            return Failure(context, "PROFILE_MISSING", "the run's design system pin has no design member.", "require");

        if (require.RequireProductMap && productMap is null)
            return Failure(context, "PROFILE_MISSING", "the run's design system pin has no productMap member.", "require");

        LoadedProfile? loaded = DesignSystemAmbient.Current?.Profile;
        bool designLoadable = design is not null && loaded?.Design is not null;
        bool productLoadable = productMap is not null && loaded?.ProductMap is not null;

        if (require.RequireDesign && !designLoadable)
            return Failure(context, "PROFILE_MISSING", "the pinned design system could not be loaded (missing, changed since it was pinned, or the design system ambient is off).", "require");

        if (require.RequireProductMap && !productLoadable)
            return Failure(context, "PROFILE_MISSING", "the pinned product profile could not be loaded (missing, changed since it was pinned, or the design system ambient is off).", "require");

        JsonObject view = new();
        if (designLoadable)
            view["design"] = new JsonObject { ["versionNumber"] = ReadInt(design!["versionNumber"]) };
        if (productLoadable)
            view["productMap"] = new JsonObject { ["versionNumber"] = ReadInt(productMap!["versionNumber"]) };

        return Success(context, view, new JsonObject(), artifactKey: null);
    }

    // -----------------------------------------------------------------
    // Shared helpers
    // -----------------------------------------------------------------

    private static StepExecutionResult Failure(StepExecutionContext context, string code, string message, string op)
    {
        context.RecordFailure(FailureClassifier.ForCode(code), code);
        JsonObject output = new()
        {
            ["view"] = null,
            ["meta"] = new JsonObject { ["op"] = op },
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return new StepExecutionResult
        {
            Output = output.ToJsonString(WebOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            TokensUsed = 0,
            Status = StepStatus.Failed,
            ErrorDetails = message,
            FailureClass = FailureClassifier.ForCode(code),
            FailureCode = code
        };
    }

    private static StepExecutionResult Success(
        StepExecutionContext context, JsonObject view, JsonObject meta, string? artifactKey)
    {
        JsonObject output = new() { ["view"] = view, ["meta"] = meta };
        return new StepExecutionResult
        {
            Output = output.ToJsonString(WebOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            TokensUsed = 0,
            Status = StepStatus.Completed,
            ArtifactStorageKey = artifactKey
        };
    }

    private async Task<Dictionary<int, AgentType>> ResolveAgentTypesAsync(
        StepExecutionContext context, IReadOnlyList<int> orders, CancellationToken ct)
    {
        var result = new Dictionary<int, AgentType>();
        if (orders.Count == 0)
            return result;

        HashSet<int> orderSet = orders.ToHashSet();
        List<WorkflowStep> steps = context.AllSteps
            .Where(step => orderSet.Contains(step.StepOrder))
            .ToList();
        if (steps.Count == 0)
            return result;

        List<Guid> definitionIds = steps.Select(step => step.AgentDefinitionId).Distinct().ToList();
        using IServiceScope scope = _scopeFactory.CreateScope();
        WorkflowEngineDbContext db = scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
        Dictionary<Guid, AgentType> types = await db.AgentDefinitions.AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id))
            .Select(definition => new { definition.Id, definition.AgentType })
            .ToDictionaryAsync(definition => definition.Id, definition => definition.AgentType, ct);

        foreach (WorkflowStep step in steps)
        {
            if (types.TryGetValue(step.AgentDefinitionId, out AgentType type))
                result[step.StepOrder] = type;
        }

        return result;
    }

    private static T? TryDeserializeAnalyzer<T>(string? output) where T : class =>
        AnalyzerOutputJson.Deserialize<T>(output);

    private static string DescribeViolations(DesignValidationResult result) =>
        string.Join("; ", result.Violations.Select(violation => $"{violation.Code} {violation.Path}: {violation.Message}"));

    private async Task<string> UploadArtifactAsync(
        Guid projectId, JsonObject artifact, string fileName, CancellationToken ct)
    {
        string localPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(localPath, artifact.ToJsonString(WebOptions), ct);
            return await _workspace.UploadArtifactAsync(projectId, localPath, fileName, "application/json", ct);
        }
        finally
        {
            TryDelete(localPath);
        }
    }

    private static JsonObject BuildArtifactJson(
        JsonObject? design, JsonNode? productMap, JsonArray sources, JsonArray diagnostics, CoverageReport coverage)
    {
        var artifact = new JsonObject
        {
            ["schema"] = ArtifactSchema,
            ["design"] = design?.DeepClone(),
            ["productMap"] = productMap?.DeepClone(),
            ["sources"] = sources,
            ["coverage"] = JsonSerializer.SerializeToNode(coverage, WebOptions),
            ["diagnostics"] = diagnostics
        };

        ApplyArtifactSizeCap(artifact);
        return artifact;
    }

    private static void ApplyArtifactSizeCap(JsonObject artifact)
    {
        if (ArtifactByteCount(artifact) <= ArtifactMaxBytes)
            return;

        JsonArray sources = artifact["sources"] as JsonArray ?? new JsonArray();
        JsonArray diagnostics = artifact["diagnostics"] as JsonArray ?? new JsonArray();
        while (ArtifactByteCount(artifact) > ArtifactMaxBytes && (sources.Count > 0 || diagnostics.Count > 0))
        {
            if (diagnostics.Count > 0)
                diagnostics.RemoveAt(diagnostics.Count - 1);
            else
                sources.RemoveAt(sources.Count - 1);
        }

        diagnostics.Add(new JsonObject
        {
            ["severity"] = "Warn",
            ["code"] = "TRUNCATED",
            ["path"] = string.Empty,
            ["message"] = $"artifact exceeded the {ArtifactMaxBytes}-byte cap; trailing sources/diagnostics were dropped."
        });
    }

    private static int ArtifactByteCount(JsonObject artifact) =>
        Encoding.UTF8.GetByteCount(artifact.ToJsonString(WebOptions));

    private static JsonArray BuildSourcesNode(
        IReadOnlyList<ImportSourceRef> sources, IReadOnlyDictionary<Guid, long> sizeById)
    {
        JsonArray array = new JsonArray();
        foreach (ImportSourceRef source in sources)
        {
            long size = source.ProjectFileId is { } id && sizeById.TryGetValue(id, out long known) ? known : 0;
            array.Add(new JsonObject
            {
                ["path"] = source.Path,
                ["importer"] = source.Importer,
                ["sha256"] = source.Sha256,
                ["sizeBytes"] = size,
                ["projectFileId"] = source.ProjectFileId?.ToString("D"),
                ["sourceRevision"] = null
            });
        }

        return array;
    }

    private static JsonArray BuildDiagnosticsNode(IReadOnlyList<ImportDiagnostic> diagnostics)
    {
        JsonArray array = new JsonArray();
        foreach (ImportDiagnostic diagnostic in diagnostics)
        {
            array.Add(new JsonObject
            {
                ["severity"] = diagnostic.Severity.ToString(),
                ["code"] = diagnostic.Code,
                // The one exit of every diagnostic: nothing model-influenced is written unredacted.
                ["path"] = DesignTokenSanitizer.ClipPath(diagnostic.Path),
                ["message"] = DesignTokenSanitizer.ClipText(diagnostic.Message, 300)
            });
        }

        return array;
    }

    private static List<Candidate> SelectCandidates(
        IReadOnlyList<ProjectWorkspaceFile> allFiles, DesignSystemParseOptions parse, List<ImportDiagnostic> diagnostics)
    {
        var candidates = new List<Candidate>();
        if (allFiles is null)
            return candidates;

        var seen = new HashSet<Guid>();
        foreach (ProjectWorkspaceFile file in allFiles)
        {
            if (file is null)
                continue;

            string fileName = file.OriginalFileName ?? string.Empty;
            string path = NormalizePath(file.OriginalPath, fileName);
            bool isLockfile = LockfileNames.Contains(fileName);

            if (!isLockfile && !IsExtractorCandidate(path, fileName) && !IsDesignCandidate(path, parse.ExtraGlobs))
                continue;

            if (!isLockfile && !TextExtensions.Contains(ExtensionOf(fileName)))
                continue;

            if (seen.Add(file.Id))
                candidates.Add(new Candidate(file, path, fileName, isLockfile));
        }

        candidates.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

        if (candidates.Count > parse.MaxFiles)
        {
            candidates = candidates.GetRange(0, parse.MaxFiles);
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                "TRUNCATED",
                string.Empty,
                $"only the first {parse.MaxFiles} candidates were considered."));
        }

        return candidates;
    }

    private static bool IsExtractorCandidate(string path, string fileName)
    {
        if (string.Equals(fileName, "package.json", StringComparison.Ordinal))
            return path.Split('/').Length <= 5;

        foreach (string glob in ExtractorFileGlobs)
        {
            if (SimpleGlob.IsMatch(glob, fileName))
                return true;
        }

        if (string.Equals(fileName, "components.json", StringComparison.Ordinal)
            || string.Equals(fileName, "angular.json", StringComparison.Ordinal))
        {
            return true;
        }

        if (CodeExtensions.Contains(ExtensionOf(fileName)))
        {
            string[] segments = path.Split('/');
            string[] directories = segments.Length > 0 ? segments[..^1] : [];
            foreach (string name in ExtractorDirectoryNames)
            {
                if (HasDirectory(directories, name))
                    return true;
            }
        }

        return path.Contains("rout", StringComparison.Ordinal);
    }

    private static bool IsDesignCandidate(string path, IReadOnlyList<string>? extraGlobs)
    {
        foreach (string glob in DesignGlobs)
        {
            if (SimpleGlob.IsMatch(glob, path))
                return true;
        }

        if (extraGlobs is not null)
        {
            foreach (string? glob in extraGlobs)
            {
                if (!string.IsNullOrEmpty(glob) && SimpleGlob.IsMatch(glob, path))
                    return true;
            }
        }

        return false;
    }

    private static bool HasDirectory(string[] directories, string name)
    {
        if (name.IndexOf('/') < 0)
            return directories.Contains(name, StringComparer.Ordinal);

        string[] parts = name.Split('/');
        for (int i = 0; i + parts.Length <= directories.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < parts.Length; j++)
            {
                if (!string.Equals(directories[i + j], parts[j], StringComparison.Ordinal))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static string NormalizePath(string? originalPath, string fileName)
    {
        string directory = (originalPath ?? string.Empty).Trim('/');
        string combined = directory.Length == 0 ? fileName : directory + "/" + fileName;

        var parts = new List<string>();
        foreach (string segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            parts.Add(segment);
        }

        return string.Join('/', parts);
    }

    private static string ExtensionOf(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot >= 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..].ToLowerInvariant() : string.Empty;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static int? ReadInt(JsonNode? node)
    {
        try
        {
            return node?.GetValue<int>();
        }
        catch
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temp-file cleanup is best effort and must never mask a step result.
        }
    }

    private readonly record struct Candidate(
        ProjectWorkspaceFile File, string Path, string FileName, bool IsLockfile);
}
