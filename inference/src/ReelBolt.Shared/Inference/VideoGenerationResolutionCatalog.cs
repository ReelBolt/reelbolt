namespace ReelBolt.Shared.Inference;

/// <summary>
/// The resolution keys each video model's price card accepts, rendered as one sentence for the VideoGenerate step
/// schema (T-105). Generated from the same price cards the budget gate prices against
/// (<see cref="MiniMaxVideoGenerationClient.DescribeModel"/>, <see cref="FalVideoCatalog"/>), never hand-written, so a
/// key added to a card (a "2K", a "4K") is discoverable the day it is added.
/// </summary>
public static class VideoGenerationResolutionCatalog
{
    /// <summary>
    /// <c>model: key, key; model: key</c> for every statically priced model. Providers that quote the price live
    /// (Higgsfield) or price by template (self-hosted ComfyUI) have no static card and are validated at run time.
    /// </summary>
    public static string Describe()
    {
        var entries = new List<string>();

        foreach (string model in MiniMaxVideoGenerationClient.ModelIds)
        {
            entries.Add($"{model}: {string.Join(", ", MiniMaxVideoGenerationClient.DescribeModel(model).PricePerSecondByResolution.Keys)}");
        }

        foreach (FalVideoEndpoint endpoint in FalVideoCatalog.Instance.All)
        {
            entries.Add($"{endpoint.Id}: {string.Join(", ", endpoint.PricePerSecondByResolution.Keys)}");
        }

        return string.Join("; ", entries);
    }
}
