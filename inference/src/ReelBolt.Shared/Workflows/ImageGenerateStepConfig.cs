using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

/// <summary>What an ImageGenerate step does: make pictures from a prompt, or edit one source picture by an instruction.</summary>
public enum ImageGenerateMode
{
    /// <summary>Text to image: <see cref="ImageGenerateStepConfig.Prompt"/> in, <see cref="ImageGenerateStepConfig.Variations"/> pictures out.</summary>
    Generate,

    /// <summary>Instruction edit: <see cref="ImageGenerateStepConfig.Source"/> plus <see cref="ImageGenerateStepConfig.Instruction"/> in.</summary>
    Edit
}

/// <summary>
/// Settings of a <see cref="Data.Models.StepType.ImageGenerate"/> step (stored as JSON in <c>image_generate_config_json</c>):
/// deterministic, non-LLM and PAID. Generate runs the ImageGeneration provider, Edit the ImageEdit provider (which sends the
/// source picture to the vendor, so it needs <see cref="AllowSourceMediaEgress"/>). Every picture is its own priced,
/// reserved ledger row; the step never ends free.
/// </summary>
[StepCard(
    "Generates pictures from a prompt, or edits one picture by an instruction, with an AI image model.",
    ["A prompt, and for Edit a source picture from the project or an earlier step (or the run's picture files)", "Optionally a list of items; {item} in the prompt is replaced by each"],
    "One to four pictures (or edited versions of one picture), or with a list of items (one per colour, say) a whole set of up to 24, one per item, optionally saved as project image files",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCreditsAndAiShots,
    ReadableBy = ["Later steps", "The project's files"],
    PairsWith = ["VideoGenerate: a generated picture can be the first frame", "ExtractFrame: edit a frame it grabbed"])]
