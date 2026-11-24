using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Opt-in local helper for an ear check: set SFX_RENDER_DIR and every synthesized library sound is rendered there as
/// <c>&lt;slug&gt;.wav</c> with the real recipe argv, beside a <c>synth.json</c> list (slug, title, category, tags,
/// length). Without the variable the test does nothing.
/// </summary>
public class SfxLibraryRenderLocalTests
{
    [FfmpegToolsFact]
    public void Renders_the_synthesized_pack_for_an_ear_check()
    {
        string? dir = Environment.GetEnvironmentVariable("SFX_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
            return;
        Directory.CreateDirectory(dir);

        foreach (SfxRecipe recipe in SfxSynthRecipes.All)
        {
            string path = Path.Combine(dir, recipe.Slug + ".wav");
            ProcessStartInfo info = new("ffmpeg") { RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
            foreach (string argument in SfxSynthRecipes.BuildArgv(recipe, path))
                info.ArgumentList.Add(argument);
            using Process process = Process.Start(info)!;
            process.StandardInput.Close();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            process.ExitCode.Should().Be(0, $"{recipe.Slug}: {stderr}");
            new FileInfo(path).Length.Should().BeGreaterThan(1000);
        }

        File.WriteAllText(Path.Combine(dir, "synth.json"), JsonSerializer.Serialize(
            SfxSynthRecipes.All.Select(r => new { slug = r.Slug, title = r.Title, category = r.Category, tags = r.Tags, durationMs = r.DurationMs }),
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
