using PdqHash;
using LibraryHasher = PdqHash.Hashing.PdqHasher;

namespace ReelBolt.Inference.Api.Services.Csam;

/// <summary>
/// Local PDQ implementation over <c>PdqHash</c> 1.1.0 (MIT, pure managed, SkiaSharp decoder).
/// <para>
/// The canonical wire value is base64 of the 32-byte hash: the library exposes the official
/// 64-character hex string, so it is converted rather than serialized by hand.
/// </para>
/// </summary>
public sealed class PdqHasher : IPdqHasher
{
    public bool TryHash(Stream stream, out string base64Hash, out int quality)
    {
        base64Hash = string.Empty;
        quality = 0;
        ArgumentNullException.ThrowIfNull(stream);

        LibraryHasher? hasher = null;
        try
        {
            hasher = new LibraryHasher();
            HashResult result = hasher.FromStream(stream, "csam-scan");
            string hex = result.Hash.toHexString();
            base64Hash = Convert.ToBase64String(Convert.FromHexString(hex));
            quality = result.Quality;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A decode failure is content, not infrastructure: the caller routes the bytes to
            // /v1/media instead. Nothing about the content is logged here.
            return false;
        }
        finally
        {
            hasher?.Dispose();
        }
    }
}
