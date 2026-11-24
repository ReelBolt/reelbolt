using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The text to post with a finished video, per platform: written on request (metered credits, nothing saved), and kept
/// per (project, output, platform, language) when the user saves it. The output is a step result of the project or an
/// editor render saved as a project file. Every action asks <see cref="IProjectAccessService"/> exactly once; a foreign or
/// missing project, or an output that is not this project's, is <c>404</c>. Edit writes, View reads saved texts.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/outputs/{outputId:guid}/post-texts")]
[Authorize]
public class PostTextsController : ControllerBase
{
    public const int MaxAboutChars = PostTextContext.MaxAboutChars;
    public const int MaxToneChars = 60;

    private static readonly Regex LanguagePattern = new(
        @"^[\p{L}][\p{L}\p{N} _-]{1,31}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IPostTextWriter _writer;
    private readonly PostTextContextBuilder _contextBuilder;
    private readonly PostTextRateLimiter? _limiter;
    private readonly INonWorkflowChargeService? _charges;
    private readonly IAgentChatClientProvider? _chatClients;
    private readonly PostTextOptions _options;
    private readonly PostTextVideoReader? _reader;
    private readonly IInferenceProviderResolver? _providers;
    private readonly ILogger<PostTextsController> _logger;

    public PostTextsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IPostTextWriter writer,
        PostTextContextBuilder contextBuilder,
        PostTextRateLimiter? limiter = null,
        INonWorkflowChargeService? charges = null,
        IAgentChatClientProvider? chatClients = null,
        IOptions<PostTextOptions>? options = null,
        ILogger<PostTextsController>? logger = null,
        PostTextVideoReader? reader = null,
        IInferenceProviderResolver? providers = null)
    {
        _reader = reader;
        _providers = providers;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _writer = writer;
        _contextBuilder = contextBuilder;
        _limiter = limiter;
        _charges = charges;
        _chatClients = chatClients;
        _options = options?.Value ?? new PostTextOptions();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PostTextsController>.Instance;
    }

    private bool BillingOn => _charges is { Options.Enabled: true };

