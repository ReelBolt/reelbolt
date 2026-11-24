using System.Collections.Generic;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>Replays the prod failure shapes of 2026-10-21..25 (profile build step 10) through current code.</summary>
public sealed class LaunchReliabilityReplayTests
{
    [Theory]
    [InlineData("""{"dependencies":["react","next"],"devDependencies":[],"packageManager":"npm"}""")]
    [InlineData("""{"dependencies":[{"name":"react","version":18,"purpose":"ui","isCore":"true"}],"devDependencies":[{"name":"vitest"}],"packageManager":"npm"}""")]
    [InlineData("""Ecco il risultato: {"dependencies":[{"name":"react","version":"18","isCore":true}],"packageManager":"npm"} fine.""")]
    [InlineData("""{"dependencies":{"react":"18"},"packageManager":null,"recommendations":"none"}""")]
    public void DependencyAnalysis_drift_still_yields_a_typed_output(string json)
    {
        Assert.NotNull(AnalyzerOutputJson.Deserialize<DependencyAnalysisOutput>(json));
    }

    [Theory]
    [InlineData("""{"projectType":"web","directories":[{"path":"app","purpose":"x","fileCount":"12"}],"entryPoints":["a"]}""")]
    [InlineData("""{"projectType":"web","directories":["app","lib"],"entryPoints":"a"}""")]
    public void CodeStructure_drift_still_yields_a_typed_output(string json)
    {
        Assert.NotNull(AnalyzerOutputJson.Deserialize<CodeStructureOutput>(json));
    }
}
