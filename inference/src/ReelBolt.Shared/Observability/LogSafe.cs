namespace ReelBolt.Shared.Observability;

/// <summary>
/// Neutralises an untrusted string before it reaches a log call (CWE-117, log forging). A structured logger keeps
/// the value in its own field, but a text sink (console, file) prints it inline, where an embedded CR or LF would
/// forge a second log line.
/// </summary>
public static class LogSafe
{
    /// <summary>The longest value kept; anything beyond is cut and marked, so a hostile value cannot flood a log line.</summary>
    public const int MaxLength = 512;

    /// <summary>
    /// Returns <paramref name="value"/> with CR and LF removed, every other control character dropped, and the
    /// length bounded. <see langword="null"/> stays <see langword="null"/>.
    /// </summary>
    public static string? Value(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // The line-break removal is deliberately the string.Replace pair, the form static analysis recognises as a
        // log-forging barrier; the control-character and length steps below build on its result.
        string noBreaks = value.Replace("\r", string.Empty).Replace("\n", string.Empty);

        var buffer = new System.Text.StringBuilder(Math.Min(noBreaks.Length, MaxLength + 1));
        foreach (char c in noBreaks)
        {
            if (!char.IsControl(c)
                && System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is not (System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.Format))
            {
                buffer.Append(c);
            }
        }

        if (buffer.Length > MaxLength)
        {
            buffer.Length = MaxLength;
            buffer.Append('…');
        }

        return buffer.ToString();
    }

    /// <summary>Extension form of <see cref="Value(string?)"/>, for use inside a log call's argument list.</summary>
    public static string? ForLog(this string? value) => Value(value);
}
