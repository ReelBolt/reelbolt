namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// One prose/documentation entry read from an import: its stored name, the archive path it came
/// from, its MIME type, the bounded inert text and the SHA-256 of that text. The text is always
/// data — markup has already been reduced to plain text and secrets have been redacted.
/// </summary>
public sealed record ImportedReferenceDocument(
    string Name,
    string Path,
    string MimeType,
    string Text,
    string Sha256);
