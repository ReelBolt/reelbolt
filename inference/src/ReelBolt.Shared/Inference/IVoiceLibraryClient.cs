namespace ReelBolt.Shared.Inference;

/// <summary>
/// The optional voice-library browsing half of a speech-synthesis backend (see docs/voiceover.md
/// "Browsing the Fish voice library"). Implemented by the Fish Audio client against the hosted API's
/// public <c>GET /model</c> listing; a backend without one simply does not implement it and the API
/// answers <c>404 voice_library_not_supported</c>. Separate from <see cref="ISpeechSynthesisClient"/>
/// for the same reason <see cref="IVoiceCloningClient"/> is: browsing is a project-screen action, not
/// something a workflow step does.
/// </summary>
public interface IVoiceLibraryClient
{
    /// <summary>
    /// One page of the vendor's voice library. Throws <see cref="NotSupportedException"/> when this
    /// particular endpoint has no library (a self-hosted fish-speech server).
    /// </summary>
    Task<VoiceLibraryPage> ListLibraryVoicesAsync(VoiceLibraryQuery query, CancellationToken ct);

    /// <summary>
    /// One PUBLIC library voice by its vendor id, or null when the vendor has no such public voice
    /// (unknown id, or a private or unlisted model). Used to name a voice on an approval card and to
    /// refuse an id that is not a library voice before it is registered. A backend without the lookup
    /// throws <see cref="NotSupportedException"/> (the default).
    /// </summary>
    Task<VoiceLibraryVoice?> GetLibraryVoiceAsync(string voiceId, CancellationToken ct) =>
        throw new NotSupportedException("This voice library cannot look a voice up by id.");
}

/// <summary>A library search: free text, an optional language code, and a 1-based page.</summary>
public sealed record VoiceLibraryQuery(string? Search, string? Language, int Page, int PageSize);

/// <summary>One library voice, vendor-neutral. <c>PreviewAvailable</c> is true when the vendor lists a sample for it.</summary>
public sealed record VoiceLibraryVoice(
    string VoiceId,
    string Name,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Tags,
    bool PreviewAvailable);

/// <summary>A page of library voices plus the vendor's total match count.</summary>
public sealed record VoiceLibraryPage(IReadOnlyList<VoiceLibraryVoice> Items, int Total);
