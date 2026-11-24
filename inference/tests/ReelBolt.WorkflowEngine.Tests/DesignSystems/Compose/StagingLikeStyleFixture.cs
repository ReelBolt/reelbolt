using System.Collections.Generic;
using ReelBolt.Shared.Data.OutputSchemas;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Compose;

/// <summary>
/// A synthesised <see cref="StyleAndThemeOutput"/> shaped like the output that killed a staging profile
/// build (DESIGN_COMPOSE_INVALID / DS_V09): upper-case hex, short hex, rgba/hsl/named colours, gradients,
/// prose and a few bad dimensions. No staging data is reproduced here.
/// </summary>
internal static class StagingLikeStyleFixture
{
    /// <summary>Additional colour keys that must survive, with the canonical hex each should become.</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpectedKept = new Dictionary<string, string>
    {
        ["violet"] = "#7c3aed",
        ["white"] = "#ffffff",
        ["short"] = "#aabbcc",
        ["shadow-soft"] = "#00000017",
        ["hsl-blue"] = "#0000ff",
        ["named-rebecca"] = "#663399",
        ["named-upper"] = "#faebd7",
        ["spaced"] = "#112233",
        ["slash-alpha"] = "#ff000080",
        ["pct"] = "#ff00ff",
        ["transparent"] = "#00000000",
        ["eight"] = "#12345678",
    };

    /// <summary>Additional colour keys that cannot be normalised and must be dropped with a warning.</summary>
    public static readonly string[] ExpectedDropped =
    [
        "gradient-hero", "radial", "image", "variable", "calc", "prose", "empty-ish", "not-a-colour",
    ];

    public static StyleAndThemeOutput Build() => new()
    {
        Colors = new ColorPalette
        {
            Primary = "#7C3AED",
            Secondary = "RGB(16, 185, 129)",
            Background = "#FFFFFF",
            Text = "green (Mantine green, the default accent)",
            Additional = new Dictionary<string, string>
            {
                ["violet"] = "#7C3AED",
                ["white"] = "#FFFFFF",
                ["short"] = "#ABC",
                ["shadow-soft"] = "rgba(0,0,0,0.09)",
                ["hsl-blue"] = "hsl(240, 100%, 50%)",
                ["named-rebecca"] = "rebeccapurple",
                ["named-upper"] = "AntiqueWhite",
                ["spaced"] = "  #112233  ",
                ["slash-alpha"] = "rgb(255 0 0 / 50%)",
                ["pct"] = "rgb(100%, 0%, 100%)",
                ["transparent"] = "transparent",
                ["eight"] = "#12345678",
                ["gradient-hero"] = "linear-gradient(135deg, #7C3AED 0%, #10B981 100%)",
                ["radial"] = "radial-gradient(circle, #fff, #000)",
                ["image"] = "url(https://example.com/a.png)",
                ["variable"] = "var(--mantine-color-blue-6)",
                ["calc"] = "calc(1px + 2px)",
                ["prose"] = "green (Mantine green, used for success states and the primary call to action)",
                ["empty-ish"] = "   ",
                ["not-a-colour"] = "not-a-colour",
            },
        },
        Typography = new Typography
        {
            PrimaryFont = "Inter",
            SecondaryFont = "Georgia",
            FontSizes = new Dictionary<string, string>
            {
                ["sm"] = "14px",
                ["lg"] = "1.5rem",
                ["bad"] = "clamp(1rem, 2vw, 2rem)",
                ["huge"] = "99999px",
            },
        },
        Spacing = new Spacing
        {
            Unit = "px",
            Scale = new Dictionary<string, string> { ["2"] = "8", ["bad"] = "auto", ["4"] = "16px" },
        },
        StylingApproach = "Mantine",
    };
}
