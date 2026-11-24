using System.Globalization;
using System.Text;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One filmstrip sprite sheet as MEASURED from a real run: the leaf name it will occupy in the
/// object store, how many thumbnails it actually decoded, and — filled in by
/// <see cref="FilmstripIndex.Plan"/> — where it sits in the flat thumbnail sequence.
/// </summary>
/// <param name="StorageLeaf">Relative object leaf, e.g. <c>filmstrip-&lt;paramsHash16&gt;-sheet-001.jpg</c>.</param>
/// <param name="FirstThumbIndex">Index of this sheet's first thumbnail in the whole filmstrip, 0-based.</param>
/// <param name="ThumbCount">Thumbnails this sheet actually carries (the last sheet is usually partial).</param>
/// <param name="FirstTimeSec">Source time of thumbnail <paramref name="FirstThumbIndex"/>.</param>
public sealed record FilmstripSheet(
    string StorageLeaf,
    int FirstThumbIndex,
    int ThumbCount,
    double FirstTimeSec);

/// <summary>A sheet's measured frame count, before its position in the sequence is known.</summary>
public sealed record FilmstripSheetFrames(string StorageLeaf, int ThumbCount);

/// <summary>
/// Builds the filmstrip JSON sidecar the editor reads to map a hover position to a sprite cell.
/// </summary>
/// <remarks>
/// <para>
/// <b>The index is built from the measured decoded frame count, never from
/// <c>containerDuration / densitySec</c>.</b> Those two disagree: the <c>fps</c> filter's
/// decimation and the container's declared duration round differently, and a rounded-up sheet
/// count is exactly the off-by-one that makes hover-scrub land on the wrong thumbnail — or on a
/// sheet that does not exist. So the service counts the sheet files ffmpeg actually wrote and
/// asks ffprobe for each one's real frame count, and this type only arranges those numbers.
/// </para>
/// <para>
/// Pure and I/O-free, so the arithmetic is unit-testable without an ffmpeg process.
/// </para>
/// </remarks>
public static class FilmstripIndex
{
    public const int Cols = 10;
    public const int Rows = 10;

    /// <summary>Thumbnails in one full sheet — the <c>tile=10x10</c> area.</summary>
    public const int ThumbsPerSheet = Cols * Rows;

    /// <summary>
    /// Assigns each measured sheet its position in the flat thumbnail sequence:
    /// <c>firstThumbIndex</c> is the running sum of the preceding sheets' counts and
    /// <c>firstTimeSec</c> is <c>firstThumbIndex * densitySec</c> (thumbnail <i>i</i> is the frame
    /// ffmpeg decimated at source time <c>i * densitySec</c>).
    /// </summary>
    public static IReadOnlyList<FilmstripSheet> Plan(int densitySec, IReadOnlyList<FilmstripSheetFrames> measuredSheets)
    {
        ArgumentNullException.ThrowIfNull(measuredSheets);

        int density = Math.Max(1, densitySec);
        var planned = new List<FilmstripSheet>(measuredSheets.Count);
        int running = 0;

        foreach (FilmstripSheetFrames measured in measuredSheets)
        {
            int count = Math.Max(0, measured.ThumbCount);
            planned.Add(new FilmstripSheet(
                measured.StorageLeaf,
                running,
                count,
                running * (double)density));
            running += count;
        }

        return planned;
    }

    /// <summary>
    /// The sidecar document. Property order is the documented shape, not alphabetical: this is a
    /// consumed document, not a hashed parameter set (the canonical-ordering rule belongs to
    /// <see cref="MediaDerivativeSpec.ParamsJson"/>), so it stays readable.
    /// </summary>
    public static string Build(
        int densitySec,
        int thumbWidth,
        int thumbHeight,
        IReadOnlyList<FilmstripSheet> sheets)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        var builder = new StringBuilder();
        builder.Append('{');
        builder.Append("\"densitySec\":").Append(Number(Math.Max(1, densitySec))).Append(',');
        builder.Append("\"thumbWidth\":").Append(Number(Math.Max(1, thumbWidth))).Append(',');
        builder.Append("\"thumbHeight\":").Append(Number(Math.Max(1, thumbHeight))).Append(',');
        builder.Append("\"cols\":").Append(Number(Cols)).Append(',');
        builder.Append("\"rows\":").Append(Number(Rows)).Append(',');
        builder.Append("\"sheetCount\":").Append(Number(sheets.Count)).Append(',');
        builder.Append("\"sheets\":[");

        for (int i = 0; i < sheets.Count; i++)
        {
            if (i > 0)
                builder.Append(',');

            FilmstripSheet sheet = sheets[i];
            builder.Append('{');
            builder.Append("\"storageLeaf\":\"").Append(Escape(sheet.StorageLeaf)).Append("\",");
            builder.Append("\"firstThumbIndex\":").Append(Number(sheet.FirstThumbIndex)).Append(',');
            builder.Append("\"thumbCount\":").Append(Number(sheet.ThumbCount)).Append(',');
            builder.Append("\"firstTimeSec\":").Append(Number(sheet.FirstTimeSec));
            builder.Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    /// <summary>
    /// The object leaf for sheet <paramref name="sheetIndex"/> (1-based, matching ffmpeg's
    /// <c>%03d</c> expansion). Carries the params hash so two filmstrip densities of one source
    /// land as sibling objects rather than overwriting each other.
    /// </summary>
    public static string SheetLeaf(string paramsHash, int sheetIndex) =>
        $"filmstrip-{paramsHash}-sheet-{sheetIndex.ToString("D3", CultureInfo.InvariantCulture)}.jpg";

    /// <summary>The object leaf of the sidecar itself.</summary>
    public static string IndexLeaf(string paramsHash) => $"filmstrip-{paramsHash}-index.json";

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
