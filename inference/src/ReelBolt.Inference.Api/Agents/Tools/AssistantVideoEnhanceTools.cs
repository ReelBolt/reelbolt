using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// "Enhance this video" as assistant and MCP tools. <c>ProposeEnhanceVideo</c> resolves the source, runs every gate the
/// REST route runs through the SAME <see cref="IVideoEnhanceService"/>, prices it and mints a confirmation token without
/// writing anything; <c>ApplyEnhanceVideo</c> re-runs those gates, verifies the token and starts the hidden
/// <see cref="WorkflowDefinitionKind.VideoEnhance"/> run, returning its execution id without waiting.
/// <para>
/// Consent: the source's picture may leave the host only under the project's recorded consent
/// (<see cref="Project.VideoEnhanceConsentAt"/>), which a person gives once in the editor. These tools never record it and
/// never accept it as an argument: a hosted operation with no recorded consent answers <c>NEEDS_CUSTOMER_OK</c> and writes
/// nothing. Local operations never leave the host and need no consent.
/// </para>
/// </summary>
public sealed class AssistantVideoEnhanceTools
{
    internal const string OpEnhanceVideo = "EnhanceVideo";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";
    internal const string NeedsCustomerOkCode = "NEEDS_CUSTOMER_OK";

    internal const string NeedsCustomerOkMessage =
        "Enhancing this video with AI sends the video to a hosted AI service, and that needs the customer's OK in the editor. "
        + "Nothing was run. Ask them to open the video in the editor and agree once; the free local enhancements "
        + "(upscale, slow motion, denoise, motion blur) can be run from here.";

    /// <summary>Progress captions, in <c>AssistantToolProgress</c>'s voice.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeEnhanceVideo"] = "Checking the video and what enhancing it would produce",
        ["ApplyEnhanceVideo"] = "Starting the video enhancement",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IVideoEnhanceService _enhance;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly ICurrentUser _currentUser;

