using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Voices;

/// <summary>Why a library call answered nothing. The REST route maps each to its status; the assistant to a code.</summary>
public enum VoiceLibraryFailureKind
{
    /// <summary>The search, language or page is out of range (REST 400).</summary>
    BadRequest,

    /// <summary>No SpeechSynthesis provider, or one with no public library (REST 404 <c>voice_library_not_supported</c>).</summary>
    NotSupported,

    /// <summary>The vendor failed; its text is logged only (REST 502 <c>voice_library_failed</c>).</summary>
    VendorFailed,

    /// <summary>The only speech model is unpriced (REST 409, <see cref="ModelUnpricedException"/>).</summary>
    Unpriced,
}

/// <summary>One library refusal: its kind and a message safe to show the caller (never vendor or transport text).</summary>
public sealed record VoiceLibraryFailure(VoiceLibraryFailureKind Kind, string Message, ModelUnpricedException? Unpriced = null);

/// <summary>A value, or why there is none.</summary>
public sealed record VoiceLibraryResult<T>(T? Value, VoiceLibraryFailure? Failure)
{
    public static VoiceLibraryResult<T> Ok(T value) => new(value, null);

    public static VoiceLibraryResult<T> Fail(VoiceLibraryFailure failure) => new(default, failure);
}

/// <summary>
/// The project's resolved SpeechSynthesis provider's public voice library (docs/voiceover.md "Browsing the Fish
/// voice library"): the ONE implementation behind <c>GET /projects/{id}/voices/library</c> and the assistant's
/// <c>SearchVoiceLibrary</c> / <c>ProposeAddLibraryVoice</c> / <c>ApplyAddLibraryVoice</c>. It authorizes nothing:
/// every caller checks the project first. A voice CLONED on the platform account (a non-Library
/// <see cref="ProjectVoice"/> in any project) is never listed and never found, cached answer or not.
/// </summary>
public sealed class ProjectVoiceLibraryService
{
    /// <summary>Voices per library page.</summary>
    public const int PageSize = 24;

    public const int MaxSearchChars = 100;
    public const int MaxPage = 200;
    public const int MaxLanguageChars = 20;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    private readonly InferenceApiDbContext _db;
    private readonly IInferenceProviderResolver _providers;
    private readonly ISpeechSynthesisClientFactory _speechFactory;
    private readonly ILogger _logger;
    private readonly MemoryCache? _cache;

    public ProjectVoiceLibraryService(
        InferenceApiDbContext db,
        IInferenceProviderResolver providers,
        ISpeechSynthesisClientFactory speechFactory,
        ILogger<ProjectVoiceLibraryService> logger,
        VoiceLibraryCache? cache = null)
        : this(db, providers, speechFactory, (ILogger)logger, cache)
    {
    }

    internal ProjectVoiceLibraryService(
        InferenceApiDbContext db,
        IInferenceProviderResolver providers,
        ISpeechSynthesisClientFactory speechFactory,
        ILogger logger,
        VoiceLibraryCache? cache)
    {
        _db = db;
        _providers = providers;
        _speechFactory = speechFactory;
        _logger = logger;
        _cache = cache?.Cache;
    }

    private static VoiceLibraryFailure NotSupported(string message) => new(VoiceLibraryFailureKind.NotSupported, message);

    private static readonly VoiceLibraryFailure VendorFailed =
        new(VoiceLibraryFailureKind.VendorFailed, "The voice library could not be loaded. Try again later.");

    /// <summary>The input rules of a search; null when the search may be sent.</summary>
    public static string? ValidateSearch(string? q, string? language, int page)
    {
        if (page < 1 || page > MaxPage)
            return $"page must be between 1 and {MaxPage}.";
        if (q is { Length: > MaxSearchChars } || language is { Length: > MaxLanguageChars })
            return $"q must be at most {MaxSearchChars} characters and language at most {MaxLanguageChars}.";
        if (!string.IsNullOrWhiteSpace(language) && !VoiceLibraryCache.IsLanguageCode(language.Trim()))
            return "language must be a language code such as 'en' or 'pt-BR'.";
        return null;
    }

