using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>What the linter knows about one project voice. Languages is null when the voice does not state any.</summary>
public sealed record LintVoiceFact(bool Exists, bool Selectable, string? UnselectableReason, string? Name, IReadOnlyList<string>? Languages);

/// <summary>
/// What the linter knows about one project avatar: the consent part of the one rule
/// (<see cref="ProjectAvatar.UnselectableReason"/>: consent missing, revoked, retention expired), never the reference
/// bytes, which only the engine hashes at run time. Exists is false for an id that is not an avatar of the project.
/// </summary>
public sealed record LintAvatarFact(bool Exists, bool Usable, string? UnusableReason, string? Name);

/// <summary>Whether a project file is usable as a picture/video overlay source.</summary>
public enum LintMediaVerdict
{
    /// <summary>In this project, an image or a video, and not quarantined.</summary>
    Usable,
    /// <summary>Not in this project.</summary>
    Missing,
    /// <summary>In this project, but not an image or a video.</summary>
    WrongKind,
    /// <summary>In this project, but quarantined by ingest moderation or CSAM screening.</summary>
    Quarantined
}

/// <summary>
/// The readiness facts the linter needs beyond the step list. Every answer may be <c>null</c> = "unknown": a
/// rule never fires on an unknown fact, so a failed or unavailable read can only make the linter quieter,
/// never wrongly loud. One instance serves one lint (or one run start) and caches what it reads.
/// </summary>
public interface IWorkflowLintFacts
{
    /// <summary>Whether a provider is usable for the capability (the explicit one, else the organization default).</summary>
    Task<bool?> ProviderAvailableAsync(InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct);

    /// <summary>The plan violation for using a step of this type, or null when entitled (or unknown).</summary>
    Task<PlanGateViolation?> StepEntitlementAsync(StepType stepType, CancellationToken ct);

    /// <summary>The plan violation for rendering at this short side, or null when entitled (or unknown).</summary>
    Task<PlanGateViolation?> ResolutionEntitlementAsync(int shortSide, CancellationToken ct);

    /// <summary>How many project files there are per brief file role; null when unknown.</summary>
    Task<IReadOnlyDictionary<RunBriefFileRole, int>?> FileCountsAsync(CancellationToken ct);

    /// <summary>
    /// How many archive uploads (zip, tar, ...) the project holds; they never count as code. 0 when unknown.
    /// </summary>
    Task<int> ArchiveFilesAsync(CancellationToken ct) => Task.FromResult(0);

    /// <summary>How many of those archives are still being unpacked (queued or running). 0 when unknown.</summary>
    Task<int> UnpackingArchivesAsync(CancellationToken ct) => Task.FromResult(0);

    /// <summary>
    /// Asks once whether these files are in the project, so the per-id answers below cost no further queries.
    /// Implementations without a cache may ignore it.
    /// </summary>
    Task WarmFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Whether this project has a file with this id; null when unknown.</summary>
    Task<bool?> ProjectFileExistsAsync(Guid fileId, CancellationToken ct);

    /// <summary>
    /// Whether a file is usable as a picture/video overlay source: in this project, image/video, not
    /// quarantined. Null when unknown (never a finding). A facts implementation that cannot answer
    /// leaves it unknown.
    /// </summary>
    Task<LintMediaVerdict?> ProjectMediaVerdictAsync(Guid fileId, CancellationToken ct) =>
        Task.FromResult<LintMediaVerdict?>(null);

    /// <summary>Whether the project has an approved design system; null when unknown.</summary>
    Task<bool?> HasApprovedDesignSystemAsync(CancellationToken ct);

    /// <summary>The project voice, or null when unknown. A voice that is not in the project answers <c>Exists = false</c>.</summary>
    Task<LintVoiceFact?> ProjectVoiceAsync(Guid voiceId, CancellationToken ct);

    /// <summary>Total seconds of source footage in the project; null when unknown (the usual case today).</summary>
    Task<double?> SourceVideoSecondsAsync(CancellationToken ct);

