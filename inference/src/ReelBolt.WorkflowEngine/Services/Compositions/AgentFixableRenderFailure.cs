using System.Text.RegularExpressions;
using ReelBolt.WorkflowEngine.Services.Compute;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>What kind of render failure the agent that wrote the composition can fix itself.</summary>
public enum AgentFixableRenderFailureKind
{
    /// <summary>Not the agent's to fix: a platform failure (timeout, killed/out of memory, unreadable video, disk, sandbox gone) or unknown.</summary>
    None,

    /// <summary>The render tool refused BEFORE Remotion ran, naming the code to change (HTML5 &lt;Video&gt;, a missing staticFile, a network font).</summary>
    PreRenderRefusal,

    /// <summary>Remotion or the bundler ran and failed on the composition's OWN code (a JavaScript, React, import or Remotion API error).</summary>
    CodeError
}

/// <summary>
/// Decides from a failed render's error text (the render tool's error as <c>RenderErrorText</c> makes it, with
/// Remotion's own error line after "Remotion said:") whether the agent that wrote the composition can fix it, so a
/// revision (and the Author's one reminder turn) can hand the error back for ONE repair turn. Production runs 22f79d02
/// (HTML5_VIDEO_IN_RENDER refusal) and a2be36db (<c>ReferenceError Label is not defined</c>).
///
/// <para>
/// Platform failures are checked FIRST and always win: a timeout, a killed or out-of-memory renderer, a video frame
/// that could not be read, a full disk, a browser that died or a sandbox that is gone get no repair turn even when the
/// text also mentions a JavaScript error, because changing the code would not fix them.
/// </para>
/// </summary>
public static class AgentFixableRenderFailure
{
    /// <summary>The pre-render refusal codes of the render tool; each refusal message starts with its code and a colon.</summary>
    public static readonly IReadOnlyList<string> PreRenderRefusalCodes =
    [
        Html5VideoPreflight.ErrorCode,
        StaticFilePreflight.ErrorCode,
        NetworkFontPreflight.ErrorCode
    ];

    /// <summary>Platform failures: the code is not at fault, so a repair turn could not help. Case-insensitive.</summary>
    private static readonly Regex Platform = new(
        string.Join("|",
        [
            // Killed, out of memory (the render tool's own kill markers, plus V8's heap message).
            @"\bSIGKILL\b", @"\bkilled\b", @"\bENOMEM\b", @"out of memory", @"constraint_memcg",
            // Deadlines. Word boundaries keep `setTimeout is not defined` a code error.
            @"\btimed out\b", @"\btimeout\b", @"deadline exceeded", @"\bdeadline\b",
            // A video Remotion could not read, or a frame the compositor did not deliver in time.
            @"could not (?:read|extract) (?:a )?frame", @"offthread-video-server", @"/proxy\?src=", @"\bdelayRender\b",
            // Disk.
            @"\bENOSPC\b", @"no space left",
            // The sandbox or its browser went away.
            @"\bECONNREFUSED\b", @"\bECONNRESET\b", @"socket hang up", @"sandbox[^.]{0,40}\b(?:not found|gone|unavailable|404)\b",
            @"failed to launch the browser", @"target closed", @"browser has disconnected", @"\bprotocol error\b",
            // The failed-render cap is final.
            @"used up its render attempts"
        ]),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The composition's own code: JavaScript error names and messages (V8), esbuild/webpack bundling errors, React
    /// render errors and Remotion's API validation. Case-sensitive, as the runtimes print them.
    /// </summary>
    private static readonly Regex Code = new(
        string.Join("|",
        [
            // V8 error names and their usual messages.
            @"\b(?:ReferenceError|TypeError|SyntaxError|RangeError|EvalError|URIError)\b",
            @"\bis not defined\b", @"\bis not a function\b", @"\bis not a constructor\b", @"\bis not iterable\b",
            @"Cannot read propert(?:y|ies) of", @"Cannot set propert(?:y|ies) of", @"Cannot access '[^']+' before initialization",
            @"Unexpected token", @"Unexpected end of", @"Unterminated (?:string|template|regular expression)", @"Invalid or unexpected token",
            // esbuild / webpack (Remotion's bundler) and Node module resolution.
            @"Module not found", @"Could not resolve", @"Can't resolve", @"Cannot find module", @"Build failed with \d+ error",
            @"Transform failed with \d+ error", @"No matching export", @"does not provide an export named", @"Expected "".+"" but found",
            // React.
            @"Minified React error", @"Objects are not valid as a React child", @"Element type is invalid", @"Too many re-renders",
            @"Rendered (?:more|fewer) hooks", @"Invalid hook call",
            // Remotion's own validation of the composition and its props.
            @"Could not find composition", @"No composition with the ID", @"[Cc]omposition\b[^.]{0,80}\bnot found",
            @"\b(?:inputRange|outputRange)\b", @"\bmust be (?:a|an|at least|at most|strictly|greater|less|positive|non-negative|finite|between|an integer|of type|one of)\b",
            @"prop (?:of|for|on) the <\w+"
        ]),
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>What kind of agent-fixable failure <paramref name="error"/> is; <see cref="AgentFixableRenderFailureKind.None"/> for anything else.</summary>
    public static AgentFixableRenderFailureKind Classify(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return AgentFixableRenderFailureKind.None;

        string text = error.TrimStart();
        if (PreRenderRefusalCodes.Any(code => text.StartsWith(code + ":", StringComparison.Ordinal)))
            return AgentFixableRenderFailureKind.PreRenderRefusal;

        if (Platform.IsMatch(text))
            return AgentFixableRenderFailureKind.None;

        return Code.IsMatch(text) ? AgentFixableRenderFailureKind.CodeError : AgentFixableRenderFailureKind.None;
    }

    /// <summary>Whether the agent can fix <paramref name="error"/> in its own code: a pre-render refusal or a code error.</summary>
    public static bool IsFixable(string? error) => Classify(error) != AgentFixableRenderFailureKind.None;
}
