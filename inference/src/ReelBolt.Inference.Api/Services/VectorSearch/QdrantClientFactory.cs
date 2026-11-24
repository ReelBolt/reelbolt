using Qdrant.Client;

namespace ReelBolt.Inference.Api.Services.VectorSearch;

/// <summary>The one place a <see cref="QdrantClient"/> is built from <see cref="VectorSearchOptions"/>.</summary>
public static class QdrantClientFactory
{
    /// <summary>
    /// Returns an error message when the options are unusable, otherwise null. An API key over
    /// plain http would be sent in the clear, so a key requires an https <c>QdrantUrl</c>.
    /// </summary>
    public static string? Validate(VectorSearchOptions options)
    {
        if (!Uri.TryCreate(options.QdrantUrl, UriKind.Absolute, out Uri? uri))
            return $"VectorSearch:QdrantUrl '{options.QdrantUrl}' is not an absolute URL.";

        if (!string.IsNullOrWhiteSpace(options.QdrantApiKey)
            && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "VectorSearch:QdrantApiKey is set but VectorSearch:QdrantUrl is not https. "
                + "Refusing to send the API key over an unencrypted connection; use an https:// URL.";
        }

        return null;
    }

    public static QdrantClient Create(VectorSearchOptions options)
    {
        string? error = Validate(options);
        if (error is not null)
            throw new InvalidOperationException(error);

        Uri uri = new(options.QdrantUrl);
        bool https = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        string? apiKey = string.IsNullOrWhiteSpace(options.QdrantApiKey) ? null : options.QdrantApiKey.Trim();
        return new QdrantClient(uri.Host, options.QdrantGrpcPort, https, apiKey);
    }
}
