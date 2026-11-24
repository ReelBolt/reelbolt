#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Every documented <c>VIDEO_*</c> setting in <c>.env.example</c> must reach the engine through
/// <c>docker-compose.yml</c>, with the default the code uses. A variable that is documented but not passed on is
/// silently ignored.
/// </summary>
public class ComposeVideoEnvWiringTests
{
    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docker-compose.yml")) && Directory.Exists(Path.Combine(dir.FullName, "inference")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("repo root not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void FfmpegThreads_ReachesTheEngine_WithTheCodeDefault()
    {
        string compose = File.ReadAllText(Path.Combine(Root(), "docker-compose.yml"));
        compose.Should().Contain("VideoEditing__FfmpegThreads: ${VIDEO_FFMPEG_THREADS:-4}");
        new ReelBolt.WorkflowEngine.Services.Video.VideoEditingOptions().FfmpegThreads.Should().Be(4);
    }

    [Fact]
    public void EveryDocumentedVideoVariable_IsReadByCompose()
    {
        string root = Root();
        string compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        string[] names = File.ReadAllLines(Path.Combine(root, ".env.example"))
            .Select(l => Regex.Match(l, "^(VIDEO_[A-Z0-9_]+)="))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .ToArray();

        names.Should().NotBeEmpty();
        foreach (string name in names)
            compose.Should().Contain("${" + name + ":", $"{name} is documented in .env.example but docker-compose.yml never passes it on");
    }
}
