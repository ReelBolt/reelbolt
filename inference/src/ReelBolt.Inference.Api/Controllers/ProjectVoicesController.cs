using ReelBolt.Inference.Api.Services.Moderation;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Voices;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// A project's voice library (see docs/voiceover.md "Voices and consent"): provider-library voices
/// and voices CLONED from the project's own recordings. Owner-scoped like every project resource —
/// the project is loaded first (<c>404</c> if it does not exist, <c>403</c> if it belongs to somebody
/// else), and a voice in another project is <c>404</c>, never <c>403</c>.
///
/// <para>
/// <b>Consent is not optional and not free text.</b> A clone is created only with a consent
/// attestation whose statement equals <see cref="RequiredConsentStatement"/> verbatim and names the
/// recorded person; the attesting user's id and the time are recorded with it. Revoking consent makes
/// the voice unselectable everywhere at once (the <c>Selectable</c> flag here and the WorkflowEngine's
/// own read of the same row), and deleting a voice deletes the vendor-side model, the reference
/// recordings it was trained from and the row — what was actually removed is reported, never assumed.
/// Retention is enforced on the read path (an expired clone is unselectable) AND by
/// <c>ProjectVoiceRetentionService</c>, which deletes expired clones whether or not anyone asks.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/voices")]
[Authorize]
public class ProjectVoicesController : ControllerBase
{
    /// <summary>
    /// The exact statement an attesting user agrees to. Fixed rather than free text so the API can
    /// never accept a weaker wording, and recorded verbatim on the row so it is what the user is
    /// accountable for.
    /// </summary>
    public const string RequiredConsentStatement =
        "I confirm that the person whose voice is in these recordings has given informed consent to " +
        "having their voice cloned and used to narrate videos in this project, that I will keep a " +
        "record of that consent, and that I will revoke or delete this voice if they withdraw it.";

    private const int MaxNameChars = 200;
    private const int MaxDescriptionChars = 2000;
    private const int MaxSubjectNameChars = 200;

    /// <summary>Voices per library page.</summary>
    public const int LibraryPageSize = ProjectVoiceLibraryService.PageSize;

    /// <summary>The fixed tenant sentence for a platform speech row's clone failure (S-53 / P2-N2).</summary>
    internal const string CloneFailedSentence = "The voice could not be cloned. Try again later.";

    /// <summary>The fixed tenant sentence for a platform transcription row's failure (S-53 / P2-N2).</summary>
    internal const string TranscriptionFailedSentence = "The reference recording could not be transcribed. Try again later.";

    /// <summary>
    /// S-53 / P2-N2: a provider row with no owning organization is the platform's own account, so its vendor
    /// error text and transport detail (which names the provider host:port) stay in the log and the tenant gets
    /// one fixed sentence; an organization's own row keeps the vendor detail because the account is theirs.
    /// </summary>
    private static string VendorFailureMessage(Guid? providerOrganizationId, string fixedSentence, Exception ex) =>
        providerOrganizationId is null ? fixedSentence : ex.Message;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IFileStorageService _storage;
    private readonly IInferenceProviderResolver _providers;
    private readonly ISpeechSynthesisClientFactory _speechFactory;
    private readonly ITranscriptionClientFactory _transcriptionFactory;
    private readonly ILogger<ProjectVoicesController> _logger;
    private readonly VoiceLibraryCache? _libraryCache;
    private readonly ReelBolt.Inference.Api.Services.Moderation.PublishModeration? _publishModeration;
    private readonly ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? _historyEraser;

