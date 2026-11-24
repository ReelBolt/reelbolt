using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PostTexts;
using ReelBolt.Inference.Api.Services.SocialPublishing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Posts of finished videos to connected social accounts (docs/social-publishing.md): create (now or scheduled), list, read,
/// change (only while still scheduled), cancel, retry, and the preflight. Authority is EDIT ON THE PROJECT, asked of
/// <see cref="IProjectAccessService"/> on every action, and the account must belong to the ACTIVE organization. A post, project,
/// output or account of another organization, or one that does not exist, is the same <c>404</c>. The engine re-checks all of
/// it when the post fires; nothing here is trusted later.
/// </summary>
[ApiController]
[Route("api/v1/social/posts")]
[Authorize]
public class SocialPostsController : ControllerBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Privacies = ["public", "unlisted", "private", "connections"];

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly ISocialOutputResolver _outputs;
    private readonly IOrganizationEntitlements _entitlements;
    private readonly TimeProvider _clock;

    public SocialPostsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        ISocialOutputResolver outputs,
        IOrganizationEntitlements entitlements,
        TimeProvider? clock = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _outputs = outputs;
        _entitlements = entitlements;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    // ------------------------------------------------------------------ preflight

    /// <summary>Checks text, size, length and picture shape against the platform's limits. Nothing is stored or sent.</summary>
    [HttpPost("preflight")]
    public async Task<ActionResult<SocialPreflightResponse>> Preflight([FromBody] PreflightSocialPostRequest request, CancellationToken ct)
    {
        if (CallerOrganization() is not { } organizationId) return NoWorkspace();
        ProjectAccessResult access = await _projectAccess.GetAsync(request.ProjectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (!SocialOutputKinds.IsValid(request.OutputKind)) return BadRequest(new { error = "invalid_output_kind" });

        SocialAccount? account = await FindAccountAsync(request.AccountId, organizationId, ct);
        if (account is null) return AccountNotFound();
        SocialResolvedOutput? output = await _outputs.ResolveAsync(organizationId, request.ProjectId, request.OutputKind, request.OutputId, ct);
        if (output is null) return OutputNotFound();

        string? invalid = ValidateText(request.Title, request.Body, request.Hashtags);
        if (invalid is not null) return BadRequest(new { error = "invalid_text", message = invalid });

        SocialPreflightResult result = RunPreflight(account.Platform, request.Title, request.Body, request.Hashtags, output, request.Options?.Privacy);
        return Ok(new SocialPreflightResponse(SocialPlatforms.Slug(account.Platform), result.LimitsVersion, result.LimitsLastChecked, result.Ok, result.Issues));
    }

    // ------------------------------------------------------------------ create

    [HttpPost]
    public async Task<ActionResult<SocialPostResponse>> Create([FromBody] CreateSocialPostRequest request, CancellationToken ct)
    {
        if (CallerOrganization() is not { } organizationId) return NoWorkspace();
        ProjectAccessResult access = await _projectAccess.GetAsync(request.ProjectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (!SocialOutputKinds.IsValid(request.OutputKind)) return BadRequest(new { error = "invalid_output_kind" });

        SocialAccount? account = await FindAccountAsync(request.AccountId, organizationId, ct);
        if (account is null) return AccountNotFound();
        if (account.Status != SocialAccountStatus.Active)
            return Conflict(new { error = "account_not_active", message = "That account needs to be reconnected before it can post." });

        if (PlanGate.CheckSocialPublishing(await _entitlements.GetAsync(organizationId, ct)) is { } violation) return violation.ToResult();

        SocialResolvedOutput? output = await _outputs.ResolveAsync(organizationId, request.ProjectId, request.OutputKind, request.OutputId, ct);
        if (output is null) return OutputNotFound();

        string? title = request.Title;
        string? body = request.Body;
        List<string>? hashtags = request.Hashtags;
        if (request.PostTextId is { } postTextId)
        {
            string textPlatformId = SocialPlatformLimits.For(account.Platform).TextPlatformId;
            PostTextRow? saved = await _db.PostTexts.AsNoTracking().FirstOrDefaultAsync(t =>
                t.Id == postTextId && t.ProjectId == request.ProjectId && t.OutputId == request.OutputId && t.Platform == textPlatformId, ct);
            if (saved is null) return NotFound(new { error = "post_text_not_found", message = "That saved post text could not be found for this video and platform." });
            title ??= saved.Title;
            body ??= saved.Body;
            hashtags ??= ReadStringList(saved.HashtagsJson);
        }

        string? invalid = ValidateText(title, body, hashtags);
        if (invalid is not null) return BadRequest(new { error = "invalid_text", message = invalid });
        string? invalidOptions = ValidateOptions(request.Options);
        if (invalidOptions is not null) return BadRequest(new { error = "invalid_options", message = invalidOptions });

        DateTime now = Now();
        SocialScheduleResult when = SocialScheduleTime.Resolve(request.PublishNow, request.ScheduledAt, request.ScheduledLocal, request.TimeZone, now);
        if (!when.Ok) return BadRequest(new { error = when.ErrorCode, message = when.ErrorMessage });

        SocialPreflightResult preflight = RunPreflight(account.Platform, title, body, hashtags, output, request.Options?.Privacy);
        if (!preflight.Ok) return UnprocessableEntity(PreflightBody(account.Platform, preflight));

        SocialPost post = new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ProjectId = request.ProjectId,
            OutputKind = request.OutputKind,
            OutputId = request.OutputId,
            SocialAccountId = account.Id,
            Platform = account.Platform,
            Format = SocialPlatformLimits.For(account.Platform).Format,
            Title = Normalize(title),
            Body = Normalize(body),
            HashtagsJson = JsonSerializer.Serialize(NormalizeTags(hashtags), Json),
            PostTextId = request.PostTextId,
            OptionsJson = SerializeOptions(request.Options),
            ScheduledAt = when.Utc,
            TimeZone = when.TimeZone,
            Status = SocialPostStatus.Scheduled,
            CreatedByUserId = _currentUser.UserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        _db.SocialPosts.Add(post);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/v1/social/posts/{post.Id}", ToResponse(post));
    }

    // ------------------------------------------------------------------ read

    [HttpGet]
    public async Task<ActionResult<SocialPostListResponse>> List(
        [FromQuery] string? status, [FromQuery] Guid? projectId, [FromQuery] Guid? accountId,
        [FromQuery] int skip = 0, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        if (CallerOrganization() is not { } organizationId) return Ok(new SocialPostListResponse([], 0));
        if (_currentUser.OrgRole is OrganizationRole.Billing) return Forbid();

        IQueryable<SocialPost> query = _db.SocialPosts.AsNoTracking().Where(p => p.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(p => p.Status == status);
        if (projectId is { } project) query = query.Where(p => p.ProjectId == project);
        if (accountId is { } account) query = query.Where(p => p.SocialAccountId == account);

        int total = await query.CountAsync(ct);
        List<SocialPost> rows = await query
            .OrderByDescending(p => p.ScheduledAt ?? p.CreatedAt)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);
        return Ok(new SocialPostListResponse(rows.Select(ToResponse).ToList(), total));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SocialPostResponse>> Get(Guid id, CancellationToken ct)
    {
        if (CallerOrganization() is not { } organizationId) return PostNotFound();
        if (_currentUser.OrgRole is OrganizationRole.Billing) return Forbid();
        SocialPost? post = await _db.SocialPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId, ct);
        return post is null ? PostNotFound() : Ok(ToResponse(post));
    }

    // ------------------------------------------------------------------ change

    /// <summary>Edits text, options or time. Only while <c>scheduled</c>; the change is a conditional UPDATE so it cannot race the sweeper's claim.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<SocialPostResponse>> Update(Guid id, [FromBody] UpdateSocialPostRequest request, CancellationToken ct)
    {
        (SocialPost? post, ActionResult? failure) = await LoadForEditAsync(id, ct);
        if (post is null) return failure!;
        if (post.Status != SocialPostStatus.Scheduled) return NotEditable(post);

        string? title = request.Title ?? post.Title;
        string? body = request.Body ?? post.Body;
        List<string> hashtags = request.Hashtags ?? SocialPublishingService.ReadHashtags(post).ToList();
        string? invalid = ValidateText(title, body, hashtags);
        if (invalid is not null) return BadRequest(new { error = "invalid_text", message = invalid });
        string? invalidOptions = ValidateOptions(request.Options);
        if (invalidOptions is not null) return BadRequest(new { error = "invalid_options", message = invalidOptions });

        DateTime now = Now();
        DateTime? scheduledAt = post.ScheduledAt;
        string? timeZone = post.TimeZone;
        if (request.ScheduledAt is not null || request.ScheduledLocal is not null)
        {
            SocialScheduleResult when = SocialScheduleTime.Resolve(false, request.ScheduledAt, request.ScheduledLocal, request.TimeZone ?? post.TimeZone, now);
            if (!when.Ok) return BadRequest(new { error = when.ErrorCode, message = when.ErrorMessage });
            scheduledAt = when.Utc;
            timeZone = when.TimeZone;
        }
        else if (request.TimeZone is not null)
        {
            SocialScheduleResult zone = SocialScheduleTime.Resolve(true, null, null, request.TimeZone, now);
            if (!zone.Ok) return BadRequest(new { error = zone.ErrorCode, message = zone.ErrorMessage });
            timeZone = zone.TimeZone;
        }

        SocialResolvedOutput? output = await _outputs.ResolveAsync(post.OrganizationId, post.ProjectId, post.OutputKind, post.OutputId, ct);
        if (output is null) return OutputNotFound();
        SocialPreflightResult preflight = RunPreflight(post.Platform, title, body, hashtags, output,
            request.Options?.Privacy ?? SocialPublishingService.ReadOptions(post).GetValueOrDefault("privacy"));
        if (!preflight.Ok) return UnprocessableEntity(PreflightBody(post.Platform, preflight));

        string? hashtagsJson = JsonSerializer.Serialize(NormalizeTags(hashtags), Json);
        string? optionsJson = request.Options is null ? post.OptionsJson : SerializeOptions(request.Options);
        string? newTitle = Normalize(title);
        string? newBody = Normalize(body);

        int rows = await _db.SocialPosts
            .Where(p => p.Id == id && p.OrganizationId == post.OrganizationId && p.Status == SocialPostStatus.Scheduled && p.LeaseOwner == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Title, newTitle)
                .SetProperty(p => p.Body, newBody)
                .SetProperty(p => p.HashtagsJson, hashtagsJson)
                .SetProperty(p => p.OptionsJson, optionsJson)
                .SetProperty(p => p.ScheduledAt, scheduledAt)
                .SetProperty(p => p.TimeZone, timeZone)
                .SetProperty(p => p.NextAttemptAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, now), ct);
        if (rows == 0) return NotEditable(await ReloadAsync(id, post.OrganizationId, ct) ?? post);

        SocialPost? updated = await ReloadAsync(id, post.OrganizationId, ct);
        return updated is null ? PostNotFound() : Ok(ToResponse(updated));
    }

    /// <summary>Cancels a post that has not started. Once the engine holds it (<c>publishing</c>) it can no longer be stopped.</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<SocialPostResponse>> Cancel(Guid id, CancellationToken ct)
    {
        (SocialPost? post, ActionResult? failure) = await LoadForEditAsync(id, ct);
        if (post is null) return failure!;

        DateTime now = Now();
        int rows = await _db.SocialPosts
            .Where(p => p.Id == id && p.OrganizationId == post.OrganizationId && (p.Status == SocialPostStatus.Scheduled || p.Status == SocialPostStatus.Draft))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Cancelled)
                .SetProperty(p => p.FailureCode, SocialFailureCodes.Cancelled)
                .SetProperty(p => p.FailureMessage, "Cancelled before it was sent.")
                .SetProperty(p => p.NextAttemptAt, (DateTime?)null)
                .SetProperty(p => p.UpdatedAt, now), ct);
        SocialPost? current = await ReloadAsync(id, post.OrganizationId, ct);
        if (rows == 0)
        {
            return current?.Status == SocialPostStatus.Publishing
                ? Conflict(new { error = "already_sending", message = "This post is being sent right now and can no longer be cancelled." })
                : Conflict(new { error = "invalid_state", message = $"A {current?.Status ?? "missing"} post cannot be cancelled.", status = current?.Status });
        }

        return Ok(ToResponse(current!));
    }

    /// <summary>Queues a failed post again (an <c>unknown</c> one only after the person confirms it is not on the channel).</summary>
    [HttpPost("{id:guid}/retry")]
    public async Task<ActionResult<SocialPostResponse>> Retry(Guid id, [FromBody] RetrySocialPostRequest? request, CancellationToken ct)
    {
        (SocialPost? post, ActionResult? failure) = await LoadForEditAsync(id, ct);
        if (post is null) return failure!;

        if (post.Status == SocialPostStatus.Unknown && request?.ConfirmNotPosted != true)
            return Conflict(new { error = "confirm_not_posted", message = "Check the channel first. If the post is not there, retry with confirmNotPosted." });
        if (post.Status is not (SocialPostStatus.Failed or SocialPostStatus.Unknown))
            return Conflict(new { error = "invalid_state", message = $"A {post.Status} post cannot be retried.", status = post.Status });

        SocialAccount? account = await FindAccountAsync(post.SocialAccountId, post.OrganizationId, ct);
        if (account is null) return AccountNotFound();
        if (account.Status != SocialAccountStatus.Active)
            return Conflict(new { error = "account_not_active", message = "That account needs to be reconnected before it can post." });
        if (PlanGate.CheckSocialPublishing(await _entitlements.GetAsync(post.OrganizationId, ct)) is { } violation) return violation.ToResult();

        string previous = post.Status;
        DateTime now = Now();
        int rows = await _db.SocialPosts
            .Where(p => p.Id == id && p.OrganizationId == post.OrganizationId && p.Status == previous)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, SocialPostStatus.Scheduled)
                .SetProperty(p => p.ScheduledAt, now)
                .SetProperty(p => p.NextAttemptAt, (DateTime?)null)
                .SetProperty(p => p.Attempts, 0)
                .SetProperty(p => p.FailureCode, (string?)null)
                .SetProperty(p => p.FailureMessage, (string?)null)
                .SetProperty(p => p.UpdatedAt, now), ct);
        SocialPost? current = await ReloadAsync(id, post.OrganizationId, ct);
        if (rows == 0) return Conflict(new { error = "invalid_state", message = "The post changed while you were retrying it.", status = current?.Status });
        return Ok(ToResponse(current!));
    }

    // ------------------------------------------------------------------ helpers

    private Guid? CallerOrganization() =>
        _currentUser.OrganizationId is { } org && _currentUser.OrgRole is not null ? org : null;

    private ActionResult NoWorkspace() => NotFound(new { error = "no_workspace", message = "Choose a workspace first." });

    private ActionResult PostNotFound() => NotFound(new { error = "post_not_found", message = "That post could not be found." });

    private ActionResult AccountNotFound() => NotFound(new { error = "account_not_found", message = "That connected account could not be found." });

    private ActionResult OutputNotFound() => NotFound(new { error = "output_not_found", message = "That video could not be found in this project." });

    private ActionResult NotEditable(SocialPost post) => Conflict(new
    {
        error = post.Status == SocialPostStatus.Publishing ? "already_sending" : "not_editable",
        message = "Only a post that has not started can be changed.",
        status = post.Status
    });

    private Task<SocialAccount?> FindAccountAsync(Guid id, Guid organizationId, CancellationToken ct) =>
        _db.SocialAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.OrganizationId == organizationId && a.Status != SocialAccountStatus.Revoked, ct);

    private Task<SocialPost?> ReloadAsync(Guid id, Guid organizationId, CancellationToken ct) =>
        _db.SocialPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.OrganizationId == organizationId, ct);

    /// <summary>The post (of the active organization) and, asked right now, the caller's edit access to its project.</summary>
    private async Task<(SocialPost?, ActionResult?)> LoadForEditAsync(Guid id, CancellationToken ct)
    {
        if (CallerOrganization() is not { } organizationId) return (null, PostNotFound());
        SocialPost? post = await ReloadAsync(id, organizationId, ct);
        if (post is null) return (null, PostNotFound());
        ProjectAccessResult access = await _projectAccess.GetAsync(post.ProjectId, ProjectPermission.Edit, ct);
        return access.Failure is { } denied ? (null, denied) : (post, null);
    }

    private static SocialPreflightResult RunPreflight(SocialPlatformKind platform, string? title, string? body, IReadOnlyList<string>? hashtags, SocialResolvedOutput output, string? privacy = null) =>
        SocialPreflight.Check(new SocialPreflightInput(platform, title, body, hashtags ?? [], output.SizeBytes, output.DurationSeconds, output.Width, output.Height, privacy));

    private static object PreflightBody(SocialPlatformKind platform, SocialPreflightResult result) => new
    {
        error = "preflight_failed",
        message = result.Issues.First(i => i.Severity == SocialPreflightSeverity.Error).Message,
        preflight = new SocialPreflightResponse(SocialPlatforms.Slug(platform), result.LimitsVersion, result.LimitsLastChecked, result.Ok, result.Issues)
    };

    private static string? ValidateText(string? title, string? body, IReadOnlyList<string>? hashtags)
    {
        if (title is { Length: > 500 }) return "The title is too long.";
        if (body is { Length: > 20000 }) return "The text is too long.";
        if (hashtags is null) return null;
        if (hashtags.Count > 50) return "There are too many hashtags.";
        foreach (string tag in hashtags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > 100) return "A hashtag is empty or too long.";
            if (!tag.Trim().TrimStart('#').All(c => char.IsLetterOrDigit(c) || c == '_')) return "A hashtag may only contain letters, digits and underscores.";
        }

        return null;
    }

    private static string? ValidateOptions(SocialPostOptionsDto? options) =>
        options?.Privacy is { } privacy && !Privacies.Contains(privacy, StringComparer.Ordinal)
            ? "Privacy must be public, unlisted, private or connections."
            : null;

    private static string? SerializeOptions(SocialPostOptionsDto? options) =>
        options is null ? null : JsonSerializer.Serialize(options, Json);

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        (tags ?? []).Select(SocialPlatformLimits.NormalizeHashtag).Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static List<string> ReadStringList(string? json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    private static SocialPostResponse ToResponse(SocialPost p)
    {
        SocialPostOptionsDto options = new();
        if (!string.IsNullOrWhiteSpace(p.OptionsJson))
        {
            try { options = JsonSerializer.Deserialize<SocialPostOptionsDto>(p.OptionsJson, Json) ?? options; }
            catch (JsonException) { /* unreadable options read as defaults */ }
        }

        return new SocialPostResponse(
            p.Id, p.ProjectId, p.OutputKind, p.OutputId, p.SocialAccountId, SocialPlatforms.Slug(p.Platform), p.Format,
            p.Title, p.Body, SocialPublishingService.ReadHashtags(p), p.PostTextId, options,
            p.ScheduledAt, SocialScheduleTime.ToLocalText(p.ScheduledAt, p.TimeZone), p.TimeZone,
            p.Status, p.Attempts, p.NextAttemptAt, p.RemotePostUrl, p.FailureCode, p.FailureMessage,
            p.CreatedByUserId, p.CreatedAt, p.UpdatedAt, p.PublishedAt,
            CanEdit: p.Status == SocialPostStatus.Scheduled,
            CanCancel: p.Status is SocialPostStatus.Scheduled or SocialPostStatus.Draft,
            CanRetry: p.Status is SocialPostStatus.Failed or SocialPostStatus.Unknown);
    }
}
