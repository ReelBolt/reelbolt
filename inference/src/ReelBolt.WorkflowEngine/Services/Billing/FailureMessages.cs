using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>
/// The one place that (a) guarantees a failed step carries a non-empty SCREAMING_SNAKE failure code and
/// (b) turns (failure code, raw detail) into the short, plain-language text shown to the user as
/// <c>workflow_executions.error_message</c>. The raw detail stays in the step's <c>error_details</c> for
/// support; nothing here changes billing (classes come from <see cref="FailureClassifier"/>).
/// </summary>
public static class FailureMessages
{
    /// <summary>A cancellation the user asked for (Stop). Class: UserCancelled.</summary>
    public const string UserCancelledCode = "USER_CANCELLED";

    /// <summary>A cancellation nobody on the user's side asked for (shutdown, transport kill). Ours.</summary>
    public const string RunStoppedCode = "RUN_STOPPED";

    /// <summary>The API key that started the run was revoked, so the work stopped. Not the user's doing.</summary>
    public const string ApiKeyRevokedCode = "API_KEY_REVOKED";

    /// <summary>...but nobody was prompted: they did not press Stop.</summary>
    public const string ApiKeyRevokedMessage = "The API key that started this run was revoked, so the run was stopped. This was not your doing; you can run it again.";

    /// <summary>Any exception or failed result with no recognisable code. Ours; refunded.</summary>
    public const string StepInternalErrorCode = "STEP_INTERNAL_ERROR";

    /// <summary>A run the recovery sweeper ended because its worker stopped responding. Ours.</summary>
    public const string RunInterruptedCode = "RUN_INTERRUPTED";

    /// <summary>
    /// A full sandbox render failed. Ours (<see cref="FailureClass.PlatformInternal"/>), so the step
    /// is refunded. The step itself carries the specific sentence naming the exit code and, when the
    /// output shows it, that the renderer was killed (likely out of memory).
    /// </summary>
    public const string RenderFailedCode = "RENDER_FAILED";

    /// <summary>The user-facing text of a user-requested stop (must keep the "Stopped by user" provenance wording).</summary>
    public const string UserStopMessage = "Stopped by user. You stopped this run before it finished; the work completed so far is kept.";

    private const string MissingSourceMessage = "A file this video uses is no longer in the project. Put it back or remove it from the timeline, then render again.";

    /// <summary>
    /// The code for a recorded cancellation reason. A key revocation is a deliberate stop, but not the
    /// user's: it keeps its own "API key revoked" wording and never the user-blaming UserStop message.
    /// </summary>
    public static string CancellationCode(string? reason) =>
        reason?.Contains(ReelBolt.Shared.IntegrationEvents.WorkflowExecutionStopRequested.ApiKeyRevokedReason, StringComparison.Ordinal) == true
            ? ApiKeyRevokedCode
            : FailureClassifier.IsUserStop(reason) ? UserCancelledCode : RunStoppedCode;

    /// <summary>
    /// Never returns null or blank: the code already known, else one recognised in the exception, else
    /// a cancellation code, else the generic fallback.
    /// </summary>
    public static string EnsureCode(string? knownCode, Exception? exception, string? cancellationReason = null)
    {
        if (!string.IsNullOrWhiteSpace(knownCode))
            return knownCode;

        if (exception is OperationCanceledException)
            return CancellationCode(cancellationReason);

        return FailureClassifier.CodeFromException(exception) ?? StepInternalErrorCode;
    }

    /// <summary>Same guarantee for a failed result that carries only output/details.</summary>
    public static string EnsureResultCode(string? knownCode, string? output, string? details)
        => !string.IsNullOrWhiteSpace(knownCode) ? knownCode
            : FailureClassifier.CodeFromOutput(output) ?? FailureClassifier.CodeFromMessage(details) ?? StepInternalErrorCode;

