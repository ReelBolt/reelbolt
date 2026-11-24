using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.WorkflowEngine.Tests.DesignSystems.Importers;
using ReelBolt.Shared.DesignSystems.ProductMap;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.ProductMap;

public sealed class ProductMapExtractorTests
{
    private static ImportFile File(string path, string content) =>
        new(path, Encoding.UTF8.GetBytes(content), new string('0', 64), null);

    private static ProductMapExtraction Extract(params ImportFile[] files) =>
        ProductMapExtractor.Extract(files);

    [Fact]
    public void Next_App_Router_Fixture_Extracts_Routes_Framework_And_Components()
    {
        ProductMapExtraction result = Extract(NextAppRouterFixture());
        ProductMapDocument map = result.Map;

        Assert.Equal("acme-app", map.Name);
        Assert.Equal("next", map.Framework.Name);
        Assert.Equal("15.1.0", map.Framework.Version);
        Assert.Equal("typescript", map.Framework.Language);
        Assert.Equal("pnpm", map.Framework.PackageManager);
        Assert.Contains("tailwind", map.Framework.Styling);
        Assert.Equal("app-router", map.RoutingStrategy);

        AssertRoute(map, "/", "page", null);
        AssertRoute(map, "/", "layout", "ANY");
        AssertRoute(map, "/pricing", "page", null);
        AssertRoute(map, "/blog/:slug", "page", null);
        AssertRoute(map, "/api/hello", "api", "GET");
        AssertRoute(map, "/api/hello", "api", "POST");

        // UI dependencies come from the runtime dependency set.
        Assert.Contains("tailwindcss", map.Dependencies.Ui);
        Assert.Contains("@radix-ui/react-slot", map.Dependencies.Ui);
        Assert.Contains("lucide-react", map.Dependencies.Ui);
        Assert.Contains("framer-motion", map.Dependencies.Ui);

        // Entry points: app/layout.tsx.
        Assert.Contains(map.EntryPoints, e => e.Path == "app/layout.tsx" && e.Role == "root layout");

        Assert.True(ProductMapValidator.Validate(map).IsValid);
    }