    /// <summary>The project avatar's consent state, or null when unknown (never a finding).</summary>
    Task<LintAvatarFact?> ProjectAvatarAsync(Guid avatarId, CancellationToken ct) => Task.FromResult<LintAvatarFact?>(null);

    /// <summary>
    /// The kind of the video-generation provider a step resolves to (the explicit one, else the organization default),
    /// for the rule that judges a model by its own declarations (WL034); null when none resolves or it is unknown.
    /// </summary>
    Task<InferenceProviderKind?> VideoGenerationProviderKindAsync(Guid? explicitProviderId, CancellationToken ct) =>
        Task.FromResult<InferenceProviderKind?>(null);

    /// <summary>
    /// Whether the video provider a step resolves to (the explicit one, else the default) knows this model, as the executor's own
    /// offline lookup answers; false only when that lookup says "not supported". Null when unknown: no provider, a provider
    /// that prices through a network oracle, or a lookup that could not be made (never a finding).
    /// </summary>
    Task<bool?> VideoModelSupportedAsync(Guid? explicitProviderId, string model, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <summary>
    /// The same question for a whole talking-head step (its config JSON): the provider is the <c>LipSync</c> one (never a
    /// <c>VideoGeneration</c> row), the model the one it would really run, and its resolution and ratio are judged after the
    /// normalisation an unbound step gets, so only a step with its own row can fail. Null when unknown, as
    /// <see cref="VideoModelSupportedAsync"/>.
    /// </summary>
    Task<bool?> LipSyncStepAcceptedAsync(string videoGenerateConfigJson, CancellationToken ct) => Task.FromResult<bool?>(null);
}

/// <summary>Builds the facts for one organization and project.</summary>
public interface IWorkflowLintFactsSource
{
    /// <summary>The facts of a project that the caller has already been authorised on.</summary>
    IWorkflowLintFacts Create(Guid? organizationId, Guid projectId);
}

/// <summary>
/// Every fact "unknown": what a lint with no database behind it sees (a unit test of the graph rules, a scan
/// with no facts source). No fact-based rule can fire.
/// </summary>
public sealed class UnknownLintFacts : IWorkflowLintFacts
{
    /// <summary>The shared instance.</summary>
    public static UnknownLintFacts Instance { get; } = new();