    /// <summary>What it would cost before it is asked for, line by line. Nothing is written, no model is called and the hourly cap is not touched.</summary>
    [HttpPost("estimate")]
    public async Task<ActionResult<PostTextEstimateResponse>> Estimate(
        Guid projectId, Guid outputId, [FromBody] PostTextRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        (ActionResult? invalid, List<PostTextPlatform> platforms) = ValidateRequest(request);
        if (invalid is not null) return invalid;
        PostTextContextBuilder.ResolvedOutput? output = await _contextBuilder.ResolveOutputAsync(projectId, outputId, ct);
        if (output is null || access.Project is null) return NotFound();

        PostTextRateState rate = _limiter?.Peek(_currentUser.UserId, projectId) ?? new PostTextRateState(0, int.MaxValue, 0);
        Plan plan;
        try
        {
            plan = await PlanAsync(access.Project, output, request, platforms, requestStills: false, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }

        if (plan.Refusal is { } refusal) return RefusalResult(refusal);

        PostTextEstimateResponse Answer(bool billing, double? credits, double? balance, bool affordable, bool included, double? total) => new(
            billing, credits, balance, affordable, included, plan.Label, plan.Language, ToUsed(plan.Context.Used),
            rate.Used, rate.Limit == int.MaxValue ? 0 : rate.Limit, rate.Full ? rate.RetryAfterSeconds : 0,
            PlatformsResponse(), plan.Items, plan.Disclosure, plan.Video, total);

        Guid org = access.Project.OrganizationId;
        if (!BillingOn || org == Guid.Empty) return Ok(Answer(false, null, null, true, false, null));
        if (!plan.AnyPlatformCost) return Ok(Answer(true, 0, null, true, true, plan.TranscriptMillis is { } t0 ? t0 / 1000.0 : 0));

        long required = Math.Max(plan.FloorMillis, plan.HeldMillis);
        long total = required + (plan.TranscriptMillis ?? 0);
        long? available = await _charges!.GetAvailableMillisAsync(org, ct);
        return Ok(Answer(true, required / 1000.0, available is null ? null : available / 1000.0,
            available is null || available >= total, false, total / 1000.0));
    }

    /// <summary>
    /// Writes the post text for the chosen platforms and returns it; nothing is saved here. With <c>readVideo</c> the frames and
    /// the render's own transcript are read too: when either is not ready the answer is <c>202 {status:"preparing"}</c> (frames
    /// were just requested, a transcript is started on the media analysis route) and nothing is held or counted. <c>402
    /// INSUFFICIENT_CREDITS</c>, <c>409</c> for a model with no price or a video the policy holds, <c>429</c> past the hourly cap,
    /// <c>503 no_chat_provider</c> when no chat provider resolves, <c>424</c> when the model gave nothing usable (charged only
    /// for what the call used), <c>422 POST_TEXT_BLOCKED</c> when the content-policy screen refuses the text.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<PostTextResponse>> Write(
        Guid projectId, Guid outputId, [FromBody] PostTextRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        (ActionResult? invalid, List<PostTextPlatform> platforms) = ValidateRequest(request);
        if (invalid is not null) return invalid;
        PostTextContextBuilder.ResolvedOutput? output = await _contextBuilder.ResolveOutputAsync(projectId, outputId, ct);
        if (output is null || access.Project is null) return NotFound();

        Guid userId = _currentUser.UserId;
        if (_limiter?.Peek(userId, projectId) is { Full: true } capped) return RateLimited(capped);

        // The chat provider is resolved once: that resolution both sizes the hold and is the row the model is called on.
        ChargeHold? hold = null;
        Plan plan;
        Guid requestId = Guid.NewGuid();
        Guid org = access.Project.OrganizationId;
        try
        {
            plan = await PlanAsync(access.Project, output, request, platforms, requestStills: true, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }

        if (plan.Refusal is { } refusal) return RefusalResult(refusal);
        if (plan.Preparing)
        {
            Response.Headers.RetryAfter = PreparingRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status202Accepted, new
            {
                status = "preparing",
                retryAfterSeconds = PreparingRetryAfterSeconds,
                video = plan.Video,
                message = "The video is being prepared. Ask again in a few seconds; start the transcript first if it is missing."
            });
        }

        long floorMillis = plan.FloorMillis;
        if (_charges is not null && org != Guid.Empty && plan.AnyPlatformCost)
        {
            hold = await _charges.BeginHoldAsync(
                org, UsageSource.PostText, Math.Max(floorMillis, plan.HeldMillis), requestId,
                new CreditLedgerContext(projectId, Note: "post-text"), ct);
            if (!hold.Allowed) return NonWorkflowChargeResults.Refusal(hold, "post text");
        }

        // Counted only now (after the 402 gate, before the model call), so a refused request never uses up a slot.
        if (_limiter is not null && !_limiter.TryAcquire(userId, projectId, out PostTextRateState taken))
        {
            await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, null, _logger);
            return RateLimited(taken);
        }

        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(0.001, _options.TimeoutSeconds)));

        PostTextWriteResult? result = null;
        PostTextContext context = plan.Context;
        bool timedOut = false;
        long? meteredMillis = null;
        long? unheldMillis = null;
        try
        {
            using (UsageScope.Begin(org == Guid.Empty ? null : org, projectId, null, null, userId, UsageSource.PostText))
            {
                ModerationAttribution attribution = new(
                    org, projectId, userId, output.ExecutionId, null, ModerationSurface.GeneratedText, ModerationContentKind.Text);
                try
                {
                    if (plan.Material is { Stills: VideoPartState.Ready } material && _reader is not null)
                    {
                        string? described = await _reader.DescribeFramesAsync(material, callCts.Token);
                        if (described is not null) context = context with { FrameDescription = described };
                    }

                    result = await _writer.WriteAsync(
                        new PostTextWriteRequest(context, platforms, plan.Language, plan.Tone, attribution, plan.Provider, plan.WebHashtags),
                        callCts.Token);
                }
                catch (OperationCanceledException) when (callCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    timedOut = true;
                }
                catch (ModelUnpricedException unpriced)
                {
                    // The model is an unpriced Platform model: no call was made, so the hold is released.
                    return ModelUnpricedResults.Conflict(unpriced);
                }

                // No chat provider means no call was made: the hold is released, not settled. A call that metered platform
                // usage no price row prices settles at least at its hold, never at the understated sum.
                if (result is not null && !timedOut)
                {
                    bool unpricedUsage = UsageScope.Current?.SawUnpricedPlatformUsage == true;
                    meteredMillis = unpricedUsage
                        ? Math.Max(UsageScope.RunningCreditsMillis, Math.Max(hold?.RequiredMillis ?? 0, floorMillis))
                        : UsageScope.RunningCreditsMillis;

                    // A failed answer is charged for what it consumed; only a call that metered nothing is released.
                    if (result.Failed && meteredMillis <= 0 && !unpricedUsage) meteredMillis = null;

                    bool reservedNothing = hold is null
                                           || hold.ReservationId is null && hold.Code != NonWorkflowChargeCodes.BillingDisabled;
                    if (reservedNothing && (meteredMillis > 0 || unpricedUsage)) unheldMillis = meteredMillis;
                }
            }
        }
        finally
        {
            await NonWorkflowChargeResults.CloseHoldAsync(_charges, hold, meteredMillis, _logger);
            if (_charges is not null && org != Guid.Empty && unheldMillis is { } unheld)
            {
                await _charges.ChargeUnheldAsync(
                    org, UsageSource.PostText, unheld, requestId, new CreditLedgerContext(projectId, Note: "post-text"), CancellationToken.None);
            }
        }

        if (timedOut)
        {
            return StatusCode(StatusCodes.Status424FailedDependency, new
            {
                code = "POST_TEXT_TIMEOUT",
                message = "The AI took too long to answer. You were not charged; please try again."
            });
        }

        if (result is null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "no_chat_provider",
                message = "No chat provider is configured for writing post text. Add one under Admin → Inference providers."
            });
        }

        double? creditsUsed = meteredMillis is > 0 ? meteredMillis.Value / 1000.0 : null;
        if (result.Blocked)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, new
            {
                code = "POST_TEXT_BLOCKED",
                creditsUsed = creditsUsed ?? 0,
                message = result.Message
            });
        }

        if (result.Failed)
        {
            return StatusCode(StatusCodes.Status424FailedDependency, new
            {
                code = "POST_TEXT_FAILED",
                creditsUsed = creditsUsed ?? 0,
                message = creditsUsed is { } used
                    ? $"The AI did not give usable text this time. About {used:0.##} credits were used; please try again."
                    : "The AI did not answer this time. You were not charged; please try again."
            });
        }

        return Ok(new PostTextResponse(
            outputId, plan.Language, plan.Tone, creditsUsed,
            result.Drafts.Select(ToDraftResponse).ToList(), ToUsed(context.Used),
            result.HashtagBasis ?? new HashtagBasis(nameof(HashtagKind.ModelSuggested), ModelSuggestedHashtagSource.SourceName, "suggested, not measured trends"),
            plan.Video));
    }

    public const int PreparingRetryAfterSeconds = 10;

    /// <summary>Everything one estimate or write needs, computed once so the two always agree.</summary>
    private sealed record Plan(
        PostTextContext Context, string Language, string? Tone, ResolvedInferenceProvider? Provider, string? Label,
        long FloorMillis, long HeldMillis, bool AnyPlatformCost, long? TranscriptMillis, bool WebHashtags,
        VideoMaterial? Material, IReadOnlyList<PostTextLineItem> Items, string? Disclosure, PostTextVideoState? Video,
        bool Preparing, PublishModerationOutcome? Refusal);

    private async Task<Plan> PlanAsync(
        Project project, PostTextContextBuilder.ResolvedOutput output, PostTextRequest request, List<PostTextPlatform> platforms,
        bool requestStills, CancellationToken ct)
    {
        PostTextContext context = await _contextBuilder.BuildAsync(project, output, request.About, new PostTextContextOptions(request.ReadVideo), ct);
        string language = ResolveLanguage(request.Language, context);
        string? tone = string.IsNullOrWhiteSpace(request.Tone) ? context.BriefTone : request.Tone.Trim();
        bool web = string.Equals(request.HashtagSource, HashtagSourceWeb, StringComparison.OrdinalIgnoreCase);
        Guid org = project.OrganizationId;
        bool pricing = _charges is not null && org != Guid.Empty;

        VideoMaterial? material = null;
        if (request.ReadVideo && _reader is not null)
        {
            material = await _reader.InspectAsync(project.Id, output.Id, org, _currentUser.UserId, requestStills, ct);
            if (material?.Refusal is { } refusal)
                return new Plan(context, language, tone, null, null, 0, 0, false, null, web, material, [], null, null, false, refusal);
        }

        if (material is { Transcript: VideoPartState.Ready, TranscriptText: { } spoken }) context = context with { RenderTranscript = spoken };

        ResolvedInferenceProvider? provider = await ResolveProviderAsync(ct);
        long? textMillis = pricing
            ? await EstimateChatAsync(provider, (PromptChars(context, platforms, language, tone) + 3L) / 4, PostTextWriter.MaxOutputTokens, ct)
            : null;

        var items = new List<PostTextLineItem>
        {
            new("text", textMillis is { } tm ? Math.Max(tm, 0) / 1000.0 : null, "included", null, false)
        };
        long held = textMillis ?? 0;
        bool anyCost = textMillis is not null;

        PostTextVideoState? video = null;
        long? transcriptMillis = null;
        if (material is not null)
        {
            // Frames: one vision call over up to six stills, inside the hold.
            long? framesMillis = null;
            string framesState = material.Stills switch
            {
                VideoPartState.Ready => "ready",
                VideoPartState.Pending => "pending",
                _ => "skipped"
            };
            if (material.Stills is VideoPartState.Ready or VideoPartState.Pending && pricing)
            {
                framesMillis = await EstimateChatAsync(
                    material.Vision, PostTextVideoReader.MaxFrames * PostTextVideoReader.TokensPerFrame + 300L, PostTextVideoReader.FrameDescriptionTokens, ct);
            }

            items.Add(new("frames", framesMillis is { } fm ? fm / 1000.0 : null, framesState, material.StillsNote, false));
            if (framesMillis is { } f) { held += f; anyCost = true; }

            // Transcript: priced here, charged by its own hold on the analysis route when started, so not part of this hold.
            string transcriptState = material.Transcript switch
            {
                VideoPartState.Ready => "ready",
                VideoPartState.Pending => "missing",
                _ => "skipped"
            };
            double? transcriptCredits = material.Transcript == VideoPartState.Ready ? 0 : null;
            if (material.Transcript == VideoPartState.Pending && pricing && _providers is not null)
            {
                TranscriptPrice? price = await TranscriptPriceEstimator.PriceAsync(_providers, _charges!, material.DurationSec, org, ct);
                if (price is not null)
                {
                    transcriptMillis = price.RequiredMillis;
                    transcriptCredits = price.RequiredMillis / 1000.0;
                }
            }

            items.Add(new("transcript", transcriptCredits, transcriptState, material.TranscriptNote, material.Transcript == VideoPartState.Pending));
            video = new PostTextVideoState(
                material.MediaRef, transcriptState, framesState, material.TranscriptNote, material.StillsNote,
                material.Transcript == VideoPartState.Pending
                    ? new PostTextStartTranscript("POST", $"/api/v1/projects/{project.Id}/media/{material.MediaRef}/analysis", new[] { MediaAnalysisKinds.Transcript })
                    : null);
        }

        if (web)
        {
            long? webMillis = null;
            string webState = "included";
            string? webNote = null;
            ResolvedWebSearchProvider? search = _providers is null ? null : await _providers.ResolveWebSearchAsync(null, ct);
            if (search is null) { webState = "skipped"; webNote = "Web search is not set up, so the writer's own tags are used."; }
            else if (search.Kind == InferenceProviderKind.Gemini) { webState = "skipped"; webNote = "This search provider is not usable here, so the writer's own tags are used."; }
            else if (pricing && search.IsPlatformManaged)
            {
                webMillis = await _charges!.EstimateAsync(new PriceRequest(
                    search.ProviderId, search.AccountId, search.Kind, InferenceProviderCapability.WebSearch, search.ModelName,
                    UsageKind.WebSearches, null, MeteringChatClient.BillingModeOf(search.OrganizationId), 1m), ct);
                held += webMillis.Value;
                anyCost = true;
            }

            items.Add(new("webSearch", webMillis is { } wm ? wm / 1000.0 : null, webState, webNote, false));
            if (webState == "skipped") web = false;
        }

        bool preparing = material is not null
                         && (material.Transcript == VideoPartState.Pending || material.Stills == VideoPartState.Pending);
        string? disclosure = Disclosure(request.ReadVideo, web);
        string? label = provider is null ? null : string.IsNullOrWhiteSpace(provider.Name) ? provider.ModelName : $"{provider.ModelName} ({provider.Name})";
        long floor = (_charges?.Options.NonWorkflow.PostTextHoldCredits ?? 0) * 1000;
        return new Plan(context, language, tone, provider, label, floor, held, anyCost, transcriptMillis, web, material, items, disclosure, video, preparing, null);
    }

    public const string HashtagSourceWeb = "web";
    public const string HashtagSourceModel = "model";

    /// <summary>What leaves the platform, in counts only. Null when nothing beyond the chat provider is involved.</summary>
    private static string? Disclosure(bool readVideo, bool web)
    {
        var parts = new List<string>();
        if (readVideo)
        {
            parts.Add($"Up to {PostTextVideoReader.MaxFrames} still frames of this video are sent to your workspace's vision provider, "
                      + "and its audio to the speech-to-text provider if no transcript exists yet.");
        }

        if (web) parts.Add("The project name and the \"about\" text you wrote are sent as a search query to your workspace's web search provider (links are removed first).");
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    private ObjectResult RefusalResult(PublishModerationOutcome refusal) =>
        StatusCode(StatusCodes.Status409Conflict, new { code = refusal.Code ?? "CONTENT_BLOCKED", message = refusal.Message });

    /// <summary>The saved texts of one output, optionally for one language. View permission.</summary>
    [HttpGet]
    public async Task<ActionResult<SavedPostTextListResponse>> List(
        Guid projectId, Guid outputId, [FromQuery] string? language, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (await _contextBuilder.ResolveOutputAsync(projectId, outputId, ct) is null) return NotFound();

        IQueryable<PostTextRow> query = _db.PostTexts.AsNoTracking().Where(t => t.ProjectId == projectId && t.OutputId == outputId);
        if (!string.IsNullOrWhiteSpace(language)) query = query.Where(t => t.Language == language.Trim().ToLowerInvariant());
        List<PostTextRow> rows = await query.OrderBy(t => t.Platform).ThenBy(t => t.Language).ToListAsync(ct);
        return Ok(new SavedPostTextListResponse(rows.Select(ToSavedResponse).ToList()));
    }

    /// <summary>
    /// Saves (creates or replaces) the text of one platform and language. The text is re-checked in code like a written one
    /// (links and ids removed, limits enforced), so what is stored is what the response shows. Edit permission.
    /// </summary>
    [HttpPut("{platform}")]
    public async Task<ActionResult<SavedPostTextResponse>> Save(
        Guid projectId, Guid outputId, string platform, [FromBody] SavePostTextRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        PostTextPlatform? target = PostTextPlatformLimits.Find(platform);
        if (target is null)
            return BadRequest(new { message = $"platform must be one of {string.Join(", ", PostTextPlatformLimits.All.Select(p => p.Id))}" });
        if (request is null || NormalizeLanguage(request.Language) is not { } language)
            return BadRequest(new { message = "language is required (a language code or name, up to 32 characters)." });
        if (request.Tone is { Length: > MaxToneChars })
            return BadRequest(new { message = $"tone can be at most {MaxToneChars} characters." });
        if (await _contextBuilder.ResolveOutputAsync(projectId, outputId, ct) is null) return NotFound();

        PostTextChecked checkedText = PostTextValidator.Check(
            new PostTextDraft(target.Id, request.Title, request.Body ?? string.Empty, request.Hashtags ?? []), target);
        if (checkedText.Draft.Body.Length == 0)
            return BadRequest(new { code = "POST_TEXT_EMPTY", message = "The text is empty once links and ids are removed." });

        PostTextRow? row = await _db.PostTexts.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.OutputId == outputId && t.Platform == target.Id && t.Language == language, ct);
        if (row is null)
        {
            if (await _db.PostTexts.CountAsync(t => t.ProjectId == projectId, ct) >= PostTextRow.MaxPerProject)
            {
                return Conflict(new
                {
                    code = "POST_TEXT_LIMIT",
                    message = $"A project can keep at most {PostTextRow.MaxPerProject} saved post texts. Delete one first."
                });
            }

            row = new PostTextRow { Id = Guid.NewGuid(), ProjectId = projectId, OutputId = outputId, Platform = target.Id, Language = language };
            _db.PostTexts.Add(row);
        }

        row.Title = checkedText.Draft.Title;
        row.Body = checkedText.Draft.Body;
        row.HashtagsJson = JsonSerializer.Serialize(checkedText.Draft.Hashtags, Json);
        row.Tone = string.IsNullOrWhiteSpace(request.Tone) ? null : request.Tone.Trim();
        row.Edited = request.Edited;
        row.UpdatedByUserId = _currentUser.UserId;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(ToSavedResponse(row));
    }

    /// <summary>Deletes the saved text of one platform and language. <c>404</c> when there is none. Edit permission.</summary>
    [HttpDelete("{platform}")]
    public async Task<IActionResult> Delete(
        Guid projectId, Guid outputId, string platform, [FromQuery] string language, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        PostTextPlatform? target = PostTextPlatformLimits.Find(platform);
        if (target is null || NormalizeLanguage(language) is not { } lang) return NotFound();

        PostTextRow? row = await _db.PostTexts.FirstOrDefaultAsync(
            t => t.ProjectId == projectId && t.OutputId == outputId && t.Platform == target.Id && t.Language == lang, ct);
        if (row is null) return NotFound();
        _db.PostTexts.Remove(row);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ---- helpers ----

    private (ActionResult? Failure, List<PostTextPlatform> Platforms) ValidateRequest(PostTextRequest? request)
    {
        if (request is null) return (BadRequest(new { message = "A body is required." }), []);
        if (request.Platforms is not { Count: > 0 })
            return (BadRequest(new { message = $"platforms must name at least one of {string.Join(", ", PostTextPlatformLimits.All.Select(p => p.Id))}" }), []);

        var platforms = new List<PostTextPlatform>();
        foreach (string id in request.Platforms)
        {
            PostTextPlatform? p = PostTextPlatformLimits.Find(id);
            if (p is null)
                return (BadRequest(new { message = $"unknown platform '{id}'; use one of {string.Join(", ", PostTextPlatformLimits.All.Select(x => x.Id))}" }), []);
            if (!platforms.Contains(p)) platforms.Add(p);
        }

        if (request.About is { Length: > MaxAboutChars })
            return (BadRequest(new { message = $"about can be at most {MaxAboutChars} characters." }), []);
        if (request.Tone is { Length: > MaxToneChars })
            return (BadRequest(new { message = $"tone can be at most {MaxToneChars} characters." }), []);
        if (request.HashtagSource is { } hs && !string.IsNullOrWhiteSpace(hs)
            && !string.Equals(hs, HashtagSourceModel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(hs, HashtagSourceWeb, StringComparison.OrdinalIgnoreCase))
            return (BadRequest(new { message = "hashtagSource must be \"model\" or \"web\"." }), []);
        if (!string.IsNullOrWhiteSpace(request.Language) && NormalizeLanguage(request.Language) is null)
            return (BadRequest(new { message = "language must be a language code or name (letters, digits, space, - or _; up to 32 characters)." }), []);
        return (null, platforms);
    }

    /// <summary>The language key: trimmed, lower case, letters digits space - and _ only; null when it does not fit.</summary>
    public static string? NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        string s = language.Trim();
        return LanguagePattern.IsMatch(s) ? s.ToLowerInvariant() : null;
    }

    /// <summary>The language override, else the run brief's language (never the UI locale), else "auto" (the language of the text).</summary>
    public static string ResolveLanguage(string? requested, PostTextContext context) =>
        NormalizeLanguage(requested) ?? NormalizeLanguage(context.BriefLanguage) ?? "auto";

    private static int PromptChars(PostTextContext context, IReadOnlyList<PostTextPlatform> platforms, string language, string? tone) =>
        PostTextWriter.SystemPrompt().Length
        + PostTextWriter.UserMessage(new PostTextWriteRequest(
            context, platforms, language, tone, new ModerationAttribution(Guid.Empty, null, null, null, null, ModerationSurface.GeneratedText, ModerationContentKind.Text), null)).Length;

    /// <summary>
    /// Millicredits one chat or vision call is expected to cost at most (input and output tokens at the provider's prices); 0 when
    /// there is no provider (the floor applies); null for a provider the workspace pays for itself.
    /// </summary>
    private async Task<long?> EstimateChatAsync(ResolvedInferenceProvider? provider, long inputTokens, int outputTokens, CancellationToken ct)
    {
        if (_charges is null || provider is null) return provider is null ? 0 : null;
        if (!provider.IsPlatformManaged) return null;
        decimal input = Math.Max(1, inputTokens);
        PriceRequest Request(UsageKind unit, decimal quantity) => new(
            provider.ProviderId, provider.AccountId, provider.Kind, provider.Capability, provider.ModelName, unit, null,
            UsageBillingMode.Platform, quantity, PromptTokens: (long)input);

        return await _charges.EstimateAsync(Request(UsageKind.LlmInputTokens, input), ct)
               + await _charges.EstimateAsync(Request(UsageKind.LlmOutputTokens, outputTokens), ct);
    }

    /// <summary>The chat provider post text resolves to; null when none resolves. An unpriced Platform model propagates (409).</summary>
    private async Task<ResolvedInferenceProvider?> ResolveProviderAsync(CancellationToken ct)
    {
        if (_chatClients is null) return null;
        try
        {
            return await _chatClients.ResolveProviderAsync(PostTextWriter.ChatAgent, null, ct);
        }
        catch (Exception ex) when (ex is not ModelUnpricedException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private ObjectResult RateLimited(PostTextRateState state)
    {
        Response.Headers.RetryAfter = state.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            code = "POST_TEXT_RATE_LIMITED",
            message = state.Message,
            used = state.Used,
            limit = state.Limit,
            windowMinutes = 60,
            retryAfter = state.RetryAfterSeconds,
            retryAfterSeconds = state.RetryAfterSeconds
        });
    }

    private static PostTextUsedResponse ToUsed(PostTextUsed u) =>
        new(u.ProjectDetails, u.Brief, u.Script, u.Narration, u.Transcript, u.About, u.VideoTranscript, u.Frames);

    private static PostTextDraftResponse ToDraftResponse(PostTextChecked c)
    {
        PostTextPlatform p = PostTextPlatformLimits.Find(c.Draft.Platform)!;
        return new PostTextDraftResponse(
            p.Id, p.Label, c.Draft.Title, c.Draft.Body, c.Draft.Hashtags, c.Text, c.Characters, c.Limit,
            p.HasTitle ? p.MaxTitleChars : null, c.Trimmed);
    }

    private static SavedPostTextResponse ToSavedResponse(PostTextRow row)
    {
        PostTextPlatform p = PostTextPlatformLimits.Find(row.Platform) ?? PostTextPlatformLimits.All[0];
        List<string> tags;
        try
        {
            tags = JsonSerializer.Deserialize<List<string>>(row.HashtagsJson, Json) ?? [];
        }
        catch (JsonException)
        {
            tags = [];
        }

        string text = PostTextValidator.Compose(new PostTextDraft(row.Platform, row.Title, row.Body, tags), p);
        return new SavedPostTextResponse(
            row.Id, row.OutputId, row.Platform, row.Language, row.Title, row.Body, tags, text, text.Length, p.MaxBodyChars,
            p.HasTitle ? p.MaxTitleChars : null, row.Tone, row.Edited, row.CreatedAt, row.UpdatedAt);
    }

    private static PostTextPlatformsResponse PlatformsResponse() => new(
        PostTextPlatformLimits.Version,
        PostTextPlatformLimits.LastChecked,
        PostTextPlatformLimits.All.Select(p => new PostTextPlatformResponse(
            p.Id, p.Label, p.HasTitle, p.MaxTitleChars, p.MaxBodyChars, p.MaxHashtags, p.SuggestedHashtags, p.HashtagsInBody)).ToList());
}

