using System.Collections.Generic;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

public sealed class AnalyzerOutputJsonTests
{
    [Fact]
    public void CodeStructureOutput_with_object_entryPoints_keeps_the_paths()
    {
        // Production run 5761becc: the analyzer returned each entry point as an object.
        const string json = """
            {"projectType":"web-app","framework":"next",
             "entryPoints":[{"path":"app/page.tsx","type":"page","description":"home"},
                            {"name":"server.ts"},
                            {"value":"src/main.tsx"},
                            {"id":"src/index.tsx"}],
             "overallArchitecture":"app router"}
            """;

        CodeStructureOutput? output = AnalyzerOutputJson.Deserialize<CodeStructureOutput>(json);

        Assert.NotNull(output);
        Assert.Equal(
            new[] { "app/page.tsx", "server.ts", "src/main.tsx", "src/index.tsx" },
            output!.EntryPoints);
    }

    [Fact]
    public void CodeStructureOutput_with_plain_strings_still_parses()
    {
        const string json = """
            {"entryPoints":["app/page.tsx","src/main.tsx"],
             "directories":[{"path":"app","purpose":"routes","fileCount":3}]}
            """;

        CodeStructureOutput? output = AnalyzerOutputJson.Deserialize<CodeStructureOutput>(json);

        Assert.NotNull(output);
        Assert.Equal(new[] { "app/page.tsx", "src/main.tsx" }, output!.EntryPoints);
        Assert.Single(output.Directories);
        Assert.Equal("app", output.Directories[0].Path);
        Assert.Equal(3, output.Directories[0].FileCount);
    }

    [Fact]
    public void CodeStructureOutput_with_empty_or_garbage_entryPoints_does_not_throw()
    {
        CodeStructureOutput? empty = AnalyzerOutputJson.Deserialize<CodeStructureOutput>(
            """{"entryPoints":[]}""");
        Assert.NotNull(empty);
        Assert.Empty(empty!.EntryPoints);

        CodeStructureOutput? mixed = AnalyzerOutputJson.Deserialize<CodeStructureOutput>(
            """{"entryPoints":[{},{"path":42},null,[],"ok"]}""");
        Assert.NotNull(mixed);
        Assert.Equal(new[] { "ok" }, mixed!.EntryPoints);

        CodeStructureOutput? scalar = AnalyzerOutputJson.Deserialize<CodeStructureOutput>(
            """{"entryPoints":"app/page.tsx"}""");
        Assert.NotNull(scalar);
        Assert.Equal(new[] { "app/page.tsx" }, scalar!.EntryPoints);

        CodeStructureOutput? unusable = AnalyzerOutputJson.Deserialize<CodeStructureOutput>(
            """{"entryPoints":42}""");
        Assert.NotNull(unusable);
        Assert.Empty(unusable!.EntryPoints);
    }

    [Fact]
    public void Unreadable_output_is_still_null()
    {
        Assert.Null(AnalyzerOutputJson.Deserialize<CodeStructureOutput>("not json"));
        Assert.Null(AnalyzerOutputJson.Deserialize<CodeStructureOutput>(" "));
        Assert.Null(AnalyzerOutputJson.Deserialize<CodeStructureOutput>(null));
    }
}
