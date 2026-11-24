using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.WorkflowEngine.Services.Fonts;

/// <summary>The outcome of fetching one catalogued font file: the verified bytes, or a refusal with a model-safe reason.</summary>
public sealed record GoogleFontFetchResult(byte[]? Bytes, string? Error)
{
    /// <summary>Whether <see cref="Bytes"/> are the verified file.</summary>
    public bool Ok => Bytes is not null;

    /// <summary>A verified file.</summary>
    public static GoogleFontFetchResult Success(byte[] bytes) => new(bytes, null);

    /// <summary>A refusal.</summary>
    public static GoogleFontFetchResult Fail(string error) => new(null, error);
}

/// <summary>Fetches a catalogued Google Fonts file at the catalog's pinned commit. The caller never supplies a URL.</summary>
public interface IGoogleFontFetcher
{
    /// <summary>Whether <c>Sandbox:GoogleFonts:Enabled</c> is on.</summary>
    bool Enabled { get; }

    /// <summary>Returns the file's bytes, verified against the catalog's SHA-256 and size cap, or a refusal. Never throws except on cancellation.</summary>
    Task<GoogleFontFetchResult> FetchAsync(GoogleFontFamily family, GoogleFontFile file, CancellationToken cancellationToken);
}

/// <summary>A shared, content-addressed store of already verified font files. Keys are SHA-256 hashes of public data, so one entry serves every tenant.</summary>
public interface IFontBlobCache
{
    /// <summary>The bytes stored under <paramref name="sha256"/>, or null.</summary>
    Task<byte[]?> GetAsync(string sha256, CancellationToken cancellationToken);

    /// <summary>Stores bytes under their hash.</summary>
    Task PutAsync(string sha256, byte[] bytes, CancellationToken cancellationToken);
}
