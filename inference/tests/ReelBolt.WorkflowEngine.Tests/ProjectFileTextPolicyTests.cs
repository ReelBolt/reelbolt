using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The one shared text policy every project-file surface delegates to. These tests pin the
/// extension set and the extension-to-MIME map directly; the drift guard below pins that the
/// four hand-written predicates still answer the same as the shared policy.
/// </summary>
public class ProjectFileTextPolicyTests
{
    [Theory]
    [InlineData("application/octet-stream", "_icons.scss")]
    [InlineData("application/octet-stream", "Contoso.Portal.Client.csproj")]
    [InlineData("application/octet-stream", "theme.sass")]
    [InlineData("application/octet-stream", "vars.less")]
    [InlineData("application/octet-stream", "App.razor")]
    [InlineData("application/octet-stream", "Index.cshtml")]
    [InlineData("application/octet-stream", "web.config")]
    [InlineData("application/octet-stream", "Directory.Build.props")]
    [InlineData("application/octet-stream", "build.targets")]
    [InlineData("application/octet-stream", ".editorconfig")]
    [InlineData("application/octet-stream", "deploy.sh")]
    [InlineData("application/octet-stream", "deploy.ps1")]
    [InlineData("application/octet-stream", "schema.sql")]
    [InlineData("application/octet-stream", "pyproject.toml")]
    [InlineData("application/octet-stream", "app.ini")]
    [InlineData("", "notes.md")]                                  // blank MIME falls back to the extension
    public void Text_extensions_are_readable_with_a_generic_mime(string mime, string name) =>
        ProjectFileTextPolicy.IsLikelyText(mime, name).Should().BeTrue();

    [Theory]
    [InlineData("audio/mpeg", "song.mp3")]
    [InlineData("image/png", "shot.png")]
    [InlineData("application/octet-stream", "shot.png")]
    [InlineData("application/octet-stream", "logo.svg")]          // svg stays an image
    [InlineData("image/svg+xml", "logo.svg")]                     // the xml substring rule must not admit an image
    [InlineData("video/mp2t", "clip.ts")]                         // a transport stream named .ts is binary
    [InlineData("video/mp4", "promo.mp4")]
    public void Binaries_are_still_refused(string mime, string name) =>
        ProjectFileTextPolicy.IsLikelyText(mime, name).Should().BeFalse();

    [Theory]
    [InlineData("application/xml", "web.config")]                // xml MIME still reads as text
    [InlineData("text/xml", "feed.xml")]
    [InlineData("application/json", "package.json")]
    [InlineData("application/javascript", "bundle.js")]
    public void The_image_refusal_does_not_over_reach_to_xml_and_json(string mime, string name) =>
        ProjectFileTextPolicy.IsLikelyText(mime, name).Should().BeTrue();

    [Fact]
    public void A_null_or_blank_mime_falls_back_to_the_extension()
    {
        ProjectFileTextPolicy.IsLikelyText(null, "notes.md").Should().BeTrue();
        ProjectFileTextPolicy.IsLikelyText("", "notes.md").Should().BeTrue();
        ProjectFileTextPolicy.IsLikelyText("   ", "notes.md").Should().BeTrue();
        ProjectFileTextPolicy.IsLikelyText(null, "shot.png").Should().BeFalse();
    }

    [Theory]
    [InlineData("_icons.scss", "text/plain")]                     // allowlisted, no specific mapping
    [InlineData("Contoso.Portal.Client.csproj", "application/xml")]
    [InlineData("Directory.Build.props", "application/xml")]
    [InlineData("build.targets", "application/xml")]
    [InlineData("web.config", "application/xml")]
    [InlineData("notes.md", "text/markdown")]
    [InlineData("post.mdx", "text/markdown")]
    [InlineData("module.mjs", "text/javascript")]
    [InlineData("legacy.cjs", "text/javascript")]
    [InlineData("app.ts", "text/plain")]
    public void Known_text_extensions_resolve_to_a_text_mime(string name, string expected) =>
        ProjectFileTextPolicy.MimeTypeForExtension(name).Should().Be(expected);

