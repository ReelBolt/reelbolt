using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Editing;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A secondary's matte is evaluated into ONE plane, not three (public CI run 45796910736: WS-108's qualifier render took
/// 126 s for 30 frames at 1080p on four cores and held the engine's single ffmpeg lane, so a render of another test
/// waited behind it for the whole 240 s). The pixels must be exactly what the three-plane form produced.
/// </summary>
public sealed class GradeMatteStreamTests
{
    private static readonly GradeQualifier Qualifier = new(HueCenter: 120, HueWidth: 15, SatMin: 0.15, LumMin: 0.1, Softness: 0.05);
    private static readonly GradeWindow Window = new(new ShapeMask("ellipse", 0.5, 0.5, 0.3, 0.3, 0.05));

    private static GradeNode Node(GradeQualifier? q, GradeWindow? w) =>
        new(Guid.Parse("00000000-0000-0000-0000-000000000900"), "Skin", true, new GradePrimaries(Saturation: 0), Qualifier: q, Window: w);

    private static string Build(GradeNode node)
    {
        List<GradeFilterBuilder.LutRequest> luts = [];
        int index = 0;
        return GradeFilterBuilder.Build(new GradeStack(true, [node]), i => $"/l{i}", luts, ref index, "gq", 0, 30);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_matte_expression_is_written_once_and_never_to_the_three_colour_planes(bool qualifier, bool window)
    {
        GradeNode node = Node(qualifier ? Qualifier : null, window ? Window : null);
        string matte = GradeFilterBuilder.Matte(node, 0, 30)!;
        string chain = Build(node);

        chain.Split(matte).Length.Should().Be(2, "the expression is in the chain exactly once");
        chain.Should().Contain($"geq=r='{matte}':g='0':b='0':a='255'");
        chain.Should().EndWith("[gq0o][gq0g][gq0k]maskedmerge");
    }

    [FfmpegTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_single_plane_matte_gives_the_same_pixels_as_the_three_plane_form_alpha_included(bool qualifier, bool window)
    {
        GradeNode node = Node(qualifier ? Qualifier : null, window ? Window : null);
        string matte = GradeFilterBuilder.Matte(node, 0, 30)!;

        // Colourful, with an alpha that varies from pixel to pixel so the alpha plane is checked too.
        const string source = "testsrc2=s=192x108:r=10:d=0.4,format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='30+mod(X*3+Y*5,200)'";
        string legacy = source + ",format=gbrap,split=3[o][c][m];[c]hue=s=0,format=gbrap[g];" +
                        $"[m]geq=r='{matte}':g='{matte}':b='{matte}':a='255'[k];[o][g][k]maskedmerge";
        string current = source + "," + Build(node);

        byte[] expected = Render(legacy);
        byte[] actual = Render(current);

        expected.Length.Should().Be(192 * 108 * 4 * 4, "four frames of gbrap");
        actual.Should().Equal(expected);
    }

    [FfmpegTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void The_matte_stream_itself_is_identical_alpha_plane_included(bool qualifier, bool window)
    {
        // The merged picture cannot show the matte's own alpha (the correction keeps alpha, so both inputs agree), so
        // the stream is compared directly: three colour planes holding the matte, alpha 255.
        GradeNode node = Node(qualifier ? Qualifier : null, window ? Window : null);
        string matte = GradeFilterBuilder.Matte(node, 0, 30)!;
        const string source = "testsrc2=s=192x108:r=10:d=0.4,format=gbrap";

        byte[] expected = Render($"{source},geq=r='{matte}':g='{matte}':b='{matte}':a='255'");
        byte[] actual = Render($"{source},split=1[x];" + GradeFilterBuilder.MatteStream("x", "k", matte) + "[k]null");

        actual.Should().Equal(expected);
        // Plane order of gbrap is g, b, r, a: the last plane of each frame is the constant 255.
        const int plane = 192 * 108;
        actual.Skip(3 * plane).Take(plane).Should().OnlyContain(b => b == 255);
    }

    private static byte[] Render(string graph)
    {
        string dir = Path.Combine(Path.GetTempPath(), "matte-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string output = Path.Combine(dir, "out.raw");
            ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-v", "error", "-y", "-f", "lavfi", "-i", graph.Split(',', 2)[0], "-vf", graph.Split(',', 2)[1], "-f", "rawvideo", "-pix_fmt", "gbrap", output })
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            System.Threading.Tasks.Task<string> stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException("ffmpeg did not finish within 120 s");
            }

            p.ExitCode.Should().Be(0, stderr.GetAwaiter().GetResult());
            return File.ReadAllBytes(output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