/// <summary>Body of write and estimate. <c>platforms</c>: ids from <c>tiktok, instagram_reels, youtube_shorts, linkedin, x</c>.</summary>
public sealed record PostTextRequest(
    IReadOnlyList<string>? Platforms, string? Tone = null, string? Language = null, string? About = null,
    bool ReadVideo = false, string? HashtagSource = null);

/// <summary>Body of save. <c>edited</c> is true when the user changed the written text by hand.</summary>
public sealed record SavePostTextRequest(string? Language, string? Title, string? Body, IReadOnlyList<string>? Hashtags, string? Tone = null, bool Edited = false);

public sealed record PostTextUsedResponse(
    bool ProjectDetails, bool Brief, bool Script, bool Narration, bool Transcript, bool About,
    bool VideoTranscript = false, bool Frames = false);

/// <summary>One priced part of a request. <c>state</c>: included, ready, pending, missing or skipped. <c>heldSeparately</c>: charged by its own route, not this hold.</summary>
public sealed record PostTextLineItem(string Kind, double? Credits, string State, string? Note, bool HeldSeparately);

/// <summary>How to start the transcript on the existing media analysis route.</summary>
public sealed record PostTextStartTranscript(string Method, string Path, IReadOnlyList<string> Kinds);

/// <summary>Where "read the video" stands. <c>transcript</c>: ready, missing or skipped; <c>frames</c>: ready, pending or skipped.</summary>
public sealed record PostTextVideoState(
    string MediaRef, string Transcript, string Frames, string? TranscriptNote, string? FramesNote, PostTextStartTranscript? StartTranscript);

