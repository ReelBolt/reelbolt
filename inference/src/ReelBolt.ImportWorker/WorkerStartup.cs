namespace ReelBolt.ImportWorker;

/// <summary>
/// Startup guards that run before the web host is built, so the one secret this service holds can
/// never be absent at runtime: a missing token throws and the process exits non-zero.
/// </summary>
public static class WorkerStartup
{
    public static string RequireToken(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("IMPORT_WORKER_TOKEN is required")
            : value;

    /// <summary>
    /// Reads the optional tunables. Anything unset, unparsable or out of range falls back to the shipped
    /// default; nothing here can disable a limit.
    /// </summary>
    public static WorkerOptions OptionsFromEnvironment(string token, Func<string, string?> getVariable)
    {
        WorkerOptions defaults = new(token);
        return defaults with
        {
            MaxConcurrency = ReadInt(getVariable("IMPORT_WORKER_MAX_CONCURRENCY"), defaults.MaxConcurrency, 1, 16),
            PdfMaxPages = ReadInt(getVariable("IMPORT_WORKER_PDF_MAX_PAGES"), defaults.PdfMaxPages, 1, 500),
        };
    }

    private static int ReadInt(string? raw, int fallback, int min, int max)
        => int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value >= min && value <= max
                ? value
                : fallback;
}
