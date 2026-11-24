using System.Globalization;
using System.Text;

namespace ReelBolt.Shared.Editing.Color;

/// <summary>
/// A parsed <c>.cube</c> 3D LUT (P7). The parser is the trust boundary between a project file and
/// ffmpeg's <c>lut3d</c>: it accepts only the Adobe/IRIDAS text format, caps the size at
/// <see cref="MaxSize"/>, requires exactly <c>size³</c> entries, clamps every value into the domain
/// and REWRITES the table itself, so the file ffmpeg reads is always one this code produced — never
/// the user's bytes. Intensity is applied here too, by blending the table toward identity, because
/// a comma-joined layer chain cannot split a stream to blend a filtered copy.
/// </summary>
public sealed class CubeLut
{
    public const int MinSize = 2;
    public const int MaxSize = 65;
    public const int Min1DSize = 2;
    public const int Max1DSize = 65536;
    public const long MaxBytes = 16L * 1024 * 1024;

    /// <summary>The size of the 3D table a shaper + 3D LUT is baked into at intensity below 1.</summary>
    public const int BakeSize = 65;

    private readonly float[]? _table;  // 3D: size³ × 3, red fastest (null for a 1D-only LUT)
    private readonly float[]? _shaper; // 1D: shaperSize × 3, canonical over [0,1] (null for a 3D-only LUT)

    private CubeLut(CubeLutKind kind, int size, float[]? table, int shaperSize, float[]? shaper, string? title)
    {
        Kind = kind;
        Size = size;
        _table = table;
        ShaperSize = shaperSize;
        _shaper = shaper;
        Title = title;
    }

    public CubeLutKind Kind { get; }

    /// <summary>The size of the main table: the 3D edge for 3D and shaper + 3D, the entry count for 1D.</summary>
    public int Size { get; }

    /// <summary>The 1D shaper's entry count for shaper + 3D (0 otherwise).</summary>
    public int ShaperSize { get; }

    public string? Title { get; }

    /// <summary>The first line of every rewritten cube; never the file's own title.</summary>
    private const string CanonicalTitleLine = "TITLE \"ReelBolt\"\n";

    /// <summary>The kind of LUT that is rendered at <paramref name="intensity"/>: a shaper + 3D LUT below 1 is baked into one 3D table.</summary>
    public CubeLutKind KindAtIntensity(double intensity)
        => Kind == CubeLutKind.ShaperThreeD && Math.Clamp(double.IsFinite(intensity) ? intensity : 1, 0, 1) < 1 ? CubeLutKind.ThreeD : Kind;

