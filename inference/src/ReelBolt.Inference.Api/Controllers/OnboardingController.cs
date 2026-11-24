using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// First-run onboarding: the caller's own tour status and getting-started checklist. Per USER, keyed by
/// the JWT's <c>sub</c>; nothing here reads or writes another user's data. Derived checklist items come
/// from the caller's real data through cheap EXISTS queries and are stored the first time they are
/// observed. Tenancy: projects, files and threads carry the tenant filter; executions do not, so
/// run_video requires the execution's project to be visible through the filtered Projects set.
/// <para>
/// It also holds the welcome answers (who the person is, what they start from; table
/// <c>user_onboarding_profiles</c>, docs/design/new-user-funnel.md sections 5 and 6). Those are account
/// data the person gives to shape their workspace, so saving them and the product event that records it
/// depend on no telemetry switch and no browser consent.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/onboarding")]
[Authorize]
public class OnboardingController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IOptions<AlphaTelemetryOptions> telemetry,
    IProductEventLog productEvents,
    ILogger<OnboardingController>? logger = null) : ControllerBase
{
    public const int MaxBodyBytes = 4 * 1024;

    /// <summary>The closed, ordered checklist.</summary>
    public static readonly IReadOnlyList<string> ItemKeys =
        ["create_project", "upload_file", "run_video", "open_result", "try_assistant", "open_editor"];

    private static readonly string[] MarkKeys = ["open_result", "open_editor"];
    private static readonly string[] TourStatuses = ["pending", "completed", "skipped"];

    private static readonly Dictionary<string, string> EventNames = new()
    {
        ["tour_started"] = ProductEventNames.OnboardingTourStarted,
        ["tour_completed"] = ProductEventNames.OnboardingTourCompleted,
        ["tour_skipped"] = ProductEventNames.OnboardingTourSkipped,
        ["checklist_dismissed"] = ProductEventNames.OnboardingChecklistDismissed,
        ["checklist_item_done"] = ProductEventNames.OnboardingChecklistItemDone,
    };

    [HttpGet]
    public async Task<ActionResult<OnboardingResponse>> Get(CancellationToken ct)
        => Ok(await ApplyAsync(null, ct));

    /// <summary>Clears the plan the person picked before signing up. Idempotent; touches only the caller's row.</summary>
    [HttpDelete("picked-plan")]
    public async Task<IActionResult> ClearPickedPlan(CancellationToken ct)
    {
        UserOnboardingProfile? row = await db.UserOnboardingProfiles.FirstOrDefaultAsync(p => p.UserId == currentUser.UserId, ct);
        if (row?.PickedPlan is not null)
        {
            row.PickedPlan = null;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    [HttpPut]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<OnboardingResponse>> Put([FromBody] SaveOnboardingRequest request, CancellationToken ct)
    {
        if (request.Tour is not null
            && (request.Tour.Status is null || !TourStatuses.Contains(request.Tour.Status)
                || request.Tour.Version is null or < 0 or > 100000))
        {
            return BadRequest(new { error = "invalid_tour" });
        }

        if (request.Marks is not null && (request.Marks.Count > MarkKeys.Length * 2 || request.Marks.Any(m => !MarkKeys.Contains(m))))
        {
            return BadRequest(new { error = "invalid_mark" });
        }

        return Ok(await ApplyAsync(request, ct));
    }

    [HttpPost("events")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Events([FromBody] OnboardingEventRequest body, CancellationToken ct)
    {
        if (!telemetry.Value.ClientIngestEnabled || !currentUser.IsAuthenticated) return NotFound();

        if (body.Event is null || !EventNames.TryGetValue(body.Event, out string? name)) return BadRequest(new { error = "unknown_event" });
        bool isItem = body.Event == "checklist_item_done";
        if (isItem ? body.Item is null || !ItemKeys.Contains(body.Item) : body.Item is not null)
        {
            return BadRequest(new { error = "invalid_item" });
        }

        // One atomic statement: the claim and its product_events row commit together.
        await productEvents.ClaimAndAppendWithDetailAsync(
            currentUser.UserId,
            currentUser.OrganizationId,
            name,
            isItem ? $"{name}:{body.Item}" : name,
            isItem ? body.Item : null,
            ProductEventSources.Server,
            ct);
        return NoContent();
    }

    /// <summary>
    /// Saves the welcome answers, or the skip. <c>{persona, material}</c>: both required, both from their
    /// closed set (<see cref="AcquisitionVocabulary"/>), else 400. <c>{skipped: true}</c>: nothing else may
    /// come with it. Answering again is allowed and overwrites persona and material; the first
    /// <c>answeredAt</c> (and the first <c>skippedAt</c>) is kept, and the persona the landing page
    /// suggested is never touched. Answers the stored profile and the starter recipe that follows from the
    /// material (null while there is none).
    /// <para>
    /// The product event (<c>onboarding.profile_saved</c> with the persona, or
    /// <c>onboarding.profile_skipped</c>) is written here, once per user, as a server row for every user:
    /// it is the record of an account change, not browser telemetry. It is best effort and written after
    /// the save, so it can neither fail nor be blamed for the request.
    /// </para>
    /// </summary>
    [HttpPut("profile")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<ActionResult<OnboardingProfileResponse>> PutProfile([FromBody] SaveOnboardingProfileRequest request, CancellationToken ct)
    {
        bool skip = request.Skipped == true;
        if (skip)
        {
            if (request.Persona is not null || request.Material is not null) return BadRequest(new { error = "invalid_profile" });
        }
        else
        {
            if (!AcquisitionVocabulary.IsPersona(request.Persona)) return BadRequest(new { error = "invalid_persona" });
            if (!AcquisitionVocabulary.IsMaterial(request.Material)) return BadRequest(new { error = "invalid_material" });
        }

        UserOnboardingProfile row = await SaveProfileAsync(skip ? null : (request.Persona!, request.Material!), ct);

        try
        {
            string name = skip ? ProductEventNames.OnboardingProfileSkipped : ProductEventNames.OnboardingProfileSaved;
            await productEvents.ClaimAndAppendWithDetailAsync(
                currentUser.UserId, currentUser.OrganizationId, name, name, skip ? null : row.Persona, ProductEventSources.Server, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "The onboarding profile event could not be recorded");
        }

        return Ok(new OnboardingProfileResponse(ToDto(row), AcquisitionVocabulary.StarterTemplateKeyFor(row.Material)));
    }

    /// <summary>Writes the answer (or, for null, the skip) onto the caller's row; retries once if a concurrent first insert won.</summary>
    private async Task<UserOnboardingProfile> SaveProfileAsync((string Persona, string Material)? answer, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            UserOnboardingProfile? row = await db.UserOnboardingProfiles.FirstOrDefaultAsync(p => p.UserId == currentUser.UserId, ct);
            bool isNew = row is null;
            DateTime now = DateTime.UtcNow;
            row ??= new UserOnboardingProfile { UserId = currentUser.UserId, CreatedAt = now };
            if (answer is { } given)
            {
                row.Persona = given.Persona;
                row.Material = given.Material;
                row.AnsweredAt ??= now;
            }
            else
            {
                row.SkippedAt ??= now;
            }

            row.UpdatedAt = now;
            if (isNew) db.UserOnboardingProfiles.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return row;
            }
            catch (DbUpdateException ex) when (isNew && attempt == 0 && IsUniqueViolation(ex))
            {
                // A concurrent first call (or the signup's suggested persona) created the row: re-read it and re-apply.
                db.ChangeTracker.Clear();
            }
        }
    }

    private static OnboardingProfileDto ToDto(UserOnboardingProfile? row) =>
        new(row?.Persona, row?.Material, row?.SuggestedPersona, row?.AnsweredAt, row?.SkippedAt, row?.PickedPlan);

    private static HashSet<string> Stored(UserOnboardingState? row)
    {
        if (row is null) return [];
        try
        {
            return (JsonSerializer.Deserialize<string[]>(row.MarksJson) ?? []).Where(ItemKeys.Contains).ToHashSet();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Applies the optional PUT change plus newly observed derived items; retries once if a concurrent first insert won.</summary>
    private async Task<OnboardingResponse> ApplyAsync(SaveOnboardingRequest? request, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            UserOnboardingState? row = await db.UserOnboardingStates.FirstOrDefaultAsync(s => s.UserId == currentUser.UserId, ct);
            HashSet<string> before = Stored(row);
            HashSet<string> done = [.. before];
            await DeriveAsync(done, ct);
            if (request?.Marks is { Count: > 0 }) done.UnionWith(request.Marks);

            bool isNew = row is null;
            row ??= new UserOnboardingState { UserId = currentUser.UserId };
            bool changed = !done.SetEquals(before);
            if (request?.Tour is { } tour && (isNew || row.TourStatus != tour.Status || row.TourVersion != tour.Version))
            {
                row.TourStatus = tour.Status!;
                row.TourVersion = tour.Version;
                row.TourUpdatedAt = DateTime.UtcNow;
                changed = true;
            }

            if (request?.ChecklistDismissed is { } dismissed && row.ChecklistDismissed != dismissed)
            {
                row.ChecklistDismissed = dismissed;
                changed = true;
            }

            if (changed)
            {
                row.MarksJson = JsonSerializer.Serialize(ItemKeys.Where(done.Contains).ToArray());
                row.UpdatedAt = DateTime.UtcNow;
                if (isNew) db.UserOnboardingStates.Add(row);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (isNew && attempt == 0 && IsUniqueViolation(ex))
                {
                    // A concurrent first call created the row: re-read it and re-apply this change.
                    db.ChangeTracker.Clear();
                    continue;
                }
            }

            UserOnboardingProfile? profile = await db.UserOnboardingProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.UserId == currentUser.UserId, ct);
            return new OnboardingResponse(
                new OnboardingTourDto(row.TourStatus, row.TourVersion, row.TourUpdatedAt),
                new OnboardingChecklistDto(row.ChecklistDismissed, ItemKeys.Select(k => new OnboardingChecklistItemDto(k, done.Contains(k))).ToList()),
                ToDto(profile),
                AcquisitionVocabulary.StarterTemplateKeyFor(profile?.Material));
        }
    }

    /// <summary>Cheap EXISTS probes, only for items not yet stored. Every probe is keyed on the caller.</summary>
    private async Task DeriveAsync(HashSet<string> done, CancellationToken ct)
    {
        Guid uid = currentUser.UserId;
        if (!done.Contains("create_project") && await db.Projects.AsNoTracking().AnyAsync(p => p.OwnerId == uid, ct)) done.Add("create_project");
        if (!done.Contains("upload_file")
            && await db.ProjectFiles.AsNoTracking().AnyAsync(f => f.Category == "userFiles" && db.Projects.Any(p => p.Id == f.ProjectId && p.OwnerId == uid), ct))
        {
            done.Add("upload_file");
        }

        // workflow_executions has no tenant filter: tie each execution to a project visible through the filtered set.
        if (!done.Contains("run_video")
            && await db.WorkflowExecutions.AsNoTracking().AnyAsync(e => e.InitiatedByUserId == uid && db.Projects.Any(p => p.Id == e.ProjectId), ct))
        {
            done.Add("run_video");
        }

        if (!done.Contains("try_assistant")
            && await db.AssistantMessages.AsNoTracking().AnyAsync(m => m.Role == "user" && db.AssistantThreads.Any(t => t.Id == m.ThreadId && t.UserId == uid), ct))
        {
            done.Add("try_assistant");
        }
    }
}
