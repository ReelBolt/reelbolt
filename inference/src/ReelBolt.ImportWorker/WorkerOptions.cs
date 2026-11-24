namespace ReelBolt.ImportWorker;

/// <summary>
/// The worker's whole configuration surface. Everything is a bounded constant except the shared
/// secret; there is no database, no storage and no provider key anywhere in this service.
/// </summary>
public sealed record WorkerOptions(
    string Token,
    long MaxBodyBytes = 33554432,
    long PdfMaxBytes = 20971520,
    int PdfTimeoutSeconds = 20,
    int PdfTextMaxBytes = 204800,
    long ZipInlineFileMaxBytes = 2097152,
    long ZipInlineTotalMaxBytes = 25165824,
    int MaxConcurrency = 2,
    int PdfMaxPages = 50)
{
    /// <summary>
    /// A record prints every member, which would put the bearer secret in any log line that formats the
    /// options. The token is deliberately left out.
    /// </summary>
    public override string ToString()
        => $"WorkerOptions {{ Token = <redacted>, MaxBodyBytes = {MaxBodyBytes}, PdfMaxBytes = {PdfMaxBytes}, "
            + $"PdfTimeoutSeconds = {PdfTimeoutSeconds}, PdfTextMaxBytes = {PdfTextMaxBytes}, "
            + $"ZipInlineFileMaxBytes = {ZipInlineFileMaxBytes}, ZipInlineTotalMaxBytes = {ZipInlineTotalMaxBytes}, "
            + $"MaxConcurrency = {MaxConcurrency}, PdfMaxPages = {PdfMaxPages} }}";
}
