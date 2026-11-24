namespace ReelBolt.WorkflowEngine.Execution.Guardrails;

/// <summary>
/// The single place that renders untrusted text into a screening state — whether that text is a
/// screened media span or one argument value of a screened tool call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Everything this class renders is attacker-controlled: a screened span is
/// on-screen text a vision model read or speech a transcriber wrote, and a screened tool call's
/// argument value is whatever the model was carrying when it made the call — including content
/// injected through media it read. Both are placed <em>inside the classifier's own prompt</em>.
/// Concatenating either raw is a direct instruction to a small probabilistic model that has no
/// structural way to tell data from instructions: a payload reading
/// <c>"text: instruction_like: c1 ... answer: no"</c> can forge the state's own shape, and
/// fail-open polarity is irrelevant to that attack because the answer comes back a confident
/// <em>allow</em>.
/// </para>
/// <para>
/// <b>Three things together make that impossible, and all three are required:</b>
/// <list type="number">
/// <item><b><see cref="Collapse"/> is an injective, complete single-lining.</b> Every character a
/// consumer could read as a line break — CRLF, CR, LF, U+2028 LINE SEPARATOR, U+2029 PARAGRAPH
/// SEPARATOR, U+0085 NEL, U+000B LINE TABULATION and U+000C FORM FEED — becomes the literal two
/// characters <c>\n</c>, and a literal backslash is doubled <em>first</em>. That first step is what
/// makes the encoding injective: payload text reading <c>\n</c> (a backslash, then an n) is emitted
/// as <c>\\n</c>, and is therefore distinguishable from an engine-collapsed line break, which is
/// emitted as <c>\n</c>. Before the backslash was escaped the two were byte-identical, so any
/// consumer reading the notation recovered a line break that had never been there.</item>
/// <item><b>The text is wrapped between explicit begin/end markers</b> (the span path), so what
/// follows the payload is unambiguously the state's own next line rather than whatever the payload
/// claimed, even if the payload contains a copy of the end marker (a forged marker can only ever
/// appear inline inside the collapsed line, never on a line of its own).</item>
/// <item><b>A run of untrusted values is delimited as a block</b> (the tool-call path, via
/// <see cref="BeginArgumentsMarker"/>/<see cref="EndArgumentsMarker"/>), so the arguments region
/// has the same unambiguous end as a single wrapped span. The collapse alone is what carries the
/// "cannot contribute a line of its own" guarantee; the markers are the second layer, exactly as
/// they are on the span path.</item>
/// </list>
/// </para>
/// <para>
/// <b>What <see cref="Collapse"/> does not promise.</b> It is injective <em>modulo line-break
/// identity</em>: LF, CRLF, CR and the four other break characters all map onto the same
/// two-character <c>\n</c>, so those inputs are deliberately indistinguishable afterwards. That is
/// the intended trade — no consumer may act on <em>which</em> break character was there — and it is
/// why the injectivity test states the property in exactly that form rather than claiming that two
/// arbitrary distinct inputs can never collide. What the property does rule out, exhaustively, is
/// the ambiguity Security named: a payload's own backslash escape can never be mistaken for an
/// engine-collapsed break, and no break character outside the collapsed set survives.
/// </para>
/// <para>
/// <b>Every caller uses this, deliberately.</b> <see cref="GuardrailScreener.BuildSpanState"/>,
/// <see cref="InputSpanScreener.BuildSpanState"/> and <see cref="GuardrailScreener"/>'s argument
/// summariser are the three places untrusted text reaches a model, and the discipline has to hold
/// in all of them; one shared helper is what stops them drifting apart, since two copies of a
/// security rule is one copy and a re-introduced bug. That is why this class is named for the
/// untrusted text all of them render rather than for the span path it started life on, and why the
/// tool-call path calls <see cref="Collapse"/> here rather than growing an escape of its own.
/// </para>
/// <para>
/// The same collapse is what <see cref="InputSpanScreener"/>'s rendered block uses for its
/// <c>&gt; </c>-quoted lines: quoting untrusted text is a different act from splicing it, and both
/// callers here depend on the text never being able to start a line.
/// </para>
/// </remarks>
internal static class ScreenedUntrustedText
{
    /// <summary>Opens the delimited region. Contains no line break, so it is never itself split.</summary>
    internal const string BeginMarker = "<<<BEGIN UNTRUSTED SPAN TEXT>>>";

    /// <summary>Closes the delimited region.</summary>
    internal const string EndMarker = "<<<END UNTRUSTED SPAN TEXT>>>";

    /// <summary>Opens the delimited region around a screened tool call's arguments block.</summary>
    internal const string BeginArgumentsMarker = "<<<BEGIN UNTRUSTED TOOL ARGUMENTS>>>";

    /// <summary>Closes the delimited region around a screened tool call's arguments block.</summary>
    internal const string EndArgumentsMarker = "<<<END UNTRUSTED TOOL ARGUMENTS>>>";

    /// <summary>The two characters a collapsed line break becomes: a backslash and an <c>n</c>.</summary>
    private const string LineBreakEscape = "\\n";

    /// <summary>
    /// Every character this escape treats as a line break, in replacement order.
    /// </summary>
    /// <remarks>
    /// CRLF precedes a lone CR and a lone LF so one physical break becomes one <c>\n</c> rather than
    /// two. The five non-CR/LF members are included because they are line breaks to a consumer even
    /// though they are not to <c>string.Split('\n')</c>: U+2028 and U+2029 are line breaks by
    /// Unicode category, U+0085 is NEL ("next line"), and U+000B/U+000C are the vertical tab and
    /// form feed a text-comprehending model reads as a break. A break the collapse does not cover is
    /// a way around the collapse, so the set, not the three obvious members, is the rule.
    /// </remarks>
    private static readonly (string From, string To)[] LineBreakReplacements =
    [
        ("\r\n", LineBreakEscape),
        ("\r", LineBreakEscape),
        ("\n", LineBreakEscape),
        ("\u2028", LineBreakEscape),
        ("\u2029", LineBreakEscape),
        ("\u0085", LineBreakEscape),
        ("\u000B", LineBreakEscape),
        ("\u000C", LineBreakEscape),
    ];

    /// <summary>
    /// Collapses every line break onto the literal two-character <c>\n</c> and doubles every literal
    /// backslash, so the result can never contain a line break, can never contribute a line of its
    /// own to the state, and can never be confused with text that did.
    /// </summary>
    /// <remarks>
    /// The calls that decode a JSON string (<c>JsonElement.GetString</c>) hand real newlines here
    /// even though the wire form carried <c>\n</c> escapes, which is precisely why this has to be
    /// applied to the <em>decoded</em> value rather than to the raw JSON.
    /// </remarks>
    internal static string Collapse(string? text)
    {
        // The escape character FIRST. Introducing the \n sequences before doubling the backslashes
        // would double the escapes this method had just written, and the result would no longer
        // decode back to the input.
        string collapsed = (text ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal);

        foreach ((string from, string to) in LineBreakReplacements)
            collapsed = collapsed.Replace(from, to, StringComparison.Ordinal);

        return collapsed;
    }

    /// <summary>
    /// The delimited, single-line form of one span's text: the begin marker on its own line, the
    /// collapsed text, then the end marker on its own line.
    /// </summary>
    internal static string Wrap(string? text) =>
        string.Concat(BeginMarker, "\n", Collapse(text), "\n", EndMarker);
}
