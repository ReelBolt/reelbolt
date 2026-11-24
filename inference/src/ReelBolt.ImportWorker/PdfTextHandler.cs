using System.Text;

namespace ReelBolt.ImportWorker;

/// <summary>
/// Turns PDF bytes into text through an <see cref="IPdfTextRunner"/>. It bounds the body, insists on
/// a <c>%PDF-</c> header in the first KiB, writes the bytes to a uniquely named temp file and removes
/// that file in <c>finally</c> whatever the runner does.
/// </summary>
public static class PdfTextHandler
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();

    public static async Task<WorkerResult> HandleAsync(
        Stream body,
        long? contentLength,
        WorkerOptions options,
        IPdfTextRunner runner,
        CancellationToken ct)
    {
        if (contentLength.HasValue && contentLength.Value > options.PdfMaxBytes)
            return new WorkerResult(413, null);

        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await body.ReadAsync(chunk.AsMemory(0, chunk.Length), ct);
            if (read <= 0)
                break;

            total += read;
            if (total > options.PdfMaxBytes)
                return new WorkerResult(413, null);

            buffer.Write(chunk, 0, read);
        }

        byte[] bytes = buffer.ToArray();
        int limit = Math.Min(bytes.Length, 1024);
        if (bytes.AsSpan(0, limit).IndexOf(PdfMagic) < 0)
            return new WorkerResult(415, null);

        string path = Path.Combine(Path.GetTempPath(), $"import-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(path, bytes, ct);

            PdfTextRunResult result = await runner.RunAsync(path, options, ct);
            if (!result.Ok)
                return new WorkerResult(422, new ErrorResponse("PDF_UNREADABLE"));

            return new WorkerResult(200, new PdfTextResponse(
                result.Text ?? string.Empty,
                result.Truncated,
                result.TotalPages,
                result.TotalPages is { } pageTotal ? Math.Min(pageTotal, options.PdfMaxPages) : null));
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort: a racing reader does not make the extraction fail.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup only.
            }
        }
    }
}
