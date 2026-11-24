namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// Configuration for CSAM hash matching (Project Arachnid Shield). Bound from the <c>Csam</c>
/// section in <see cref="InferenceApiServiceCollectionExtensions.AddInferenceApiServices"/>.
/// Disabled by default: a deployment that has not configured an Arachnid account scans nothing and
/// leaves rows <see cref="ReelBolt.Shared.Data.Models.CsamScanStatus.NotScanned"/>.
/// </summary>
public sealed class CsamOptions
{
    public const string SectionName = "Csam";

    public ArachnidOptions Arachnid { get; set; } = new();
    public CsamSweeperOptions Sweeper { get; set; } = new();
}

/// <summary>The Project Arachnid Shield account and endpoint. This is the only host the control calls.</summary>
public sealed class ArachnidOptions
{
    public bool Enabled { get; set; }

    /// <summary>The only outbound base URL. Relative request paths are appended to it.</summary>
    public string? BaseUrl { get; set; } = "https://shield.projectarachnid.com";

    /// <summary>
    /// Account username, from <c>ARACHNID_API_USERNAME</c> (bound as <c>Csam__Arachnid__Username</c>).
    /// Blank means "not configured": every scan fails open and nothing leaves the host.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Account password, from <c>ARACHNID_API_PASSWORD</c> (bound as <c>Csam__Arachnid__Password</c>).
    /// Never logged, never put in a URL or query string; sent as HTTP Basic auth only.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>The header the Basic credentials are sent in. Defaults to <c>Authorization</c>.</summary>
    public string AuthHeader { get; set; } = "Authorization";

    /// <summary>Per-request bound. The upload's scan never waits longer than this.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Images up to this size are hashed locally and sent to <c>/v1/pdq</c>; larger ones use <c>/v1/media</c>.</summary>
    public long MaxInlineHashBytes { get; set; } = 64 * 1024 * 1024;
}

/// <summary>The background re-scan of rows no in-request path reached.</summary>
public sealed class CsamSweeperOptions
{
    /// <summary>0 disables the sweeper.</summary>
    public int IntervalMinutes { get; set; } = 15;

    public int BatchSize { get; set; } = 25;

    /// <summary>Also retry <see cref="ReelBolt.Shared.Data.Models.CsamScanStatus.NotScanned"/> rows older than 24 hours.</summary>
    public bool RescanNotScanned { get; set; }
}
