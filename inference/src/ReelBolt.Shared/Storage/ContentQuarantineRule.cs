namespace ReelBolt.Shared.Storage;

/// <summary>
/// The pure part of the project quarantine marker rule, shared by the API (which writes the marker) and the engine
/// (which reads it), so the margin lives in one place. Also names how engine-made outputs are told apart in
/// <c>project_files</c>.
/// </summary>
public static class ContentQuarantineRule
{
    /// <summary>
    /// How far after the marker a timestamp still counts as before it. The marker is written by the API and output
    /// timestamps by the engine, each on its own host's clock, so a small skew must not let an old output through.
    /// </summary>
    public static readonly TimeSpan ClockSkewMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when earlier output may predate the marker: any of <paramref name="stamps"/> is missing, or at or before the
    /// marker plus <see cref="ClockSkewMargin"/>. No marker, nothing predates it.
    /// </summary>
    public static bool Predates(DateTime? marker, params DateTime?[] stamps) =>
        marker is { } value && (stamps.Length == 0 || stamps.Any(s => s is null || s.Value <= value + ClockSkewMargin));

    /// <summary>
    /// <see cref="ProjectFileSourceKindOutputCopy"/>: the mark an assistant save puts on a copy of an engine output it
    /// places under <c>userFiles</c>. No sync path treats it as a source: github, cloud and folder sync compare
    /// <c>SourceKind</c> to their own value only.
    /// </summary>
    public const string ProjectFileSourceKindOutputCopy = "output_copy";

    /// <summary>The category engine-registered renders carry.</summary>
    public const string OutputCategory = "outputFiles";

    /// <summary>
    /// Fragment of a generated take's storage key (<c>projects/{id}/agentFiles/generated/</c>): VideoGenerate takes are
    /// registered in place with Category <c>agentFiles</c>, not <c>outputFiles</c>, so the key names them.
    /// </summary>
    public const string GeneratedKeyFragment = "/agentFiles/generated/";
}
