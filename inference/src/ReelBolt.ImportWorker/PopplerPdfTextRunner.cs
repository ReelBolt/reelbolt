using System.Diagnostics;
using System.Text;

namespace ReelBolt.ImportWorker;

/// <summary>One attempt at extracting text from a PDF file on disk.</summary>
/// <param name="TotalPages">The document's page count when pdfinfo could tell; null when unknown.</param>
public sealed record PdfTextRunResult(bool Ok, string? Text, bool Truncated, string? ErrorCode, int? TotalPages = null);

/// <summary>
/// The seam between the endpoint and the real extractor. Tests supply a fake so the no-web-host
/// handler can be exercised without poppler installed.
/// </summary>
public interface IPdfTextRunner
{
    Task<PdfTextRunResult> RunAsync(string path, WorkerOptions options, CancellationToken ct);
}

/// <summary>
/// Runs the poppler CLI as an argument list (never a shell) with an almost empty environment, a
/// hard timeout and a capped stdout. The process is always killed on the way out.
/// </summary>
public sealed class PopplerPdfTextRunner : IPdfTextRunner
{
    private readonly string _executable;
    private readonly string _infoExecutable;

    /// <param name="executable">The program to run; tests point this at a fake script.</param>
    /// <param name="infoExecutable">The page-count program (poppler's pdfinfo); a failure only leaves the page count unknown.</param>
    public PopplerPdfTextRunner(string executable = "pdftotext", string infoExecutable = "pdfinfo")
    {
        _executable = executable;
        _infoExecutable = infoExecutable;
    }

    public async Task<PdfTextRunResult> RunAsync(string path, WorkerOptions options, CancellationToken ct)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // -l caps the pages parsed: a 20 MiB PDF can hold thousands of pages and the timeout alone is a long time.
        foreach (string argument in new[] { "-nopgbrk", "-enc", "UTF-8", "-l", options.PdfMaxPages.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-");

        // pdftotext needs nothing but a PATH to be found; everything else is deliberately absent.
        startInfo.Environment.Clear();
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(pathVariable)
            ? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
            : pathVariable;

        using Process process = new() { StartInfo = startInfo };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.PdfTimeoutSeconds));

        try
        {
            if (!process.Start())
                return Failure();

            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);

            using MemoryStream output = new();
            byte[] chunk = new byte[8192];
            bool truncated = false;

            while (true)
            {
                int read = await process.StandardOutput.BaseStream.ReadAsync(chunk.AsMemory(0, chunk.Length), timeout.Token);
                if (read <= 0)
                    break;

                if (output.Length + read > options.PdfTextMaxBytes)
                {
                    long allowed = options.PdfTextMaxBytes - output.Length;
                    if (allowed > 0)
                        output.Write(chunk, 0, (int)allowed);
                    truncated = true;
                    break;
                }

                output.Write(chunk, 0, read);
            }

            if (truncated)
                TryKill(process);

            int exitCode;
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                exitCode = process.ExitCode;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                return Failure();
            }

            try
            {
                await stderr;
            }
            catch (Exception)
            {
                // stderr is intentionally discarded; only the exit code and stdout matter.
            }

            if (!truncated && exitCode != 0)
                return Failure();

            string text = Encoding.UTF8.GetString(output.ToArray()).Replace("\0", string.Empty, StringComparison.Ordinal);
            int? totalPages = await CountPagesAsync(path, ct);
            return new PdfTextRunResult(true, text, truncated, null, totalPages);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return Failure();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
        {
            TryKill(process);
            return Failure();
        }
    }

    /// <summary>Asks pdfinfo for the page count. Best effort: any failure, timeout or odd output is "unknown" (null).</summary>
    private async Task<int?> CountPagesAsync(string path, CancellationToken ct)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _infoExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(path);
        startInfo.Environment.Clear();
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(pathVariable)
            ? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
            : pathVariable;

        using Process process = new() { StartInfo = startInfo };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (!process.Start())
                return null;

            Task stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            byte[] buffer = new byte[16384];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), timeout.Token);
                if (read <= 0)
                    break;
                total += read;
            }

            TryKill(process);
            await process.WaitForExitAsync(timeout.Token);
            try
            {
                await stderr;
            }
            catch (Exception)
            {
                // stderr is discarded.
            }

            foreach (string line in Encoding.UTF8.GetString(buffer, 0, total).Split('\n'))
            {
                if (line.StartsWith("Pages:", StringComparison.Ordinal)
                    && int.TryParse(line.AsSpan(6).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int pages)
                    && pages >= 0)
                    return pages;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (Exception)
        {
            TryKill(process);
            return null;
        }
    }

    private static PdfTextRunResult Failure() => new(false, null, false, "PDF_UNREADABLE");

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // The process already exited, or never started; either way there is nothing to kill.
        }
    }
}
