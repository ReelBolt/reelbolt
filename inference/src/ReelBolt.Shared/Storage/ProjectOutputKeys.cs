namespace ReelBolt.Shared.Storage;

/// <summary>
/// The one rule for which stored objects count as a project's playable step outputs. Renders and compiles live
/// under <c>projects/{projectId}/outputFiles/</c>; VideoGenerate clips (and other generated media) live under
/// <c>projects/{projectId}/agentFiles/generated/</c>. Every path that turns a step result into something a caller can
/// fetch, register or reuse as a source (output links, GetOutput, register-output, StepResult sources) must use this
/// rule so a generated clip is addressable exactly like a render, and nothing outside the two prefixes ever is.
/// </summary>
public static class ProjectOutputKeys
{
    /// <summary>Prefix of renders and compiles, with a trailing slash.</summary>
    public static string OutputFilesPrefix(Guid projectId) => $"projects/{projectId}/outputFiles/";

    /// <summary>Prefix of generated media (VideoGenerate clips and similar), with a trailing slash.</summary>
    public static string GeneratedPrefix(Guid projectId) => $"projects/{projectId}/agentFiles/generated/";

    /// <summary>
    /// True when <paramref name="storageKey"/> is a playable output of <paramref name="projectId"/>: under one of the
    /// two prefixes (ordinal, trailing slash), with no <c>..</c> segment and no backslash.
    /// </summary>
    public static bool IsAddressableOutput(Guid projectId, string? storageKey)
    {
        if (string.IsNullOrEmpty(storageKey) || storageKey.Contains("..", StringComparison.Ordinal)
            || storageKey.Contains('\\'))
        {
            return false;
        }

        return storageKey.StartsWith(OutputFilesPrefix(projectId), StringComparison.Ordinal)
            || storageKey.StartsWith(GeneratedPrefix(projectId), StringComparison.Ordinal);
    }

    /// <summary>Prefix of exported pictures, with a trailing slash.</summary>
    public static string ThumbnailsPrefix(Guid projectId) => $"projects/{projectId}/userFiles/thumbnails/";

    /// <summary>
    /// True when the platform made the bytes behind <paramref name="storageKey"/>: an addressable output or a
    /// picture export under the thumbnails prefix (same key checks as <see cref="IsAddressableOutput"/>).
    /// </summary>
    public static bool IsDerivedOutputFile(Guid projectId, string? storageKey)
    {
        if (IsAddressableOutput(projectId, storageKey)) return true;
        if (string.IsNullOrEmpty(storageKey) || storageKey.Contains("..", StringComparison.Ordinal)
            || storageKey.Contains('\\'))
        {
            return false;
        }

        return storageKey.StartsWith(ThumbnailsPrefix(projectId), StringComparison.Ordinal);
    }
}
