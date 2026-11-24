using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Archives;

/// <summary>
/// Pure helpers for unpacking: which rows are zips, the target folder's name, how an entry's path becomes a stored
/// path, and which MIME an unpacked file is stored under. Every stored path goes through
/// <see cref="ProjectFilePath.NormalizeRelativePath"/> and the same name rules uploads use; nothing the worker
/// sends is trusted beyond the validated manifest.
/// </summary>
public static class ArchiveUnpackPaths
{
    /// <summary>Longest stored path (target folder included) an unpacked file may have.</summary>
    public const int MaxStoredPathLength = 2000;

    private const int MaxFolderNameLength = 200;
    private const int MaxSegmentLength = 255;

    private static readonly HashSet<string> ZipMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/zip", "application/x-zip-compressed", "application/x-zip"
    };

    /// <summary>A user-uploaded row that is a zip: a <c>.zip</c> name or a zip MIME, in the user files, not synced.</summary>
    public static bool IsZipFile(ProjectFile file) =>
        file.SourceKind is null
        && string.Equals(file.Category, "userFiles", StringComparison.Ordinal)
        && (string.Equals(Path.GetExtension(file.OriginalFileName), ".zip", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(file.MimeType) && ZipMimeTypes.Contains(file.MimeType)));

    /// <summary>The folder name for a zip: its base name without separators, control characters or dots at the ends.</summary>
    public static string SanitizeFolderName(string? archiveName)
    {
        string name = Path.GetFileNameWithoutExtension((archiveName ?? string.Empty).Replace('\\', '/').Split('/').Last());
        char[] kept = [.. name.Where(c => !char.IsControl(c) && c != '/' && c != '\\')];
        string cleaned = new string(kept).Trim().Trim('.').Trim();
        if (cleaned.Length > MaxFolderNameLength) cleaned = cleaned[..MaxFolderNameLength].TrimEnd();
        return cleaned.Length == 0 ? "archive" : cleaned;
    }

    /// <summary>Joins an optional parent directory and a folder name into a normalized directory path.</summary>
    public static string CombineDirectory(string? parent, string folderName) =>
        string.IsNullOrWhiteSpace(parent) ? folderName : $"{parent.TrimEnd('/')}/{folderName}";

    /// <summary>
    /// The one top-level folder every path shares, or null: only when each path has at least two segments and they all
    /// start with the same one.
    /// </summary>
    public static string? SharedTopFolder(IReadOnlyList<string> manifestPaths)
    {
        if (manifestPaths.Count == 0) return null;
        string? first = null;
        foreach (string path in manifestPaths)
        {
            string[] segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2) return null;
            first ??= segments[0];
            if (!string.Equals(first, segments[0], StringComparison.Ordinal)) return null;
        }

        return first;
    }

    /// <summary>
    /// The stored path of one entry under <paramref name="targetDirectory"/>, or false when the path fails the upload
    /// name rules (traversal, empty, over-long, control characters) and the entry must be skipped.
    /// </summary>
    public static bool TryBuildPath(string targetDirectory, string relativePath, string? stripFolder, out string path)
    {
        path = string.Empty;
        string rel = relativePath.Replace('\\', '/');
        if (stripFolder is not null)
        {
            string[] parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !string.Equals(parts[0], stripFolder, StringComparison.Ordinal)) return false;
            rel = string.Join('/', parts.Skip(1));
        }

        try
        {
            string normalized = ProjectFilePath.NormalizeRelativePath(string.IsNullOrEmpty(targetDirectory) ? rel : $"{targetDirectory}/{rel}");
            if (normalized.Length > MaxStoredPathLength) return false;
            if (targetDirectory.Length > 0 && !normalized.StartsWith(targetDirectory + "/", StringComparison.Ordinal)) return false;
            foreach (string segment in normalized.Split('/'))
            {
                if (segment.Length == 0 || segment.Length > MaxSegmentLength || segment is "." or ".." || segment.Any(char.IsControl))
                    return false;
            }

            path = normalized;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static readonly Dictionary<string, string> ExtraMimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images and documents the upload path would take from the browser's declared type.
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".bmp"] = "image/bmp",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".pdf"] = "application/pdf",
        [".wasm"] = "application/wasm",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".opus"] = "audio/ogg",
        [".oga"] = "audio/ogg",
        [".wmv"] = "video/x-ms-wmv",
        [".flv"] = "video/x-flv",
        // Source and config files the shared text policy does not list: they must still count as code.
        [".json5"] = "application/json",
        [".jsonc"] = "application/json",
        [".map"] = "application/json",
        [".webmanifest"] = "application/json",
        [".lock"] = "text/plain",
        [".example"] = "text/plain",
        [".env"] = "text/plain",
        [".gitignore"] = "text/plain",
        [".gitattributes"] = "text/plain",
        [".npmrc"] = "text/plain",
        [".nvmrc"] = "text/plain",
        [".prettierrc"] = "text/plain",
        [".eslintrc"] = "text/plain",
        [".gradle"] = "text/plain",
        [".kts"] = "text/plain",
        [".scala"] = "text/plain",
        [".groovy"] = "text/plain",
        [".c"] = "text/plain",
        [".h"] = "text/plain",
        [".cc"] = "text/plain",
        [".cpp"] = "text/plain",
        [".hpp"] = "text/plain",
        [".m"] = "text/plain",
        [".mm"] = "text/plain",
        [".pl"] = "text/plain",
        [".ex"] = "text/plain",
        [".exs"] = "text/plain",
        [".erl"] = "text/plain",
        [".hs"] = "text/plain",
        [".clj"] = "text/plain",
        [".elm"] = "text/plain",
        [".tf"] = "text/plain",
        [".graphql"] = "text/plain",
        [".gql"] = "text/plain",
        [".proto"] = "text/plain",
        [".cfg"] = "text/plain",
        [".conf"] = "text/plain",
        [".properties"] = "text/plain",
        [".mk"] = "text/plain",
        [".cmake"] = "text/plain",
        [".bat"] = "text/plain",
        [".cmd"] = "text/plain",
        [".bash"] = "text/plain",
        [".zsh"] = "text/plain",
        [".fish"] = "text/plain",
        [".tsv"] = "text/plain",
        [".rst"] = "text/plain",
        [".tex"] = "text/plain",
        [".log"] = "text/plain",
        [".xaml"] = "application/xml",
        [".plist"] = "application/xml",
        [".xsl"] = "application/xml",
        [".xslt"] = "application/xml",
        [".htm"] = "text/html",
        [".jsonl"] = "text/plain",
        [".ipynb"] = "application/json",
    };

    private static readonly HashSet<string> ExtensionlessTextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Makefile", "Procfile", "Gemfile", "Rakefile", "Brewfile", "Jenkinsfile", "Vagrantfile",
        "LICENSE", "LICENCE", "README", "CHANGELOG", "CODEOWNERS", "AUTHORS", "NOTICE", "CNAME",
    };

    /// <summary>
    /// The MIME to store an unpacked file under, from its name: media from the upload extension map, text and code from
    /// the shared text policy plus a supplement of common source extensions, images and documents by extension, else
    /// <c>application/octet-stream</c>. Code files resolve to <c>text/*</c> or a json/xml type so they count as code.
    /// </summary>
    public static string ResolveMimeType(string fileName)
    {
        string? mime = DirectUploadService.ResolveContentType(null, fileName);
        if (!string.IsNullOrEmpty(mime) && !string.Equals(mime, "application/octet-stream", StringComparison.OrdinalIgnoreCase))
            return mime;

        string ext = Path.GetExtension(fileName);
        if (ext.Length > 0 && ExtraMimeTypes.TryGetValue(ext, out string? extra)) return extra;
        if (ext.Length == 0 && ExtensionlessTextNames.Contains(fileName)) return "text/plain";
        return "application/octet-stream";
    }

    /// <summary>Whether <paramref name="mime"/> says the file is media that needs its derivative set.</summary>
    public static bool IsMedia(string? mime) => UploadDerivativeRequest.IsMediaMimeType(mime);
}