    /// <summary>Parses the text of a <c>.cube</c> file. Throws <see cref="CubeLutException"/> on anything outside the format.</summary>
    public static CubeLut Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxBytes) throw TooLarge();

        int size3 = 0;
        int size1 = 0;
        string? title = null;
        double domainMin = 0;
        double domainMax = 1;
        bool hasDomain = false;
        string? domainKeyword = null;
        (double A, double B)? range1 = null;
        (double A, double B)? range3 = null;
        List<float> values = [];
        // No table can ever need more rows than the largest 1D plus the largest 3D; reject earlier than that
        // only once the declared sizes are known, but never let the row list outgrow this bound.
        long hardCap = Max1DSize + (long)MaxSize * MaxSize * MaxSize;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string head = tokens[0];
            if (head.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                title = line[5..].Trim().Trim('"');
                continue;
            }

            if (head.Equals("LUT_3D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryInt(tokens, out size3) || size3 < MinSize || size3 > MaxSize)
                    throw new CubeLutException("size_out_of_range", $"LUT_3D_SIZE must be an integer in [{MinSize}, {MaxSize}]", MinSize, MaxSize, "LUT_3D_SIZE");
                continue;
            }

            if (head.Equals("LUT_1D_SIZE", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryInt(tokens, out size1) || size1 < Min1DSize || size1 > Max1DSize)
                    throw new CubeLutException("size_out_of_range", $"LUT_1D_SIZE must be an integer in [{Min1DSize}, {Max1DSize}]", Min1DSize, Max1DSize, "LUT_1D_SIZE");
                continue;
            }

            if (head.Equals("DOMAIN_MIN", StringComparison.OrdinalIgnoreCase))
            {
                domainMin = ParseNumbers(tokens, 1, 3).Min();
                hasDomain = true;
                domainKeyword ??= "DOMAIN_MIN";
                continue;
            }

            if (head.Equals("DOMAIN_MAX", StringComparison.OrdinalIgnoreCase))
            {
                domainMax = ParseNumbers(tokens, 1, 3).Max();
                hasDomain = true;
                domainKeyword ??= "DOMAIN_MAX";
                continue;
            }

            if (head.Equals("LUT_1D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
            {
                double[] r = ParseNumbers(tokens, 1, 2);
                range1 = (r[0], r[1]);
                continue;
            }

            if (head.Equals("LUT_3D_INPUT_RANGE", StringComparison.OrdinalIgnoreCase))
            {
                double[] r = ParseNumbers(tokens, 1, 2);
                range3 = (r[0], r[1]);
                continue;
            }

            // A line that starts with a letter and is not a number word (nan/inf are data, refused later as not finite) is a keyword we do not know.
            if (char.IsLetter(line[0]) && !double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                string keyword = KeywordForMessage(head);
                throw new CubeLutException("unsupported_keyword", $"the keyword {keyword} is not supported", keyword: keyword);
            }

            double[] triple = ParseNumbers(tokens, 0, 3);
            if (values.Count >= hardCap * 3) throw new CubeLutException("wrong_row_count", "the LUT has more rows than any LUT size allows", hardCap, values.Count / 3 + 1);
            values.Add((float)triple[0]);
            values.Add((float)triple[1]);
            values.Add((float)triple[2]);
        }

        if (size3 == 0 && size1 == 0) throw new CubeLutException("size_missing", "LUT_3D_SIZE or LUT_1D_SIZE is missing");
        bool combined = size3 != 0 && size1 != 0;
        if (combined && hasDomain)
            throw new CubeLutException("unsupported_keyword", $"{domainKeyword} cannot be used in a file with both a 1D and a 3D table; use LUT_1D_INPUT_RANGE / LUT_3D_INPUT_RANGE", keyword: domainKeyword);

        long rows1 = size1;
        long rows3 = size3 == 0 ? 0 : (long)size3 * size3 * size3;
        long expectedRows = rows1 + rows3;
        long foundRows = values.Count / 3;
        if (foundRows != expectedRows)
        {
            string what = combined ? $"a {size1}-entry shaper plus a {size3}³ table" : size3 != 0 ? $"a {size3}³ LUT" : $"a {size1}-entry 1D LUT";
            throw new CubeLutException("wrong_row_count", $"{what} needs {expectedRows} entries, found {foundRows}", expectedRows, foundRows);
        }

        if (size3 != 0 && size1 == 0)
        {
            (double lo, double hi) = range3 ?? (domainMin, domainMax);
            if (!(hi > lo)) throw BadDomain(range3 is null ? "DOMAIN_MAX must exceed DOMAIN_MIN" : "LUT_3D_INPUT_RANGE must have its second value above its first");
            return new CubeLut(CubeLutKind.ThreeD, size3, Normalise(values, 0, rows3, lo, hi - lo), 0, null, title);
        }

        (double a, double b) = range1 ?? (domainMin, domainMax);
        if (!(b > a)) throw BadDomain(range1 is null ? "DOMAIN_MAX must exceed DOMAIN_MIN" : "LUT_1D_INPUT_RANGE must have its second value above its first");
        if (size3 == 0)
            return new CubeLut(CubeLutKind.OneD, size1, null, 0, Canonical1D(Normalise(values, 0, rows1, 0, 1), size1, a, b), title);

        (double c, double d) = range3 ?? (0, 1);
        if (!(d > c)) throw BadDomain("LUT_3D_INPUT_RANGE must have its second value above its first");
        // Shaper outputs land on 3D coordinates (s - c) / (d - c), clamped; then the input range is canonicalised away.
        float[] shaper = Normalise(values, 0, rows1, c, d - c);
        float[] cube = Normalise(values, rows1, rows3, 0, 1);
        return new CubeLut(CubeLutKind.ShaperThreeD, size3, cube, size1, Canonical1D(shaper, size1, a, b), title);
    }

    public static async Task<CubeLut> LoadAsync(string path, CancellationToken ct)
    {
        FileInfo info = new(path);
        if (!info.Exists) throw new FileNotFoundException("LUT file missing", path);
        if (info.Length > MaxBytes) throw TooLarge();
        return Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }

    /// <summary>The LUT blended toward identity: 1 is this LUT, 0 is a no-op. A shaper + 3D LUT below 1 comes back as one baked 65³ 3D LUT.</summary>
    public CubeLut WithIntensity(double intensity)
    {
        double k = Math.Clamp(double.IsFinite(intensity) ? intensity : 1, 0, 1);
        if (k >= 1) return this;
        switch (Kind)
        {
            case CubeLutKind.OneD:
                {
                    float[] blended = new float[_shaper!.Length];
                    int n = Size;
                    for (int i = 0; i < n; i++)
                    {
                        float ramp = (float)i / (n - 1);
                        for (int ch = 0; ch < 3; ch++)
                            blended[i * 3 + ch] = (float)(ramp + (_shaper[i * 3 + ch] - ramp) * k);
                    }

                    return new CubeLut(CubeLutKind.OneD, n, null, 0, blended, Title);
                }

            case CubeLutKind.ShaperThreeD:
                return Bake(k);
            default:
                {
                    // Entry by entry (not through the sampler), so a 3D LUT's blend is exactly what it always was.
                    float[] blended = new float[_table!.Length];
                    int n = Size;
                    int i = 0;
                    for (int b = 0; b < n; b++)
                    {
                        for (int g = 0; g < n; g++)
                        {
                            for (int r = 0; r < n; r++)
                            {
                                float ir = (float)r / (n - 1);
                                float ig = (float)g / (n - 1);
                                float ib = (float)b / (n - 1);
                                blended[i] = (float)(ir + (_table[i] - ir) * k);
                                blended[i + 1] = (float)(ig + (_table[i + 1] - ig) * k);
                                blended[i + 2] = (float)(ib + (_table[i + 2] - ib) * k);
                                i += 3;
                            }
                        }
                    }

                    return new CubeLut(CubeLutKind.ThreeD, n, blended, 0, null, Title);
                }
        }
    }

    /// <summary>The LUT in canonical <c>.cube</c> text: what ffmpeg reads (the 1D table for a 1D LUT, the 3D table otherwise). Deterministic for a given table.</summary>
    public string ToCubeText()
    {
        StringBuilder sb = new();
        // A constant title: ffmpeg reads .cube lines into a fixed 512-byte buffer, so a long user title would spill
        // into lines of its own (its own size and rows). Nothing from the user's file is written here but numbers.
        sb.Append(CanonicalTitleLine);
        float[] rows;
        if (Kind == CubeLutKind.OneD)
        {
            sb.Append("LUT_1D_SIZE ").Append(Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            rows = _shaper!;
        }
        else
        {
            sb.Append("LUT_3D_SIZE ").Append(Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            rows = _table!;
        }

        AppendRows(sb, rows);
        return sb.ToString();
    }

    /// <summary>The canonical <c>.cube</c> text of the 1D shaper stage of a shaper + 3D LUT (read by ffmpeg's <c>lut1d</c> before the <c>lut3d</c>).</summary>
    public string ToShaperCubeText()
    {
        if (_shaper is null || Kind != CubeLutKind.ShaperThreeD) throw new InvalidOperationException("this LUT has no separate shaper stage");
        StringBuilder sb = new();
        // A constant title: ffmpeg reads .cube lines into a fixed 512-byte buffer, so a long user title would spill
        // into lines of its own (its own size and rows). Nothing from the user's file is written here but numbers.
        sb.Append(CanonicalTitleLine);
        sb.Append("LUT_1D_SIZE ").Append(ShaperSize.ToString(CultureInfo.InvariantCulture)).Append('\n');
        AppendRows(sb, _shaper);
        return sb.ToString();
    }

    /// <summary>The mapped colour of one input (for tests and the shot-match stats): the 1D table linearly, the 3D table trilinearly.</summary>
    public (float R, float G, float B) Sample(float r, float g, float b)
    {
        if (Kind == CubeLutKind.OneD) return Shape(_shaper!, Size, r, g, b);
        if (Kind == CubeLutKind.ShaperThreeD)
        {
            (float sr, float sg, float sb) = Shape(_shaper!, ShaperSize, r, g, b);
            return Direct(sr, sg, sb);
        }

        return Direct(r, g, b);
    }

    private (float R, float G, float B) Direct(float r, float g, float b)
    {
        int n = Size;
        float[] table = _table!;
        float fr = Math.Clamp(r, 0, 1) * (n - 1);
        float fg = Math.Clamp(g, 0, 1) * (n - 1);
        float fb = Math.Clamp(b, 0, 1) * (n - 1);
        int r0 = (int)Math.Floor(fr), g0 = (int)Math.Floor(fg), b0 = (int)Math.Floor(fb);
        int r1 = Math.Min(n - 1, r0 + 1), g1 = Math.Min(n - 1, g0 + 1), b1 = Math.Min(n - 1, b0 + 1);
        float tr = fr - r0, tg = fg - g0, tb = fb - b0;
        float[] acc = new float[3];
        foreach ((int ri, float wr) in new[] { (r0, 1 - tr), (r1, tr) })
            foreach ((int gi, float wg) in new[] { (g0, 1 - tg), (g1, tg) })
                foreach ((int bi, float wb) in new[] { (b0, 1 - tb), (b1, tb) })
                {
                    float w = wr * wg * wb;
                    if (w == 0) continue;
                    int idx = ((bi * n + gi) * n + ri) * 3;
                    acc[0] += table[idx] * w;
                    acc[1] += table[idx + 1] * w;
                    acc[2] += table[idx + 2] * w;
                }

        return (acc[0], acc[1], acc[2]);
    }

    /// <summary>Tetrahedral interpolation exactly as ffmpeg's <c>lut3d</c> does it.</summary>
    private (float R, float G, float B) Tetrahedral(float r, float g, float b)
    {
        int n = Size;
        float[] t = _table!;
        float fr = Math.Clamp(r, 0, 1) * (n - 1);
        float fg = Math.Clamp(g, 0, 1) * (n - 1);
        float fb = Math.Clamp(b, 0, 1) * (n - 1);
        int r0 = (int)Math.Floor(fr), g0 = (int)Math.Floor(fg), b0 = (int)Math.Floor(fb);
        int r1 = Math.Min(n - 1, r0 + 1), g1 = Math.Min(n - 1, g0 + 1), b1 = Math.Min(n - 1, b0 + 1);
        float dr = fr - r0, dg = fg - g0, db = fb - b0;
        int At(int ri, int gi, int bi) => ((bi * n + gi) * n + ri) * 3;
        int c000 = At(r0, g0, b0), c111 = At(r1, g1, b1);
        int c100 = At(r1, g0, b0), c010 = At(r0, g1, b0), c001 = At(r0, g0, b1);
        int c110 = At(r1, g1, b0), c101 = At(r1, g0, b1), c011 = At(r0, g1, b1);
        int p1, p2;
        float s0, s1, s2, s3;
        if (dr > dg)
        {
            if (dg > db) { p1 = c100; p2 = c110; s0 = 1 - dr; s1 = dr - dg; s2 = dg - db; s3 = db; }
            else if (dr > db) { p1 = c100; p2 = c101; s0 = 1 - dr; s1 = dr - db; s2 = db - dg; s3 = dg; }
            else { p1 = c001; p2 = c101; s0 = 1 - db; s1 = db - dr; s2 = dr - dg; s3 = dg; }
        }
        else
        {
            if (db > dg) { p1 = c001; p2 = c011; s0 = 1 - db; s1 = db - dg; s2 = dg - dr; s3 = dr; }
            else if (db > dr) { p1 = c010; p2 = c011; s0 = 1 - dg; s1 = dg - db; s2 = db - dr; s3 = dr; }
            else { p1 = c010; p2 = c110; s0 = 1 - dg; s1 = dg - dr; s2 = dr - db; s3 = db; }
        }

        float Mix(int off) => (t[c000 + off] * s0) + (t[p1 + off] * s1) + (t[p2 + off] * s2) + (t[c111 + off] * s3);
        return (Mix(0), Mix(1), Mix(2));
    }

    /// <summary>The shaper and 3D table composed into one <see cref="BakeSize"/>³ table (the 3D leg sampled tetrahedrally, as ffmpeg does), blended toward identity by <paramref name="k"/>.</summary>
    private CubeLut Bake(double k)
    {
        float[] baked = BlendTowardIdentity(BakeSize, (r, g, b) =>
        {
            (float sr, float sg, float sb) = Shape(_shaper!, ShaperSize, r, g, b);
            return Tetrahedral(sr, sg, sb);
        }, k);
        return new CubeLut(CubeLutKind.ThreeD, BakeSize, baked, 0, null, Title);
    }

    private static float[] BlendTowardIdentity(int n, Func<float, float, float, (float R, float G, float B)> map, double k)
    {
        float[] blended = new float[(long)n * n * n * 3];
        int i = 0;
        for (int b = 0; b < n; b++)
        {
            for (int g = 0; g < n; g++)
            {
                for (int r = 0; r < n; r++)
                {
                    float ir = (float)r / (n - 1);
                    float ig = (float)g / (n - 1);
                    float ib = (float)b / (n - 1);
                    (float mr, float mg, float mb) = map(ir, ig, ib);
                    blended[i] = (float)(ir + (mr - ir) * k);
                    blended[i + 1] = (float)(ig + (mg - ig) * k);
                    blended[i + 2] = (float)(ib + (mb - ib) * k);
                    i += 3;
                }
            }
        }

        return blended;
    }

    private static (float R, float G, float B) Shape(float[] canon, int n, float r, float g, float b)
        => (Eval1D(canon, n, 0, r), Eval1D(canon, n, 1, g), Eval1D(canon, n, 2, b));

    private static float Eval1D(float[] canon, int n, int channel, float x)
    {
        float t = Math.Clamp(x, 0, 1) * (n - 1);
        int i0 = (int)Math.Floor(t);
        int i1 = Math.Min(n - 1, i0 + 1);
        float f = t - i0;
        float v0 = canon[i0 * 3 + channel];
        return v0 + ((canon[i1 * 3 + channel] - v0) * f);
    }

    /// <summary>Entries over [0,1]: <c>canon[i] = eval(i/(N-1))</c> with the input range [a,b] mapped onto the table. Verbatim when a == 0 and b == 1.</summary>
    private static float[] Canonical1D(float[] parsed, int n, double a, double b)
    {
        if (a == 0 && b == 1) return parsed;
        float[] canon = new float[parsed.Length];
        for (int i = 0; i < n; i++)
        {
            double x = (double)i / (n - 1);
            double t = Math.Clamp((x - a) / (b - a), 0, 1) * (n - 1);
            int i0 = (int)Math.Floor(t);
            int i1 = Math.Min(n - 1, i0 + 1);
            double f = t - i0;
            for (int ch = 0; ch < 3; ch++)
            {
                double v0 = parsed[i0 * 3 + ch];
                canon[i * 3 + ch] = (float)(v0 + ((parsed[i1 * 3 + ch] - v0) * f));
            }
        }

        return canon;
    }

    private static float[] Normalise(List<float> values, long firstRow, long rows, double min, double span)
    {
        float[] table = new float[rows * 3];
        long offset = firstRow * 3;
        for (long i = 0; i < table.Length; i++)
        {
            double normalised = (values[(int)(offset + i)] - min) / span;
            table[i] = (float)Math.Clamp(double.IsFinite(normalised) ? normalised : 0, 0, 1);
        }

        return table;
    }

    private static void AppendRows(StringBuilder sb, float[] rows)
    {
        sb.Append("DOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1\n");
        for (int i = 0; i < rows.Length; i += 3)
        {
            sb.Append(rows[i].ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
              .Append(rows[i + 1].ToString("0.######", CultureInfo.InvariantCulture)).Append(' ')
              .Append(rows[i + 2].ToString("0.######", CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    private static bool TryInt(string[] tokens, out int value)
    {
        value = 0;
        return tokens.Length == 2 && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static CubeLutException TooLarge() => new("too_large", $"the LUT is larger than {MaxBytes} bytes", MaxBytes);

    private static CubeLutException BadDomain(string message) => new("bad_domain", message);

    private static double[] ParseNumbers(string[] tokens, int skip, int count)
    {
        if (tokens.Length - skip != count)
            throw new CubeLutException("malformed", $"expected {count} numbers, found {tokens.Length - skip}");
        double[] result = new double[count];
        for (int i = 0; i < count; i++)
        {
            string token = tokens[skip + i];
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                throw new CubeLutException("malformed", $"'{Echo(token)}' is not a number");
            if (!float.IsFinite(parsed))
                throw new CubeLutException("not_finite", $"'{Echo(token)}' is not a finite number");
            result[i] = parsed;
        }

        return result;
    }

    /// <summary>The unknown keyword as it may be echoed (message, step output, logs): letters, digits and '_' only, at most 40 characters.</summary>
    private static string KeywordForMessage(string head)
    {
        string clean = new(head.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray());
        return clean.Length == 0 ? "?" : clean;
    }

    /// <summary>A token from the file as it may be echoed in a message: printable ASCII only, at most 40 characters.</summary>
    private static string Echo(string token)
    {
        string clean = new(token.Where(c => c is >= ' ' and <= '~').Take(40).ToArray());
        return token.Length > 40 ? clean + "…" : clean;
    }
}

/// <summary>Which tables a <c>.cube</c> file holds.</summary>
public enum CubeLutKind
{
    ThreeD,
    OneD,
    ShaperThreeD,
}

/// <summary>A <c>.cube</c> file the parser refuses. <see cref="Code"/> is a stable lowercase reason the web localises.</summary>
public sealed class CubeLutException : FormatException
{
    public CubeLutException(string code, string message, long? expected = null, long? found = null, string? keyword = null)
        : base(message)
    {
        Code = code;
        Expected = expected;
        Found = found;
        Keyword = keyword;
    }

    public string Code { get; }

    public long? Expected { get; }

    public long? Found { get; }

    public string? Keyword { get; }

    /// <summary>True for the reasons that mean "valid file, outside what we support" rather than "broken file".</summary>
    public bool IsUnsupported => Code is "too_large" or "size_out_of_range" or "unsupported_keyword";
}
