using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ReelBolt.WorkflowEngine.Agents.Tools;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

public class SandboxPackageCatalogTests
{
    private static string TemplateDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "sandbox", "template", "package-catalog.json")))
            dir = Path.GetDirectoryName(dir);
        return dir is null ? throw new InvalidOperationException("repo root not found") : Path.Combine(dir, "sandbox", "template");
    }

    [Fact]
    public void Catalog_FileEqualsPackageJsonDependencies_AndEngineList()
    {
        string template = TemplateDir();
        using JsonDocument cat = JsonDocument.Parse(File.ReadAllText(Path.Combine(template, "package-catalog.json")));
        using JsonDocument pkg = JsonDocument.Parse(File.ReadAllText(Path.Combine(template, "package.json")));

        Dictionary<string, string> fromFile = cat.RootElement.EnumerateArray()
            .ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("version").GetString()!);
        Dictionary<string, string> fromPackageJson = pkg.RootElement.GetProperty("dependencies").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        Dictionary<string, string> inEngine = ReactRemotionSandboxTools.SandboxPackageCatalog.ToDictionary(c => c.Name, c => c.Version);

        Assert.Equal(fromPackageJson.OrderBy(p => p.Key), fromFile.OrderBy(p => p.Key));
        Assert.Equal(fromFile.OrderBy(p => p.Key), inEngine.OrderBy(p => p.Key));
        Assert.All(fromFile.Values, v => Assert.Matches(@"^\d+\.\d+\.\d+$", v));
        Assert.DoesNotContain("@remotion/google-fonts", fromFile.Keys);
        Assert.Equal("3.15.1", fromFile["gsap"]);
        Assert.Contains("@remotion/gif", fromFile.Keys);
        Assert.Contains("@remotion/lottie", fromFile.Keys);
        Assert.Contains("@remotion/transitions", fromFile.Keys);
    }

    [Fact]
    public void InstallFromCatalog_CatalogNames_AreAlreadyInstalled()
    {
        using JsonDocument doc = JsonDocument.Parse(ReactRemotionSandboxTools.InstallFromCatalog(["framer-motion", "gsap", "@remotion/shapes@4.0.0"]));
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("framer-motion: already installed at 14.14.0", doc.RootElement.GetProperty("message").GetString());
        Assert.Contains("gsap: already installed at 3.15.1", doc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void InstallFromCatalog_Unknown_RefusesWithCatalogAndNoNetworkStatement()
    {
        using JsonDocument doc = JsonDocument.Parse(ReactRemotionSandboxTools.InstallFromCatalog(["left-pad@1", "framer-motion"]));
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        string error = doc.RootElement.GetProperty("error").GetString()!;
        Assert.Contains("no network", error);
        Assert.Contains("left-pad", error);
        Assert.Contains("lucide-react@1.77.0", doc.RootElement.GetProperty("availablePackages").EnumerateArray().Select(e => e.GetString()));
    }
}
