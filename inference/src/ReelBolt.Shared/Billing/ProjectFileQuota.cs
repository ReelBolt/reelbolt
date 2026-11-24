using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Billing;

/// <summary>
/// The single source of which project-file bytes count against the plan's storage quota. Built-in library
/// sounds copied into a project are free of quota, so every growth or freed-bytes computation that takes an
/// existing row's size into account uses <see cref="CountedSize"/> rather than <c>SizeBytes</c>.
/// </summary>
public static class ProjectFileQuota
{
    /// <summary>The <c>ProjectFile.SourceKind</c> of a built-in library sound added to a project (quota-exempt).</summary>
    public const string SfxLibrarySourceKind = "sfx_library";

    /// <summary>The bytes this row contributes to the organization's used storage (0 for a library sound).</summary>
    public static long CountedSize(ProjectFile? file) =>
        file is null || file.SourceKind == SfxLibrarySourceKind ? 0 : file.SizeBytes;
}