    /// <summary>One page of the library, bounded, cached per (provider, search, language, page) and without cloned voices.</summary>
    public async Task<VoiceLibraryResult<VoiceLibraryResponse>> SearchAsync(
        Guid projectId, string? q, string? language, int page, CancellationToken ct)
    {
        if (ValidateSearch(q, language, page) is { } invalid)
            return VoiceLibraryResult<VoiceLibraryResponse>.Fail(new VoiceLibraryFailure(VoiceLibraryFailureKind.BadRequest, invalid));

        (ResolvedSpeechSynthesisProvider? provider, IVoiceLibraryClient? library, VoiceLibraryFailure? failure) =
            await ResolveLibraryAsync(projectId, "browsing voices", ct);
        if (failure is not null)
            return VoiceLibraryResult<VoiceLibraryResponse>.Fail(failure);

        VoiceLibraryQuery query = new(string.IsNullOrWhiteSpace(q) ? null : q.Trim(), string.IsNullOrWhiteSpace(language) ? null : language.Trim(), page, PageSize);
        string cacheKey = VoiceLibraryCache.Key(provider!.ProviderId?.ToString() ?? provider.Name, query.Search, query.Language, query.Page);
        if (_cache is null || !_cache.TryGetValue(cacheKey, out VoiceLibraryResponse? response) || response is null)
        {
            try
            {
                VoiceLibraryPage result = await library!.ListLibraryVoicesAsync(query, ct);
                response = VoiceLibraryCache.Bound(result, page, PageSize);
            }
            catch (NotSupportedException)
            {
                return VoiceLibraryResult<VoiceLibraryResponse>.Fail(NotSupported($"Provider '{provider.Name}' ({provider.Kind}) has no voice library."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The vendor's error text and transport errors (which name the provider host:port) are
                // logged only; a project viewer gets a generic message.
                _logger.LogWarning(ex, "Listing the voice library failed for project {ProjectId}", projectId);
                return VoiceLibraryResult<VoiceLibraryResponse>.Fail(VendorFailed);
            }

            _cache?.Set(cacheKey, response, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheTtl });
        }

        return VoiceLibraryResult<VoiceLibraryResponse>.Ok(await WithoutClonedVoicesAsync(response, ct));
    }

    /// <summary>
    /// One public library voice by its vendor id, bounded like a listing item. A null value (with no failure)
    /// means "not a library voice": unknown to the vendor, private, or a clone made on the platform account.
    /// </summary>
    public async Task<VoiceLibraryResult<VoiceLibraryItemResponse?>> FindAsync(Guid projectId, string voiceId, CancellationToken ct)
    {
        string id = voiceId?.Trim() ?? string.Empty;
        if (id.Length == 0 || id.Length > VoiceLibraryCache.MaxVoiceIdChars)
            return VoiceLibraryResult<VoiceLibraryItemResponse?>.Ok(null);

        // A platform clone is never a library voice, whatever the vendor says (checked before any vendor call).
        if (await IsClonedVoiceAsync(id, ct))
            return VoiceLibraryResult<VoiceLibraryItemResponse?>.Ok(null);

        (ResolvedSpeechSynthesisProvider? provider, IVoiceLibraryClient? library, VoiceLibraryFailure? failure) =
            await ResolveLibraryAsync(projectId, "looking up a library voice", ct);
        if (failure is not null)
            return VoiceLibraryResult<VoiceLibraryItemResponse?>.Fail(failure);

        VoiceLibraryVoice? voice;
        try
        {
            voice = await library!.GetLibraryVoiceAsync(id, ct);
        }
        catch (NotSupportedException)
        {
            return VoiceLibraryResult<VoiceLibraryItemResponse?>.Fail(NotSupported($"Provider '{provider!.Name}' ({provider.Kind}) has no voice library."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Looking up a library voice failed for project {ProjectId}", projectId);
            return VoiceLibraryResult<VoiceLibraryItemResponse?>.Fail(VendorFailed);
        }

        if (voice is null || !string.Equals(voice.VoiceId, id, StringComparison.Ordinal))
            return VoiceLibraryResult<VoiceLibraryItemResponse?>.Ok(null);

        VoiceLibraryItemResponse? bounded = VoiceLibraryCache.Bound(new VoiceLibraryPage([voice], 1), 1, 1).Items.FirstOrDefault();
        return VoiceLibraryResult<VoiceLibraryItemResponse?>.Ok(bounded);
    }

    /// <summary>True when the vendor id is a voice CLONED (not a library voice) in any project.</summary>
    public Task<bool> IsClonedVoiceAsync(string voiceId, CancellationToken ct) =>
        _db.ProjectVoices.AnyAsync(v => v.Source != ProjectVoiceSource.Library && v.RemoteVoiceId == voiceId, ct);

    private async Task<(ResolvedSpeechSynthesisProvider?, IVoiceLibraryClient?, VoiceLibraryFailure?)> ResolveLibraryAsync(
        Guid projectId, string doing, CancellationToken ct)
    {
        ResolvedSpeechSynthesisProvider? provider;
        try
        {
            provider = await _providers.ResolveSpeechSynthesisAsync(null, ct);
        }
        catch (ModelUnpricedException unpriced)
        {
            return (null, null, new VoiceLibraryFailure(VoiceLibraryFailureKind.Unpriced, unpriced.Message, unpriced));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Speech provider resolution failed while {Doing} for project {ProjectId}", doing, projectId);
            provider = null;
        }

        if (provider is null)
            return (null, null, NotSupported("No SpeechSynthesis provider is configured."));

        IVoiceLibraryClient? library = null;
        if (provider.Kind == InferenceProviderKind.FishAudio)
        {
            try
            {
                library = _speechFactory.Get(provider).Unwrap<IVoiceLibraryClient>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Speech client construction failed while {Doing} for project {ProjectId}", doing, projectId);
            }
        }

        return library is null
            ? (provider, null, NotSupported($"Provider '{provider.Name}' ({provider.Kind}) has no voice library."))
            : (provider, library, null);
    }

    /// <summary>
    /// Drops any voice whose vendor id is a CLONED ProjectVoice in any project: clones made on the
    /// platform account must never be listed to anyone. Applied on every answer (cached or fresh), so a
    /// clone made after the page was cached is hidden too.
    /// </summary>
    private async Task<VoiceLibraryResponse> WithoutClonedVoicesAsync(VoiceLibraryResponse response, CancellationToken ct)
    {
        List<string> ids = response.Items.Select(i => i.VoiceId).Distinct().ToList();
        if (ids.Count == 0) return response;
        HashSet<string> cloned = (await _db.ProjectVoices
            .Where(v => v.Source != ProjectVoiceSource.Library && ids.Contains(v.RemoteVoiceId))
            .Select(v => v.RemoteVoiceId)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        return cloned.Count == 0 ? response : response with { Items = response.Items.Where(i => !cloned.Contains(i.VoiceId)).ToList() };
    }
}