public sealed record PostTextPlatformResponse(
    string Id, string Label, bool HasTitle, int MaxTitleChars, int MaxBodyChars, int MaxHashtags, int SuggestedHashtags, bool HashtagsInBody);

public sealed record PostTextPlatformsResponse(int Version, string LastChecked, IReadOnlyList<PostTextPlatformResponse> Items);

public sealed record PostTextDraftResponse(
    string Platform, string Label, string? Title, string Body, IReadOnlyList<string> Hashtags,
    string Text, int Characters, int Limit, int? TitleLimit, bool Trimmed);

public sealed record PostTextResponse(
    Guid OutputId, string Language, string? Tone, double? CreditsUsed,
    IReadOnlyList<PostTextDraftResponse> Drafts, PostTextUsedResponse Used, HashtagBasis HashtagBasis, PostTextVideoState? Video = null);

public sealed record PostTextEstimateResponse(
    bool BillingEnabled, double? Credits, double? Balance, bool Affordable, bool Included, string? Model, string Language,
    PostTextUsedResponse Used, int RateUsed, int RateLimit, int RetryAfterSeconds, PostTextPlatformsResponse Platforms,
    IReadOnlyList<PostTextLineItem> LineItems, string? Disclosure, PostTextVideoState? Video, double? TotalCredits);

public sealed record SavedPostTextResponse(
    Guid Id, Guid OutputId, string Platform, string Language, string? Title, string Body, IReadOnlyList<string> Hashtags,
    string Text, int Characters, int Limit, int? TitleLimit, string? Tone, bool Edited, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record SavedPostTextListResponse(IReadOnlyList<SavedPostTextResponse> Items);
