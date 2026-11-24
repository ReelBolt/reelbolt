namespace ReelBolt.Shared.Inference;

/// <summary>
/// What a reference recording really is, read from its first bytes. A declared type is only what the uploader's
/// browser or file extension said; a video model that derives the format from the data it receives refuses a
/// recording whose label and bytes disagree, so the label sent is always the one the bytes earn.
/// </summary>
public static class ReferenceAudioFormat
{
    /// <summary>The media type written into the data URI for WAV bytes.</summary>
    public const string WavMediaType = "audio/wav";

    /// <summary>The media type written into the data URI for MP3 bytes (never <c>audio/mpeg</c>, which the vendor reads as ".mpeg").</summary>
    public const string Mp3MediaType = "audio/mp3";

    /// <summary>
    /// <see cref="WavMediaType"/> for a RIFF/WAVE header, <see cref="Mp3MediaType"/> for an ID3 tag or an MPEG audio
    /// frame sync (layer III), otherwise null (m4a/aac, ogg/opus, flac, webm and anything else).
    /// </summary>
    public static string? Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 12
            && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F'
            && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E')
        {
            return WavMediaType;
        }

        if (head.Length >= 3 && head[0] == 'I' && head[1] == 'D' && head[2] == '3')
        {
            return Mp3MediaType;
        }

        // 11-bit frame sync, MPEG-1/2/2.5 (version bits != 01), layer III (bits 01) and not the reserved forms.
        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0
            && ((head[1] >> 3) & 0x3) != 0x1 && ((head[1] >> 1) & 0x3) == 0x1)
        {
            return Mp3MediaType;
        }

        return null;
    }

    /// <summary>What the customer is told when a stored recording cannot be sent: nothing was submitted or charged.</summary>
    public const string ReuploadMessage = "A voice recording of this avatar is not a WAV or MP3 file by its content (M4A, AAC, OGG, FLAC, WebM and other formats are not accepted). Nothing was submitted and nothing was charged. Re-upload the recording as WAV or MP3 and add it to the avatar again.";

    /// <summary>The sentence shown when a recording is neither WAV nor MP3 by its bytes.</summary>
    public const string NotSupportedMessage = "it is not a WAV or MP3 recording (other audio formats such as M4A, AAC, OGG, FLAC or WebM are not accepted; export it as WAV or MP3)";
}

/// <summary>
/// A reference recording the video client refused before building any request (it is neither WAV nor MP3 by its
/// bytes). Nothing reached the vendor, so the caller releases the take and tells the customer to re-upload.
/// </summary>
public sealed class ReferenceAudioFormatException : ArgumentException
{
    public ReferenceAudioFormatException(string message, string? paramName)
        : base(message, paramName)
    {
    }
}
