namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// Whether a workflow uses app inputs at all (docs/design/app-inputs.md 6): an <c>input</c> input or a request template in
/// its manifest, or a binding in a step (a <c>{{input.</c> placeholder, an <c>appInputs</c> map, an <c>input.*</c>
/// condition). The one test the API's run gate and the engine's snapshot share, so a workflow that uses none is left
/// exactly as it was on both sides.
/// </summary>
public static class AppInputUsage
{
    /// <summary>True when the manifest or the steps use app inputs.</summary>
    public static bool Uses(AppManifest? manifest, IReadOnlyList<AppBindingStep> steps)
    {
        if (manifest is not null
            && (!string.IsNullOrWhiteSpace(manifest.RequestTemplate)
                || (manifest.Inputs ?? []).Any(i => i.Kind == AppManifestVocabulary.KindInput)))
            return true;
        if (steps.Count == 0)
            return false;
        AppBindingMap map = AppBindingAnalysis.Analyze(null, steps);
        return map.Uses.Count > 0 || map.Problems.Count > 0
            || steps.Any(s => s.ConfigJson?.Contains("\"" + AppInputPlaceholders.AppInputsKey + "\"", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>True when the manifest itself declares app inputs (an <c>input</c> input or a request template).</summary>
    public static bool Declares(AppManifest? manifest) => Uses(manifest, []);
}
