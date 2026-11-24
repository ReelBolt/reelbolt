namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>Computes the local PDQ hash of an image so only 32 bytes leave the host.</summary>
public interface IPdqHasher
{
    /// <summary>
    /// Hash one image stream. Returns false when the bytes cannot be decoded as an image (the
    /// caller then falls back to the provider's media endpoint), never throws for content.
    /// </summary>
    bool TryHash(Stream stream, out string base64Hash, out int quality);
}
