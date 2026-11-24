#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.ImportWorker;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Worker;

/// <summary>
/// The real <see cref="PopplerPdfTextRunner"/> against fake executables (POSIX shell scripts): argv, cleared
/// environment, output truncation, timeout and kill, stderr draining, failure mapping and cancellation. The handler
/// tests use a fake runner and therefore cannot see any of this.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class PopplerPdfTextRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdf-runner-" + Guid.NewGuid().ToString("N"));

    public PopplerPdfTextRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Script(string body)
    {
        string path = Path.Combine(_dir, "fake-pdftotext-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static readonly WorkerOptions Options = new("t");

    [UnixShellFact]
    public async Task Argv_is_an_argument_list_with_the_page_cap_and_the_pdf_before_stdout_dash()
    {
        PopplerPdfTextRunner runner = new(Script("printf '%s\\n' \"$@\""));

        PdfTextRunResult result = await runner.RunAsync("/tmp/x y;rm -rf z.pdf", Options with { PdfMaxPages = 7 }, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Text!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Equal(
            "-nopgbrk", "-enc", "UTF-8", "-l", "7", "/tmp/x y;rm -rf z.pdf", "-");
    }

    [UnixShellFact]
    public async Task The_page_count_comes_from_pdfinfo_and_is_unknown_when_pdfinfo_fails()
    {
        string info = Script("echo 'Title:          x'\necho 'Pages:          120'\necho 'Encrypted:      no'");
        PopplerPdfTextRunner withInfo = new(Script("echo text"), info);
        (await withInfo.RunAsync("/tmp/a.pdf", Options, CancellationToken.None)).TotalPages.Should().Be(120);

        PopplerPdfTextRunner failing = new(Script("echo text"), Script("echo 'Pages: lots'\nexit 1"));
        PdfTextRunResult unknown = await failing.RunAsync("/tmp/a.pdf", Options, CancellationToken.None);
        unknown.Ok.Should().BeTrue();
        unknown.TotalPages.Should().BeNull();

        PopplerPdfTextRunner missing = new(Script("echo text"), Path.Combine(_dir, "no-such-pdfinfo"));
        (await missing.RunAsync("/tmp/a.pdf", Options, CancellationToken.None)).TotalPages.Should().BeNull();
    }

    [UnixShellFact]
    public async Task Default_page_cap_is_fifty()
    {
        PopplerPdfTextRunner runner = new(Script("printf '%s\\n' \"$@\""));

        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options, CancellationToken.None);

        result.Text!.Split('\n').SkipWhile(a => a != "-l").Skip(1).First().Should().Be("50");
    }

    [UnixShellFact]
    public async Task Environment_is_cleared_to_path_only()
    {
        Environment.SetEnvironmentVariable("PDS_RUNNER_CANARY", "leak-me");
        try
        {
            PopplerPdfTextRunner runner = new(Script("/usr/bin/env"));

            PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options, CancellationToken.None);

            result.Ok.Should().BeTrue();
            result.Text.Should().NotContain("PDS_RUNNER_CANARY");
            result.Text.Should().NotContain("HOME=");
            result.Text.Should().Contain("PATH=");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PDS_RUNNER_CANARY", null);
        }
    }

    [UnixShellFact]
    public async Task Output_over_the_cap_is_truncated_flagged_and_does_not_hang()
    {
        PopplerPdfTextRunner runner = new(Script("head -c 5000000 /dev/zero | tr '\\0' 'a'"));

        Stopwatch sw = Stopwatch.StartNew();
        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options with { PdfTextMaxBytes = 1000 }, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Truncated.Should().BeTrue();
        result.Text!.Length.Should().Be(1000);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [UnixShellFact]
    public async Task Output_exactly_at_the_cap_is_not_flagged_truncated()
    {
        PopplerPdfTextRunner runner = new(Script("head -c 1000 /dev/zero | tr '\\0' 'a'"));

        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options with { PdfTextMaxBytes = 1000 }, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Truncated.Should().BeFalse();
        result.Text!.Length.Should().Be(1000);
    }

    [UnixShellFact]
    public async Task A_run_over_the_timeout_fails_and_the_process_is_killed()
    {
        string pidFile = Path.Combine(_dir, "pid");
        PopplerPdfTextRunner runner = new(Script($"echo $$ > '{pidFile}'\nexec sleep 60"));

        Stopwatch sw = Stopwatch.StartNew();
        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options with { PdfTimeoutSeconds = 1 }, CancellationToken.None);
        sw.Stop();

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("PDF_UNREADABLE");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(40), "the timeout, not the 60 s sleep, ends the run");

        int pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        await WaitUntilGone(pid);
    }

    [UnixShellFact]
    public async Task Heavy_stderr_is_drained_so_the_process_never_blocks_on_a_full_pipe()
    {
        // 4 MiB on stderr overflows the 64 KiB pipe; without a reader the script blocks until the timeout.
        PopplerPdfTextRunner runner = new(Script("head -c 4194304 /dev/zero >&2\nprintf done"));

        Stopwatch sw = Stopwatch.StartNew();
        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options with { PdfTimeoutSeconds = 20 }, CancellationToken.None);

        result.Ok.Should().BeTrue();
        result.Text.Should().Be("done");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(40));
    }

    [UnixShellFact]
    public async Task Non_zero_exit_without_truncation_is_a_failure()
    {
        PopplerPdfTextRunner runner = new(Script("echo 'Syntax Error' >&2\nprintf partial\nexit 1"));

        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Text.Should().BeNull();
        result.ErrorCode.Should().Be("PDF_UNREADABLE");
    }

    [UnixShellFact]
    public async Task Nul_bytes_are_stripped_from_the_text()
    {
        PopplerPdfTextRunner runner = new(Script("printf 'a\\000b'"));

        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options, CancellationToken.None);

        result.Text.Should().Be("ab");
    }

    [UnixShellFact]
    public async Task A_missing_executable_is_a_failure_not_an_exception()
    {
        PopplerPdfTextRunner runner = new(Path.Combine(_dir, "does-not-exist"));

        PdfTextRunResult result = await runner.RunAsync("/tmp/a.pdf", Options, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.ErrorCode.Should().Be("PDF_UNREADABLE");
    }

    [UnixShellFact]
    public async Task Caller_cancellation_kills_the_process_and_propagates()
    {
        string pidFile = Path.Combine(_dir, "pid");
        PopplerPdfTextRunner runner = new(Script($"echo $$ > '{pidFile}'\nexec sleep 60"));
        using CancellationTokenSource cts = new();

        Task<PdfTextRunResult> run = runner.RunAsync("/tmp/a.pdf", Options with { PdfTimeoutSeconds = 60 }, cts.Token);
        await WaitForFile(pidFile);
        cts.Cancel();

        Func<Task> act = async () => await run;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await WaitUntilGone(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [PdfToTextFact]
    public async Task Real_pdftotext_honours_the_page_cap()
    {
        string path = Path.Combine(_dir, "three.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(ThreePagePdf()));

        PdfTextRunResult capped = await new PopplerPdfTextRunner().RunAsync(path, Options with { PdfMaxPages = 1 }, CancellationToken.None);
        PdfTextRunResult all = await new PopplerPdfTextRunner().RunAsync(path, Options with { PdfMaxPages = 50 }, CancellationToken.None);

        capped.Ok.Should().BeTrue();
        capped.Text.Should().Contain("PAGEONE").And.NotContain("PAGETWO").And.NotContain("PAGETHREE");
        all.Text.Should().Contain("PAGEONE").And.Contain("PAGETWO").And.Contain("PAGETHREE");
    }

    private static string ThreePagePdf()
    {
        StringBuilder pdf = new("%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        pdf.Append("2 0 obj<</Type/Pages/Kids[3 0 R 6 0 R 9 0 R]/Count 3>>endobj\n");
        string[] words = { "PAGEONE", "PAGETWO", "PAGETHREE" };
        for (int i = 0; i < 3; i++)
        {
            int page = 3 + (i * 3);
            string content = $"BT /F1 18 Tf 20 50 Td ({words[i]}) Tj ET";
            pdf.Append($"{page} 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 100]/Contents {page + 1} 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj\n");
            pdf.Append($"{page + 1} 0 obj<</Length {content.Length}>>stream\n{content}\nendstream\nendobj\n");
            if (i == 0)
                pdf.Append("5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\n");
            else
                pdf.Append($"{page + 2} 0 obj<<>>endobj\n");
        }

        pdf.Append("trailer<</Root 1 0 R/Size 10>>\n%%EOF\n");
        return pdf.ToString();
    }

    private static async Task WaitForFile(string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("the fake executable never started");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntilGone(int pid)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (Directory.Exists($"/proc/{pid}") && !IsZombie(pid))
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"process {pid} survived the runner (not killed)");
            await Task.Delay(50);
        }
    }

    private static bool IsZombie(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/stat").Contains(") Z ", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return true;
        }
    }
}
