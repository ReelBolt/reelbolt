using System.Text;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The user turn the platform writes when a watched run finishes. Built ONLY from a fixed template, the status word of an
/// enum and the run reference: never a name, a run error, a step output or a file's content.
/// Deterministic, so a retry of the same wake reuses the unanswered row instead of appending a second one.
/// </summary>
public static class AssistantRunWakeText
{
    public const int MaxNameChars = 80;

    /// <summary>
    /// The model-visible wake text: a fixed template, the status word of an enum and the opaque run reference (the execution
    /// id, so GetWorkflowExecution works). NO name or any other text from the database reaches the model through it; the
    /// workflow's name lives only in the stored metadata, for display.
    /// </summary>
    public static string Build(Guid executionId, ExecutionStatus status) =>
        $"[Run update] A run you were watching has finished. Status: {Word(status)}. Run reference: {executionId}.";

    /// <summary>The status as a word. Anything not terminal reads as Cancelled so no free text can slip in.</summary>
    public static string Word(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Passed => "Passed",
        ExecutionStatus.Failed => "Failed",
        _ => "Cancelled"
    };

    /// <summary>
    /// Letters, digits, spaces and a few plain marks only (no quotes, brackets, markdown, control characters or
    /// newlines), whitespace collapsed, at most <see cref="MaxNameChars"/> characters; "workflow" when nothing is left.
    /// </summary>
    public static string Name(string? raw)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (char c in raw ?? string.Empty)
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ',' or '&' or '(' or ')')
            {
                if (space && sb.Length > 0)
                    sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            else
            {
                space = true;
            }

            if (sb.Length >= MaxNameChars)
                break;
        }

        string name = sb.ToString().Trim();
        return name.Length == 0 ? "workflow" : name;
    }
}
