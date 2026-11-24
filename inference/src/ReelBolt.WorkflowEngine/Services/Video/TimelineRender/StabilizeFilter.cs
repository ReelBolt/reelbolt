namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>
/// The ONE definition of the two-pass <c>vidstab</c> recipe. The timeline render
/// (<see cref="RegionFilterGraphBuilder"/>) and the editor's stabilised preview proxy
/// (<see cref="StabilizedProxyArgvBuilder"/>) both build their filters from here, so the picture the
/// viewer shows and the picture the render writes cannot drift apart on a parameter.
/// </summary>
/// <remarks>
/// The render stabilises the clip's own span (trimmed, retimed, at the target fps); the preview proxy
/// stabilises the whole source at proxy size. The motion estimate of a short clip's edges can therefore
/// differ slightly between the two, which is why the viewer marks the stabilised preview approximate.
/// Bump <see cref="RecipeVersion"/> when any parameter below changes: it is part of the proxy's cache
/// identity, so a changed recipe re-derives instead of serving the old look.
/// </remarks>
internal static class StabilizeFilter
{
    /// <summary>Part of the stabilised proxy's cache identity.</summary>
    public const int RecipeVersion = 1;

    public const int Shakiness = 5;
    public const int Accuracy = 15;
    public const int Smoothing = 10;
    public const int OptZoom = 1;
    public const string Interpolation = "bilinear";

    /// <summary>Pass 1: analyse the picture, writing the motion file to <paramref name="trfPath"/>.</summary>
    public static string Detect(string trfPath) =>
        $"vidstabdetect=shakiness={Shakiness}:accuracy={Accuracy}:result='{EscapePath(trfPath)}'";

    /// <summary>Pass 2: apply the motion file written by <see cref="Detect"/>.</summary>
    public static string Transform(string trfPath) =>
        $"vidstabtransform=input='{EscapePath(trfPath)}':smoothing={Smoothing}:optzoom={OptZoom}:interpol={Interpolation}";

    /// <summary>
    /// Escapes a path for use inside a single-quoted filter option. Paths are the engine's own scratch
    /// paths, so this is belt-and-braces, not the boundary.
    /// </summary>
    public static string EscapePath(string path) =>
        path.Replace("\\", "\\\\").Replace("'", "\\'").Replace(":", "\\:");
}
