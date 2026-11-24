using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Services.DesignSystems;

/// <summary>
/// The loaded design and product documents of the run on this async flow, plus the options that
/// bound each block. Rendering is delegated to P2-16's <see cref="DesignSystemPromptView"/>.
/// </summary>
public sealed class DesignSystemRunState
{
    /// <summary>The options the VideoCompile config is deserialized with — the same web-cased, string-enum shape the executor uses.</summary>
    private static readonly JsonSerializerOptions VideoCompileConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly DesignSystemAmbientOptions _options;
    private readonly IReadOnlyDictionary<Guid, string>? _projectFileNames;

    internal DesignSystemRunState(
        LoadedProfile profile,
        DesignSystemAmbientOptions options,
        IReadOnlyDictionary<Guid, string>? projectFileNames = null,
        BrandAssetCatalog? brandAssets = null)
    {
        BrandAssets = brandAssets ?? BrandAssetCatalog.FromDocument(profile.Design);
        Profile = profile;
        _options = options;
        _projectFileNames = projectFileNames;
    }

    public LoadedProfile Profile { get; }

    /// <summary>The design system's brand images with their subject, form and measured tone; read synchronously by the tools.</summary>
    public BrandAssetCatalog BrandAssets { get; }

    /// <summary>
    /// The FULL fenced design and product blocks for <paramref name="agentType"/> (either may be
    /// null when that role has no view or the member was unusable).
    /// </summary>
    public (string? Design, string? Product) BlocksFor(AgentType agentType)
    {
        (DesignView? designView, ProductView? productView) = DesignSystemRoleRegistry.ViewsFor(agentType);

        string? design = Profile.Design is { } document && designView is { } view
            ? DesignSystemPromptView.RenderDesign(
                document,
                view,
                Profile.DesignVersion,
                _options.DesignMaxChars,
                _projectFileNames,
                omitLocale: RunBriefAmbient.Current?.Language is not null)
            : null;

        string? product = Profile.ProductMap is { } productDocument && productView is { } mapView
            ? DesignSystemPromptView.RenderProduct(productDocument, mapView, Profile.ProductVersion, _options.ProductMaxChars)
            : null;

        return (design, product);
    }

    /// <summary>
    /// The full fenced block for one design <paramref name="view"/>, or null when the run's pin
    /// carries no design document or the view produces no line. The <see cref="DesignView.GraphicsPalette"/>
    /// view (P3-08) names the logo's project file, using the names <see cref="LoadAsync"/> resolved
    /// and never its bytes.
    /// </summary>
    public string? RenderDesignView(DesignView view) =>
        Profile.Design is { } document
            ? DesignSystemPromptView.RenderDesign(
                document,
                view,
                Profile.DesignVersion,
                _options.DesignMaxChars,
                _projectFileNames,
                omitLocale: RunBriefAmbient.Current?.Language is not null)
            : null;

    /// <summary>
    /// The cache-key component for a step (consumed by P2-18): the first 16 hex chars of each
    /// rendered block, or <c>-</c> for a missing one, or null when the role consumes no block —
    /// plus, for <see cref="StepType.VideoCompile"/> with brand overrides on, the derived-values
    /// component <c>c={sha256(derived override string)[0..16]}</c> (SPEC 14.2). Either part may be
    /// absent; null means neither applies, so the key is exactly what it was before.
    /// </summary>
    public string? CacheKeyComponent(StepType stepType, AgentType? agentType, WorkflowStep? step = null)
    {
        string? blockComponent = BlockCacheComponent(stepType, agentType);
        string? brandComponent = BrandOverrideCacheComponent(stepType, step);

        if (blockComponent is null)
            return brandComponent;
        return brandComponent is null ? blockComponent : blockComponent + "|" + brandComponent;
    }

    private string? BlockCacheComponent(StepType stepType, AgentType? agentType)
    {
        AgentType? resolved = stepType == StepType.ColorGradeRoom
            ? AgentType.ColorGradeDirector
            : agentType;

        if (resolved is not { } type)
            return null;

        (DesignView? designView, ProductView? productView) = DesignSystemRoleRegistry.ViewsFor(type);
        if (designView is null && productView is null)
            return null;

        (string? design, string? product) = BlocksFor(type);

        string designHash = design is null ? "-" : CanonicalJson.Sha256Hex(design)[..16];
        string productHash = product is null ? "-" : CanonicalJson.Sha256Hex(product)[..16];
        return $"d={designHash}|p={productHash}";
    }