    /// <inheritdoc />
    public Task<bool?> ProviderAvailableAsync(InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <inheritdoc />
    public Task<PlanGateViolation?> StepEntitlementAsync(StepType stepType, CancellationToken ct) => Task.FromResult<PlanGateViolation?>(null);

    /// <inheritdoc />
    public Task<PlanGateViolation?> ResolutionEntitlementAsync(int shortSide, CancellationToken ct) => Task.FromResult<PlanGateViolation?>(null);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<RunBriefFileRole, int>?> FileCountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<RunBriefFileRole, int>?>(null);

    /// <inheritdoc />
    public Task<bool?> ProjectFileExistsAsync(Guid fileId, CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <inheritdoc />
    public Task<LintMediaVerdict?> ProjectMediaVerdictAsync(Guid fileId, CancellationToken ct) => Task.FromResult<LintMediaVerdict?>(null);

    /// <inheritdoc />
    public Task<bool?> HasApprovedDesignSystemAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    /// <inheritdoc />
    public Task<LintVoiceFact?> ProjectVoiceAsync(Guid voiceId, CancellationToken ct) => Task.FromResult<LintVoiceFact?>(null);

    /// <inheritdoc />
    public Task<double?> SourceVideoSecondsAsync(CancellationToken ct) => Task.FromResult<double?>(null);
}

/// <summary>The production facts source: database reads scoped to the project, provider resolution scoped to the organization.</summary>
public sealed class DbWorkflowLintFactsSource(
    InferenceApiDbContext db,
    IInferenceProviderResolver providers,
    IOrganizationEntitlements entitlements,
    IVideoGenerationClientFactory? videoClients = null) : IWorkflowLintFactsSource
{
    /// <inheritdoc />
    public IWorkflowLintFacts Create(Guid? organizationId, Guid projectId) =>
        new DbWorkflowLintFacts(db, providers, entitlements, organizationId, projectId, videoClients);
}

/// <summary>
/// Facts read from the database. Each question is asked at most once per instance. A read that throws is an
/// unknown answer: lint is advisory and must never turn a database hiccup into a finding.
/// </summary>
public sealed class DbWorkflowLintFacts(
    InferenceApiDbContext db,
    IInferenceProviderResolver providers,
    IOrganizationEntitlements entitlements,
    Guid? organizationId,
    Guid projectId,
    IVideoGenerationClientFactory? videoClients = null) : IWorkflowLintFacts
{
    /// <inheritdoc />
    public async Task<bool?> VideoModelSupportedAsync(Guid? explicitProviderId, string model, CancellationToken ct)
    {
        if (videoClients is null || string.IsNullOrWhiteSpace(model))
            return null;
        try
        {
            ResolvedVideoGenerationProvider? provider = await providers.ResolveVideoGenerationAsync(explicitProviderId, ct);
            if (provider is null)
                return null;
            IVideoGenerationClient client = videoClients.Get(provider);
            // A client that prices through the vendor's network oracle is judged by that oracle at run time, never here.
            if (client is IVideoGenerationPriceOracle)
                return null;
            client.Describe(model);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool?> LipSyncStepAcceptedAsync(string videoGenerateConfigJson, CancellationToken ct)
    {
        if (videoClients is null)
            return null;
        try
        {
            VideoGenerateStepConfig? config = System.Text.Json.JsonSerializer.Deserialize<VideoGenerateStepConfig>(
                videoGenerateConfigJson, VideoGenerateStepConfig.JsonOptions);
            if (config is null)
                return null;
            ResolvedVideoGenerationProvider? provider = await providers.ResolveLipSyncAsync(config.ProviderId, ct);
            if (provider is null)
                return null;
            IVideoGenerationClient client = videoClients.Get(provider);
            if (client is IVideoGenerationPriceOracle)
                return null;
            LipSyncStepResolution.Apply(config, provider);
            return LipSyncStepResolution.SettingsAccepted(config, provider, client.Describe(config.Model));
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private readonly Dictionary<(InferenceProviderCapability, Guid?), bool?> _providers = [];
    private OrganizationEntitlementSet? _entitlementSet;
    private Task<IReadOnlyDictionary<RunBriefFileRole, int>?>? _fileCounts;
    private Task<bool?>? _designApproved;

    /// <inheritdoc />
    public async Task<bool?> ProviderAvailableAsync(InferenceProviderCapability capability, Guid? explicitProviderId, CancellationToken ct)
    {
        if (_providers.TryGetValue((capability, explicitProviderId), out bool? known))
            return known;

        bool? answer;
        try
        {
            answer = capability switch
            {
                InferenceProviderCapability.SpeechSynthesis => await providers.ResolveSpeechSynthesisAsync(explicitProviderId, ct) is not null,
                InferenceProviderCapability.VideoGeneration => await providers.ResolveVideoGenerationAsync(explicitProviderId, ct) is not null,
                InferenceProviderCapability.LipSync => await providers.ResolveLipSyncAsync(explicitProviderId, ct) is not null,
                InferenceProviderCapability.MusicGeneration => await providers.ResolveMusicGenerationAsync(explicitProviderId, ct) is not null,
                InferenceProviderCapability.SoundEffectGeneration => await providers.ResolveSoundEffectGenerationAsync(explicitProviderId, ct) is not null,
                InferenceProviderCapability.Transcription => await providers.ResolveTranscriptionAsync(explicitProviderId, ct) is not null,
                InferenceProviderCapability.Vision => await providers.ResolveVisionAsync(explicitProviderId, ct) is not null,
                _ => null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = null;
        }

        _providers[(capability, explicitProviderId)] = answer;
        return answer;
    }

    private async Task<OrganizationEntitlementSet?> EntitlementsAsync(CancellationToken ct)
    {
        if (_entitlementSet is not null)
            return _entitlementSet;
        if (organizationId is not { } org)
            return null;
        try
        {
            return _entitlementSet = await entitlements.GetAsync(org, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<PlanGateViolation?> StepEntitlementAsync(StepType stepType, CancellationToken ct) =>
        await EntitlementsAsync(ct) is { } set ? PlanGate.CheckStep(stepType, set) : null;

    /// <inheritdoc />
    public async Task<PlanGateViolation?> ResolutionEntitlementAsync(int shortSide, CancellationToken ct) =>
        await EntitlementsAsync(ct) is { } set ? PlanGate.CheckResolution(shortSide, set) : null;

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<RunBriefFileRole, int>?> FileCountsAsync(CancellationToken ct) =>
        _fileCounts ??= LoadFileCountsAsync(ct);

    private int _archives;

    /// <inheritdoc />
    public async Task<int> UnpackingArchivesAsync(CancellationToken ct)
    {
        if (await ArchiveFilesAsync(ct) == 0) return 0;
        try
        {
            return await new ProjectReadinessFacts(db).CountUnpackingAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task<int> ArchiveFilesAsync(CancellationToken ct)
    {
        await FileCountsAsync(ct);
        return _archives;
    }

    private async Task<IReadOnlyDictionary<RunBriefFileRole, int>?> LoadFileCountsAsync(CancellationToken ct)
    {
        try
        {
            (int video, int audio, int image, int code, int archives) = await new ProjectReadinessFacts(db).CountFilesWithArchivesAsync(projectId, ct);
            _archives = archives;
            // Audio is both music and sound effects (either may be what a step wants); documents are not counted.
            return new Dictionary<RunBriefFileRole, int>
            {
                [RunBriefFileRole.Video] = video,
                [RunBriefFileRole.Music] = audio,
                [RunBriefFileRole.Sfx] = audio,
                [RunBriefFileRole.Image] = image,
                [RunBriefFileRole.Code] = code,
                [RunBriefFileRole.Document] = 0,
                [RunBriefFileRole.InsertContent] = image + video
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private readonly Dictionary<Guid, bool> _fileKnown = [];
    private readonly Dictionary<Guid, LintMediaVerdict> _mediaKnown = [];

    /// <summary>The most ids one facts instance will look up in total; further ids stay unknown (silent).</summary>
    public const int MaxFileLookups = 1000;

    /// <summary>Ids per query, so no statement carries an unbounded list.</summary>
    public const int FileLookupChunk = 200;

    /// <inheritdoc />
    public async Task WarmFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct)
    {
        int budget = MaxFileLookups - _fileKnown.Count;
        List<Guid> unknown = [.. fileIds.Distinct().Where(id => !_fileKnown.ContainsKey(id)).Take(Math.Max(budget, 0))];
        if (unknown.Count == 0)
            return;
        try
        {
            foreach (Guid[] chunk in unknown.Chunk(FileLookupChunk))
            {
                HashSet<Guid> present = [.. await db.ProjectFiles.AsNoTracking()
                    .Where(f => f.ProjectId == projectId && chunk.Contains(f.Id))
                    .Select(f => f.Id)
                    .ToListAsync(ct)];
                foreach (Guid id in chunk)
                    _fileKnown[id] = present.Contains(id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // unknown stays unknown
        }
    }

    /// <inheritdoc />
    public async Task<bool?> ProjectFileExistsAsync(Guid fileId, CancellationToken ct)
    {
        if (!_fileKnown.ContainsKey(fileId))
            await WarmFilesAsync([fileId], ct);
        return _fileKnown.TryGetValue(fileId, out bool known) ? known : null;
    }

    /// <inheritdoc />
    public async Task<LintMediaVerdict?> ProjectMediaVerdictAsync(Guid fileId, CancellationToken ct)
    {
        if (_mediaKnown.TryGetValue(fileId, out LintMediaVerdict cached))
            return cached;

        // The same total per-lint budget as ProjectFileExistsAsync. Past it, a new id is simply
        // unknown (never a finding), so a pathological workflow cannot make the linter issue an
        // unbounded number of queries.
        if (_mediaKnown.Count >= MaxFileLookups)
            return null;

        try
        {
            var row = await db.ProjectFiles.AsNoTracking()
                .Where(f => f.ProjectId == projectId && f.Id == fileId)
                .Select(f => new { f.MimeType, f.CsamStatus, f.ModerationStatus })
                .FirstOrDefaultAsync(ct);

            if (row is null)
            {
                // Memoize the miss too: a repeated missing id must not re-query forever.
                _mediaKnown[fileId] = LintMediaVerdict.Missing;
                return LintMediaVerdict.Missing;
            }

            LintMediaVerdict verdict =
                row.CsamStatus == CsamScanStatus.Quarantined || row.ModerationStatus == FileModerationStatus.Quarantined
                    ? LintMediaVerdict.Quarantined
                    : row.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                      || row.MimeType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                        ? LintMediaVerdict.Usable
                        : LintMediaVerdict.WrongKind;

            _mediaKnown[fileId] = verdict;
            return verdict;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // unknown: never a finding
        }
    }

    /// <inheritdoc />
    public Task<bool?> HasApprovedDesignSystemAsync(CancellationToken ct) => _designApproved ??= LoadDesignApprovedAsync(ct);

    private async Task<bool?> LoadDesignApprovedAsync(CancellationToken ct)
    {
        try
        {
            DesignSystem? system = await db.DesignSystems.AsNoTracking()
                .FirstOrDefaultAsync(s => s.ProjectId == projectId && s.Kind == DesignSystemKinds.Design, ct);
            if (system is null)
                return false;
            // A linked library carries its own approved version; its state is not read here, so do not claim "missing".
            if (system.ParentLibraryId is not null)
                return true;
            if (system.CurrentVersionId is not { } versionId)
                return false;
            return await db.DesignSystemVersions.AsNoTracking()
                .AnyAsync(v => v.Id == versionId && v.DesignSystemId == system.Id && v.State == DesignVersionState.Approved, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<LintVoiceFact?> ProjectVoiceAsync(Guid voiceId, CancellationToken ct)
    {
        try
        {
            ProjectVoice? voice = await db.ProjectVoices.AsNoTracking()
                .FirstOrDefaultAsync(v => v.Id == voiceId && v.ProjectId == projectId, ct);
            if (voice is null)
                return new LintVoiceFact(false, false, "not_found", null, null);
            DateTime now = DateTime.UtcNow;
            return new LintVoiceFact(true, voice.IsSelectable(now), voice.UnselectableReason(now), voice.Name, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<double?> SourceVideoSecondsAsync(CancellationToken ct) => Task.FromResult<double?>(null);

    /// <inheritdoc />
    public async Task<InferenceProviderKind?> VideoGenerationProviderKindAsync(Guid? explicitProviderId, CancellationToken ct)
    {
        try
        {
            return (await providers.ResolveVideoGenerationAsync(explicitProviderId, ct))?.Kind;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<LintAvatarFact?> ProjectAvatarAsync(Guid avatarId, CancellationToken ct)
    {
        try
        {
            ProjectAvatar? avatar = await db.ProjectAvatars.AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, ct);
            // An avatar of a kind that is switched off is treated as absent, as every avatar route treats it.
            if (avatar is null || !ReelBolt.Shared.Workflows.Apps.AppStepFacts.AvatarKindEnabled(avatar.SubjectKind))
                return new LintAvatarFact(false, false, "avatar_not_found", null);

            // Only the consent half of the one rule: a stand-in usable image reference makes the reference checks
            // pass, so the answer is exactly consent missing, revoked or expired (the engine checks the media).
            string? reason = avatar.UnselectableReason(
                DateTime.UtcNow, [new AvatarReferenceState(AvatarReferenceKind.Image, true, false)],
                await ReelBolt.Shared.Avatars.AvatarVariantOriginals.LoadAsync(db, projectId, [avatar], ct));
            return new LintAvatarFact(true, reason is null, reason, avatar.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