    [Fact]
    public void An_unknown_extension_has_no_mapped_mime() =>
        ProjectFileTextPolicy.MimeTypeForExtension("shot.png").Should().BeNull();

    [Theory]
    [InlineData("application/octet-stream", "_icons.scss")]
    [InlineData("application/octet-stream", "Contoso.Portal.Client.csproj")]
    [InlineData("text/markdown", "notes.md")]
    [InlineData("application/json", "package.json")]
    [InlineData("application/octet-stream", "App.tsx")]
    [InlineData("text/plain", "readme.unknownext")]
    [InlineData(null, "notes.md")]
    public void IsKnownBinary_is_false_for_text(string? mime, string name) =>
        ProjectFileTextPolicy.IsKnownBinary(mime, name).Should().BeFalse();

    [Theory]
    [InlineData("application/octet-stream", "song.mp3")]
    [InlineData("application/octet-stream", "shot.png")]
    [InlineData("application/octet-stream", "logo.svg")]          // an image, by extension
    [InlineData("application/octet-stream", "manual.pdf")]
    [InlineData("application/octet-stream", "bundle.zip")]
    [InlineData("audio/mpeg", "song.unknownext")]                 // a binary family by MIME
    [InlineData("image/svg+xml", "logo.unknownext")]              // an image, by MIME
    [InlineData("application/pdf", "document.unknownext")]
    public void IsKnownBinary_is_true_for_binaries(string mime, string name) =>
        ProjectFileTextPolicy.IsKnownBinary(mime, name).Should().BeTrue();

    [Fact]
    public void The_read_limit_is_sixteen_mebibytes() =>
        ProjectFileTextPolicy.MaxReadBytes.Should().Be(16 * 1024 * 1024);

    [Fact]
    public void Every_allowlisted_extension_has_a_mime_and_svg_is_not_allowlisted()
    {
        ProjectFileTextPolicy.TextExtensions.Should().NotBeEmpty();
        ProjectFileTextPolicy.TextExtensions.Should().NotContain(".svg");
        ProjectFileTextPolicy.TextExtensions.Should().Contain(new[]
        {
            ".scss", ".sass", ".less", ".csproj", ".razor", ".cshtml", ".config",
            ".props", ".targets", ".editorconfig", ".sh", ".ps1", ".sql", ".toml", ".ini"
        });

        foreach (string extension in ProjectFileTextPolicy.TextExtensions)
        {
            ProjectFileTextPolicy.MimeTypeForExtension("sample" + extension)
                .Should().NotBeNull($"every allowlisted extension must map to a MIME ({extension})");
        }
    }

    // ── The narrower write set (the model's authoring tools) ─────────────────

    [Theory]
    [InlineData("deploy.bat")]                                    // a script/execution-bearing extension, never admitted
    [InlineData("x.psm1")]
    [InlineData("Makefile")]                                      // extensionless: an extension set cannot admit it
    [InlineData("evil.sh")]                                       // excluded from the write set
    [InlineData("evil.sh/.")]                                     // a path, not the name the writer stores
    [InlineData("evil.sh/")]
    [InlineData("dir/evil.tsx")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("..")]
    public void IsWritableText_is_false_for_a_name_the_write_set_does_not_admit(string? name) =>
        ProjectFileTextPolicy.IsWritableText(name).Should().BeFalse();

    [Theory]
    [InlineData("_icons.scss")]
    [InlineData("scene-01.tsx")]
    [InlineData("notes.md")]
    [InlineData("data.json")]
    public void IsWritableText_is_true_for_an_admitted_extension(string name) =>
        ProjectFileTextPolicy.IsWritableText(name).Should().BeTrue();

