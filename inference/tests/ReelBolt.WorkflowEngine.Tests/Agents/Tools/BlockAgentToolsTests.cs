using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Blocks;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Tests.Services.Blocks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents.Tools;

public sealed class BlockAgentToolsTests : IDisposable
{
    private const string PackageJson = "{\"dependencies\":{\"remotion\":\"4.0.542\",\"react\":\"^19.2.0\"}}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "block-tools-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _workspace = new(StringComparer.Ordinal) { ["package.json"] = PackageJson };
    private readonly List<string> _writes = [];
    private readonly Dictionary<string, string> _src = new()
    {
        ["wipe/Wipe.tsx"] = "export const Wipe = () => null;\n",
        ["_shared/motion.ts"] = "export const ease = 1;\n"
    };

    private Func<string, Exception?>? _listFault;
    private Func<string, SandboxActionResult?>? _writeFault;

    public BlockAgentToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal static BlockAgentTools CreateInert() => new(
        Mock.Of<IWorkflowExecutionContextAccessor>(), Mock.Of<ISandboxSessionFactory>(),
        Mock.Of<IBlockCatalog>(c => c.Blocks == new List<BlockEntry>()), NullLogger<BlockAgentTools>.Instance);

    private BlockAgentTools Create(string[]? requires = null)
    {
        BlockCatalogTests.WriteCorpus(_root,
        [
            BlockCatalogTests.Block("wipe", "transitions", ["_shared/motion.ts", "wipe/Wipe.tsx"], _src, requires: requires ?? ["remotion"]),
            BlockCatalogTests.Block("needs-map", "maps", ["_shared/motion.ts", "wipe/Wipe.tsx"], _src, requires: ["mapbox-gl"])
        ], _src);
        // "needs-map" shares wipe's files only for brevity; its folder is "wipe", not its id, so it is dropped.
        IBlockCatalog catalog = BlockCatalogTests.Load(_root);

        Mock<ISandboxSession> session = new();
        session.Setup(s => s.ListFilesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string dir, CancellationToken _) =>
        {
            if (_listFault?.Invoke(dir) is { } fault)
                throw fault;
            string prefix = dir == "." ? "" : dir + "/";
            List<SandboxFileEntry> entries = _workspace.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !k[prefix.Length..].Contains('/'))
                .Select(k => new SandboxFileEntry(k[prefix.Length..], false, 1, DateTime.UtcNow)).ToList();
            if (entries.Count == 0 && !_workspace.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                throw new SandboxRequestFailedException(HttpStatusCode.NotFound, "no such directory");
            return Task.FromResult<IReadOnlyList<SandboxFileEntry>>(entries);
        });
        session.Setup(s => s.ReadTextAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string p, CancellationToken _) => Task.FromResult(new SandboxTextFile(p, _workspace[p])));
        session.Setup(s => s.WriteTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string p, string c, CancellationToken _) =>
            {
                if (_writeFault?.Invoke(p) is { } faulted)
                    return Task.FromResult(faulted);
                _writes.Add(p);
                _workspace[p] = c;
                return Task.FromResult(new SandboxActionResult(true));
            });
        Mock<ISandboxSessionFactory> factory = new();
        factory.Setup(f => f.Create(It.IsAny<Guid>())).Returns(session.Object);
        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.SetupGet(a => a.Current).Returns(new WorkflowExecutionContext { ExecutionId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), CorrelationId = "c" });
        return new BlockAgentTools(accessor.Object, factory.Object, catalog, NullLogger<BlockAgentTools>.Instance);
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    [Fact]
    public async Task AddBlock_writes_only_under_the_fixed_prefix_and_records_the_lock()
    {
        BlockAgentTools tools = Create();

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        _writes.Should().BeEquivalentTo("src/blocks/_shared/motion.ts", "src/blocks/wipe/Wipe.tsx", "src/blocks/blocks.lock.json");
        _writes.Should().OnlyContain(p => p.StartsWith("src/blocks/") && !p.Contains(".."));
        JsonElement lockBlock = Json(_workspace["src/blocks/blocks.lock.json"]).GetProperty("blocks")[0];
        lockBlock.GetProperty("id").GetString().Should().Be("wipe");
        lockBlock.GetProperty("version").GetString().Should().Be("1.0.0");
        lockBlock.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("sha256").GetString()).Should()
            .BeEquivalentTo(new[] { _src["wipe/Wipe.tsx"], _src["_shared/motion.ts"] }.Select(BlockCatalogTests.Sha));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/abs/path")]
    [InlineData("wipe/../x")]
    [InlineData("")]
    [InlineData("src/blocks/wipe")]
    public async Task AddBlock_takes_an_id_never_a_path_so_traversal_cannot_write(string hostile)
    {
        BlockAgentTools tools = Create();

        JsonElement r = Json(await tools.AddBlock(hostile));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AddBlock_is_idempotent_when_files_are_identical()
    {
        BlockAgentTools tools = Create();
        await tools.AddBlock("wipe");
        _writes.Clear();

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        r.GetProperty("unchanged").GetArrayLength().Should().Be(2);
        _writes.Should().BeEmpty("identical files and an unchanged lock need no writes");
    }

    [Fact]
    public async Task AddBlock_refuses_an_edited_file_and_writes_nothing()
    {
        BlockAgentTools tools = Create();
        _workspace["src/blocks/wipe/Wipe.tsx"] = "// my edit\n";

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("error").GetString().Should().Contain("src/blocks/wipe/Wipe.tsx");
        _writes.Should().BeEmpty("the shared helper must not be written when another file conflicts");
        _workspace["src/blocks/wipe/Wipe.tsx"].Should().Be("// my edit\n");
        _workspace.Should().NotContainKey("src/blocks/blocks.lock.json");
    }

    [Fact]
    public async Task AddBlock_refuses_a_missing_package_naming_the_alternative()
    {
        BlockAgentTools tools = Create(requires: ["remotion", "d3-geo"]);

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("code").GetString().Should().Be("missing_package");
        r.GetProperty("error").GetString().Should().Contain("d3-geo").And.Contain("InstallNpmPackages");
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AddBlock_upserts_the_lock_entry_keeping_other_blocks()
    {
        BlockAgentTools tools = Create();
        _workspace["src/blocks/blocks.lock.json"] =
            "{\"blocks\":[{\"id\":\"wipe\",\"version\":\"0.1.0\",\"files\":[]},{\"id\":\"zzz\",\"version\":\"2.0.0\",\"files\":[]}]}";

        await tools.AddBlock("wipe");

        JsonElement[] blocks = Json(_workspace["src/blocks/blocks.lock.json"]).GetProperty("blocks").EnumerateArray().ToArray();
        blocks.Select(b => b.GetProperty("id").GetString()).Should().Equal("wipe", "zzz");
        blocks[0].GetProperty("version").GetString().Should().Be("1.0.0");
        blocks[0].GetProperty("files").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task AddBlock_reports_an_unknown_id_as_data()
    {
        JsonElement r = Json(await Create().AddBlock("nope"));

        r.GetProperty("code").GetString().Should().Be("unknown_block");
    }

    [Fact]
    public void ListBlocks_is_compact_filters_by_category_and_query_and_rejects_unknown_categories()
    {
        BlockAgentTools tools = Create();

        tools.ListBlocks().Should().Contain("wipe [transitions]");
        tools.ListBlocks("titles").Should().StartWith("No block matches");
        tools.ListBlocks(null, "wipe").Should().Contain("wipe");
        tools.ListBlocks("gizmos").Should().Contain("Unknown category").And.Contain("transitions, titles, captions, charts, maps, effects, backgrounds, devices, shapes");
    }

    private const string LockFile = "src/blocks/blocks.lock.json";

    [Fact]
    public async Task AddBlock_write_not_ok_reports_write_failed_and_what_was_written()
    {
        BlockAgentTools tools = Create();
        _writeFault = p => p.EndsWith("Wipe.tsx") ? new SandboxActionResult(false) : null;

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("code").GetString().Should().Be("write_failed");
        r.GetProperty("error").GetString().Should().Contain("src/blocks/_shared/motion.ts").And.Contain("lock was not updated");
        _workspace.Should().NotContainKey(LockFile);
    }

    [Fact]
    public async Task AddBlock_thrown_write_reports_write_failed_not_nothing_written()
    {
        BlockAgentTools tools = Create();
        _writeFault = p => p.EndsWith("Wipe.tsx") ? throw new SandboxRequestFailedException(HttpStatusCode.BadGateway, "boom") : null;

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("code").GetString().Should().Be("write_failed");
        r.GetProperty("error").GetString().Should().Contain("Written before the failure: src/blocks/_shared/motion.ts");
        r.GetProperty("error").GetString().Should().NotContain("Nothing further");
    }

    [Fact]
    public async Task AddBlock_lock_write_failure_is_ok_with_lockUpdated_false_and_a_warning()
    {
        BlockAgentTools tools = Create();
        _writeFault = p => p == LockFile ? throw new SandboxRequestFailedException(HttpStatusCode.BadGateway, "boom") : null;

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        r.GetProperty("lockUpdated").GetBoolean().Should().BeFalse();
        r.GetProperty("warning").GetString().Should().Contain("could not be written");
    }

    [Fact]
    public async Task AddBlock_non_404_list_failure_is_sandbox_error()
    {
        BlockAgentTools tools = Create();
        _listFault = _ => new SandboxRequestFailedException(HttpStatusCode.InternalServerError, "down");

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeFalse();
        r.GetProperty("code").GetString().Should().Be("sandbox_error");
        _writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"blocks\":{}}")]
    [InlineData("{\"blocks\":[{\"id\":5}]}")]
    [InlineData("{\"blocks\":[3]}")]
    public async Task AddBlock_refuses_a_corrupt_lock_and_writes_nothing(string lockText)
    {
        BlockAgentTools tools = Create();
        _workspace[LockFile] = lockText;

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("code").GetString().Should().Be("lock_corrupt");
        _writes.Should().BeEmpty();
        _workspace[LockFile].Should().Be(lockText);
    }

    [Fact]
    public async Task AddBlock_with_some_files_existing_and_some_missing_writes_only_the_missing()
    {
        BlockAgentTools tools = Create();
        _workspace["src/blocks/_shared/motion.ts"] = _src["_shared/motion.ts"];

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        r.GetProperty("unchanged").GetArrayLength().Should().Be(1);
        _writes.Should().BeEquivalentTo("src/blocks/wipe/Wipe.tsx", LockFile);
    }

    [Fact]
    public async Task AddBlock_upgrades_an_unedited_shared_helper_from_an_earlier_catalog_version()
    {
        BlockAgentTools tools = Create();
        const string old = "export const ease = 0;\n";
        _workspace["src/blocks/_shared/motion.ts"] = old;
        _workspace[LockFile] = "{\"blocks\":[{\"id\":\"other\",\"version\":\"1.0.0\",\"files\":[{\"path\":\"_shared/motion.ts\",\"sha256\":\"" + BlockCatalogTests.Sha(old) + "\"}]}]}";

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        r.GetProperty("upgraded").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo("src/blocks/_shared/motion.ts");
        _workspace["src/blocks/_shared/motion.ts"].Should().Be(_src["_shared/motion.ts"]);
        Json(_workspace[LockFile]).GetProperty("blocks").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task AddBlock_still_refuses_a_shared_helper_that_matches_neither_catalog_nor_lock()
    {
        BlockAgentTools tools = Create();
        _workspace["src/blocks/_shared/motion.ts"] = "// agent edit\n";
        _workspace[LockFile] = "{\"blocks\":[{\"id\":\"other\",\"files\":[{\"path\":\"_shared/motion.ts\",\"sha256\":\"" + BlockCatalogTests.Sha("x") + "\"}]}]}";

        JsonElement r = Json(await tools.AddBlock("wipe"));

        r.GetProperty("code").GetString().Should().Be("existing_file_differs");
        _writes.Should().BeEmpty();
    }

    [Fact]
    public async Task AddBlock_returns_sandbox_error_for_a_timeout_cancellation_but_rethrows_caller_cancel()
    {
        BlockAgentTools tools = Create();
        _listFault = _ => new TaskCanceledException("HttpClient timeout");

        JsonElement r = Json(await tools.AddBlock("wipe"));
        r.GetProperty("code").GetString().Should().Be("sandbox_error");

        using CancellationTokenSource cts = new();
        cts.Cancel();
        Func<Task> act = () => tools.AddBlock("wipe", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void GetBlock_returns_props_example_requires_and_target_files()
    {
        JsonElement r = Json(Create().GetBlock("wipe"));

        r.GetProperty("example").GetString().Should().Be("<X />");
        r.GetProperty("id").GetString().Should().Be("wipe");
        r.GetProperty("requires")[0].GetProperty("name").GetString().Should().Be("remotion");
        r.GetProperty("files").EnumerateArray().Select(f => f.GetString()).Should()
            .BeEquivalentTo("src/blocks/_shared/motion.ts", "src/blocks/wipe/Wipe.tsx");
    }

    [Fact]
    public void Tool_descriptions_list_all_nine_categories()
    {
        foreach (string method in new[] { nameof(BlockAgentTools.ListBlocks), nameof(BlockAgentTools.AddBlock) })
        {
            string description = ((System.ComponentModel.DescriptionAttribute)typeof(BlockAgentTools).GetMethod(method)!
                .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Single()).Description;
            foreach (string category in BlockCategories.All)
                description.Should().Contain(category, $"{method} must name the {category} category");
        }
    }

    [Theory]
    [InlineData(AgentType.AuthorAgent, true)]
    [InlineData(AgentType.CompositionReviser, true)]
    [InlineData(AgentType.MotionGraphicsPlanner, true)]
    [InlineData(AgentType.MotionGraphicsDirector, true)]
    [InlineData(AgentType.GraphicDesigner, true)]
    [InlineData(AgentType.ReviewAgent, false)]
    [InlineData(AgentType.ShotDirector, false)]
    [InlineData(AgentType.Custom, false)]
    public void SandboxBlocks_is_granted_to_exactly_the_five_authoring_agents(AgentType agent, bool granted)
    {
        ToolGroupCatalog.GroupsFor(agent).Contains(ToolGroup.SandboxBlocks).Should().Be(granted);
        if (granted)
            ToolGroupCatalog.GroupsFor(agent).Should().Contain(ToolGroup.SandboxAuthoring, "AddBlock writes to the sandbox, so the grant implies the uncached authoring class");
    }

    [Fact]
    public void SandboxBlocks_function_names_are_real_methods_on_BlockAgentTools()
    {
        ToolGroupCatalog.FunctionNamesFor(ToolGroup.SandboxBlocks).Should().BeEquivalentTo("ListBlocks", "GetBlock", "AddBlock");
        foreach (string name in ToolGroupCatalog.FunctionNamesFor(ToolGroup.SandboxBlocks))
            typeof(BlockAgentTools).GetMethod(name).Should().NotBeNull();
    }
}
