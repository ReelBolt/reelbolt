using System.Runtime.CompilerServices;
using ReelBolt.Shared.Data.OutputSchemas;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>Registers the lenient <see cref="ScriptwriterOutputJson.Options"/> as the reader of <see cref="ScriptwriterOutput"/> for structured-output validation.</summary>
internal static class ScriptwriterOutputOptionsRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        StructuredOutputOptions.Register(typeof(ScriptwriterOutput), ScriptwriterOutputJson.Options);
        // Analyzer outputs are read by AnalyzerOutputJson (TolerantStringList); validate through the same options.
        foreach (Type analyzer in AnalyzerOutputTypes)
            StructuredOutputOptions.Register(analyzer, AnalyzerOutputJson.ReaderOptions);
    }

    internal static readonly Type[] AnalyzerOutputTypes =
    [
        typeof(CodeStructureOutput),
        typeof(DependencyAnalysisOutput),
        typeof(ComponentInventoryOutput),
        typeof(RouteAndApiOutput),
        typeof(StyleAndThemeOutput),
    ];
}
