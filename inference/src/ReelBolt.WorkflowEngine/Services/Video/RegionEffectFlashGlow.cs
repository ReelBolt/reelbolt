namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>The filter graphs of the <c>Flash</c> and <c>Glow</c> region effects (constants here, strength is the only knob).</summary>
internal static class RegionEffectFlashGlow
{
    // Flash: brightness added inside the mask, and a mild contrast lift, at strength 1.0.
    private const double FlashGain = 0.45;
    private const double FlashContrast = 0.15;

    // Glow: bloom sigma as a share of the short side, brightness and saturation lift, at strength 1.0.
    private const double GlowSigmaFraction = 0.02;
    private const double GlowGain = 0.12;
    private const double GlowSaturation = 0.25;

    internal static string Flash(string input, string mask, string output, int slot, double strength)
    {
        double gain = Math.Round(FlashGain * strength, 4);
        double contrast = Math.Round(1 + FlashContrast * strength, 4);
        return $"{input}split=2[ree{slot}b][ree{slot}h1];" +
               $"[ree{slot}h1]eq=brightness={FfmpegArgvFormat.Number(gain)}:contrast={FfmpegArgvFormat.Number(contrast)},format=yuva420p[ree{slot}h2];" +
               $"[ree{slot}h2]{mask}alphamerge[ree{slot}h3];" +
               $"[ree{slot}b][ree{slot}h3]overlay=x=0:y=0:format=yuv420{output}";
    }

    internal static string Glow(string input, string mask, string output, int slot, int shortSide, double strength)
    {
        double sigma = Math.Round(Math.Max(1.0, shortSide * GlowSigmaFraction * strength), 2);
        double gain = Math.Round(GlowGain * strength, 4);
        double saturation = Math.Round(1 + GlowSaturation * strength, 4);
        return $"{input}split=2[ree{slot}b][ree{slot}g1];" +
               $"[ree{slot}g1]gblur=sigma={FfmpegArgvFormat.Number(sigma)},eq=brightness={FfmpegArgvFormat.Number(gain)}:saturation={FfmpegArgvFormat.Number(saturation)}," +
               $"format=yuva420p[ree{slot}g2];" +
               $"[ree{slot}g2]{mask}alphamerge[ree{slot}g3];" +
               $"[ree{slot}b][ree{slot}g3]overlay=x=0:y=0:format=yuv420{output}";
    }
}