public sealed record ImageGenerateStepConfig(
    [property: Description("Generate (text to image) or Edit (change one source picture by an instruction).")]
    ImageGenerateMode Mode,
    [property: Description("Generate only: what to draw, at most 4000 characters.")]
    [property: AppBindable(AppInputType.ShortText, AppInputType.LongText, Label = "What to draw", MaxLength = ImageGenerateStepConfig.MaxPromptLength)]
    string? Prompt = null,
    [property: Description("Edit only: what to change in the source picture, at most 4000 characters.")]
    [property: AppBindable(AppInputType.ShortText, AppInputType.LongText, Label = "What to change", MaxLength = ImageGenerateStepConfig.MaxPromptLength)]
    string? Instruction = null,
    [property: Description("Edit only: the one picture to change. A project file (ProjectFile) or an earlier step's picture (StepResult / StepOutput). Source=BriefFiles with Role=Image edits each picture file chosen for the run, one edit per file.")]
    VideoSourceRef? Source = null,
    [property: Description("How many pictures per item (or per step without an ItemList), 1 to 4. Generate draws that many from the prompt; Edit makes that many edited versions of the same source with the same instruction. Each is priced, reserved and registered on its own.")]
    int Variations = 1,
    [property: Description("Spend limit for the whole step in credits; null limits it to the priced total.")]
    double? MaxSpendCredits = null,
    [property: Description("Provider row id; null uses the default ImageGeneration (Generate) or ImageEdit (Edit) row.")]
    Guid? ProviderId = null,
    [property: Description("Aspect ratio word such as 16:9 or 1:1, where the model takes one. Not with Width/Height.")]
    string? AspectRatio = null,
    [property: Description("Generate only: width in pixels, with Height. Not with AspectRatio.")]
    int? Width = null,
    [property: Description("Generate only: height in pixels, with Width. Not with AspectRatio.")]
    int? Height = null,
    [property: Description("Register each picture as an image project file (default true).")]
    bool RegisterProjectFile = true,
    [property: Description("Edit only: consent for the source picture to be uploaded to the hosted model (presigned GET, 10 minutes). false refuses with EGRESS_REFUSED and uploads nothing.")]
    bool AllowSourceMediaEgress = false,
    [property: Description("Project file name prefix (letters, digits, - and _); null uses image.")]
    string? FileNamePrefix = null,
    [property: Description("Batch: a list of items, one per line or comma separated (at most 24, each at most 120 characters, duplicates dropped). The step then makes the pictures once per item, and {item} in the Prompt/Instruction is replaced by each item (it must appear). Total pictures = items x Variations, at most 24. Edit with Source=BriefFiles takes the run's picture files as its items instead, so not both.")]
    [property: AppBindable(AppInputType.ShortText, AppInputType.LongText, Label = "List of items (one per line)", MaxLength = ImageGenerateStepConfig.MaxItemListLength)]
    string? ItemList = null,
    [property: Description("Resolution tier word 1K, 2K or 4K (any case), where the model prices by tier (Nano Banana); null lets the engine choose the smallest tier that covers OutputWidth/OutputHeight, or the model's default. Not a pixel size: use OutputWidth/OutputHeight for an exact one.")]
    [property: AppBindable(AppInputType.Choice, Label = "Picture resolution", Choices = ["1K", "2K", "4K"])]
    string? Resolution = null,
    [property: Description("Exact final width in pixels (64 to 8192), with OutputHeight, for Generate and Edit. The engine asks the model for the nearest tier and shape, then resizes and centre-crops to exactly this size. Not with Width/Height/AspectRatio.")]
    [property: AppBindable(AppInputType.Number, Label = "Output width (px)", Min = 64, Max = 8192, Integer = true)]
    int? OutputWidth = null,
    [property: Description("Exact final height in pixels (64 to 8192), with OutputWidth. See OutputWidth.")]
    [property: AppBindable(AppInputType.Number, Label = "Output height (px)", Min = 64, Max = 8192, Integer = true)]
    int? OutputHeight = null)
{
    public const int MaxPromptLength = 4000;

    public const int MaxVariations = 4;

    /// <summary>Most items an <see cref="ItemList"/> may hold.</summary>
    public const int MaxItems = 24;

    /// <summary>Longest one item may be.</summary>
    public const int MaxItemLength = 120;

    /// <summary>Longest raw <see cref="ItemList"/> text (an app input's cap).</summary>
    public const int MaxItemListLength = 4000;

    /// <summary>Most pictures one step may make in all (items x Variations).</summary>
    public const int MaxTotalImages = 24;

    /// <summary>The token in the Prompt/Instruction that each item replaces.</summary>
    public const string ItemToken = "{item}";

    private const int MaxPixelEdge = 8192;

    /// <summary>Smallest exact output edge.</summary>
    public const int MinOutputEdge = 64;

    /// <summary>The resolution tier words, with the long edge each is assumed to deliver.</summary>
    public static readonly IReadOnlyDictionary<string, int> TierLongEdges =
        new Dictionary<string, int>(StringComparer.Ordinal) { ["1K"] = 1024, ["2K"] = 2048, ["4K"] = 4096 };

    /// <summary><see cref="Resolution"/> normalised to upper case; null when unset.</summary>
    [JsonIgnore]
    public string? ResolutionTier => string.IsNullOrWhiteSpace(Resolution) ? null : Resolution.Trim().ToUpperInvariant();

    private static readonly Regex SafePrefix = new("^[A-Za-z0-9_-]{1,40}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AspectWord = new(@"^[0-9]{1,2}[:_x][0-9]{1,2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Serializer settings every reader and writer of the column uses (enums by name).</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The prompt text the vendor is given: the prompt for Generate, the instruction for Edit.</summary>
    [JsonIgnore]
    public string EffectivePrompt => (Mode == ImageGenerateMode.Edit ? Instruction : Prompt) ?? string.Empty;

    /// <summary>
    /// The number of pictures the step makes (what every hold, estimate and budget reservation prices): items x
    /// <see cref="Variations"/>. Without an <see cref="ItemList"/> it is <see cref="Variations"/>. With Edit and
    /// <c>Source=BriefFiles</c> the file count is only known at run time, so it is the most the step may ever make
    /// (<see cref="MaxTotalImages"/> rounded down to whole variations), never less than the real count.
    /// </summary>
    [JsonIgnore]
    public int ImageCount => UsesBriefFiles ? BriefFileCap * Math.Max(1, Variations) : Math.Max(1, ParseItems(ItemList).Items.Count) * Variations;

    /// <summary>True when the step edits the run brief's picture files, one edit per file.</summary>
    [JsonIgnore]
    public bool UsesBriefFiles => Mode == ImageGenerateMode.Edit && Source is { Kind: VideoSourceKind.BriefFiles };

    /// <summary>Most brief files one step may edit (<see cref="MaxTotalImages"/> / <see cref="Variations"/>).</summary>
    [JsonIgnore]
    public int BriefFileCap => MaxTotalImages / Math.Max(1, Math.Clamp(Variations, 1, MaxVariations));

    /// <summary>The items of <see cref="ItemList"/> (empty when none).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Items => ParseItems(ItemList).Items;

    /// <summary>The prompt or instruction for one item: <see cref="ItemToken"/> replaced by the item; the text as written when there is no item.</summary>
    public string PromptFor(string? item) =>
        item is null ? EffectivePrompt : EffectivePrompt.Replace(ItemToken, item, StringComparison.Ordinal);

    /// <summary>
    /// Splits a list on newlines, commas and semicolons, trims, drops empties and case-insensitive duplicates (first spelling
    /// wins, order kept). <c>Errors</c> names every problem (too many items, an item too long, a control character); the
    /// items found are still returned.
    /// </summary>
    public static (IReadOnlyList<string> Items, List<string> Errors) ParseItems(string? raw)
    {
        List<string> items = [];
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(raw))
            return (items, errors);

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string part in raw.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Length == 0 || !seen.Add(part))
                continue;
            if (part.Any(char.IsControl))
                errors.Add($"ItemList item '{part.Replace('\t', ' ')[..Math.Min(part.Length, 20)]}' must not contain control characters.");
            else if (part.Length > MaxItemLength)
                errors.Add($"ItemList items must be at most {MaxItemLength} characters; one is {part.Length}.");
            items.Add(part);
        }

        if (items.Count > MaxItems)
            errors.Add($"ItemList must have at most {MaxItems} items; it has {items.Count}.");
        return (items, errors);
    }

    [JsonIgnore]
    public string EffectivePrefix => FileNamePrefix ?? "image";

    /// <summary>Every reason this config cannot run. Empty means valid.</summary>
    public List<string> Validate()
    {
        List<string> errors = [];

        if (!Enum.IsDefined(Mode))
        {
            errors.Add($"Mode must be one of: {string.Join(", ", Enum.GetNames<ImageGenerateMode>())}.");
            return errors;
        }

        bool edit = Mode == ImageGenerateMode.Edit;
        string? text = edit ? Instruction : Prompt;
        string field = edit ? "Instruction" : "Prompt";
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add($"{field} is required for {Mode}.");
        }
        else
        {
            if (text.Length > MaxPromptLength)
                errors.Add($"{field} must be at most {MaxPromptLength} characters; it is {text.Length}.");
            if (text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
                errors.Add($"{field} must not contain control characters.");
        }

        if (edit)
        {
            if (Prompt is not null)
                errors.Add("Prompt is for Generate only; Edit takes an Instruction.");
            ValidateEditSource(errors);
            if (Variations < 1 || Variations > MaxVariations)
                errors.Add($"Variations must be from 1 to {MaxVariations}; it is {Variations}.");
            if (Width is not null || Height is not null)
                errors.Add("Width and Height are for Generate only.");
        }
        else
        {
            if (Instruction is not null)
                errors.Add("Instruction is for Edit only; Generate takes a Prompt.");
            if (Source is not null)
                errors.Add("Source is for Edit only.");
            if (AllowSourceMediaEgress)
                errors.Add("AllowSourceMediaEgress is for Edit only; Generate sends no source.");
            if (Variations < 1 || Variations > MaxVariations)
                errors.Add($"Variations must be from 1 to {MaxVariations}; it is {Variations}.");
        }

        ValidateBatch(errors, text);

        if (AspectRatio is not null && !AspectWord.IsMatch(AspectRatio))
            errors.Add("AspectRatio must be a ratio word such as 16:9 or 1:1.");

        if (Width is not null || Height is not null)
        {
            if (Width is null or <= 0 || Height is null or <= 0)
                errors.Add("Width and Height must both be set, and positive.");
            else if (Width > MaxPixelEdge || Height > MaxPixelEdge)
                errors.Add($"Width and Height must be at most {MaxPixelEdge} pixels.");
            if (AspectRatio is not null)
                errors.Add("Give a size as Width/Height OR as an AspectRatio, not both.");
        }

        ValidateOutputSize(errors);

        if (MaxSpendCredits is { } cap && (!double.IsFinite(cap) || cap <= 0))
            errors.Add("MaxSpendCredits must be a positive number of credits when set.");

        if (ProviderId == Guid.Empty)
            errors.Add("ProviderId must not be the empty id; leave it null to use the default provider.");

        if (FileNamePrefix is not null && !SafePrefix.IsMatch(FileNamePrefix))
            errors.Add("FileNamePrefix must be 1-40 letters, digits, '-' or '_'.");

        return errors;
    }

    private void ValidateOutputSize(List<string> errors)
    {
        bool anySize = OutputWidth is not null || OutputHeight is not null;
        if (anySize)
        {
            if (OutputWidth is null || OutputHeight is null)
            {
                errors.Add("OutputWidth and OutputHeight must both be set, or neither.");
            }
            else if (OutputWidth is < MinOutputEdge or > MaxPixelEdge || OutputHeight is < MinOutputEdge or > MaxPixelEdge)
            {
                errors.Add($"OutputWidth and OutputHeight must each be from {MinOutputEdge} to {MaxPixelEdge} pixels.");
            }

            if (Width is not null || Height is not null || AspectRatio is not null)
                errors.Add("Give one way to set the size: OutputWidth/OutputHeight, or Width/Height, or an AspectRatio.");
        }

        if (Resolution is null)
            return;
        string? tier = ResolutionTier;
        if (tier is null || !TierLongEdges.ContainsKey(tier))
        {
            errors.Add("Resolution must be one of 1K, 2K or 4K.");
        }
        else if (OutputWidth is { } w && OutputHeight is { } h && Math.Max(w, h) > TierLongEdges[tier])
        {
            string minimum = TierLongEdges.First(t => t.Value >= Math.Max(w, h)).Key;
            errors.Add($"Resolution {tier} is too small for {w}x{h}; use {minimum} or larger, or leave Resolution empty.");
        }
    }

    private void ValidateBatch(List<string> errors, string? text)
    {
        if (ItemList is not null)
        {
            if (ItemList.Length > MaxItemListLength)
                errors.Add($"ItemList must be at most {MaxItemListLength} characters; it is {ItemList.Length}.");
            (IReadOnlyList<string> items, List<string> itemErrors) = ParseItems(ItemList);
            errors.AddRange(itemErrors);
            if (items.Count == 0)
                errors.Add("ItemList has no items; leave it null for a single run or list at least one item.");
            else if (text is not null && !text.Contains(ItemToken, StringComparison.Ordinal))
                errors.Add($"{(Mode == ImageGenerateMode.Edit ? "Instruction" : "Prompt")} must contain {ItemToken} when ItemList is set; each item replaces it.");
            else if (text is not null && items.Max(i => i.Length) is var longest
                && text.Replace(ItemToken, new string('x', longest), StringComparison.Ordinal).Length > MaxPromptLength)
                errors.Add($"The text with the longest item filled in must be at most {MaxPromptLength} characters.");
            if (UsesBriefFiles)
                errors.Add("ItemList is not allowed with Source=BriefFiles; each brief file is one item.");
        }

        if (Variations is >= 1 and <= MaxVariations && ImageCount > MaxTotalImages)
            errors.Add($"A step may make at most {MaxTotalImages} pictures in all (items x Variations); this one makes {ImageCount}.");
    }

    private void ValidateEditSource(List<string> errors)
    {
        if (Source is null)
        {
            errors.Add("Source is required for Edit: the one picture to change.");
            return;
        }

        if (Source.Kind is not (VideoSourceKind.ProjectFile or VideoSourceKind.StepOutput or VideoSourceKind.PreviousStepOutput or VideoSourceKind.StepResult or VideoSourceKind.BriefFiles))
            errors.Add($"Source={Source.Kind} is not supported; ImageGenerate Edit takes a project file, a step output, a step result or the run's picture files (BriefFiles).");
        if (Source.Kind == VideoSourceKind.BriefFiles && Source.Role != Briefs.RunBriefFileRole.Image)
            errors.Add("Source=BriefFiles for ImageGenerate must use Role=Image.");
        if (Source.Validate() is { } sourceError)
            errors.Add(sourceError);
        if (Source.InSec is not null || Source.OutSec is not null)
            errors.Add("Source.InSec/OutSec are for video; they are not supported by ImageGenerate.");
        if (Source.Kind == VideoSourceKind.ProjectFile && (Source.ProjectFileId is null || Source.ProjectFileId == Guid.Empty))
            errors.Add("Source=ProjectFile requires ProjectFileId.");
        if (Source.Kind == VideoSourceKind.StepOutput && Source.StepOrder is null)
            errors.Add("Source=StepOutput requires StepOrder.");
    }
}
