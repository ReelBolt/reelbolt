using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.WorkflowEngine.Services.Blocks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Blocks;

public sealed class BlockCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "blocks-test-" + Guid.NewGuid().ToString("N"));

    public BlockCatalogTests() => Directory.CreateDirectory(Path.Combine(_root, "src"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Writes src files and a catalog; returns the catalog entry objects so a test can tweak them.</summary>
    internal static void WriteCorpus(string root, IEnumerable<object> blocks, Dictionary<string, string> files)
    {
        foreach ((string path, string content) in files)
        {
            string full = Path.Combine(root, "src", path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        File.WriteAllText(Path.Combine(root, "catalog.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, blocks }));
    }

    internal static object Block(string id, string category, string[] filePaths, Dictionary<string, string> contents, string? shaOverride = null,
        string[]? requires = null) => new
        {
            id,
            version = "1.0.0",
            title = id,
            category,
            description = "A block called " + id,
            tags = new[] { "t" },
            requires = (requires ?? []).Select(r => new { name = r, version = "1.0.0" }).ToArray(),
            files = filePaths.Select(p => new { path = p, sha256 = shaOverride ?? Sha(contents[p]) }).ToArray(),
            props = "{}",
            example = "<X />"
        };

    internal static BlockCatalog Load(string root) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Blocks:CorpusPath"] = root }).Build(),
            NullLogger<BlockCatalog>.Instance);

    [Fact]
    public void A_valid_block_loads_with_its_verified_content()
    {
        Dictionary<string, string> files = new() { ["a-one/A.tsx"] = "export const A = 1;\n", ["_shared/h.ts"] = "export const h = 2;\n" };
        WriteCorpus(_root, [Block("a-one", "titles", ["_shared/h.ts", "a-one/A.tsx"], files)], files);

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().ContainSingle().Which.Files.Select(f => f.Path).Should().BeEquivalentTo("_shared/h.ts", "a-one/A.tsx");
        catalog.Find("a-one")!.Files.First(f => f.Path == "a-one/A.tsx").Content.Should().Be("export const A = 1;\n");
        catalog.Failures.Should().BeEmpty();
    }

    [Fact]
    public void A_tampered_file_drops_that_block_only()
    {
        Dictionary<string, string> files = new() { ["good/G.tsx"] = "g", ["bad/B.tsx"] = "b" };
        WriteCorpus(_root, [Block("good", "titles", ["good/G.tsx"], files), Block("bad", "titles", ["bad/B.tsx"], files)], files);
        File.WriteAllText(Path.Combine(_root, "src", "bad", "B.tsx"), "tampered");

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Select(b => b.Id).Should().Equal("good");
        catalog.Failures.Should().ContainSingle(f => f.Id == "bad" && f.Reason.Contains("sha256"));
    }

    [Fact]
    public void A_duplicate_id_drops_every_claimant()
    {
        Dictionary<string, string> files = new() { ["dup/D.tsx"] = "d", ["ok/O.tsx"] = "o" };
        WriteCorpus(_root, [Block("dup", "titles", ["dup/D.tsx"], files), Block("dup", "titles", ["dup/D.tsx"], files), Block("ok", "shapes", ["ok/O.tsx"], files)], files);

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Select(b => b.Id).Should().Equal("ok");
        catalog.Failures.Should().Contain(f => f.Id == "dup" && f.Reason.Contains("duplicate"));
    }

    [Theory]
    [InlineData("../evil/E.tsx")]
    [InlineData("/etc/passwd")]
    [InlineData("bad/../../E.tsx")]
    [InlineData("bad\\E.tsx")]
    [InlineData("other/E.tsx")]
    [InlineData("bad/E.exe")]
    [InlineData("bad/.hidden.tsx")]
    [InlineData("E.tsx")]
    public void A_bad_path_drops_the_block_and_reads_nothing_outside_the_corpus(string badPath)
    {
        Dictionary<string, string> files = new() { ["bad/B.tsx"] = "b" };
        object block = new
        {
            id = "bad",
            version = "1.0.0",
            title = "bad",
            category = "titles",
            description = "d",
            tags = new string[0],
            requires = new object[0],
            files = new[] { new { path = badPath, sha256 = Sha("b") } },
            props = "",
            example = ""
        };
        WriteCorpus(_root, [block], files);

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().BeEmpty();
        catalog.Failures.Should().ContainSingle(f => f.Id == "bad");
    }

    [Fact]
    public void A_symlinked_file_drops_the_block()
    {
        Dictionary<string, string> files = new() { ["lnk/L.tsx"] = "real", ["other/Real.tsx"] = "real" };
        WriteCorpus(_root, [Block("lnk", "titles", ["lnk/L.tsx"], files)], files);
        string link = Path.Combine(_root, "src", "lnk", "L.tsx");
        File.Delete(link);
        File.CreateSymbolicLink(link, Path.Combine(_root, "src", "other", "Real.tsx"));

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().BeEmpty();
        catalog.Failures.Should().ContainSingle(f => f.Reason.Contains("symbolic link"));
    }

    [Fact]
    public void A_symlinked_directory_drops_the_block()
    {
        Dictionary<string, string> files = new() { ["real/L.tsx"] = "x", ["lnk/L.tsx"] = "x" };
        WriteCorpus(_root, [Block("lnk", "titles", ["lnk/L.tsx"], files)], files);
        Directory.Delete(Path.Combine(_root, "src", "lnk"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(_root, "src", "lnk"), Path.Combine(_root, "src", "real"));

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().BeEmpty();
        catalog.Failures.Should().ContainSingle(f => f.Reason.Contains("symbolic link"));
    }

    [Fact]
    public void An_unknown_category_drops_the_block()
    {
        Dictionary<string, string> files = new() { ["x/X.tsx"] = "x" };
        WriteCorpus(_root, [Block("x", "gizmos", ["x/X.tsx"], files)], files);

        Load(_root).Blocks.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_corpus_yields_an_empty_catalog_not_an_exception()
    {
        BlockCatalog catalog = Load(Path.Combine(_root, "nope"));

        catalog.Blocks.Should().BeEmpty();
        catalog.Find("anything").Should().BeNull();
    }

    [Fact]
    public void An_unparsable_catalog_json_yields_an_empty_catalog()
    {
        File.WriteAllText(Path.Combine(_root, "catalog.json"), "{ not json");

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().BeEmpty();
    }

    [Fact]
    public void An_oversize_corpus_file_drops_its_block()
    {
        Dictionary<string, string> files = new() { ["big/B.tsx"] = new string('x', 256 * 1024 + 1) };
        WriteCorpus(_root, [Block("big", "titles", ["big/B.tsx"], files)], files);

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().BeEmpty();
        catalog.Failures.Should().NotBeEmpty();
    }

    [Fact]
    public void A_non_utf8_corpus_file_drops_its_block()
    {
        Dictionary<string, string> files = new() { ["bin/B.tsx"] = "x" };
        WriteCorpus(_root, [Block("bin", "titles", ["bin/B.tsx"], files)], files);
        byte[] bytes = [0xff, 0xfe, 0xfd, 0x80];
        File.WriteAllBytes(Path.Combine(_root, "src", "bin", "B.tsx"), bytes);
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        WriteCorpus(_root, [Block("bin", "titles", ["bin/B.tsx"], files, shaOverride: sha)], new());

        BlockCatalog catalog = Load(_root);

        catalog.Blocks.Should().BeEmpty();
        catalog.Failures.Should().NotBeEmpty();
    }

    [Fact]
    public void The_shipped_corpus_loads_every_block_in_catalog_json()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "blocks", "catalog.json")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("blocks/catalog.json must be found by walking up from the test binaries");

        string shipped = Path.Combine(dir!, "blocks");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(shipped, "catalog.json")));
        int declared = doc.RootElement.GetProperty("blocks").GetArrayLength();

        BlockCatalog catalog = Load(shipped);

        catalog.Failures.Should().BeEmpty();
        catalog.Blocks.Should().HaveCount(declared);
    }
}
