using System.Security.Cryptography;
using System.Text;

namespace ReelBolt.Shared.Moderation;

/// <summary>
/// The audit row's content identity: a lowercase hex SHA-256 over a length-prefixed canonical form
/// of the classified input. Raw text/media never leaves memory — the hash is what
/// <c>ContentModerationDecision.ContentSha256</c> stores.
/// </summary>
/// <remarks>
/// Length prefixes (7-bit-encoded strings and explicit byte counts) keep the encoding unambiguous:
/// without them, moving a byte between the text and the first image (or between a content type and
/// its bytes) could hash the same way as a different input.
/// </remarks>
public static class ModerationContentHash
{
    public static string Compute(ModerationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        using MemoryStream buffer = new();
        using (BinaryWriter writer = new(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(input.Kind.ToString());
            writer.Write(string.IsNullOrEmpty(input.Text) ? "-" : input.Text);

            IReadOnlyList<ModerationImageInput> images = input.Images ?? [];
            writer.Write(images.Count);
            foreach (ModerationImageInput image in images)
            {
                ArgumentNullException.ThrowIfNull(image);
                writer.Write(image.ContentType ?? string.Empty);
                writer.Write(image.Bytes?.Length ?? 0);
                if (image.Bytes is { Length: > 0 })
                    writer.Write(image.Bytes);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }
}