    /// <summary>
    /// The run-level message for a thrown exception. Exceptions whose text is already written for the user
    /// (a credit refusal, a run-class change) keep it; everything else goes through <see cref="UserMessage"/>.
    /// </summary>
    public static string RunMessage(Exception exception)
        => exception is CreditRefusalException or Execution.RunClassChangedException
            ? exception.Message
            : UserMessage(EnsureCode(null, exception), exception.Message);

    /// <summary>Longest first-party step message the run shows verbatim.</summary>
    public const int MaxStepUserMessageChars = 600;

    /// <summary>
    /// The run-level message when the failing step's own class, code and first-party message are known.
    /// A user class shows the step's message (normalised) when it has one, else a class-aware sentence;
    /// a platform class (or nothing known) behaves exactly like <see cref="RunMessage(Exception)"/>.
    /// </summary>
    public static string RunMessage(Exception exception, FailureClass stepClass, string? stepCode, string? stepUserMessage)
    {
        if (exception is CreditRefusalException or Execution.RunClassChangedException)
            return exception.Message;

        if (!IsUserClass(stepClass))
        {
            // A render failure is platform-caused but its text is specific ("the video renderer
            // exited with code 1…", "was killed (likely out of memory)"): show the step's own
            // sentence, never the generic one.
            if (stepCode == RenderFailedCode)
                return NormalizeUserMessage(stepUserMessage) ?? UserMessage(RenderFailedCode);

            return stepCode == "NARRATION_DOES_NOT_FIT" && exception is not (CreditRefusalException or Execution.RunClassChangedException)
                ? UserMessage(stepCode)
                : RunMessage(exception);
        }

        string? normalised = NormalizeUserMessage(stepUserMessage);
        if (normalised is not null)
            return normalised;

        return stepClass switch
        {
            FailureClass.ByoProviderError =>
                "The AI provider connected to your workspace returned an error. Check its API key and usage limits, then run it again.",
            FailureClass.InsufficientCredits =>
                "There were not enough credits to finish this run. Add credits or lower the run's cost, then run it again.",
            FailureClass.UserInput or FailureClass.EntitlementDenied =>
                string.IsNullOrWhiteSpace(stepCode) || stepCode == StepInternalErrorCode || !IsUserClass(FailureClassifier.ForCode(stepCode))
                    ? "A step could not run with the current settings or inputs. Check your inputs and settings, then run it again."
                    : UserMessage(stepCode),
            _ => UserStopMessage,
        };
    }

    private static bool IsUserClass(FailureClass c) => c is FailureClass.UserInput or FailureClass.EntitlementDenied
        or FailureClass.InsufficientCredits or FailureClass.ByoProviderError or FailureClass.UserCancelled;

    private static string? NormalizeUserMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        System.Text.StringBuilder sb = new(message.Length);
        bool pendingSpace = false;
        foreach (char ch in message)
        {
            System.Globalization.UnicodeCategory cat = char.GetUnicodeCategory(ch);
            bool bidiOrZeroWidth = ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u200E' or '\u200F'
                || (ch >= '\u202A' && ch <= '\u202E') || (ch >= '\u2066' && ch <= '\u2069');
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (bidiOrZeroWidth || cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format)
                continue;

            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
        }

        string text = sb.ToString();
        if (text.Length == 0)
            return null;
        if (text.Length <= MaxStepUserMessageChars)
            return text;