    public ProjectVoicesController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IFileStorageService storage,
        IInferenceProviderResolver providers,
        ISpeechSynthesisClientFactory speechFactory,
        ITranscriptionClientFactory transcriptionFactory,
        ILogger<ProjectVoicesController> logger,
        VoiceLibraryCache? cache = null,
        ReelBolt.Inference.Api.Services.Moderation.PublishModeration? publishModeration = null,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? historyEraser = null)
    {
        _historyEraser = historyEraser;
        _publishModeration = publishModeration;
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _storage = storage;
        _providers = providers;
        _speechFactory = speechFactory;
        _transcriptionFactory = transcriptionFactory;
        _logger = logger;
        _libraryCache = cache;
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<ProjectVoiceResponse>>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        List<ProjectVoice> voices = await _db.ProjectVoices
            .Where(v => v.ProjectId == projectId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync(ct);
        Dictionary<Guid, string> providerNames = await ProviderNamesAsync(voices, ct);

        DateTime now = DateTime.UtcNow;
        return Ok(voices.Select(v => ToResponse(v, now, providerNames)).ToList());
    }

    /// <summary>
    /// Browses the project's resolved SpeechSynthesis provider's public voice library (Fish Audio's
    /// <c>GET /model</c>). Same read rule as the rest of the Voices tab (View). A provider with no library
    /// answers <c>404 voice_library_not_supported</c>. Each (provider, search, language, page) is cached
    /// for 10 minutes. No preview audio is proxied: the response says whether one exists, nothing more.
    /// </summary>
    [HttpGet("library")]
    public async Task<ActionResult<VoiceLibraryResponse>> Library(
        Guid projectId, [FromQuery] string? q, [FromQuery] string? language, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        VoiceLibraryResult<VoiceLibraryResponse> result = await LibraryService().SearchAsync(projectId, q, language, page, ct);
        if (result.Failure is { } failure)
        {
            return failure.Kind switch
            {
                VoiceLibraryFailureKind.BadRequest => BadRequest(new { error = failure.Message }),
                VoiceLibraryFailureKind.Unpriced => ModelUnpricedResults.Conflict(failure.Unpriced!),
                VoiceLibraryFailureKind.NotSupported => NotFound(new { error = "voice_library_not_supported", message = failure.Message }),
                _ => StatusCode(StatusCodes.Status502BadGateway, new { error = "voice_library_failed", message = failure.Message }),
            };
        }

        return Ok(result.Value);
    }

    /// <summary>The one library implementation, shared with the assistant's voice tools.</summary>
    private ProjectVoiceLibraryService LibraryService() =>
        new(_db, _providers, _speechFactory, _logger, _libraryCache);

    [HttpGet("{voiceId:guid}")]
    public async Task<ActionResult<ProjectVoiceResponse>> Get(Guid projectId, Guid voiceId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectVoice? voice = await _db.ProjectVoices.FirstOrDefaultAsync(v => v.Id == voiceId && v.ProjectId == projectId, ct);
        if (voice is null) return NotFound();

        return Ok(ToResponse(voice, DateTime.UtcNow, await ProviderNamesAsync([voice], ct)));
    }

    /// <summary>Registers a provider-library voice by its vendor id. No person in the project is its subject, so no consent is captured; it is selectable immediately and never expires.</summary>
    [HttpPost]
    public async Task<ActionResult<ProjectVoiceResponse>> CreateLibraryVoice(Guid projectId, [FromBody] CreateLibraryVoiceRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameChars)
            return BadRequest(new { error = $"name is required and must be at most {MaxNameChars} characters." });
        if (string.IsNullOrWhiteSpace(request.RemoteVoiceId) || request.RemoteVoiceId.Length > 500)
            return BadRequest(new { error = "remoteVoiceId is required and must be at most 500 characters." });
        if (request.Description is { Length: > MaxDescriptionChars })
            return BadRequest(new { error = $"description must be at most {MaxDescriptionChars} characters." });

        if (request.InferenceProviderId is Guid providerId)
        {
            ActionResult? providerError = await CheckSpeechProviderAsync(providerId, ct);
            if (providerError is not null) return providerError;
        }

        // A Library row captures no consent, so it must not carry the vendor id of another project's clone: that
        // would let this project speak in a cloned voice whose consent it never attested (same rows as the save check).
        string remoteVoiceId = request.RemoteVoiceId.Trim();
        if (await VoiceoverVoiceConsentSaveValidator.VendorRows(_db, projectId, [remoteVoiceId])
                .AnyAsync(v => v.ProjectId != projectId && v.Source == ProjectVoiceSource.Cloned, ct))
            return BadRequest(new { error = VendorVoiceIdRule.Message(remoteVoiceId) });

        var voice = new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            InferenceProviderId = request.InferenceProviderId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Source = ProjectVoiceSource.Library,
            RemoteVoiceId = remoteVoiceId,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = _currentUser.UserId
        };
        _db.ProjectVoices.Add(voice);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { projectId, voiceId = voice.Id },
            ToResponse(voice, DateTime.UtcNow, await ProviderNamesAsync([voice], ct)));
    }

    /// <summary>
    /// Clones a voice from this project's recordings. Consent is captured in the same request and
    /// validated before a single byte leaves the host: a missing or non-verbatim statement, or a
    /// missing subject name, is <c>400</c> with nothing created anywhere.
    /// </summary>
    [HttpPost("clone")]
    public async Task<ActionResult<ProjectVoiceResponse>> Clone(Guid projectId, [FromBody] CloneVoiceRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // ---- Consent first, before anything is read or sent. ----
        if (!string.Equals(request.ConsentStatement?.Trim(), RequiredConsentStatement, StringComparison.Ordinal))
        {
            return BadRequest(new
            {
                error = "consent_statement_required",
                message = "Cloning a voice requires the consent attestation, verbatim.",
                requiredStatement = RequiredConsentStatement
            });
        }

        if (string.IsNullOrWhiteSpace(request.ConsentSubjectName) || request.ConsentSubjectName.Length > MaxSubjectNameChars)
            return BadRequest(new { error = "consent_subject_required", message = "Name the person whose voice is in the recordings." });
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameChars)
            return BadRequest(new { error = $"name is required and must be at most {MaxNameChars} characters." });
        if (request.Description is { Length: > MaxDescriptionChars })
            return BadRequest(new { error = $"description must be at most {MaxDescriptionChars} characters." });
        if (request.RetentionDays < 1 || request.RetentionDays > ProjectVoiceLimits.MaxRetentionDays)
            return BadRequest(new { error = $"retentionDays must be between 1 and {ProjectVoiceLimits.MaxRetentionDays}." });
        if (request.ReferenceProjectFileIds is null || request.ReferenceProjectFileIds.Count is < 1 or > ProjectVoiceLimits.MaxReferenceFiles)
            return BadRequest(new { error = $"referenceProjectFileIds must name between 1 and {ProjectVoiceLimits.MaxReferenceFiles} audio files." });

        // ---- The recordings must be THIS project's own audio files — never another project's object. ----
        List<Guid> fileIds = request.ReferenceProjectFileIds.Distinct().ToList();
        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id)
                        && f.CsamStatus != CsamScanStatus.Quarantined
                        && f.ModerationStatus != FileModerationStatus.Quarantined)
            .ToListAsync(ct);
        if (files.Count != fileIds.Count)
            return BadRequest(new { error = "reference_file_not_found", message = "Every reference recording must be a file of this project." });
        // A file that is not served is not a recording of this project, whatever its type: refused before any read or vendor call.
        if ((await OutputGate.HeldFileIdsAsync(_db, projectId, files, ct)).Count > 0)
            return BadRequest(new { error = "reference_file_not_found", message = "Every reference recording must be a file of this project." });
        if (files.Any(f => !f.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && !f.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { error = "reference_file_not_audio", message = "Reference recordings must be audio or video files." });
        if (files.Sum(f => f.SizeBytes) > ProjectVoiceLimits.MaxReferenceBytes)
            return BadRequest(new { error = "reference_files_too_large", message = $"Reference recordings total more than {ProjectVoiceLimits.MaxReferenceBytes} bytes." });

        // ---- The provider must be a SpeechSynthesis row whose client can clone. ----
        ResolvedSpeechSynthesisProvider? provider;
        try
        {
            provider = await _providers.ResolveSpeechSynthesisAsync(request.InferenceProviderId, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech provider resolution failed while cloning a voice for project {ProjectId}", projectId);
            provider = null;
        }
        if (provider is null)
            return BadRequest(new { error = "no_speech_provider", message = "No SpeechSynthesis provider is configured (or the named one is not a SpeechSynthesis row)." });

        IVoiceCloningClient? cloner;
        try
        {
            cloner = _speechFactory.Get(provider).Unwrap<IVoiceCloningClient>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech client construction failed while cloning a voice for project {ProjectId}", projectId);
            cloner = null;
        }
        if (cloner is null)
            return BadRequest(new { error = "cloning_not_supported", message = $"Provider '{provider.Name}' ({provider.Kind}) has no voice-cloning API. Only Fish Audio providers can clone." });

        // A copy of production data (tools/local-staging) shares the real Fish account, and vendor deletes are
        // refused there, so a voice cloned in it could never be removed. Refuse to create one.
        if (FishAudioSpeechSynthesisClient.VendorDeletesDisabled)
            return StatusCode(StatusCodes.Status409Conflict, new { error = "vendor_writes_disabled", message = "Voice cloning is disabled in this environment because it cannot delete the voices it creates." });

        // ---- What this backend needs (a self-hosted fish-speech server: one WAV + its transcript). ----
        VoiceCloningRequirements requirements;
        try
        {
            requirements = await cloner.GetRequirementsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reach the speech provider while cloning a voice for project {ProjectId}", projectId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "clone_failed", message = VendorFailureMessage(provider.OrganizationId, CloneFailedSentence, ex) });
        }

        if (files.Count > requirements.MaxReferenceRecordings)
        {
            return BadRequest(new
            {
                error = "too_many_reference_files",
                message = $"Provider '{provider.Name}' builds a voice from at most {requirements.MaxReferenceRecordings} recording(s)."
            });
        }

        // ---- Download the recordings and create the vendor model. ----
        var references = new List<VoiceReferenceAudio>(files.Count);
        foreach (ProjectFile file in files)
        {
            // A recording that is still a generated output's bytes goes to the vendor only with a current verdict.
            if (_publishModeration is not null
                && await _publishModeration.GateStoredCopyAsync(projectId, file.StorageKey, ct) is { } recordingRefused)
                return recordingRefused.ToRefusal(this);

            await using Stream stream = await _storage.DownloadAsync(projectId, file.StorageKey, ct);
            await using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            references.Add(new VoiceReferenceAudio(buffer.ToArray(), file.OriginalFileName, file.MimeType));
        }

        if (requirements.RequiresWav && references.Any(r => !FishAudioSpeechSynthesisClient.IsWav(r.Bytes)))
        {
            return BadRequest(new
            {
                error = "reference_file_not_wav",
                message = $"Provider '{provider.Name}' stores the reference recording as a WAV file. Upload a .wav recording."
            });
        }

        if (requirements.RequiresReferenceTranscript)
        {
            ActionResult? transcriptError = await AddTranscriptsAsync(references, projectId, ct);
            if (transcriptError is not null) return transcriptError;
        }

        CreatedVoice created;
        try
        {
            created = await cloner.CreateVoiceAsync(
                new VoiceCloneRequest(request.Name.Trim(), references, request.Description?.Trim()), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice cloning failed for project {ProjectId}", projectId);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "clone_failed", message = VendorFailureMessage(provider.OrganizationId, CloneFailedSentence, ex) });
        }

        DateTime now = DateTime.UtcNow;
        var voice = new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            InferenceProviderId = provider.ProviderId,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = created.RemoteVoiceId,
            ReferenceProjectFileIdsJson = JsonSerializer.Serialize(fileIds, JsonOptions),
            ConsentAttestedByUserId = _currentUser.UserId,
            ConsentAttestedAt = now,
            ConsentStatement = RequiredConsentStatement,
            ConsentSubjectName = request.ConsentSubjectName.Trim(),
            RetentionUntil = now.AddDays(request.RetentionDays),
            CreatedAt = now,
            CreatedByUserId = _currentUser.UserId
        };
        _db.ProjectVoices.Add(voice);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { projectId, voiceId = voice.Id },
            ToResponse(voice, now, await ProviderNamesAsync([voice], ct)));
    }

    /// <summary>
    /// Transcribes each reference recording through the DEFAULT Transcription provider, for a
    /// backend that needs to be told what is said in the recording (a self-hosted fish-speech
    /// server keeps the transcript next to the audio and conditions every synthesis on the pair).
    /// Runs only after consent was validated, like everything else that reads the recordings.
    /// </summary>
    private async Task<ActionResult?> AddTranscriptsAsync(List<VoiceReferenceAudio> references, Guid projectId, CancellationToken ct)
    {
        ResolvedTranscriptionProvider? asr;
        try
        {
            asr = await _providers.ResolveTranscriptionAsync(null, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return ModelUnpricedResults.Conflict(unpriced);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Transcription provider resolution failed while cloning a voice for project {ProjectId}", projectId);
            asr = null;
        }
        if (asr is null)
        {
            return BadRequest(new
            {
                error = "transcription_provider_required",
                message = "This speech provider needs a transcript of the reference recording, and no default Transcription provider is configured."
            });
        }

        for (int i = 0; i < references.Count; i++)
        {
            VoiceReferenceAudio reference = references[i];
            string transcript;
            try
            {
                ITranscriptionClient client = _transcriptionFactory.Get(asr);
                // Metered to the project's organization as editor usage; an outer scope wins.
                using IDisposable usageScope = UsageScope.BeginIfNone(
                    _currentUser.OrganizationId, projectId, _currentUser.UserId, UsageSource.Editor);
                await using var audio = new MemoryStream(reference.Bytes, writable: false);
                TranscriptResult result = await client.TranscribeAsync(audio, reference.FileName, language: null, wordTimestamps: false, ct);
                transcript = result.Text?.Trim() ?? string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transcribing a reference recording failed while cloning a voice for project {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status502BadGateway, new { error = "transcription_failed", message = VendorFailureMessage(asr.OrganizationId, TranscriptionFailedSentence, ex) });
            }

            if (transcript.Length == 0)
            {
                return BadRequest(new
                {
                    error = "reference_has_no_speech",
                    message = $"No speech was recognized in '{reference.FileName}'. Use a recording of the person speaking."
                });
            }

            references[i] = reference with { Transcript = transcript };
        }

        return null;
    }

    /// <summary>Revokes consent: the voice stays on record (who attested, when, and now when it was revoked) but is unselectable everywhere from this moment.</summary>
    [HttpPost("{voiceId:guid}/revoke-consent")]
    public async Task<ActionResult<ProjectVoiceResponse>> RevokeConsent(Guid projectId, Guid voiceId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectVoice? voice = await _db.ProjectVoices.FirstOrDefaultAsync(v => v.Id == voiceId && v.ProjectId == projectId, ct);
        if (voice is null) return NotFound();
        if (voice.Source != ProjectVoiceSource.Cloned)
            return BadRequest(new { error = "not_a_cloned_voice", message = "A library voice carries no consent to revoke." });

        if (voice.ConsentRevokedAt is null)
        {
            voice.ConsentRevokedAt = DateTime.UtcNow;
            voice.ConsentRevokedByUserId = _currentUser.UserId;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(ToResponse(voice, DateTime.UtcNow, await ProviderNamesAsync([voice], ct)));
    }

    /// <summary>
    /// Deletes the voice: the vendor-side model (best effort, reported), the reference recordings
    /// it was trained from (storage object + <c>project_files</c> row), and the row itself. The row
    /// is removed even when the vendor refused, so a revoked consent is never silently kept alive
    /// here — but the vendor outcome is reported so the operator can follow up.
    /// </summary>
    [HttpDelete("{voiceId:guid}")]
    public async Task<ActionResult<DeleteVoiceResponse>> Delete(Guid projectId, Guid voiceId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Delete, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        ProjectVoice? voice = await _db.ProjectVoices.FirstOrDefaultAsync(v => v.Id == voiceId && v.ProjectId == projectId, ct);
        if (voice is null) return NotFound();

        (bool remoteDeleted, string? note) = await DeleteRemoteModelAsync(voice, ct);
        int referencesDeleted = voice.Source == ProjectVoiceSource.Cloned
            ? await DeleteReferenceFilesAsync(voice, ct)
            : 0;

        _db.ProjectVoices.Remove(voice);
        await _db.SaveChangesAsync(ct);

        return Ok(new DeleteVoiceResponse(voice.Id, RowDeleted: true, remoteDeleted, note, referencesDeleted));
    }

    // ---- Shared with ProjectVoiceRetentionService through these two helpers' public twins. ----

    internal async Task<(bool Deleted, string? Note)> DeleteRemoteModelAsync(ProjectVoice voice, CancellationToken ct) =>
        await DeleteRemoteModelAsync(voice, _providers, _speechFactory, _logger, ct);

    /// <summary>Best-effort vendor deletion. A Library voice is never deleted at the vendor — it is not ours.</summary>
    public static async Task<(bool Deleted, string? Note)> DeleteRemoteModelAsync(
        ProjectVoice voice,
        IInferenceProviderResolver providers,
        ISpeechSynthesisClientFactory speechFactory,
        ILogger logger,
        CancellationToken ct)
    {
        if (voice.Source != ProjectVoiceSource.Cloned)
            return (false, "library_voice_not_owned");
        if (voice.InferenceProviderId is null)
            return (false, "provider_row_deleted");

        try
        {
            ResolvedSpeechSynthesisProvider? provider = await providers.ResolveSpeechSynthesisAsync(voice.InferenceProviderId, ct);
            if (provider is null)
                return (false, "provider_not_resolvable");
            if (speechFactory.Get(provider).Unwrap<IVoiceCloningClient>() is not { } cloner)
                return (false, "cloning_not_supported");
            bool deleted = await cloner.DeleteVoiceAsync(voice.RemoteVoiceId, ct);
            return (deleted, deleted ? null : "vendor_refused");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Vendor deletion of voice {VoiceId} failed", voice.Id);
            return (false, "vendor_error");
        }
    }

    internal async Task<int> DeleteReferenceFilesAsync(ProjectVoice voice, CancellationToken ct) =>
        await DeleteReferenceFilesAsync(voice, _db, _storage, _logger, ct, _historyEraser);

    /// <summary>Removes the reference recordings a clone was trained from: the storage object and the project_files row of each, best effort per file.</summary>
    public static async Task<int> DeleteReferenceFilesAsync(
        ProjectVoice voice,
        InferenceApiDbContext db,
        IFileStorageService storage,
        ILogger logger,
        CancellationToken ct,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? historyEraser = null)
    {
        List<Guid> ids = ParseReferenceIds(voice.ReferenceProjectFileIdsJson);
        if (ids.Count == 0)
            return 0;

        List<ProjectFile> files = await db.ProjectFiles
            .Where(f => f.ProjectId == voice.ProjectId && ids.Contains(f.Id)
                        && f.CsamStatus != CsamScanStatus.Quarantined)
            .ToListAsync(ct);
        // A file that is not served keeps its object and its row; only the voice goes.
        // When the marker cannot be read, nothing can be told apart: every recording keeps its object and its row.
        ReelBolt.Shared.Storage.ProjectOutputHoldState state = await OutputGate.ReadAsync(db, voice.ProjectId, ct);
        if (state.IsActive && state.Unreadable)
            return 0;
        HashSet<Guid> notServed = await OutputGate.HeldFileIdsAsync(db, voice.ProjectId, files, ct);
        // A recording an avatar also lists (its Speech sample) stays with the avatar: the voice goes, the file does not.
        notServed.UnionWith(await ReelBolt.Shared.Avatars.AvatarFileUse.ReferencedFileIdsAsync(db, [.. files.Select(f => f.Id)], ct));
        int deleted = 0;
        foreach (ProjectFile file in files.Where(f => !notServed.Contains(f.Id)))
        {
            try
            {
                await storage.DeleteAsync(voice.ProjectId, file.StorageKey, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not delete reference recording {StorageKey} for voice {VoiceId}", file.StorageKey, voice.Id);
                continue;
            }

            db.ProjectFiles.Remove(file);
            deleted++;
            // The recording is gone for good, so its protected history copies go too (a failure is logged;
            // a restore refuses a recording whose bytes no longer match, and the history sweep retries).
            if (historyEraser is not null)
            {
                try
                {
                    await historyEraser.EraseFileAsync(voice.ProjectId, file.Id, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not erase the history copies of reference recording {FileId} for voice {VoiceId}", file.Id, voice.Id);
                }
            }
        }

        return deleted;
    }

    internal static List<Guid> ParseReferenceIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<ActionResult?> CheckSpeechProviderAsync(Guid providerId, CancellationToken ct)
    {
        // The id names a model capability (docs/design/model-catalog.md §6).
        ModelCapability? row = await _db.ModelCapabilities.FindAsync([providerId], ct);
        if (row is null)
            return BadRequest(new { error = "provider_not_found" });
        if (row.Capability != InferenceProviderCapability.SpeechSynthesis)
            return BadRequest(new { error = "provider_not_speech_synthesis", message = "A voice belongs to a SpeechSynthesis provider." });
        return null;
    }

    private async Task<Dictionary<Guid, string>> ProviderNamesAsync(IReadOnlyCollection<ProjectVoice> voices, CancellationToken ct)
    {
        List<Guid> ids = voices.Where(v => v.InferenceProviderId.HasValue).Select(v => v.InferenceProviderId!.Value).Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<Guid, string>();
        return await _db.ModelCapabilities
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Model.DisplayName })
            .ToDictionaryAsync(c => c.Id, c => c.DisplayName, ct);
    }

    internal static ProjectVoiceResponse ToResponse(ProjectVoice v, DateTime now, IReadOnlyDictionary<Guid, string> providerNames) =>
        new(
            v.Id,
            v.ProjectId,
            v.InferenceProviderId,
            v.InferenceProviderId is Guid pid && providerNames.TryGetValue(pid, out string? name) ? name : null,
            v.Name,
            v.Description,
            v.Source.ToString(),
            v.RemoteVoiceId,
            ParseReferenceIds(v.ReferenceProjectFileIdsJson),
            v.IsSelectable(now),
            v.UnselectableReason(now),
            v.ConsentAttestedByUserId,
            v.ConsentAttestedAt,
            v.ConsentStatement,
            v.ConsentSubjectName,
            v.ConsentRevokedAt,
            v.RetentionUntil,
            v.CreatedAt);
}
