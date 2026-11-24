namespace ReelBolt.WorkflowEngine.Execution.Guardrails;

/// <summary>
/// One ordered input span awaiting screening: a piece of text the VideoAnalyze step already read out
/// of the source video. <see cref="Site"/> names where it came from — caption spans carry the
/// <c>c:</c> site (Phase 2 vision shot captions, <c>c{n}</c> ids) and ASR spans the <c>t:</c> site
/// (transcript spans, <c>t{n}</c> ids) — and is carried through to the rendered block so a reader can
/// tell which half of the view a flagged quote came from.
/// </summary>
/// <remarks>
/// Structurally a twin of T-101's <see cref="GuardrailSpan"/> (same three fields), and deliberately
/// a separate type rather than a reuse: <see cref="GuardrailSpan"/> is the single-span API's argument,
/// while this one is a member of an <em>ordered batch</em>, and screening a batch is what makes the
/// batching and chunking contract (one <c>DecideAsync</c> per chunk, never one per span) expressible
/// at all.
/// </remarks>
/// <param name="SpanId">The span's opaque view id (<c>c3</c>, <c>t17</c>, ...).</param>
/// <param name="Text">The span's text, exactly as the analysis step produced it.</param>
/// <param name="Site">The origin site — <c>c:</c> for a caption, <c>t:</c> for an ASR span.</param>
public sealed record InputSpan(string SpanId, string Text, string Site);

/// <summary>
/// The screening outcome for exactly one input span. Every input span gets one of these — a span the
/// model said nothing about is <c>Flagged = false, PInstructionLike = 0</c>, never absent.
/// </summary>
/// <param name="SpanId">The span's id, unchanged.</param>
/// <param name="Site">The span's origin site, unchanged.</param>
/// <param name="Text">The span's text, unchanged.</param>
/// <param name="PInstructionLike">
/// The model's P(the span reads like an instruction addressed to an AI assistant or a tool). Zero when
/// no answer arrived — no provider, a failed chunk, or a malformed/absent answer.
/// </param>
/// <param name="Flagged">True when <see cref="PInstructionLike"/> reached the caller's <c>AcceptAt</c>.</param>
public sealed record InputSpanScreening(
    string SpanId,
    string Site,
    string Text,
    double PInstructionLike,
    bool Flagged);

/// <summary>
/// The result of screening an ordered span list.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Spans"/> is set-equal AND order-equal to the input, on every code path.</b> Screening
/// quotes and flags media-derived text; it never drops it. On-screen text and transcribed speech are
/// routinely legitimate content (a title card that reads "Follow us on Instagram", a spoken "cut to
/// the close-up"), and the model's job here is to point at the suspicious ones, not to censor the
/// view. A span that returned no answer is present with <c>Flagged = false</c>.
/// </para>
/// <para>
/// <see cref="RenderedBlock"/> is the <em>empty string</em> when nothing was flagged — not a header,
/// not a placeholder — because the caller splices it straight into a prompt and an empty block has to
/// leave that prompt byte-identical to one built without screening at all.
/// </para>
/// </remarks>
/// <param name="Spans">One entry per input span, same order, same count.</param>
/// <param name="FlaggedSpanIds">The flagged spans' ids, in input order.</param>
/// <param name="RenderedBlock">The quoted-and-flagged prompt block, or <c>""</c> when nothing is flagged.</param>
public sealed record InputScreeningResult(
    IReadOnlyList<InputSpanScreening> Spans,
    IReadOnlyList<string> FlaggedSpanIds,
    string RenderedBlock)
{
    /// <summary>
    /// The empty result: no spans, nothing flagged, nothing to render. Returned for an empty input and
    /// used as the base every no-op path builds on.
    /// </summary>
    public static InputScreeningResult Empty { get; } = new([], [], string.Empty);

    /// <summary>True when at least one span was flagged, i.e. <see cref="RenderedBlock"/> is non-empty.</summary>
    public bool HasFlags => FlaggedSpanIds.Count > 0;
}