    public AssistantVideoEnhanceTools(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IVideoEnhanceService enhance,
        IConfirmationTokenService confirmationTokens,
        ICurrentUser currentUser)
    {
        _db = db;
        _projectAccess = projectAccess;
        _enhance = enhance;
        _confirmationTokens = confirmationTokens;
        _currentUser = currentUser;
    }

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string shape =
            " op is what to do: Upscale, Interpolate (smoothed slow motion), Denoise, MotionBlur or Extend (generate new picture past the border; hosted only). engine is Local (free, done on our own servers) or Hosted (an AI model, paid, needs the customer's one-time OK in the editor); Denoise and MotionBlur are local only. "
            + "Name the source with exactly one of fileId (a project file, from ListProjectFiles) or outputId (a finished render, from GetWorkflowExecution). "
            + "For Upscale pass exactly one of upscaleFactor (2 or 4) or targetHeight (720, 1080, 1440 or 2160); for Interpolate frameMultiplier (2, 4 or 8 locally; 2 or 4 hosted); for Denoise denoiseStrength (Light or Strong) and optionally keepGrain; for MotionBlur shutterAngle (90 to 360, default 180); for Extend exactly one of targetAspect (16:9, 9:16, 1:1 or 4:5) or sides + one amount: set marginLeft, marginRight, marginTop and marginBottom to the SAME value (0.01 to 0.5 of that side) on every side that grows and leave the others at 0 (different amounts are refused, because the model takes one amount for all the sides it grows), and optionally prompt (what the new edges should show, up to 500 characters).";

        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (string op, string engine, Guid? fileId = null, Guid? outputId = null, int? upscaleFactor = null,
                    int? targetHeight = null, int? frameMultiplier = null, string? denoiseStrength = null,
                    bool? keepGrain = null, int? shutterAngle = null, Guid? projectId = null, string? targetAspect = null,
                    double? marginLeft = null, double? marginRight = null, double? marginTop = null, double? marginBottom = null,
                    string? prompt = null)
                    => ProposeAsync(context, op, engine, fileId, outputId, upscaleFactor, targetHeight, frameMultiplier,
                        denoiseStrength, keepGrain, shutterAngle, projectId,
                        new ExtendArgs(targetAspect, marginLeft, marginRight, marginTop, marginBottom, prompt)),
                name: "ProposeEnhanceVideo",
                description: "Proposes enhancing one video of a project file or a finished render: upscale, smooth slow motion, denoise or motion blur. Checks the same plan, length, consent and credit gates as the editor and returns a confirmationToken and a plain-language summary WITHOUT writing or running anything. A hosted operation with no recorded project consent is refused: the customer must agree once in the editor." + shape + " " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyEnhanceVideo."),
            AIFunctionFactory.Create(
                (string op, string engine, string confirmationToken, Guid? fileId = null, Guid? outputId = null,
                    int? upscaleFactor = null, int? targetHeight = null, int? frameMultiplier = null,
                    string? denoiseStrength = null, bool? keepGrain = null, int? shutterAngle = null, Guid? projectId = null,
                    string? targetAspect = null, double? marginLeft = null, double? marginRight = null, double? marginTop = null,
                    double? marginBottom = null, string? prompt = null)
                    => ApplyAsync(context, op, engine, confirmationToken, fileId, outputId, upscaleFactor, targetHeight,
                        frameMultiplier, denoiseStrength, keepGrain, shutterAngle, projectId,
                        new ExtendArgs(targetAspect, marginLeft, marginRight, marginTop, marginBottom, prompt)),
                name: "ApplyEnhanceVideo",
                description: "Starts the video enhancement a prior ProposeEnhanceVideo call proposed and returns its executionId without waiting (check it with GetExecutionStatus or WatchExecution). confirmationToken is verified against the proposal's operation, engine, source and settings; a fabricated or stale token, or a changed argument, is refused and nothing runs. A hosted operation runs only under the project's recorded consent; without it the tool answers that the customer must agree in the editor and writes nothing." + shape + " " + AssistantProjectScope.ProjectIdNote),
        };
    }

    private async Task<string> ProposeAsync(
        AssistantToolContext context, string? op, string? engine, Guid? fileId, Guid? outputId, int? upscaleFactor,
        int? targetHeight, int? frameMultiplier, string? denoiseStrength, bool? keepGrain, int? shutterAngle, Guid? projectId,
        ExtendArgs? extend)
    {
        Prepared prepared = await PrepareAsync(context, op, engine, fileId, outputId, upscaleFactor, targetHeight,
            frameMultiplier, denoiseStrength, keepGrain, shutterAngle, projectId, CancellationToken.None, extend);
        if (prepared.Error is not null)
            return prepared.Error;

        string token = _confirmationTokens.Compute(OpEnhanceVideo, prepared.Payload!, context.UserId);
        return JsonSerializer.Serialize(new
        {
            operation = OpEnhanceVideo,
            confirmationToken = token,
            summary = prepared.Summary,
            projectName = prepared.Facts!.Project.Name,
            op = prepared.Facts.Op,
            engine = prepared.Facts.Engine,
            source = prepared.Facts.SourceName,
            seconds = prepared.Facts.Estimate.Seconds,
            outputWidth = prepared.Facts.Estimate.OutputWidth,
            outputHeight = prepared.Facts.Estimate.OutputHeight,
            outputFps = prepared.Facts.Estimate.OutputFps,
            hosted = prepared.Facts.Estimate.Hosted,
            credits = prepared.Facts.Estimate.Hosted && prepared.Facts.Estimate.BillingEnabled
                ? prepared.Facts.Estimate.Credits
                : 0,
        }, Json);
    }

    private async Task<string> ApplyAsync(
        AssistantToolContext context, string? op, string? engine, string? confirmationToken, Guid? fileId,
        Guid? outputId, int? upscaleFactor, int? targetHeight, int? frameMultiplier, string? denoiseStrength,
        bool? keepGrain, int? shutterAngle, Guid? projectId, ExtendArgs? extend)
    {
        Prepared prepared = await PrepareAsync(context, op, engine, fileId, outputId, upscaleFactor, targetHeight,
            frameMultiplier, denoiseStrength, keepGrain, shutterAngle, projectId, CancellationToken.None, extend);
        if (prepared.Error is not null)
            return prepared.Error;

        if (!_confirmationTokens.Verify(OpEnhanceVideo, prepared.Payload!, context.UserId, confirmationToken ?? string.Empty))
            return Error(StaleTokenMessage);

        // The stored project consent is the ONLY source of egress, and the request body has no flag to set it.
        VideoEnhanceCaller caller = new(context.UserId, prepared.Facts!.Project.OrganizationId, prepared.Facts.Project, _currentUser.EmailVerified);
        VideoEnhanceResult<VideoEnhanceQueuedResponse> started = await _enhance.RequestAsync(
            caller, prepared.Facts.MediaRef, prepared.Facts.Body, CancellationToken.None);
        if (started.Refusal is { } refusal)
            return RefusalJson(refusal);

        return JsonSerializer.Serialize(new
        {
            executionId = started.Value!.ExecutionId,
            projectName = prepared.Facts.Project.Name,
            op = started.Value.Op,
            engine = started.Value.Engine,
            hosted = started.Value.Hosted,
            state = started.Value.State,
            credits = started.Value.Hosted && started.Value.Credits > 0 ? started.Value.Credits : 0,
            hint = "The enhancement is running; it does not wait. Check it with GetExecutionStatus using this executionId (or WatchExecution in chat). The enhanced video is added to the project's files when it is done.",
        }, Json);
    }

    private Task<Prepared> PrepareAsync(
        AssistantToolContext context, string? op, string? engine, Guid? fileId, Guid? outputId, int? upscaleFactor,
        int? targetHeight, int? frameMultiplier, string? denoiseStrength, bool? keepGrain, int? shutterAngle,
        Guid? projectId, CancellationToken ct, ExtendArgs? extend = null) =>
        PrepareAsync(_db, _projectAccess, _enhance, _currentUser, context, op, engine, fileId, outputId, upscaleFactor,
            targetHeight, frameMultiplier, denoiseStrength, keepGrain, shutterAngle, projectId, ct, extend);

    /// <summary>
    /// Everything both tools decide the same way, so a token can only be minted for an enhancement Apply would start.
    /// Resolves the project at Edit on every call, maps the REST route's codes unchanged, and never grants consent.
    /// </summary>
    internal static async Task<Prepared> PrepareAsync(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IVideoEnhanceService enhance,
        ICurrentUser currentUser,
        AssistantToolContext context,
        string? op,
        string? engine,
        Guid? fileId,
        Guid? outputId,
        int? upscaleFactor,
        int? targetHeight,
        int? frameMultiplier,
        string? denoiseStrength,
        bool? keepGrain,
        int? shutterAngle,
        Guid? projectId,
        CancellationToken ct,
        ExtendArgs? extend = null)
    {
        // Authority first, re-read on every call: a project the caller may not edit is absent.
        Project? project = AssistantProjectScope.Resolve(projectAccess, context, ProjectPermission.Edit, projectId, out string scopeError);
        if (project is null)
            return Prepared.Fail(scopeError);

        if ((fileId is null) == (outputId is null))
            return Prepared.Fail(Error("name the source with exactly one of fileId (a project file) or outputId (a finished render).", "INVALID_REQUEST"));

        string mediaRef = fileId is { } f ? $"file:{f:D}" : $"step_result:{outputId:D}";
        // The prompt is customer text bound for a vendor: control characters stripped, trimmed, length-bounded, and only the
        // sanitized text is ever sent, shown or signed. Nothing raw reaches the body, the token payload or the summary.
        string? prompt = null;
        if (!string.IsNullOrWhiteSpace(extend?.Prompt))
        {
            SanitizedPrompt sanitized = GeneratedPromptSanitizer.Sanitize(extend!.Prompt, VideoEnhanceStepConfig.MaxPromptLength);
            if (sanitized.ExceedsMaxLength)
                return Prepared.Fail(Error($"prompt must be at most {VideoEnhanceStepConfig.MaxPromptLength} characters.", "INVALID_REQUEST"));
            prompt = string.IsNullOrWhiteSpace(sanitized.Text) ? null : sanitized.Text.Trim();
        }

        string? aspect = string.IsNullOrWhiteSpace(extend?.TargetAspect) ? null : extend!.TargetAspect!.Trim();
        var body = new VideoEnhanceRequestBody(
            op, engine, UpscaleFactor: upscaleFactor, TargetHeight: targetHeight, FrameMultiplier: frameMultiplier,
            DenoiseStrength: denoiseStrength, KeepGrain: keepGrain, ShutterAngle: shutterAngle,
            TargetAspect: aspect, MarginLeft: extend?.MarginLeft, MarginRight: extend?.MarginRight,
            MarginTop: extend?.MarginTop, MarginBottom: extend?.MarginBottom, Prompt: prompt);

        VideoEnhanceCaller caller = new(currentUser.UserId, project.OrganizationId, project, currentUser.EmailVerified);
        VideoEnhanceResult<VideoEnhanceEstimate> estimate = await enhance.EstimateAsync(caller, mediaRef, body, ct);
        if (estimate.Refusal is { } refused)
            return Prepared.Fail(RefusalJson(refused));

        VideoEnhanceEstimate value = estimate.Value!;
        if (value.UnavailableCode is { } code)
        {
            // The route's consent refusal is the one the model must turn into "ask them to agree in the editor".
            return code == VideoEnhanceService.ConsentRequiredCode
                ? Prepared.Fail(Error(NeedsCustomerOkMessage, NeedsCustomerOkCode))
                : Prepared.Fail(Error(value.UnavailableReason ?? "this video cannot be enhanced right now.", code));
        }

        // The route's 402, at Propose too: no token is minted for a hosted run the workspace cannot pay for.
        if (value.Hosted && value.BillingEnabled && !value.Affordable)
        {
            long requiredMillis = (long)Math.Ceiling(value.Credits * 1000);
            long balanceMillis = (long)Math.Floor(value.BalanceCredits * 1000);
            return Prepared.Fail(Error(
                ReelBolt.Inference.Api.Services.Billing.CreditShortfallMessage.Build(requiredMillis, null, balanceMillis),
                "INSUFFICIENT_CREDITS"));
        }

        string sourceName = fileId is { } id
            ? await db.ProjectFiles.AsNoTracking()
                .Where(x => x.Id == id && x.ProjectId == project.Id)
                .Select(x => x.OriginalFileName)
                .FirstOrDefaultAsync(ct) ?? "the file"
            : "an earlier render";

        string payload = PayloadOf(project.Id, mediaRef, op, engine, upscaleFactor, targetHeight, frameMultiplier,
            denoiseStrength, keepGrain, shutterAngle, body);

        Facts facts = new(project, mediaRef, body, sourceName, Normalize(op), Normalize(engine), value);
        return new Prepared(null, facts, payload, Summary(facts));
    }

    private static string PayloadOf(
        Guid projectId, string mediaRef, string? op, string? engine, int? upscaleFactor, int? targetHeight,
        int? frameMultiplier, string? denoiseStrength, bool? keepGrain, int? shutterAngle, VideoEnhanceRequestBody body) =>
        JsonSerializer.Serialize(new
        {
            op = Normalize(op),
            engine = Normalize(engine),
            projectId,
            mediaRef,
            upscaleFactor,
            targetHeight,
            frameMultiplier,
            denoiseStrength = Normalize(denoiseStrength),
            keepGrain,
            shutterAngle,
            targetAspect = body.TargetAspect,
            marginLeft = body.MarginLeft,
            marginRight = body.MarginRight,
            marginTop = body.MarginTop,
            marginBottom = body.MarginBottom,
            prompt = body.Prompt,
        }, Json);

    /// <summary>Enums are persisted by name; normalise the casing so a re-proposal with "upscale" still verifies.</summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (Enum.TryParse(value, ignoreCase: true, out VideoEnhanceOp op) && Enum.IsDefined(op))
            return op.ToString();
        if (Enum.TryParse(value, ignoreCase: true, out VideoEnhanceEngine engine) && Enum.IsDefined(engine))
            return engine.ToString();
        if (Enum.TryParse(value, ignoreCase: true, out VideoEnhanceDenoiseStrength strength) && Enum.IsDefined(strength))
            return strength.ToString();
        return value.Trim();
    }

    /// <summary>The operation in plain words, e.g. "upscale the video 2×".</summary>
    internal static string DescribeOp(string? op, int? upscaleFactor, int? targetHeight, int? frameMultiplier, VideoEnhanceRequestBody? body = null)
    {
        string normalized = Normalize(op) ?? (op?.Trim() ?? string.Empty);
        return normalized switch
        {
            nameof(VideoEnhanceOp.Upscale) => upscaleFactor is { } factor
                ? $"upscale the video {factor}×"
                : targetHeight is { } height ? $"upscale the video to {height}p" : "upscale the video",
            nameof(VideoEnhanceOp.Interpolate) => frameMultiplier is { } multiplier
                ? $"smooth the motion to {multiplier}× the frame rate"
                : "smooth the motion",
            nameof(VideoEnhanceOp.Denoise) => "remove noise from the video",
            nameof(VideoEnhanceOp.MotionBlur) => "add motion blur to the video",
            nameof(VideoEnhanceOp.Extend) => "extend the picture " + DescribeExtendShape(body),
            _ => normalized.ToLowerInvariant(),
        };
    }

    /// <summary>
    /// Text for an approval card: also drops the invisible characters that can make a line read differently from what it holds
    /// (Unicode bidi controls U+202A..U+202E, U+2066..U+2069, U+200E/F and the zero-width U+200B..U+200D, U+2060, U+FEFF).
    /// Only the card text is filtered; the sanitizer other callers share is unchanged.
    /// </summary>
    internal static string CardSafe(string text) =>
        string.Concat(text.Where(c => c is not (>= '\u202A' and <= '\u202E') and not (>= '\u2066' and <= '\u2069')
            and not ('\u200E' or '\u200F') and not (>= '\u200B' and <= '\u200D') and not ('\u2060' or '\uFEFF'))).Trim();

    /// <summary>The new shape in plain words: "to vertical 9:16", "wider on the left and right", never the raw numbers alone.</summary>
    internal static string DescribeExtendShape(VideoEnhanceRequestBody? body)
    {
        if (body is null)
            return "past its border";
        if (!string.IsNullOrWhiteSpace(body.TargetAspect))
        {
            return body.TargetAspect.Trim() switch
            {
                "9:16" => "to vertical 9:16",
                "16:9" => "to widescreen 16:9",
                "1:1" => "to square 1:1",
                "4:5" => "to portrait 4:5",
                string other => $"to {other}",
            };
        }

        static string Sides(string first, double? a, string second, double? b) =>
            a is > 0 && b is > 0 ? $"on the {first} and {second}" : a is > 0 ? $"on the {first}" : b is > 0 ? $"on the {second}" : string.Empty;

        string horizontal = Sides("left", body.MarginLeft, "right", body.MarginRight);
        string vertical = Sides("top", body.MarginTop, "bottom", body.MarginBottom);
        List<string> parts = [];
        if (horizontal.Length > 0)
            parts.Add($"wider {horizontal}");
        if (vertical.Length > 0)
            parts.Add($"taller {vertical}");
        return parts.Count == 0 ? "past its border" : string.Join(" and ", parts);
    }

    /// <summary>The Extend-only arguments of both tools, kept together so the other operations' signatures stay as they were.</summary>
    internal sealed record ExtendArgs(
        string? TargetAspect, double? MarginLeft, double? MarginRight, double? MarginTop, double? MarginBottom, string? Prompt);

    private static string Summary(Facts facts)
    {
        VideoEnhanceEstimate estimate = facts.Estimate;
        string what = DescribeOp(facts.Op, facts.Body.UpscaleFactor, facts.Body.TargetHeight, facts.Body.FrameMultiplier, facts.Body);
        string length = estimate.Seconds > 0 ? $"{estimate.Seconds} s long" : "of unknown length";
        string output = estimate.OutputWidth > 0 && estimate.OutputHeight > 0
            ? $" The result is {estimate.OutputWidth}×{estimate.OutputHeight} at {estimate.OutputFps:0.###} fps."
            : string.Empty;
        string price = estimate.Hosted
            ? estimate.BillingEnabled ? $"About {estimate.Credits:0.##} credits." : "It is paid."
            : "It is free: it runs on our own servers and uses no AI model.";
        return $"{char.ToUpperInvariant(what[0])}{what[1..]} of {Quote(facts.SourceName)} in {Quote(facts.Project.Name)} ({length}).{output} {price} "
            + "It starts a run and does not wait; the enhanced video is added to the project's files when it finishes.";
    }

    internal static string Quote(string value) => $"\"{value.Replace("\"", "'")}\"";

    internal static string RefusalJson(VideoEnhanceRefusal refusal)
    {
        // A bare 404 is the route's "absent media": never a 403, and it names nothing.
        if (refusal.Body is null)
            return refusal.StatusCode == StatusCodes.Status404NotFound
                ? Error("not found", "NOT_FOUND")
                : Error("the video cannot be enhanced right now.");
        return JsonSerializer.Serialize(refusal.Body, Json);
    }

    internal static string Error(string message, string? code = null) =>
        code is null
            ? JsonSerializer.Serialize(new { error = message }, Json)
            : JsonSerializer.Serialize(new { error = message, code }, Json);

    /// <summary>What a proposal saw: the source, the plan's facts and the exact body Apply would send.</summary>
    internal sealed record Facts(
        Project Project,
        string MediaRef,
        VideoEnhanceRequestBody Body,
        string SourceName,
        string? Op,
        string? Engine,
        VideoEnhanceEstimate Estimate);

    internal sealed record Prepared(string? Error, Facts? Facts, string? Payload, string? Summary)
    {
        public static Prepared Fail(string error) => new(error, null, null, null);
    }
}
