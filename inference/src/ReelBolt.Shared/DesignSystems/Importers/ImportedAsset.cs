namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// One binary asset an importer selected from its input, ready for the caller to store as a
/// project file. <see cref="Role"/> is a design-document role (<c>logo</c>/<c>icon</c>),
/// <see cref="Variant"/> the role's variant name, and <see cref="Sha256"/> the digest the importer
/// recomputed from <see cref="Content"/>.
/// </summary>
public sealed record ImportedAsset(
    string Role,
    string? Variant,
    string Name,
    string Path,
    string MimeType,
    string Sha256,
    ReadOnlyMemory<byte> Content,
    string? Subject = null,
    string? Form = null,
    string? Tone = null,
    IReadOnlyList<string>? ReadableOn = null,
    double? Luminance = null);
