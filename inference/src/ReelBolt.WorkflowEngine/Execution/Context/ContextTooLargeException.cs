namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// Thrown by <see cref="ToolLoopCompactingChatClient"/> BEFORE a model call whose context, after
/// every compaction the provider allows, is still over
/// <see cref="ToolLoopBudgetOptions.HardMaxChars"/>. Failing here, with a message the author can
/// act on, replaces the provider's own context-limit 400 a few calls later. The step is ours to
/// refund: <see cref="FailureCode"/> maps to <c>PlatformInternal</c> in
/// <c>FailureClassifier</c>, and the message carries the code so it is recognised from the text.
/// </summary>
public sealed class ContextTooLargeException : InvalidOperationException
{
    /// <summary>The stable failure code.</summary>
    public const string FailureCode = "CONTEXT_TOO_LARGE";

    /// <summary>The user-facing explanation, verbatim.</summary>
    public const string Explanation =
        "This step read more than the model can hold at once. Ask it to read files in slices (outline + line ranges) instead of whole files.";

    public ContextTooLargeException(int contextChars, int hardMaxChars)
        : base($"{Explanation} ({FailureCode}: {contextChars} chars over the {hardMaxChars}-char ceiling)")
    {
        ContextChars = contextChars;
        HardMaxChars = hardMaxChars;
    }

    /// <summary>What the call would have sent, in characters, instructions included.</summary>
    public int ContextChars { get; }

    /// <summary>The ceiling it was over.</summary>
    public int HardMaxChars { get; }

    /// <summary>Always <see cref="FailureCode"/>.</summary>
    public string Code => FailureCode;
}
