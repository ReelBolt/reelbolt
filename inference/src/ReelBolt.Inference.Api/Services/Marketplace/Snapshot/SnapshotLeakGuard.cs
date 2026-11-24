using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>
/// The backstop that runs on the FINAL snapshot JSON whatever the catalog said: every string and every key is scanned, and a single
/// Guid-shaped token, <c>projects/</c> storage-key shape, presigned-URL shape (<c>X-Amz-</c>) or secret-shaped token refuses the
/// recipe with <c>UNRECOGNIZED_ID</c>. The message names the kind of thing found, never the thing itself.
/// </summary>
public static partial class SnapshotLeakGuard
{
    public const string RefusalCode = "UNRECOGNIZED_ID";

    // No boundary check: a hex character next to the token is part of it, so the recipe is refused all the same.
    [GeneratedRegex(@"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidShape();

    // The undashed form: a run of 32 or more hex characters that includes a digit (a long run of letters a-f is just a word or filler).
    [GeneratedRegex(@"[0-9a-fA-F]{32,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexRun();

    [GeneratedRegex(@"(?i)(?<![a-z0-9])projects/[a-z0-9_{-]{4,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StorageKey();

    // Provider key shapes the shared redactor does not know: ElevenLabs sk_, Replicate r8_, Hugging Face hf_, Runway key_.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:sk_|r8_|hf_|key_)[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProviderKey();

    [GeneratedRegex(@"[A-Za-z0-9+/_=-]{32,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LongToken();

    // A model slug such as "vendor/model-name/variant" (lowercase words joined by - and /): long, but not a key.
    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:/[a-z0-9]+(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ModelSlug();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Email();

    // A link with a credential in its query string, with or without a scheme.
    [GeneratedRegex(@"(?i)[?&;](?:token|access_token|api_?key|key|sig|signature|secret|auth|password)=[^&\s]{8,}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CredentialQuery();

    [GeneratedRegex(@"(?i)X-Amz-", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Presigned();

    /// <summary>The refusals for <paramref name="snapshotJson"/>; empty when it is clean.</summary>
    public static IReadOnlyList<PortableRefusal> Scan(string snapshotJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(snapshotJson);
        }
        catch (JsonException)
        {
            return [new PortableRefusal(RefusalCode, null, "The recipe could not be checked for private ids.")];
        }

        List<PortableRefusal> found = [];
        Visit(root, null, found);
        return [.. found.DistinctBy(f => (f.StepOrder, f.Message))];
    }

    /// <summary>The kind of private thing a text holds, or null when it holds none.</summary>
    public static string? Classify(string text)
    {
        try
        {
            if (GuidShape().IsMatch(text) || HexRun().Matches(text).Any(m => m.Value.Any(char.IsDigit)))
                return "an id";
            if (StorageKey().IsMatch(text))
                return "a storage path";
            if (Presigned().IsMatch(text) || CredentialQuery().IsMatch(text))
                return "a signed link";
            if (Email().IsMatch(text))
                return "an email address";
            if (ProviderKey().IsMatch(text) || SecretRedactor.Redact(text).count > 0 || HasHighEntropyToken(text))
                return "a secret";
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return "text that could not be checked";
        }
    }

    /// <summary>A long unbroken run of letters and digits with the spread of random characters is a key, not a word.</summary>
    private static bool HasHighEntropyToken(string text)
    {
        foreach (Match m in LongToken().Matches(text))
        {
            string token = m.Value;
            if (!token.Any(char.IsDigit) || !token.Any(char.IsLetter))
                continue;
            // A model slug (vendor/model-name/variant) is exempt only while every segment is short; the entropy
            // check still runs on each segment, so a secret shaped like "vendor/<random>" is not let through.
            if (ModelSlug().IsMatch(token))
            {
                foreach (string segment in token.Split('/'))
                {
                    if (segment.Length > MaxSlugSegment || (segment.Length >= 20 && IsRandomLooking(segment)))
                        return true;
                }

                // Pieces too short to judge alone are judged together: random characters keep their spread across the
                // separators, a readable slug (words joined by - and /) does not.
                if (IsRandomLooking(token.Replace("/", "").Replace("-", ""), SlugWholeEntropy))
                    return true;

                continue;
            }

            if (IsRandomLooking(token))
                return true;
        }

        return false;
    }

    private const int MaxSlugSegment = 24;

    private const double SlugWholeEntropy = 4.4;

    private static bool IsRandomLooking(string token, double threshold = 4.0)
    {
        if (!token.Any(char.IsDigit) || !token.Any(char.IsLetter))
            return false;
        double entropy = 0;
        foreach (IGrouping<char, char> g in token.GroupBy(c => c))
        {
            double p = (double)g.Count() / token.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy >= threshold;
    }

    private static void Visit(JsonNode? node, int? stepOrder, List<PortableRefusal> found)
    {
        switch (node)
        {
            case JsonObject obj:
                int? order = stepOrder;
                if (obj["order"] is JsonValue ov && ov.TryGetValue(out int o) && obj.ContainsKey("stepType"))
                    order = o;
                foreach (KeyValuePair<string, JsonNode?> kv in obj)
                {
                    Report(kv.Key, order, found);
                    Visit(kv.Value, order, found);
                }

                break;
            case JsonArray arr:
                foreach (JsonNode? child in arr)
                    Visit(child, stepOrder, found);
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                Report(value.GetValue<string>(), stepOrder, found);
                break;
        }
    }

    private static void Report(string text, int? stepOrder, List<PortableRefusal> found)
    {
        if (Classify(text) is { } kind)
            found.Add(new PortableRefusal(RefusalCode, stepOrder, $"The recipe still contains {kind} that belongs to you, so it cannot be published."));
    }
}
