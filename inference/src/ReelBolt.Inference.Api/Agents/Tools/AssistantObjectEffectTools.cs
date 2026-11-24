using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// "Blur the license plate" as assistant and MCP tools. <c>ProposeObjectEffect</c> resolves the timeline, the video and the
/// clips BY NAME in the active project, runs every gate the analysis service runs (plan, length, credits through the
/// estimate) without creating anything, and mints a confirmation token; <c>ApplyObjectEffect</c> re-resolves, re-runs the
/// gates and queues the object analysis with the timeline intent stored on the request row. The finished effect is written
/// to the timeline by the server-side finalizer when the analysis completes; neither tool edits the timeline itself.
/// The assistant NEVER turns on the project's footage-sharing consent (<c>ObjectEffectsConsentAt</c>): with none on file the
/// proposal is a plain notice that a person must agree in the editor, and no token is minted.
/// </summary>
public sealed class AssistantObjectEffectTools
{
    internal const string OpObjectEffect = "ObjectEffect";
    internal const string StaleTokenMessage = "stale confirmationToken, re-propose and try again";
    internal const int MaxClips = 20;
    internal const double DefaultStrength = 0.7;

    /// <summary>The closed set of effect words the assistant accepts, with the name the analysis service stores.</summary>
    internal static readonly IReadOnlyDictionary<string, string> EffectNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["blur"] = "Blur",
        ["pixelate"] = "Pixelate",
        ["highlight"] = "Highlight",
        ["spotlight"] = "Spotlight",
        ["colorpop"] = "ColorPop",
        ["outline"] = "Outline",
    };

    /// <summary>Progress captions, in <c>AssistantToolProgress</c>'s voice.</summary>
    public static readonly IReadOnlyDictionary<string, string> Captions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ProposeObjectEffect"] = "Checking the clips and what finding the object would cost",
        ["ApplyObjectEffect"] = "Starting the search for the object",
    };

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly IObjectAnalysisService _analysis;
    private readonly ObjectEffectApplier _applier;
    private readonly IConfirmationTokenService _confirmationTokens;
    private readonly ICurrentUser _currentUser;

    public AssistantObjectEffectTools(
        InferenceApiDbContext db,
        IProjectAccessService projectAccess,
        IObjectAnalysisService analysis,
        ObjectEffectApplier applier,
        IConfirmationTokenService confirmationTokens,
        ICurrentUser currentUser)
    {
        _db = db;
        _projectAccess = projectAccess;
        _analysis = analysis;
        _applier = applier;
        _confirmationTokens = confirmationTokens;
        _currentUser = currentUser;
    }

    public IReadOnlyList<AIFunction> GetTools(AssistantToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string shape =
            " timelineName and fileName are NAMES, never ids: timelineName is the timeline in the active project (left out: its only timeline, else the most recently edited one, and the answer says which); fileName is the video to find the object in (left out: the one video the timeline's clips use). objectDescription is the thing to find, in plain words (1 to 80 characters, for example \"the license plate\"). effect is one of blur, pixelate, highlight, spotlight, colorpop, outline. strength is 0 to 1 (default 0.7). everythingExceptObject true applies the effect to everything but the object.";
        return new List<AIFunction>
        {
            AIFunctionFactory.Create(
                (string objectDescription, string effect, string? timelineName = null, string? fileName = null, double? strength = null, bool? everythingExceptObject = null, Guid? projectId = null)
                    => ProposeAsync(context, objectDescription, effect, timelineName, fileName, strength, everythingExceptObject, projectId),
                name: "ProposeObjectEffect",
                description: "Proposes finding an object in a video clip on an editor timeline and putting an effect on it (blur a face or a license plate, highlight a product) and returns a confirmationToken plus what will happen in plain words and what finding the object costs (credits, or free when the same search was already done). Writes nothing and starts nothing. If the project has not yet allowed its video frames to be sent to the object-detection service, it returns a notice instead of a token: only the user can allow that, in the editor, and the assistant never can." + shape + " " + AssistantProjectScope.ProjectIdNote + " The proposal's confirmationToken is accepted by ApplyObjectEffect."),
            AIFunctionFactory.Create(
                (string objectDescription, string effect, string confirmationToken, string? timelineName = null, string? fileName = null, double? strength = null, bool? everythingExceptObject = null, Guid? projectId = null)
                    => ApplyAsync(context, objectDescription, effect, confirmationToken, timelineName, fileName, strength, everythingExceptObject, projectId),
                name: "ApplyObjectEffect",
                description: "STARTS the search for the object a prior ProposeObjectEffect call proposed (it spends credits unless the same search was done before). confirmationToken is verified against the proposal's timeline, clips, video, object, effect and strength; a fabricated or stale token or a changed argument is refused and nothing starts. The effect is NOT on the timeline yet when this returns, unless it says it was added: it appears in the editor when the search finishes, usually within minutes, and nothing is changed if the object cannot be found. This tool does not wait and the assistant will not be woken when it finishes." + shape + " " + AssistantProjectScope.ProjectIdNote + " Returns {status (queued | added | ...), summary, ...} in plain words."),
        };
    }

    // ------------------------------------------------------------------------------------------------ propose

    private async Task<string> ProposeAsync(
        AssistantToolContext context, string? objectDescription, string? effect, string? timelineName, string? fileName,
        double? strength, bool? everythingExceptObject, Guid? projectId)
    {
        Prepared prepared = await PrepareAsync(context, objectDescription, effect, timelineName, fileName, strength, everythingExceptObject, projectId);
        if (prepared.Error is not null)
            return prepared.Error;

        string token = _confirmationTokens.Compute(OpObjectEffect, prepared.Payload!, context.UserId, _currentUser.OrganizationId);
        Target target = prepared.Target!;
        Estimate estimate = prepared.Estimate!;
        return JsonSerializer.Serialize(new
        {
            operation = OpObjectEffect,
            confirmationToken = token,
            summary = Summary(target, estimate, prepared.Project!.Name),
            projectName = ModelSafeText.Clean(prepared.Project.Name),
            timelineName = ModelSafeText.Clean(target.TimelineName),
            fileName = ModelSafeText.Clean(target.FileName),
            clips = target.ItemIds.Count,
            @object = ModelSafeText.Clean(target.Prompt),
            effect = target.Effect.ToLowerInvariant(),
            everythingExceptObject = target.Invert,
            estimate = new { credits = estimate.Credits, cached = estimate.Cached, billingEnabled = estimate.BillingEnabled, upperBound = estimate.UpperBound },
            note = target.Note is null ? null : ModelSafeText.Clean(target.Note, 300),
            hint = "Call ApplyObjectEffect right after this with the same arguments and this confirmationToken. Finding the object takes a few minutes: tell the user the effect will show up on the timeline in the editor when it finishes.",
        }, Json);
    }

    // ------------------------------------------------------------------------------------------------ apply

    private async Task<string> ApplyAsync(
        AssistantToolContext context, string? objectDescription, string? effect, string? confirmationToken, string? timelineName,
        string? fileName, double? strength, bool? everythingExceptObject, Guid? projectId)
    {
        if (!_currentUser.EmailVerified)
            return Error(EmailVerificationGate.Message, EmailVerificationGate.ErrorCode);

        Prepared prepared = await PrepareAsync(context, objectDescription, effect, timelineName, fileName, strength, everythingExceptObject, projectId);
        if (prepared.Error is not null)
            return prepared.Error;

        if (!_confirmationTokens.Verify(OpObjectEffect, prepared.Payload!, context.UserId, _currentUser.OrganizationId, confirmationToken ?? string.Empty))
            return Error(StaleTokenMessage);

        Project project = prepared.Project!;
        Target target = prepared.Target!;
        ObjectAnalysisResult<ObjectAnalysisQueuedResponse> queued = await _analysis.RequestAsync(
            CallerOf(project),
            MediaRef(target),
            new ObjectAnalysisRequestBody(target.Prompt, null, target.Effect, target.Strength, target.Invert),
            new ObjectAnalysisIntent(target.TimelineId, target.ItemIds),
            CancellationToken.None);
        if (queued.Refusal is { } refusal)
            return RefusalJson(refusal);

        ObjectAnalysisQueuedResponse response = queued.Value!;
        if (response.Message == ObjectAnalysisService.AlreadyInProgressMessage)
        {
            // An identical request for this timeline is already running; its intent is the one that will be applied.
            return JsonSerializer.Serialize(new
            {
                status = "queued",
                summary = response.Message + " Nothing new was started and nothing more will be charged. I will not be woken when it finishes, so tell the user to check the editor.",
                projectName = ModelSafeText.Clean(project.Name),
                timelineName = ModelSafeText.Clean(target.TimelineName),
            }, Json);
        }

        bool done = queued.SuccessStatus == StatusCodes.Status200OK;
        string clips = Clips(target.ItemIds.Count);
        if (!done)
        {
            return JsonSerializer.Serialize(new
            {
                status = "queued",
                summary = $"Started looking for {target.Prompt} in {Quote(target.FileName)}. When it is found, the {target.Effect.ToLowerInvariant()} will be added to {clips} on the timeline {Quote(target.TimelineName)}. "
                    + "This usually takes a few minutes. Nothing is on the timeline yet; if the object cannot be found, nothing is changed and the user is told in the editor. I will not be woken when it finishes, so tell the user to check the editor.",
                projectName = ModelSafeText.Clean(project.Name),
                timelineName = ModelSafeText.Clean(target.TimelineName),
                note = target.Note is null ? null : ModelSafeText.Clean(target.Note, 300),
            }, Json);
        }

        // The same analysis was already done: apply it now, with the finalizer's own core, instead of waiting for the next sweep.
        await _applier.ApplyOneAsync(project.Id, response.RequestId, CancellationToken.None);
        ObjectEffectRequest? row = await _db.ObjectEffectRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == response.RequestId);
        if (row is { Status: ObjectEffectRequest.StatusApplied, AppliedRevision: not null })
        {
            return JsonSerializer.Serialize(new
            {
                status = "added",
                summary = $"Added the {target.Effect.ToLowerInvariant()} on {target.Prompt} to {clips} on the timeline {Quote(target.TimelineName)}; it was found before, so this was free."
                    + (string.IsNullOrWhiteSpace(row.FailureMessage) ? string.Empty : $" Details: {ModelSafeText.Clean(row.FailureMessage, 400)}"),
                projectName = ModelSafeText.Clean(project.Name),
                timelineName = ModelSafeText.Clean(target.TimelineName),
            }, Json);
        }

        if (row is { Status: ObjectEffectRequest.StatusNeedsReview })
        {
            return JsonSerializer.Serialize(new
            {
                status = "needs_review",
                summary = $"The object was found, but the timeline {Quote(target.TimelineName)} changed in the meantime, so the {target.Effect.ToLowerInvariant()} was not added on its own. "
                    + "It is finished and will be offered in the editor for the user to apply or discard. Nothing on the timeline was changed.",
                projectName = ModelSafeText.Clean(project.Name),
                timelineName = ModelSafeText.Clean(target.TimelineName),
            }, Json);
        }

        if (row is { Status: ObjectEffectRequest.StatusApplyFailed or ObjectEffectRequest.StatusFailed })
        {
            return JsonSerializer.Serialize(new
            {
                status = "not_added",
                summary = $"The effect could not be added: {ModelSafeText.Clean(string.IsNullOrWhiteSpace(row.FailureMessage) ? ObjectAnalysisMessages.For(row.FailureCode) : row.FailureMessage, 400)} Nothing was changed on the timeline.",
                projectName = ModelSafeText.Clean(project.Name),
            }, Json);
        }

        return JsonSerializer.Serialize(new
        {
            status = "queued",
            summary = $"The object was already found before; the {target.Effect.ToLowerInvariant()} will be added to {clips} on the timeline {Quote(target.TimelineName)} within a minute. Nothing is changed if it cannot be added. I will not be woken, so tell the user to check the editor.",
            projectName = ModelSafeText.Clean(project.Name),
            timelineName = ModelSafeText.Clean(target.TimelineName),
        }, Json);
    }

    // ------------------------------------------------------------------------------------------------ shared decisions

    /// <summary>Everything both tools decide the same way, so a token is only minted for what Apply would queue.</summary>
    private async Task<Prepared> PrepareAsync(
        AssistantToolContext context, string? objectDescription, string? effect, string? timelineName, string? fileName,
        double? strength, bool? everythingExceptObject, Guid? projectId)
    {
        // Authority first: re-read on every call, a refusal writes nothing.
        Project? project = AssistantProjectScope.Resolve(_projectAccess, context, ProjectPermission.Edit, projectId, out string scopeError);
        if (project is null)
            return Prepared.Fail(scopeError);

        TargetResult resolved = await ResolveTargetAsync(
            _db, project.Id, objectDescription, effect, timelineName, fileName, strength, everythingExceptObject, CancellationToken.None);
        if (resolved.Error is not null)
            return Prepared.Fail(Error(resolved.Error, "INVALID_REQUEST"));
        Target target = resolved.Target!;

        ObjectAnalysisResult<ObjectAnalysisEstimate> result = await _analysis.EstimateAsync(
            CallerOf(project), MediaRef(target), new ObjectAnalysisEstimateRequest(target.Prompt), CancellationToken.None);
        if (result.Refusal is { } refusal)
            return Prepared.Fail(RefusalJson(refusal));
        ObjectAnalysisEstimate estimate = result.Value!;

        if (!string.IsNullOrWhiteSpace(estimate.UnavailableReason))
            return Prepared.Fail(Error(estimate.UnavailableReason, "UNAVAILABLE"));

        // The assistant never gives this consent: a person does, once, in the editor. No consent, no token.
        if (!estimate.Consented)
            return Prepared.Fail(ConsentNotice());

        if (!estimate.Cached && await _analysis.PeekRunRateAsync(CallerOf(project), CancellationToken.None) is { } limited)
            return Prepared.Fail(RefusalJson(limited));

        if (!estimate.Affordable)
        {
            return Prepared.Fail(Error(
                CreditShortfallMessage.BuildFromCredits(estimate.Credits, null, estimate.BalanceCredits), "INSUFFICIENT_CREDITS"));
        }

        string payload = JsonSerializer.Serialize(new
        {
            projectId = project.Id,
            timelineId = target.TimelineId,
            itemIds = target.ItemIds.Select(i => i.ToString("D")).OrderBy(i => i, StringComparer.Ordinal),
            sourceId = target.SourceFileId,
            prompt = target.Prompt,
            effect = target.Effect,
            strength = target.Strength,
            invert = target.Invert,
            // The price the user was shown is part of what they agreed to: if it is different when Apply re-estimates (credits up,
            // or a cached search that would now be charged), the token no longer matches and a new proposal is needed.
            credits = Math.Round(estimate.Credits, 3),
            cached = estimate.Cached,
        });
        return new Prepared(null, project, target, new Estimate(estimate.BillingEnabled, estimate.Credits, estimate.Cached, estimate.UpperBound), payload);
    }

    /// <summary>The notice that mirrors the REST route's: plain words, what the user does, and no token.</summary>
    internal static string ConsentNotice() =>
        JsonSerializer.Serialize(new
        {
            error = "This project has not yet allowed its video frames to be sent to our object-detection service, and only you can allow that. Nothing was started.",
            code = ObjectAnalysisMessages.ConsentRequired,
            needsBeforeRunning = new[] { new { ruleId = "WL015", severity = "error", message = ObjectAnalysisMessages.ConsentNeeds } },
        }, Json);

    /// <summary>
    /// The timeline, video and clips a call means, decided from the database by NAME (the tool and its approval card both
    /// call this). Plain refusals name the options by name, never by id.
    /// </summary>
    internal static async Task<TargetResult> ResolveTargetAsync(
        InferenceApiDbContext db, Guid projectId, string? objectDescription, string? effect, string? timelineName, string? fileName,
        double? strength, bool? everythingExceptObject, CancellationToken ct)
    {
        string trimmedEffect = (effect ?? string.Empty).Trim();
        if (!EffectNames.TryGetValue(trimmedEffect, out string? effectName))
            return TargetResult.Fail($"effect must be one of: {string.Join(", ", EffectNames.Keys)}.");

        double strengthValue = strength ?? DefaultStrength;
        if (!double.IsFinite(strengthValue) || strengthValue < 0 || strengthValue > 1)
            return TargetResult.Fail("strength must be between 0 and 1.");

        string described = (objectDescription ?? string.Empty).Trim();
        if (described.Length == 0 || described.Length > ObjectEffectRequest.MaxPromptLength)
            return TargetResult.Fail($"objectDescription must be 1 to {ObjectEffectRequest.MaxPromptLength} characters: say what to find, for example \"the license plate\".");
        if (described.Any(char.IsControl))
            return TargetResult.Fail("objectDescription cannot contain control characters.");
        string prompt = SegmentationPrompt.Sanitize(described);
        if (prompt.Length == 0)
            return TargetResult.Fail($"objectDescription must be 1 to {ObjectEffectRequest.MaxPromptLength} characters: say what to find, for example \"the license plate\".");

        List<EditTimelineEntity> timelines = await db.EditTimelines.AsNoTracking()
            .Where(t => t.ProjectId == projectId && t.DeletedAt == null)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);
        if (timelines.Count == 0)
            return TargetResult.Fail("this project has no timeline yet. Open the editor and create one first.");

        string? note = null;
        EditTimelineEntity timeline;
        string requested = (timelineName ?? string.Empty).Trim();
        if (requested.Length > 0)
        {
            List<EditTimelineEntity> named = timelines.Where(t => string.Equals(t.Name, requested, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 0)
                return TargetResult.Fail($"no timeline is called {Quote(requested)}. The timelines in this project are: {Names(timelines.Select(t => t.Name))}.");
            if (named.Count > 1)
                return TargetResult.Fail($"more than one timeline is called {Quote(requested)}; rename one of them in the editor so it can be told apart.");
            timeline = named[0];
        }
        else if (timelines.Count == 1)
        {
            timeline = timelines[0];
        }
        else
        {
            timeline = timelines[0];
            note = $"The project has {timelines.Count} timelines, so I used the one edited most recently: {Quote(timeline.Name)}.";
        }

        EditTimeline? document;
        try
        {
            document = JsonSerializer.Deserialize<EditTimeline>(timeline.DocumentJson, EditTimelineJson.Options);
        }
        catch (JsonException)
        {
            document = null;
        }

        if (document is null)
            return TargetResult.Fail($"the timeline {Quote(timeline.Name)} could not be read.");

        List<MediaClip> projectFileClips = document.Tracks
            .SelectMany(t => t.Items)
            .OfType<MediaClip>()
            .Where(c => c.AssetRef is { Kind: AssetRefKind.ProjectFile, ProjectFileId: not null })
            .ToList();
        List<Guid> fileIds = projectFileClips.Select(c => c.AssetRef.ProjectFileId!.Value).Distinct().ToList();
        List<ProjectFile> files = await db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id) && f.MimeType.StartsWith("video/"))
            .ToListAsync(ct);
        if (files.Count == 0)
            return TargetResult.Fail($"the timeline {Quote(timeline.Name)} has no video clips from your files to look in.");

        ProjectFile source;
        string wantedFile = (fileName ?? string.Empty).Trim();
        if (wantedFile.Length > 0)
        {
            List<ProjectFile> byName = files.Where(f => string.Equals(f.OriginalFileName, wantedFile, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byName.Count == 0)
                return TargetResult.Fail($"no clip on the timeline {Quote(timeline.Name)} plays a video called {Quote(wantedFile)}. The videos it uses are: {Names(files.Select(f => f.OriginalFileName))}.");
            if (byName.Count > 1)
                return TargetResult.Fail($"more than one video on the timeline {Quote(timeline.Name)} is called {Quote(wantedFile)}; rename one of them so it can be told apart.");
            source = byName[0];
        }
        else if (files.Count == 1)
        {
            source = files[0];
        }
        else
        {
            return TargetResult.Fail($"the timeline {Quote(timeline.Name)} uses more than one video, so say which one with fileName: {Names(files.Select(f => f.OriginalFileName))}.");
        }

        List<Guid> itemIds = projectFileClips
            .Where(c => c.AssetRef.ProjectFileId == source.Id)
            .Select(c => c.Id)
            .Distinct()
            .ToList();
        if (itemIds.Count > MaxClips)
            return TargetResult.Fail($"{Quote(source.OriginalFileName)} is used by {itemIds.Count} clips on the timeline {Quote(timeline.Name)}; at most {MaxClips} can be done at once. Do part of the timeline in the editor instead.");

        return TargetResult.Ok(new Target(
            timeline.Id, timeline.Name, source.Id, source.OriginalFileName, itemIds, prompt, effectName, strengthValue, everythingExceptObject ?? false, note));
    }

    internal static string MediaRef(Target target) => "file:" + target.SourceFileId.ToString("D");

    private ObjectAnalysisCaller CallerOf(Project project) =>
        new(_currentUser.UserId, project.OrganizationId, project, _currentUser.EmailVerified);

    private static string Summary(Target target, Estimate estimate, string projectName)
    {
        string price = estimate.Cached
            ? "Finding it is free: the same search was already done."
            : estimate.BillingEnabled ? estimate.UpperBound ? $"Finding the object costs up to {CreditCostWording.Up(estimate.Credits)} credits (the video's length could not be read, so this is the most it can cost)." : $"Finding the object costs about {CreditCostWording.Up(estimate.Credits)} credits." : "Finding the object is free.";
        return $"Find {target.Prompt} in {Quote(target.FileName)} and add a {target.Effect.ToLowerInvariant()}"
            + (target.Invert ? " to everything except it" : " on it")
            + $" for {Clips(target.ItemIds.Count)} on the timeline {Quote(target.TimelineName)} in {Quote(projectName)}. {price} "
            + "The effect shows up in the editor when the search finishes, usually within a few minutes.";
    }

    internal static string Clips(int count) => count == 1 ? "1 clip" : $"{count} clips";

    internal static string Quote(string value) => "“" + ModelSafeText.Clean(value) + "”";

    private static string Names(IEnumerable<string> names) =>
        string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).Take(10).Select(Quote));

    /// <summary>A service refusal in the model's voice: its own message and code, never ids.</summary>
    private static string RefusalJson(ObjectAnalysisRefusal refusal)
    {
        string? message = null;
        string? code = null;
        object? needs = null;
        if (refusal.Body is not null)
        {
            JsonElement body = JsonSerializer.SerializeToElement(refusal.Body, Json);
            if (body.ValueKind == JsonValueKind.Object)
            {
                if (body.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
                if (body.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String) code = c.GetString();
                if (body.TryGetProperty("needsBeforeRunning", out JsonElement n)) needs = n;
            }
        }

        if (string.Equals(code, ObjectAnalysisMessages.ConsentRequired, StringComparison.Ordinal))
            return ConsentNotice();
        message ??= refusal.StatusCode == StatusCodes.Status404NotFound
            ? "that video could not be found in this project."
            : "the object search could not be started.";
        return needs is null
            ? Error(message, code)
            : JsonSerializer.Serialize(new { error = message, code, needsBeforeRunning = needs }, Json);
    }

    private static string Error(string message, string? code = null) =>
        code is null
            ? JsonSerializer.Serialize(new { error = message })
            : JsonSerializer.Serialize(new { error = message, code });

    /// <summary>What a call means, resolved by name.</summary>
    internal sealed record Target(
        Guid TimelineId, string TimelineName, Guid SourceFileId, string FileName, IReadOnlyList<Guid> ItemIds,
        string Prompt, string Effect, double Strength, bool Invert, string? Note);

    internal sealed record TargetResult(Target? Target, string? Error)
    {
        public static TargetResult Ok(Target target) => new(target, null);

        public static TargetResult Fail(string error) => new(null, error);
    }

    private sealed record Estimate(bool BillingEnabled, double Credits, bool Cached, bool UpperBound);

    private sealed record Prepared(string? Error, Project? Project, Target? Target, Estimate? Estimate, string? Payload)
    {
        public static Prepared Fail(string error) => new(error, null, null, null, null);
    }
}
