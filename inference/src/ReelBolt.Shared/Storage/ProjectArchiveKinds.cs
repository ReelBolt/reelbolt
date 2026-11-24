using System.Linq.Expressions;
using System.Reflection;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Storage;

/// <summary>
/// The one test for "this upload is an archive" (zip, tar, gzip, 7z, rar, bzip2, xz, zstd), by MIME type or file
/// extension. Archives are not readable code: the readiness checks and the engine's unpacking share this rule.
/// </summary>
public static class ProjectArchiveKinds
{
    private static readonly HashSet<string> Mimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/zip", "application/x-zip-compressed", "application/gzip", "application/x-gzip",
        "application/x-tar", "application/x-7z-compressed", "application/vnd.rar", "application/x-bzip2",
        "application/x-xz", "application/zstd", "application/x-zstd"
    };

    private static readonly string[] Extensions = [".zip", ".tar", ".gz", ".tgz", ".7z", ".rar", ".bz2", ".xz", ".zst"];

    /// <summary>Whether the MIME type or the file name's extension is an archive kind.</summary>
    public static bool IsArchive(string? mimeType, string? fileName)
    {
        if (!string.IsNullOrWhiteSpace(mimeType) && Mimes.Contains(mimeType.Split(';')[0].Trim()))
            return true;
        return !string.IsNullOrWhiteSpace(fileName)
            && Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The same test as <see cref="IsArchive"/>, as a query predicate over a project file (MIME type or extension).</summary>
    public static Expression<Func<ProjectFile, bool>> ArchivePredicate { get; } = BuildPredicate();

    /// <summary>Only the project files that are archives, translated to SQL.</summary>
    public static IQueryable<ProjectFile> WhereArchive(this IQueryable<ProjectFile> files) => files.Where(ArchivePredicate);

    /// <summary>Only the project files that are not archives.</summary>
    public static IQueryable<ProjectFile> WhereNotArchive(this IQueryable<ProjectFile> files)
    {
        Expression<Func<ProjectFile, bool>> p = ArchivePredicate;
        return files.Where(Expression.Lambda<Func<ProjectFile, bool>>(Expression.Not(p.Body), p.Parameters));
    }

    private static Expression<Func<ProjectFile, bool>> BuildPredicate()
    {
        ParameterExpression file = Expression.Parameter(typeof(ProjectFile), "f");
        MethodInfo toLower = typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
        MethodInfo endsWith = typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;
        Expression mime = Expression.Call(Expression.Property(file, nameof(ProjectFile.MimeType)), toLower);
        Expression name = Expression.Call(Expression.Property(file, nameof(ProjectFile.OriginalFileName)), toLower);

        Expression? body = null;
        foreach (string m in Mimes)
        {
            Expression test = Expression.Equal(mime, Expression.Constant(m.ToLowerInvariant()));
            body = body is null ? test : Expression.OrElse(body, test);
        }

        foreach (string e in Extensions)
            body = Expression.OrElse(body!, Expression.Call(name, endsWith, Expression.Constant(e)));
        return Expression.Lambda<Func<ProjectFile, bool>>(body!, file);
    }
}
