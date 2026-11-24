namespace ReelBolt.Shared.Inference;

/// <summary>
/// The <c>output_format</c> tokens the ElevenLabs-family endpoints accept, narrowed to the ones the platform offers.
/// </summary>
/// <remarks>
/// Both fal's ElevenLabs endpoints and the direct API take a 19-value enum (research/audio-vendors.md A1, B1).
/// Only the MP3 and Opus tokens are offered: <c>pcm_*</c>, <c>ulaw_8000</c> and <c>alaw_8000</c> are raw
/// headerless samples that ffprobe cannot read a duration from, and the engine meters the probed duration.
/// The higher-bitrate tokens are tier-gated by ElevenLabs (192 kbps MP3 needs Creator+); a refusal there comes
/// back as a classified vendor error, not a guess here.
/// </remarks>
public static class AudioOutputFormats
{
    /// <summary>The format sent when a request names none: both vendors' own default.</summary>
    public const string Default = "mp3_44100_128";

    /// <summary>The tokens common to fal's schema and the direct API that the platform offers.</summary>
    public static readonly IReadOnlyList<string> Offered =
    [
        "mp3_22050_32", "mp3_44100_32", "mp3_44100_64", "mp3_44100_96", "mp3_44100_128", "mp3_44100_192",
        "opus_48000_32", "opus_48000_64", "opus_48000_96", "opus_48000_128", "opus_48000_192",
    ];

    /// <summary>True for a token the platform offers.</summary>
    public static bool IsOffered(string? token) => token is not null && Offered.Contains(token, StringComparer.Ordinal);

    /// <summary>The media type and extension a token produces.</summary>
    /// <exception cref="ArgumentException">The token is not offered.</exception>
    public static (string MimeType, string Extension) FileTypeOf(string token)
    {
        if (!IsOffered(token))
        {
            throw new ArgumentException("'" + token + "' is not an offered output format.", nameof(token));
        }

        return token.StartsWith("mp3_", StringComparison.Ordinal) ? ("audio/mpeg", ".mp3") : ("audio/ogg", ".opus");
    }

    /// <summary>The refusal sentence for a token that is not offered.</summary>
    public static string Refusal(string token) =>
        "The output format '" + token + "' is not offered. Use one of: " + string.Join(", ", Offered) +
        " (raw pcm/ulaw/alaw output has no container, so its duration cannot be probed).";
}