    [Theory]
    [InlineData("application/octet-stream", "build.sh")]
    [InlineData("application/octet-stream", "deploy.ps1")]
    [InlineData("application/octet-stream", "Contoso.Portal.Client.csproj")]
    [InlineData("application/octet-stream", "Directory.Build.props")]
    [InlineData("application/octet-stream", "build.targets")]
    [InlineData("application/octet-stream", "App.razor")]
    [InlineData("application/octet-stream", "Index.cshtml")]
    [InlineData("application/octet-stream", "web.config")]
    public void The_split_leaves_the_read_set_unchanged(string mime, string name) =>
        ProjectFileTextPolicy.IsLikelyText(mime, name).Should().BeTrue();

    [Fact]
    public void The_write_set_is_the_read_set_minus_the_script_build_and_config_types()
    {
        ProjectFileTextPolicy.WriteExtensions.Should().NotBeEmpty();
        ProjectFileTextPolicy.WriteExtensions.Should().BeSubsetOf(ProjectFileTextPolicy.TextExtensions);
        ProjectFileTextPolicy.WriteExtensions.Should().NotContain(new[]
        {
            ".sh", ".ps1", ".csproj", ".props", ".targets", ".razor", ".cshtml", ".config"
        });
        ProjectFileTextPolicy.WriteExtensions.Should().Contain(new[] { ".scss", ".sass", ".less", ".md", ".tsx", ".json" });
    }

    [Fact]
    public void Every_writable_extension_is_also_readable()
    {
        // The write predicate must always be a subset of the read predicate: nothing the model may
        // author may be something a tool would then refuse to read back.
        foreach (string extension in ProjectFileTextPolicy.WriteExtensions)
        {
            ProjectFileTextPolicy.IsWritableText("sample" + extension)
                .Should().BeTrue($"{extension} is in the write set");
            ProjectFileTextPolicy.IsLikelyText("application/octet-stream", "sample" + extension)
                .Should().BeTrue($"{extension} must also be readable");
        }
    }
}

/// <summary>
/// Every hand-written predicate must answer exactly what the shared policy answers. A
/// reflection-invoked matrix keeps a future edit to one of them from drifting a surface away from
/// the others. The engine's read gate is deliberately absent: it decides from the bytes
/// (<c>ProjectFileWorkspace.ReadFileAsync</c>, pinned by
/// <see cref="ProjectFileWorkspaceTextReadTests"/>) rather than from this allowlist, because an
/// allowlist cannot both admit an unlisted text file and refuse a listed binary.
/// </summary>
public class ProjectFileTextDriftGuardTests
{
    private static readonly (string Mime, string Name)[] FixedCases =
    {
        ("audio/mpeg", "song.mp3"),
        ("image/png", "shot.png"),
        ("application/octet-stream", "shot.png"),
        ("application/octet-stream", "logo.svg"),
        ("video/mp2t", "clip.ts"),
        ("text/html", "Index.cshtml"),
        ("image/svg+xml", "logo.svg"),   // an image: every predicate must answer false
        ("", "notes.md"),
        (null!, "notes.md"),
        ("application/octet-stream", "Contoso.Portal.Client.csproj"),
    };

    private static IEnumerable<(string Mime, string Name)> Matrix()
    {
        foreach ((string mime, string name) in FixedCases)
            yield return (mime, name);

        foreach (string extension in ProjectFileTextPolicy.TextExtensions)
            yield return ("application/octet-stream", "sample" + extension);
    }

    [Fact]
    public void Every_predicate_answers_identically()
    {
        foreach ((string mime, string name) in Matrix())
        {
            bool expected = ProjectFileTextPolicy.IsLikelyText(mime, name);

            Check("ProjectFilesController.IsLikelyText",
                InvokePrivate(typeof(ProjectFilesController), "IsLikelyText", mime, name));
            Check("AssistantFileTools.IsLikelyText",
                InvokePrivate(typeof(AssistantFileTools), "IsLikelyText", mime, name));
            Check("BriefFileResolver.IsLikelyText",
                InvokePrivate(typeof(BriefFileResolver), "IsLikelyText", mime, name));

            void Check(string site, bool actual) =>
                actual.Should().Be(expected, $"{site} diverged for ({mime}, {name})");
        }
    }

