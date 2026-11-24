using System.ComponentModel;
using System.Diagnostics;

namespace ReelBolt.Inference.Api.Services.SfxLibrary;

/// <inheritdoc />
/// <remarks>Runs the system <c>ffmpeg</c> with an argument list (never a shell). A missing binary answers null once and is logged.</remarks>
public sealed class FfmpegSfxRenderer(ILogger<FfmpegSfxRenderer> logger) : ISfxRenderer
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<byte[]?> RenderAsync(SfxRecipe recipe, CancellationToken ct)
    {
        string dir = Directory.CreateTempSubdirectory("sfx-").FullName;
        try
        {
            string output = Path.Combine(dir, "out.wav");
            return await RunAsync(SfxSynthRecipes.BuildArgv(recipe, output), output, recipe.Slug, ct);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    public async Task<byte[]?> NormalizeAsync(byte[] source, CancellationToken ct)
    {
        string dir = Directory.CreateTempSubdirectory("sfx-").FullName;
        try
        {
            string input = Path.Combine(dir, "in.bin");
            string output = Path.Combine(dir, "out.wav");
            await File.WriteAllBytesAsync(input, source, ct);
            return await RunAsync(SfxPackNormalizer.BuildArgv(input, output), output, "pack-normalize", ct);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    private async Task<byte[]?> RunAsync(IReadOnlyList<string> args, string output, string what, CancellationToken ct)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string a in args) psi.ArgumentList.Add(a);
        try
        {
            using Process process = Process.Start(psi)!;
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                logger.LogWarning("Sound library: ffmpeg timed out rendering {What}.", what);
                return null;
            }

            if (process.ExitCode != 0 || !File.Exists(output))
            {
                logger.LogWarning("Sound library: ffmpeg failed for {What} (exit {Code}): {Error}", what, process.ExitCode, Trim(await stderr));
                return null;
            }

            return await File.ReadAllBytesAsync(output, ct);
        }
        catch (Win32Exception)
        {
            logger.LogWarning("Sound library: ffmpeg is not available; the library stays empty.");
            return null;
        }
    }

    private static string Trim(string s) => s.Length <= 400 ? s : s[..400];

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>The fixed ffmpeg arguments that bring a downloaded pack file to the library format.</summary>
public static class SfxPackNormalizer
{
    /// <summary>Longest sound a pack item may keep, in seconds.</summary>
    public const double MaxSeconds = 5;

    public static IReadOnlyList<string> BuildArgv(string inputPath, string outputPath) =>
    [
        "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
        "-bitexact", "-i", inputPath,
        "-vn", "-map_metadata", "-1",
        "-af",
        "silenceremove=start_periods=1:start_threshold=-50dB:stop_periods=-1:stop_duration=0.25:stop_threshold=-50dB," +
        $"atrim=0:{MaxSeconds},afade=t=in:st=0:d=0.003,loudnorm=I=-20:TP=-3:LRA=7," +
        $"aresample={SfxSynthRecipes.SampleRate},aformat=sample_fmts=s16:channel_layouts=mono",
        "-ar", SfxSynthRecipes.SampleRate.ToString(), "-ac", "1", "-c:a", "pcm_s16le",
        "-fflags", "+bitexact", "-flags:a", "+bitexact", "-f", "wav", outputPath
    ];
}
