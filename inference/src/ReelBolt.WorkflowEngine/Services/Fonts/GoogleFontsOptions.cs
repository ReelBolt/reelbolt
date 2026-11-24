namespace ReelBolt.WorkflowEngine.Services.Fonts;

/// <summary>
/// <c>Sandbox:GoogleFonts</c>: the engine-side Google Fonts fetch behind the <c>StageFont</c> tool. The host,
/// the pinned commit, the size cap and the no-redirect rule are NOT options: they are fixed in
/// <see cref="GoogleFontFetcher"/> so a configuration typo cannot widen what the engine reaches.
/// </summary>
public sealed class GoogleFontsOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Sandbox:GoogleFonts";

    /// <summary>Whether <c>StageFont</c> may fetch from Google Fonts. Off by default: the engine has no egress unless an operator allows it.</summary>
    public bool Enabled { get; set; }

    /// <summary>Deadline for one file download, in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 30;
}