    [Fact]
    public void Extracted_map_never_carries_secrets_from_comments_or_route_paths()
    {
        const string openAi = "sk-" + "AAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string github = "ghp_" + "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

        ProductMapExtraction result = Extract(
            File("package.json", "{\"name\":\"acme\",\"dependencies\":{\"next\":\"15.1.0\",\"react\":\"19.0.0\"}}"),
            File("app/layout.tsx", "export default function RootLayout() { return null; }"),
            File("app/hooks/" + github + "/page.tsx", "export default function Hook() { return null; }"),
            File(
                "components/Billing.tsx",
                "/**\n * Charges the card using " + openAi + " and nothing else.\n */\nexport default function Billing() { return null; }"));

        string serialized = ProductMapJson.ToNode(result.Map).ToJsonString();

        Assert.DoesNotContain(openAi, serialized);
        Assert.DoesNotContain(github, serialized);
        Assert.Contains("[redacted]", serialized);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "SECRET_REDACTED");
        Assert.DoesNotContain(openAi, string.Join("|", result.Diagnostics.Select(d => d.Message + d.Path)));
    }

    [Fact]
    public void Components_Carry_Jsdoc_Responsibility_Required_First_KeyProps_And_Core_Ordering()
    {
        ProductMapDocument map = Extract(NextAppRouterFixture()).Map;

        ProductMapComponent button = map.Components.Single(c => c.Name == "Button");
        Assert.True(button.Core);
        Assert.Equal("Primary action button for the whole app.", button.Responsibility);
        Assert.Equal(new[] { "label", "onClick", "disabled", "size" }, button.KeyProps);

        ProductMapComponent priceCard = map.Components.Single(c => c.Name == "PriceCard");
        Assert.True(priceCard.Core);
        Assert.Equal("Shows one plan and its price.", priceCard.Responsibility);
        Assert.Equal(new[] { "plan", "featured" }, priceCard.KeyProps);

        ProductMapComponent banner = map.Components.Single(c => c.Name == "LegacyBanner");
        Assert.False(banner.Core);

        // Core components are listed first.
        List<bool> flags = map.Components.Select(c => c.Core).ToList();
        for (int i = 1; i < flags.Count; i++)
            Assert.False(flags[i] && !flags[i - 1], "core components must precede non-core ones");

        // Every emitted item is static provenance.
        Assert.All(map.Routes, r => Assert.Equal("static", r.Source));
        Assert.All(map.Components, c => Assert.Equal("static", c.Source));
    }

    [Fact]
    public void Deterministic_Digest_Is_Stable_Across_Runs()
    {
        ProductMapDocument first = Extract(NextAppRouterFixture()).Map;
        ProductMapDocument second = Extract(NextAppRouterFixture()).Map;

        Assert.Equal(ProductMapJson.Serialize(first), ProductMapJson.Serialize(second));
    }

    [Fact]
    public void Caps_Truncate_Files_And_Components()
    {
        var files = new List<ImportFile>();
        for (int i = 0; i < 3001; i++)
            files.Add(File($"components/C{i}.tsx", $"export default function C{i}() {{ return null; }}"));

        files.Add(File("package.json", "{\"name\":\"big\"}"));

        ProductMapExtraction result = ProductMapExtractor.Extract(files);

        Assert.Contains(result.Diagnostics, d => d.Code == "TRUNCATED");
        Assert.True(result.Map.Components.Count <= 300);
    }

    [Fact]
    public void Catastrophic_Route_Input_Completes_Quickly()
    {
        var builder = new StringBuilder(200 * 1024 + 32);
        while (builder.Length < 200 * 1024)
            builder.Append("<Route ");

        ProductMapExtraction result = HostileInput.RunWithin(
            TimeSpan.FromSeconds(20), () => Extract(File("src/routes.tsx", builder.ToString())));
        Assert.Empty(result.Map.Routes);
    }

    [Fact]
    public void Next_Pages_Router_Is_Extracted()
    {
        ProductMapExtraction result = Extract(
            File(
                "package.json",
                "{\"name\":\"pages-app\",\"dependencies\":{\"next\":\"^14.0.0\",\"react\":\"^18.0.0\"},\"devDependencies\":{\"typescript\":\"^5.0.0\"}}"),
            File("tsconfig.json", "{}"),
            File("pages/index.tsx", "export default function Home() { return null; }"),
            File("pages/about.tsx", "export default function About() { return null; }"),
            File("pages/blog/[slug].tsx", "export default function Post() { return null; }"),
            File("pages/_app.tsx", "export default function App() { return null; }"),
            File("pages/api/hello.ts", "export default function handler() { return null; }"));

        Assert.Equal("pages-router", result.Map.RoutingStrategy);
        AssertRoute(result.Map, "/", "page", null);
        AssertRoute(result.Map, "/about", "page", null);
        AssertRoute(result.Map, "/blog/:slug", "page", null);
        AssertRoute(result.Map, "/api/hello", "api", "ANY");
        Assert.DoesNotContain(result.Map.Routes, r => r.File == "pages/_app.tsx");
        Assert.Contains(
            result.Map.EntryPoints,
            e => e.Path == "pages/_app.tsx" && e.Role == "pages application shell");
    }

    [Fact]
    public void Vite_React_Router_Is_Extracted()
    {
        ProductMapExtraction result = Extract(
            File(
                "package.json",
                "{\"name\":\"vite-app\",\"dependencies\":{\"vite\":\"^5.0.0\",\"react\":\"^18.0.0\"}}"),
            File(
                "src/App.tsx",
                "import { Route } from 'react-router-dom';\nexport default function App() { return <Route path=\"/about\" element={<div />} />; }"),
            File("src/routes.tsx", "export const routes = [{ path: \"/dashboard\" }];"),
            File("src/main.tsx", "console.log('boot');"));

        Assert.Equal("vite", result.Map.Framework.Name);
        Assert.Equal("5.0.0", result.Map.Framework.Version);
        Assert.Equal("react-router", result.Map.RoutingStrategy);
        AssertRoute(result.Map, "/about", "page", null);
        AssertRoute(result.Map, "/dashboard", "page", null);
        Assert.Contains(result.Map.EntryPoints, e => e.Path == "src/main.tsx");
    }

    [Fact]
    public void SvelteKit_Routes_Are_Extracted()
    {
        ProductMapExtraction result = Extract(
            File(
                "package.json",
                "{\"name\":\"svelte-app\",\"devDependencies\":{\"@sveltejs/kit\":\"^2.0.0\"}}"),
            File("src/routes/+page.svelte", "<h1>Home</h1>"),
            File("src/routes/about/+page.svelte", "<h1>About</h1>"),
            File("src/routes/api/hello/+server.ts", "export function GET() { return null; }"));

        Assert.Equal("svelte", result.Map.Framework.Name);
        Assert.Equal("2.0.0", result.Map.Framework.Version);
        Assert.Equal("sveltekit", result.Map.RoutingStrategy);
        AssertRoute(result.Map, "/", "page", null);
        AssertRoute(result.Map, "/about", "page", null);
        AssertRoute(result.Map, "/api/hello", "api", "ANY");
    }

    [Fact]
    public void Monorepo_With_Two_Package_Json_Files_Picks_The_Shallowest()
    {
        ProductMapExtraction result = Extract(
            File(
                "package.json",
                "{\"name\":\"root\",\"dependencies\":{\"next\":\"^15.0.0\",\"tailwindcss\":\"^4.0.0\"}}"),
            File(
                "apps/web/package.json",
                "{\"name\":\"web\",\"dependencies\":{\"next\":\"^14.0.0\"}}"));

        Assert.Equal("root", result.Map.Name);
        Assert.Equal("next", result.Map.Framework.Name);
        Assert.Equal("15.0.0", result.Map.Framework.Version);
    }

    private static void AssertRoute(
        ProductMapDocument map,
        string path,
        string kind,
        string? method)
    {
        Assert.Contains(
            map.Routes,
            route => route.Path == path && route.Kind == kind && route.Method == method);
    }

    private static ImportFile[] NextAppRouterFixture() =>
    [
        File(
            "package.json",
            """
            {
              "name": "acme-app",
              "dependencies": {
                "next": "^15.1.0",
                "react": "^19.0.0",
                "tailwindcss": "^4.0.0",
                "@radix-ui/react-slot": "^1.1.0",
                "lucide-react": "^0.460.0",
                "framer-motion": "^11.0.0"
              },
              "devDependencies": {
                "typescript": "^5.6.0"
              }
            }
            """),
        File("pnpm-lock.yaml", string.Empty),
        File("tsconfig.json", "{}"),
        File(
            "app/layout.tsx",
            "export default function RootLayout({ children }: { children: React.ReactNode }) { return <html><body>{children}</body></html>; }"),
        File(
            "app/page.tsx",
            "import Button from \"@/components/ui/Button\";\nexport default function Home() { return <Button />; }"),
        File(
            "app/about/page.tsx",
            "import PriceCard from \"@/components/PriceCard\";\nexport default function About() { return <PriceCard />; }"),
        File(
            "app/(marketing)/pricing/page.tsx",
            "import Button from \"@/components/ui/Button\";\nimport PriceCard from \"@/components/PriceCard\";\nexport default function Pricing() { return <PriceCard />; }"),
        File(
            "app/blog/[slug]/page.tsx",
            "import PriceCard from \"@/components/PriceCard\";\nexport default function Post() { return <PriceCard />; }"),
        File(
            "app/api/hello/route.ts",
            "export async function GET() { return new Response(\"ok\"); }\nexport async function POST() { return new Response(\"ok\"); }"),
        File(
            "components/ui/Button.tsx",
            """
            /**
             * Primary action button for the whole app.
             */
            interface ButtonProps {
              onClick?: () => void;
              label: string;
              disabled?: boolean;
              size?: string;
            }

            export default function Button({ label, disabled }: ButtonProps) {
              return <button disabled={disabled}>{label}</button>;
            }
            """),
        File(
            "components/PriceCard.tsx",
            """
            // Shows one plan and its price.
            interface PriceCardProps {
              plan: string;
              featured?: boolean;
            }

            export default function PriceCard({ plan }: PriceCardProps) {
              return <div>{plan}</div>;
            }
            """),
        File(
            "components/LegacyBanner.tsx",
            "export function LegacyBanner() { return null; }"),
    ];
}
