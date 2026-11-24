using System.Globalization;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Diff;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Pure check of a freshly imported design document, run before the customer is told "imported".
/// An import that finds nothing still produces a valid Draft (voice-only, empty), and approving it
/// would replace a good design with an empty one, so the check answers two plain warnings:
/// <see cref="ZeroTokensCode"/> when the import carries no tokens at all, and
/// <see cref="LosesTokensCode"/> when the current approved version has tokens the import lacks.
/// It never refuses or mutates anything: the Draft/approve split is the guard, these are the words.
/// </summary>
public static class DesignImportGuard
{
    /// <summary>The import carries no design tokens at all.</summary>
    public const string ZeroTokensCode = "IMPORT_ZERO_TOKENS";

    /// <summary>The import lacks tokens the current approved version has.</summary>
    public const string LosesTokensCode = "IMPORT_LOSES_TOKENS";

    /// <summary>How many tokens the document carries.</summary>
    public static int CountTokens(JsonObject? document) =>
        document is null ? 0 : DesignDocument.Tokens(document).Count();

    /// <summary>
    /// The warnings for <paramref name="imported"/> against the project's current approved document
    /// (<paramref name="current"/>, null when none) numbered <paramref name="currentVersionNumber"/>.
    /// Empty when the import is at least as rich as the current version. Never throws.
    /// </summary>
    public static IReadOnlyList<ImportDiagnostic> Assess(
        JsonObject? imported, JsonObject? current, int? currentVersionNumber)
    {
        var result = new List<ImportDiagnostic>();
        try
        {
            int importedCount = CountTokens(imported);
            int currentCount = CountTokens(current);

            if (importedCount == 0)
            {
                string versionNote = currentCount > 0 && currentVersionNumber is { } n
                    ? $" Version {n.ToString(CultureInfo.InvariantCulture)} has {currentCount.ToString(CultureInfo.InvariantCulture)} and would be replaced by an empty design if you approve this draft."
                    : " Approving this draft would give your videos no brand colours or fonts.";
                result.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    ZeroTokensCode,
                    string.Empty,
                    "The import found no colours, fonts or other design tokens in the files you chose." + versionNote
                    + " Check the file format, or reject this draft.")
                { Axis = DesignImportAxis.Tokens });
                return result;
            }

            if (current is not null && imported is not null && currentCount > 0)
            {
                int lost = DesignDocumentDiffer.Diff(current, imported).Removed.Count;
                if (lost > 0)
                {
                    string version = currentVersionNumber is { } v
                        ? $"version {v.ToString(CultureInfo.InvariantCulture)}"
                        : "the current version";
                    result.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        LosesTokensCode,
                        string.Empty,
                        $"This import is missing {lost.ToString(CultureInfo.InvariantCulture)} of the {currentCount.ToString(CultureInfo.InvariantCulture)} tokens in {version}. "
                        + "Approving it would remove them. Review the changes before you approve.")
                    { Axis = DesignImportAxis.Tokens });
                }
            }
        }
        catch (Exception)
        {
            // A check that fails says nothing: the Draft review banner still shows the diff.
        }

        return result;
    }
}