    [Fact]
    public void Svg_is_refused_by_the_policy_and_every_api_predicate()
    {
        // image/svg+xml contains "xml"; the audio/video/image refusal must sit above that rule so an
        // image never enters the text path, and every predicate must agree it is not text.
        ProjectFileTextPolicy.IsLikelyText("image/svg+xml", "logo.svg").Should().BeFalse();
        InvokePrivate(typeof(ProjectFilesController), "IsLikelyText", "image/svg+xml", "logo.svg").Should().BeFalse();
        InvokePrivate(typeof(AssistantFileTools), "IsLikelyText", "image/svg+xml", "logo.svg").Should().BeFalse();
        InvokePrivate(typeof(BriefFileResolver), "IsLikelyText", "image/svg+xml", "logo.svg").Should().BeFalse();
    }

    private static bool InvokePrivate(Type type, string methodName, string? mime, string? name)
    {
        MethodInfo? method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull($"{type.Name}.{methodName} must exist as a private static predicate");
        return (bool)method!.Invoke(null, new object?[] { mime, name })!;
    }

    [Fact]
    public void Every_write_predicate_consumer_answers_like_the_policy()
    {
        // The write predicate is narrower than the read predicate: a script, build or config
        // extension is refused even with a text/plain MIME. Pin the assistant's write call site to
        // the shared policy, so a future edit cannot quietly widen it back to the read rule. (The
        // engine's EditProjectFile/WriteProjectFile consumers are pinned by ProjectFileAgentToolsTests.)
        (string Mime, string Name)[] cases =
        {
            ("text/plain", "build.sh"),
            ("text/plain", "deploy.ps1"),
            ("text/plain", "Contoso.Portal.Client.csproj"),
            ("text/plain", "Directory.Build.props"),
            ("text/plain", "build.targets"),
            ("text/plain", "App.razor"),
            ("text/plain", "Index.cshtml"),
            ("text/plain", "web.config"),
            ("text/plain", "scene-01.tsx"),
            ("text/plain", "_icons.scss"),
            ("application/octet-stream", "theme.sass"),
            ("text/markdown", "notes.md"),
            ("application/octet-stream", "shot.png"),
            ("application/octet-stream", "logo.svg"),
        };

        foreach ((string mime, string name) in cases)
        {
            bool expected = ProjectFileTextPolicy.IsWritableText(name);
            InvokeTryPrepareWrite(mime, name)
                .Should().Be(expected, $"AssistantFileTools.TryPrepareWrite diverged for ({mime}, {name})");
        }
    }

    [Fact]
    public void TryPrepareWrite_refuses_a_bat_name_with_a_text_plain_mime()
    {
        // The MIME type can no longer admit anything. Before this, ("text/plain", "deploy.bat") was
        // writable: the deny list did not name .bat and the predicate trusted text/*.
        InvokeTryPrepareWrite("text/plain", "deploy.bat").Should().BeFalse();
        InvokeTryPrepareWrite("application/octet-stream", "deploy.bat").Should().BeFalse();
    }

    /// <summary>
    /// Invokes the assistant's private write gate with a valid (tiny) body and returns its verdict,
    /// so the test pins the actual call site rather than a helper that might be bypassed.
    /// </summary>
    private static bool InvokeTryPrepareWrite(string mime, string name)
    {
        MethodInfo? method = typeof(AssistantFileTools)
            .GetMethod("TryPrepareWrite", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("AssistantFileTools.TryPrepareWrite must exist as a private static method");

        object?[] args =
            [null, name, mime, Convert.ToBase64String(Encoding.UTF8.GetBytes("x")), null, null];
        return (bool)method!.Invoke(null, args)!;
    }
}
