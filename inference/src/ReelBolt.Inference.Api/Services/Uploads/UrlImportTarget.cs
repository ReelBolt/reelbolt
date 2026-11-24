using ReelBolt.Shared;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Uploads;

/// <summary>
/// The no-network half of a URL import, shared by the proposal (which must not fetch anything) and the
/// import itself, so both judge "the same URL" and "the same target path" identically: the URL is an
/// absolute https URL without credentials whose host passes <see cref="GeneratedClipUrlGuard.EnsureAllowed"/>,
/// and the target is a sanitised file name (the caller's, else the URL path's last segment) under an
/// optional folder, with <c>..</c> refused.
/// </summary>
public static class UrlImportTarget
{
    /// <summary>The name used when neither the caller nor the URL path supplies one.</summary>
    public const string FallbackFileName = "imported-file";

    private const int MaxFileNameLength = 255;

    /// <summary>Null when <paramref name="url"/> may be fetched; otherwise the refusal. Never touches the network.</summary>
    public static UrlImportRefusal? ValidateUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed))
            return UrlImportRefusal.UrlRefused("The URL is not an absolute https URL.");

        try
        {
            GeneratedClipUrlGuard.EnsureAllowed(parsed.AbsoluteUri);
        }
        catch (InvalidOperationException ex)
        {
            return UrlImportRefusal.UrlRefused(ex.Message.Replace("Download URL", "The URL", StringComparison.Ordinal));
        }

        uri = parsed;
        return null;
    }

    /// <summary>
    /// The normalised folder, file name and project path the import writes to, or the refusal for a name or
    /// folder that is not a valid project path.
    /// </summary>
    public static UrlImportRefusal? ResolveTarget(Uri uri, string? fileName, string? directoryPath, out UrlImportPath? target)
    {
        ArgumentNullException.ThrowIfNull(uri);
        target = null;

        string name;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            name = fileName.Trim();
            if (name.IndexOfAny(['/', '\\']) >= 0 || name is "." or ".." || name.Any(char.IsControl) || name.Length > MaxFileNameLength)
                return UrlImportRefusal.InvalidPath("fileName must be a plain file name: no folders, no '..'.");
        }
        else
        {
            name = NameFromUrl(uri);
            if (name is "." or "..")
                return UrlImportRefusal.InvalidPath("The URL's last path segment is not a usable file name; pass fileName.");
        }

        try
        {
            string? directory = ProjectFilePath.NormalizeDirectoryPath(directoryPath);
            if (directory is not null && directory.Any(char.IsControl))
                return UrlImportRefusal.InvalidPath("directoryPath is not a valid project folder.");
            string normalizedName = ProjectFilePath.NormalizeRelativePath(name);
            if (normalizedName.Contains('/'))
                return UrlImportRefusal.InvalidPath("fileName must be a plain file name: no folders, no '..'.");
            target = new UrlImportPath(directory, normalizedName, ProjectFilePath.CombineDirectoryAndFileName(directory, normalizedName));
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return UrlImportRefusal.InvalidPath("fileName and directoryPath must be relative and must not contain '..'.");
        }
    }

    /// <summary>"scheme://host" — the only part of a user URL that may be logged or shown (paths and queries carry signatures).</summary>
    public static string Origin(Uri uri) => $"{uri.Scheme}://{uri.IdnHost}";

    private static string NameFromUrl(Uri uri)
    {
        string last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(last);
        }
        catch (UriFormatException)
        {
            decoded = last;
        }

        string cleaned = new string(decoded.Where(c => !char.IsControl(c)).Select(c => c is '/' or '\\' ? '_' : c).ToArray()).Trim();
        if (cleaned.Length > MaxFileNameLength)
        {
            string ext = Path.GetExtension(cleaned);
            if (ext.Length > 16) ext = string.Empty;
            cleaned = cleaned[..(MaxFileNameLength - ext.Length)] + ext;
        }

        return cleaned.Length == 0 ? FallbackFileName : cleaned;
    }
}

/// <summary>Where an import lands: the normalised folder (null = project root), the file name, and their join.</summary>
public sealed record UrlImportPath(string? DirectoryPath, string FileName, string OriginalPath);