    private string? BrandOverrideCacheComponent(StepType stepType, WorkflowStep? step)
    {
        if (stepType != StepType.VideoCompile || step?.VideoCompileConfigJson is not { Length: > 0 } json)
            return null;

        VideoCompileStepConfig? config = ParseVideoCompileConfig(json);
        return config is null ? null : DeriveVideoCompileOverrides(config)?.CacheKeyComponent;
    }

    private static VideoCompileStepConfig? ParseVideoCompileConfig(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<VideoCompileStepConfig>(json, VideoCompileConfigJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The brand-derived overlay colour and caption font for a VideoCompile config (SPEC 14.2), or
    /// null when the config opts into neither flag or the run's pin carries no design document. The
    /// returned record is what the executor applies and what the step-cache key's <c>c=</c>
    /// component hashes; it is a pure function of the config and <see cref="Profile"/>.
    /// </summary>
    public VideoCompileBrandOverrides? DeriveVideoCompileOverrides(VideoCompileStepConfig config)
    {
        if ((!config.DesignSystemColors && !config.DesignSystemFonts) || Profile.Design is not { } design)
            return null;

        var warnings = new List<BrandOverrideWarning>();
        string? overlayFontColor = null;
        string? overlayBoxColor = null;
        CaptionFont? font = null;

        if (config.DesignSystemColors)
        {
            BrandOverlayPick pick = BrandOverlayColors.Pick(design);
            overlayFontColor = pick.FontColor;
            if (pick.FontColor is not null)
                overlayBoxColor = pick.BoxColor;
            if (pick.Warning is not null)
                warnings.Add(new BrandOverrideWarning(
                    pick.Warning,
                    "The brand palette has no colour that reads on a dark overlay, so the configured overlay colours were kept."));
        }

        if (config.DesignSystemFonts && config.CaptionFont == CaptionFont.Auto)
        {
            IReadOnlyList<string> families = BrandFontMatcher.RequestedFamilies(design);
            if (families.Count > 0)
            {
                (CaptionFont nearest, string? warning) = BrandFontMatcher.NearestCaptionFont(families);
                font = nearest;
                if (warning is not null)
                    warnings.Add(new BrandOverrideWarning(warning, SubstitutionMessage(warning, nearest)));
            }
        }

        if (overlayFontColor is null && font is null && warnings.Count == 0)
            return null;

        return new VideoCompileBrandOverrides(
            overlayFontColor,
            overlayBoxColor,
            font,
            warnings,
            BuildCacheKeyComponent(config, overlayFontColor, overlayBoxColor, font));
    }

    private static string? BuildCacheKeyComponent(
        VideoCompileStepConfig config,
        string? overlayFontColor,
        string? overlayBoxColor,
        CaptionFont? font)
    {
        var parts = new List<string>();
        if (config.DesignSystemColors)
            parts.Add("color=" + (overlayFontColor ?? "-") + ";box=" + (overlayBoxColor ?? "-"));
        if (config.DesignSystemFonts && config.CaptionFont == CaptionFont.Auto)
            parts.Add("font=" + (font?.ToString() ?? "-"));

        return parts.Count == 0
            ? null
            : "c=" + CanonicalJson.Sha256Hex(string.Join("|", parts))[..16];
    }

    /// <summary>A plain-language message for a <c>font_substituted:requested-&gt;used</c> code.</summary>
    private static string SubstitutionMessage(string warning, CaptionFont font)
    {
        const string prefix = "font_substituted:";
        string body = warning.StartsWith(prefix, StringComparison.Ordinal) ? warning[prefix.Length..] : warning;
        int arrow = body.IndexOf("->", StringComparison.Ordinal);
        string requested = arrow >= 0 ? body[..arrow] : body;
        return $"The brand font '{requested}' is not available for captions, so {BrandFontMatcher.FamilyOf(font)} was used instead.";
    }

    /// <summary>
    /// The state for <paramref name="execution"/>, or null when the ambient is disabled, the run
    /// carries no pin, the loader is not registered, or neither member resolved. Never throws.
    /// </summary>
    public static async Task<DesignSystemRunState?> LoadAsync(
        WorkflowExecution execution,
        IServiceProvider sp,
        CancellationToken ct)
    {
        DesignSystemAmbientOptions? options = sp.GetService<IOptions<DesignSystemAmbientOptions>>()?.Value;
        if (options is null || !options.Enabled)
            return null;

        DesignSystemPin? pin = DesignSystemPinJson.TryParse(execution.DesignSystemPinJson);
        if (pin is null)
            return null;

        PinnedDocumentLoader? loader = sp.GetService<PinnedDocumentLoader>();
        if (loader is null)
            return null;

        LoadedProfile profile = await loader.LoadAsync(pin, ct);
        if (profile.Design is null && profile.ProductMap is null)
            return null;

        // The GraphicsPalette block names the logo's project file but never carries its bytes;
        // resolving the name here keeps the Shared renderer pure. Best effort: a lookup failure (or
        // no database at all, as in a minimally-constructed test) omits the line, never the block.
        IReadOnlyDictionary<Guid, string>? fileNames =
            await ResolveProjectFileNamesAsync(
                profile.Design, execution.ProjectId, sp.GetService<WorkflowEngineDbContext>(), ct);

        // Whose logo each image is, and which backgrounds it reads on: persisted keys when the document
        // has them, measured from the stored file when it is a legacy document. Best effort, bounded.
        BrandAssetCatalog brandAssets = await BrandAssetCatalog.BuildAsync(
            profile.Design,
            profile.ProductMap?.Name,
            sp.GetService<IProjectFileWorkspace>(),
            execution.ProjectId,
            ct);

        return new DesignSystemRunState(profile, options, fileNames, brandAssets);
    }

    private static async Task<IReadOnlyDictionary<Guid, string>?> ResolveProjectFileNamesAsync(
        JsonObject? design,
        Guid projectId,
        WorkflowEngineDbContext? db,
        CancellationToken ct)
    {
        if (design is null || db is null)
            return null;

        Guid[] ids = AssetFileIds(design);
        if (ids.Length == 0)
            return null;

        try
        {
            var rows = await db.ProjectFiles
                .AsNoTracking()
                .Where(file => file.ProjectId == projectId && ids.Contains(file.Id))
                .Select(file => new { file.Id, file.OriginalFileName })
                .ToListAsync(ct);

            if (rows.Count == 0)
                return null;

            var names = new Dictionary<Guid, string>();
            foreach (var row in rows)
                names[row.Id] = row.OriginalFileName;

            return names;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every <c>project-file:&lt;guid&gt;</c> reference under <c>assets.*</c>.</summary>
    private static Guid[] AssetFileIds(JsonObject design)
    {
        if (design["assets"] is not JsonObject assets)
            return [];

        var ids = new List<Guid>();
        foreach (KeyValuePair<string, JsonNode?> asset in assets)
        {
            if (asset.Value is not JsonObject value)
                continue;

            AddFileId(ids, value["fileRef"]);
            if (value["variants"] is JsonArray variants)
            {
                foreach (JsonNode? variant in variants)
                {
                    if (variant is JsonObject variantObject)
                        AddFileId(ids, variantObject["fileRef"]);
                }
            }
        }

        return ids.Distinct().ToArray();
    }

    private static void AddFileId(List<Guid> ids, JsonNode? node)
    {
        if (node is not JsonValue value || value.GetValueKind() != System.Text.Json.JsonValueKind.String)
            return;

        string? fileRef = value.GetValue<string>();
        const string prefix = "project-file:";
        if (fileRef is null || !fileRef.StartsWith(prefix, StringComparison.Ordinal))
            return;

        if (Guid.TryParse(fileRef[prefix.Length..], out Guid id))
            ids.Add(id);
    }
}

/// <summary>One brand-override fallback the run recorded (SPEC 14.2): a stable code and a plain-language message.</summary>
public sealed record BrandOverrideWarning(string Code, string Message);

/// <summary>The brand-derived VideoCompile overrides (SPEC 14.2) and the step-cache component they hash to.</summary>
public sealed record VideoCompileBrandOverrides(
    string? OverlayFontColor,
    string? OverlayBoxColor,
    CaptionFont? Font,
    IReadOnlyList<BrandOverrideWarning> Warnings,
    string? CacheKeyComponent);
