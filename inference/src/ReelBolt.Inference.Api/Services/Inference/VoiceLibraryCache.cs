using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Inference;

/// <summary>
/// The voice-library listing cache (<c>GET /projects/{id}/voices/library</c>). A dedicated
/// <see cref="MemoryCache"/> with a <see cref="MemoryCacheOptions.SizeLimit"/> — not the shared
/// <c>IMemoryCache</c> — so a caller cycling searches cannot grow it without bound or evict other
/// features' entries. Every entry is one listing page with <c>Size = 1</c>, and what is stored is
/// already bounded by <see cref="Bound"/> (items per page, tags/languages per voice, string lengths).
/// </summary>
public sealed partial class VoiceLibraryCache : IDisposable
{
    /// <summary>Most listing pages held at once.</summary>
    public const long MaxEntries = 512;

    public const int MaxVoiceIdChars = 200;
    public const int MaxNameChars = 200;
    public const int MaxTagsPerVoice = 10;
    public const int MaxTagChars = 40;
    public const int MaxLanguagesPerVoice = 10;
    public const int MaxLanguageChars = 16;

    /// <summary>Options of the dedicated cache.</summary>
    public static MemoryCacheOptions Options => new() { SizeLimit = MaxEntries };

    public MemoryCache Cache { get; } = new(Options);

    /// <summary>
    /// Collision-free key: every part is length-prefixed (a null part is <c>-1</c>), so no value of
    /// one part can be read as a separator between two others.
    /// </summary>
    public static string Key(string provider, string? search, string? language, int page)
    {
        StringBuilder sb = new("voice-library");
        foreach (string? part in new[] { provider, search, language })
            sb.Append('|').Append(part?.Length ?? -1).Append(':').Append(part);
        return sb.Append('|').Append(page).ToString();
    }

    /// <summary>A language filter must look like a language code (<c>en</c>, <c>pt-BR</c>).</summary>
    public static bool IsLanguageCode(string language) => LanguageCodePattern().IsMatch(language);

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageCodePattern();

    /// <summary>Caps a vendor page to what the API serves and caches.</summary>
    public static VoiceLibraryResponse Bound(VoiceLibraryPage result, int page, int pageSize) => new(
        result.Items
            .Where(v => !string.IsNullOrWhiteSpace(v.VoiceId) && v.VoiceId.Length <= MaxVoiceIdChars)
            .Take(pageSize)
            .Select(v => new VoiceLibraryItemResponse(
                v.VoiceId,
                Clip(v.Name, MaxNameChars),
                v.Languages.Take(MaxLanguagesPerVoice).Select(l => Clip(l, MaxLanguageChars)).ToList(),
                v.Tags.Take(MaxTagsPerVoice).Select(t => Clip(t, MaxTagChars)).ToList(),
                v.PreviewAvailable))
            .ToList(),
        Math.Max(0, result.Total), page, pageSize);

    private static string Clip(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    public void Dispose() => Cache.Dispose();
}
