using System.Text.Json;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// The packages baked into the sandbox image (<c>sandbox/template/package-catalog.json</c>, which a drift test pins to the
/// template's <c>package.json</c> and to this list). The sandbox has no network, so <c>InstallNpmPackages</c> answers from
/// this list and never reaches the sandbox <c>POST /packages</c> endpoint.
/// </summary>
public partial class ReactRemotionSandboxTools
{
    internal static readonly IReadOnlyList<(string Name, string Version, string Purpose)> SandboxPackageCatalog =
    [
        ("@phosphor-icons/react", "2.1.10", "Icon set"),
        ("@react-spring/web", "10.1.2", "Physics-based animation"),
        ("@react-three/drei", "10.7.9", "Helpers for react-three-fiber"),
        ("@react-three/fiber", "9.8.1", "React renderer for three.js"),
        ("@remotion/captions", "4.0.542", "Caption data types and helpers"),
        ("@remotion/cli", "4.0.542", "CLI: compositions, render, bundle"),
        ("@remotion/fonts", "4.0.542", "Load local font files (no network)"),
        ("@remotion/gif", "4.0.542", "GIF playback"),
        ("@remotion/layout-utils", "4.0.542", "Text measurement and fit"),
        ("@remotion/lottie", "4.0.542", "Lottie animation playback"),
        ("@remotion/media-utils", "4.0.542", "Audio/video metadata and waveform helpers"),
        ("@remotion/motion-blur", "4.0.542", "Trail and camera motion blur"),
        ("@remotion/noise", "4.0.542", "Perlin-style noise"),
        ("@remotion/paths", "4.0.542", "SVG path utilities"),
        ("@remotion/renderer", "4.0.542", "Server-side rendering"),
        ("@remotion/shapes", "4.0.542", "SVG shape generators"),
        ("@remotion/three", "4.0.542", "Remotion integration for three.js"),
        ("@remotion/transitions", "4.0.542", "Scene transitions"),
        ("@remotion/zod-types", "4.0.542", "Zod types for composition props"),
        ("d3", "7.9.0", "Data-driven visualisation"),
        ("framer-motion", "13.4.6", "Declarative animation"),
        ("gsap", "3.15.0", "Timeline animation, driven by the frame (see the ReelBolt gsap guide)"),
        ("lottie-web", "5.13.0", "Lottie runtime (peer of @remotion/lottie)"),
        ("lucide-react", "1.59.0", "Icon set"),
        ("react", "19.3.0", "UI runtime"),
        ("react-dom", "19.3.0", "UI runtime"),
        ("remotion", "4.0.542", "Core framework"),
        ("simplex-noise", "4.0.3", "Simplex noise"),
        ("three", "0.172.0", "3D graphics"),
        ("zod", "4.6.7", "Schema validation for composition props")
    ];

    /// <summary>The npm package name of a spec such as <c>pkg</c>, <c>pkg@1</c>, <c>@scope/pkg@latest</c>.</summary>
    internal static string PackageNameOf(string spec)
    {
        string s = (spec ?? string.Empty).Trim();
        int at = s.IndexOf('@', s.StartsWith('@') ? 1 : 0);
        return at > 0 ? s[..at] : s;
    }

    internal static string InstallFromCatalog(IReadOnlyList<string> packages)
    {
        List<string> installed = [];
        List<string> unavailable = [];
        foreach (string spec in packages)
        {
            string name = PackageNameOf(spec);
            var hit = SandboxPackageCatalog.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit.Name is null)
                unavailable.Add(name);
            else
                installed.Add($"{hit.Name}: already installed at {hit.Version}");
        }

        if (unavailable.Count == 0)
            return JsonSerializer.Serialize(new { ok = true, message = string.Join("; ", installed) }, ResultJsonOptions);

        return JsonSerializer.Serialize(new
        {
            ok = false,
            error = "The sandbox has no network, so packages cannot be installed. Not available: " + string.Join(", ", unavailable)
                + ". Use only the preinstalled packages.",
            alreadyInstalled = installed,
            availablePackages = SandboxPackageCatalog.Select(c => $"{c.Name}@{c.Version}").ToArray()
        }, ResultJsonOptions);
    }
}
