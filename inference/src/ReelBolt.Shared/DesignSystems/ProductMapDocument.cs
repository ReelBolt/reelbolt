namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// The typed product-map document (SPEC 4.3): a technology-and-structure summary of a codebase.
/// Every enum-like member is a plain string validated against a closed set by
/// <see cref="ProductMapValidator"/>; the record carries no behaviour and no IO.
/// </summary>
public sealed record ProductMapDocument(
    string Schema,
    string? Name,
    ProductMapFramework Framework,
    string? Architecture,
    string? RoutingStrategy,
    IReadOnlyList<ProductMapDirectory> Directories,
    IReadOnlyList<ProductMapEntryPoint> EntryPoints,
    IReadOnlyList<ProductMapRoute> Routes,
    IReadOnlyList<ProductMapComponent> Components,
    ProductMapDependencies Dependencies,
    IReadOnlyList<string> Patterns,
    IReadOnlyDictionary<string, ProductMapProvenance> Provenance,
    IReadOnlyList<ProductMapPreviewCard>? PreviewCards = null)
{
    /// <summary>
    /// The canonical schema identifier. It is named <c>SchemaId</c> rather than <c>Schema</c>
    /// because the positional member <see cref="Schema"/> already occupies the name
    /// <c>Schema</c>; a const of the same name would be a duplicate-member compile error.
    /// </summary>
    public const string SchemaId = "reelbolt.product-map/1";
}

/// <summary>Detected framework, language, styling and packaging facts.</summary>
public sealed record ProductMapFramework(
    string Name,
    string Version,
    string Language,
    IReadOnlyList<string> Styling,
    string PackageManager,
    string ProjectType);

/// <summary>One directory of the analysed tree and its inferred purpose.</summary>
public sealed record ProductMapDirectory(string Path, string Purpose);

/// <summary>One application entry point and the role it plays.</summary>
public sealed record ProductMapEntryPoint(string Path, string Role);

/// <summary>One page, layout or API route.</summary>
public sealed record ProductMapRoute(
    string Path,
    string File,
    string Kind,
    string? Method,
    string? Title,
    bool RequiresAuth,
    string? Source);

/// <summary>One inventoried component and the responsibility attributed to it.</summary>
public sealed record ProductMapComponent(
    string Name,
    string Path,
    string Responsibility,
    IReadOnlyList<string> KeyProps,
    bool Core,
    string? Source);

/// <summary>Runtime, development and UI dependencies.</summary>
public sealed record ProductMapDependencies(
    IReadOnlyList<ProductMapDependency> Runtime,
    IReadOnlyList<ProductMapDependency> Dev,
    IReadOnlyList<string> Ui);

/// <summary>One dependency name and version.</summary>
public sealed record ProductMapDependency(string Name, string Version);

/// <summary>How one section of the document was produced.</summary>
public sealed record ProductMapProvenance(string Method, IReadOnlyList<string> SourceIds);

/// <summary>
/// One preview card of the exported design system as inert, structured data: the card's identity,
/// its reduced plain text (never its markup or a script body) and the relative image basenames it
/// refers to. Nothing here is executed or fetched.
/// </summary>
public sealed record ProductMapPreviewCard(
    string Name,
    string? Group,
    string? Subtitle,
    string? Viewport,
    string? Path,
    string? Text,
    IReadOnlyList<string> Assets,
    bool RuntimeRendered);
