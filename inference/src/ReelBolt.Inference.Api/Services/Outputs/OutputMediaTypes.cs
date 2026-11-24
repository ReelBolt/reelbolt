namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>
/// The MIME type of a stored output from its extension (a step result has no MIME column). Mirrors
/// <c>MediaController.InferMediaMimeType</c>; anything unknown is <c>null</c>.
/// </summary>
internal static class OutputMediaTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".gif"] = "image/gif",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".m4a"] = "audio/mp4",
        [".aac"] = "audio/aac",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".srt"] = "application/x-subrip",
        [".vtt"] = "text/vtt",
    };

    internal static string? FromFileName(string? fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out string? mime) ? mime : null;

    internal static bool IsVideo(string? mime) => mime is not null && mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVideoOrAudio(string? mime) =>
        mime is not null &&
        (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    internal static bool IsMedia(string? mime) =>
        IsVideoOrAudio(mime) || (mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
}