        int cut = text.LastIndexOf(". ", MaxStepUserMessageChars - 1, MaxStepUserMessageChars, StringComparison.Ordinal);
        return cut >= 0
            ? text[..(cut + 1)]
            : text[..(MaxStepUserMessageChars - 1)].TrimEnd() + "…";
    }

    /// <summary>
    /// The user-readable run message for a failure code. <paramref name="rawDetail"/> is accepted so callers
    /// pass everything they have, but it is never echoed: it holds internal ids, paths and parser text.
    /// </summary>
    public static string UserMessage(string? code, string? rawDetail = null)
    {
        _ = rawDetail;
        string key = string.IsNullOrWhiteSpace(code) ? StepInternalErrorCode : code;
        return key switch
        {
            UserCancelledCode => UserStopMessage,
            ApiKeyRevokedCode => ApiKeyRevokedMessage,
            RunStoppedCode => "The run was stopped by the platform (for example a restart) before it finished. This was not your doing; you can run it again.",
            RunInterruptedCode => "The run was interrupted because the worker running it stopped responding. This was on our side; you can run it again.",
            "DESIGN_COMPOSE_INVALID" => "We could not produce a valid design for this video. This was on our side; please run it again.",
            "SCRIPTWRITER_OUTPUT_INVALID" => "The script writer returned a script we could not use. This was on our side; please run it again.",
            "SEGMENTATION_BAD_RESPONSE" => "The object-detection service returned an answer we could not read. This was on our side; please run it again.",
            "BUDGET_EXCEEDED" => "This run went over its budget limit, so it was stopped. Raise the budget or simplify the request and run it again.",
            "PLAN_NOT_ENTITLED" => "Your current plan does not include this feature. Upgrade your plan or remove that step to continue.",
            "NO_RENDER_PRODUCED" => "The run finished without producing a rendered video. This was on our side; please run it again.",
            "INSERT_CONTENT_MISSING" => "Nothing was chosen to put on the green screen. Pick a picture or a video for the screen, then run it again.",
            "INSERT_NOT_APPLIED" or "PIN_INSERT_NO_REGIONS" => "We could not find a screen we could follow in your clip, so nothing was changed. Screens that are small or hand-held, have a pattern or markers across them, or leave the frame are hard to follow. You can run it again with a clip where the screen is large, steady and stays in view, or put your video in a corner or full screen instead; the assistant can do that for you.",
            "INSERT_TRACKING_FAILED" => "Scanning your clip for the screen did not finish. This was on our side and nothing was changed, so run it again. If it keeps happening, contact support, or ask the assistant to put your video in a corner instead.",
            "NARRATION_DOES_NOT_FIT" => "The narration ran too long for its scene. You were not charged for this step. Run it again, or shorten the script.",
            "NARRATION_ANCHOR_LOST" => "The edit cut away the part of the video some narration was written for, and it no longer fits anywhere. You were not charged for this step. Run it again, or shorten the script.",
            "INSERT_CONTENT_UNUSABLE" => "The picture or video you chose for the screen could not be used, so nothing was changed. Choose a different picture or video (a common format such as PNG, JPG or MP4) and run it again.",
            "INSERT_FILTER_UNAVAILABLE" => "Our video tools are missing a feature needed to put content on a screen. This was on our side; please contact support.",
            "SOURCE_TOO_LARGE" or "INPUT_TOO_LARGE" => "This video is larger than we can edit from one source. Compress it or use a smaller file, then run it again. You were not charged.",
            "SOURCE_TOO_LONG" or "DURATION_EXCEEDED" => "This video is longer than we can edit from one source. Cut it into shorter parts and run each part. You were not charged.",
            "SOURCE_TOO_DEMANDING" => "This video is too demanding for us to analyse in the time we allow. Try a shorter clip or a smaller export, for example 1080p, then run it again. You were not charged.",
            "SCRATCH_BUSY" => "Our editing machines are busy, nothing was charged. Run it again in a few minutes.",
            RenderFailedCode => "The video renderer failed on our side, so credits for this step were refunded. Run it again; if it keeps happening, contact support.",
            "ASSET_UNRESOLVED" => MissingSourceMessage,
            "ASSET_OUTSIDE_PROJECT" => MissingSourceMessage,
            StepInternalErrorCode => "Something went wrong on our side while running this step. Please run it again; if it keeps happening, contact support.",
            _ => FailureClassifier.ForCode(key) switch
            {
                FailureClass.UserInput or FailureClass.EntitlementDenied =>
                    "A step could not run with the current settings or inputs. Check your inputs and settings, then run it again.",
                FailureClass.UserCancelled => UserStopMessage,
                _ => "Something went wrong on our side while running this step. Please run it again; if it keeps happening, contact support.",
            },
        };
    }
}
