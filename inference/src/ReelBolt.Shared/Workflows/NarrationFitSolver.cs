namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The deterministic narration-fit solver: it measures how much narration a voiceover line has to
/// spill past the window it was written for, and returns the facts as a closed, serializable report.
///
/// <para>
/// <b>This is code, never a model.</b> No agent, no <c>IChatClient</c>, no <c>ITranscriptionClient</c>,
/// no clock, no RNG, no I/O, no ffmpeg: <see cref="Solve"/> is a pure function of its argument, and
/// calling it twice on the same input returns an equal report (and byte-identical JSON). That is the
/// whole point — the narration path's review loop turns on an <b>overflow number</b>, and a number a
/// language model produced would be unfalsifiable.
/// </para>
/// <para>
/// It lives in <c>ReelBolt.Shared/Workflows</c> rather than in the WorkflowEngine's own tree because
/// both services may read a fit report and <c>Services/Video/**</c> is a different department's
/// surface. It deliberately does NOT read, require or reimplement the compile-side picture window
/// (<c>VideoCompileStepExecutor</c> measures a line against the containing output placement after
/// seams); the window here is the <b>declared narration window</b> — see
/// <see cref="NarrationFitLine.WindowStartSec"/>.
/// </para>
/// <para>
/// <b>No word alignment is required to solve.</b> <see cref="NarrationFitLineResult.WordCount"/> comes
/// from the sanitized text that was actually spoken, and
/// <see cref="NarrationFitLineResult.AlignedWordCount"/> is carried through only as evidence;
/// <see cref="NarrationFitLineResult.WordsPerSecond"/> and
/// <see cref="NarrationFitLineResult.OverflowWords"/> are identical on a deployment with no ASR
/// provider and on one with a perfect recognizer.
/// </para>
/// </summary>
public static class NarrationFitSolver
{
    /// <summary>
    /// <see cref="NarrationFitLineResult.WindowSource"/> for a line whose window was computed from
    /// declared start times. A CLOSED set, serialized as this literal.
    /// </summary>
    public const string WindowSourceDeclaredStarts = "declared_starts";

    /// <summary>
    /// <see cref="NarrationFitLineResult.WindowSource"/> for a line with no usable window. A CLOSED
    /// set, serialized as this literal. Such a line never contributes overflow and always
    /// <see cref="NarrationFitLineResult.Fits"/> — an unknown window is an absence of evidence, never
    /// a defect to report as one.
    /// </summary>
    public const string WindowSourceNone = "none";

    /// <summary>
    /// Decimal places every second-valued field on the report is rounded to. Three, matching the
    /// sibling duration fields the compile-side fit facts use, so two reports of the same run
    /// compare cleanly instead of differing in the 16th digit.
    /// </summary>
    public const int SecondsPrecision = 3;

    /// <summary>
    /// About 1.8 words per second of scene time: the <b>comfort writing budget</b> used for advice
    /// and the writer prompts (replaces the old "max 8 words" folk rule). It is never a pass/fail
    /// threshold — fit is judged on measured play duration. See <see cref="WordBudget"/>.
    /// </summary>
    public const double WordsPerSecond = 1.8;

    /// <summary>The hard physical ceiling: about 4.0 words per second. A line above
    /// this cannot be spoken in its window by any voice at any supported tempo, so it
    /// is refused before synthesis. Everything at or below it is synthesized and
    /// judged on its measured play duration.</summary>
    public const double HardCeilingWordsPerSecond = 4.0;

    /// <summary>The fastest pitch-preserving speed-up (ffmpeg <c>atempo</c>) a line may be given to fit its scene.</summary>
    public const double MaxTempoFit = 1.15;

    /// <summary>How far a line may run past its window (seconds) before it counts as overlapping the next voice.</summary>
    public const double OverflowToleranceSec = 0.1;

    /// <summary>The words that fit a scene of <paramref name="windowSec"/> seconds: floor(window x 1.8). Never negative.</summary>
    public static int WordBudget(double windowSec) =>
        double.IsFinite(windowSec) && windowSec > 0 ? (int)Math.Floor(windowSec * WordsPerSecond) : 0;

    /// <summary>The most words a scene of windowSec can carry at the hard ceiling:
    /// floor(window × 4.0). Never negative.</summary>
    public static int HardWordCeiling(double windowSec) =>
        double.IsFinite(windowSec) && windowSec > 0 ? (int)Math.Floor(windowSec * HardCeilingWordsPerSecond) : 0;

    /// <summary>
    /// True when <paramref name="wordCount"/> words cannot be spoken in <paramref name="windowSec"/>
    /// even at the hard physical ceiling: words &gt; <see cref="HardWordCeiling"/>. This is the hard
    /// physical ceiling, decided BEFORE any synthesis so no paid call is wasted on a line no voice
    /// can speak. Everything at or below it is synthesized and judged on measured play duration.
    /// </summary>
    public static bool IsUnrescuable(int wordCount, double windowSec) =>
        wordCount > HardWordCeiling(windowSec);

    /// <summary>
    /// True when a step's total pre-flight-admitted narration words stay inside the funnel's spend
    /// bound (<c>Voiceover:MaxAdmittedNarrationWords</c>). This is a COST ceiling, not a fit rule:
    /// it bounds how many words one Voiceover step may send to the speech provider even when every
    /// line is individually speakable, because <c>MaxBytes</c> caps the request size but not the
    /// number of words. Distinct from <see cref="IsUnrescuable"/>, which is the per-line physical
    /// limit; the two must never be conflated. A non-positive <paramref name="maxAdmittedWords"/>
    /// means "no bound" (the caller normally substitutes the generous default first).
    /// </summary>
    public static bool IsSynthesisAdmissible(int admittedWords, int maxAdmittedWords) =>
        maxAdmittedWords <= 0 || admittedWords <= maxAdmittedWords;

    /// <summary>
    /// The tempo to speed a line of <paramref name="playDurationSec"/> up by to fit
    /// <paramref name="windowSec"/>: null when it already fits within the tolerance, otherwise the
    /// needed factor clamped to <see cref="MaxTempoFit"/> (a hair above the exact ratio so the
    /// result lands inside the window). Pure.
    /// </summary>
    public static double? PlanTempo(double playDurationSec, double windowSec)
    {
        if (!double.IsFinite(playDurationSec) || !double.IsFinite(windowSec) || windowSec <= 0)
            return null;
        if (playDurationSec - windowSec <= OverflowToleranceSec)
            return null;
        double needed = Math.Ceiling(playDurationSec / windowSec * 1000.0) / 1000.0 + 0.002;
        return Math.Round(Math.Min(MaxTempoFit, needed), 4);
    }

    /// <summary>
    /// The report for an empty (or entirely null-element) input. Every count is 0 and both second
    /// totals are <c>0.0</c> — NEVER null and never NaN, so a consumer reads
    /// <c>meta.fit.overflowTotalSec</c> without a null check on every path.
    /// </summary>
    public static readonly NarrationFitReport Empty = new(
        Lines: [],
        MeasuredLineCount: 0,
        OverflowLineCount: 0,
        OverflowWords: 0,
        OverflowTotalSec: 0.0,
        MaxOverflowSec: 0.0,
        UnknownWindowLineCount: 0);

    /// <summary>
    /// Counts the whitespace-separated tokens in a line's SANITIZED text — the text that was actually
    /// spoken, which is what makes this number comparable to a duration.
    ///
    /// <para>
    /// It is public because the caller that HAS the sanitized text (the voiceover step) must count it
    /// here rather than with a second, subtly different split: the whole report's arithmetic is only
    /// as trustworthy as this one number, and two implementations of "how many words" would drift.
    /// Empty, whitespace-only and null all count 0.
    /// </para>
    /// </summary>
    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Split on Unicode whitespace (the null separator is character.IsWhiteSpace), dropping empty
        // entries so runs of spaces count once — the same rule the sanitizer's collapse step uses.
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Solves every line and sums the report. Deterministic, never throws: a null list, a null
    /// element or a non-finite input all produce a well-formed report rather than an exception,
    /// because a failure here would have to be invented into the step's own output anyway.
    /// </summary>
    public static NarrationFitReport Solve(IReadOnlyList<NarrationFitLine>? lines)
    {
        if (lines is null || lines.Count == 0)
            return Empty;

        var results = new List<NarrationFitLineResult>(lines.Count);

        foreach (NarrationFitLine line in lines)
        {
            // Defensive only: the declared element type says this cannot happen, and a null is
            // skipped rather than dereferenced so "never throws" survives a caller that lies.
            if (line is null)
                continue;

            results.Add(SolveLine(line));
        }

        if (results.Count == 0)
            return Empty;

        int measuredLineCount = 0;
        int overflowLineCount = 0;
        int overflowWords = 0;
        int unknownWindowLineCount = 0;
        double overflowTotalSec = 0.0;
        double maxOverflowSec = 0.0;

        foreach (NarrationFitLineResult result in results)
        {
            // "Measured" means "this line's window was known", which is exactly the lines that
            // contribute to (or are excluded from) the overflow arithmetic — reported alongside
            // UnknownWindowLineCount so a partial measurement can never be read as a complete one.
            if (result.WindowSec is null)
                unknownWindowLineCount++;
            else
                measuredLineCount++;

            if (!result.Fits)
                overflowLineCount++;

            overflowWords += result.OverflowWords;
            overflowTotalSec += result.OverflowSec;
            maxOverflowSec = Math.Max(maxOverflowSec, result.OverflowSec);
        }

        return new NarrationFitReport(
            Lines: results,
            MeasuredLineCount: measuredLineCount,
            OverflowLineCount: overflowLineCount,
            OverflowWords: overflowWords,
            OverflowTotalSec: Math.Round(overflowTotalSec, SecondsPrecision),
            MaxOverflowSec: Math.Round(maxOverflowSec, SecondsPrecision),
            UnknownWindowLineCount: unknownWindowLineCount);
    }

    /// <summary>
    /// Solves one line. Exposed so a caller that has a single line can ask without building a list,
    /// and so the per-line rules can be pinned one at a time. Never throws.
    /// </summary>
    public static NarrationFitLineResult SolveLine(NarrationFitLine line)
    {
        if (line is null)
            return new NarrationFitLineResult(
                Id: string.Empty,
                AnchorId: null,
                WindowSec: null,
                PlayDurationSec: 0.0,
                WordCount: 0,
                AlignedWordCount: null,
                WordsPerSecond: 0.0,
                OverflowSec: 0.0,
                OverflowWords: 0,
                Fits: true,
                WindowSource: WindowSourceNone);

        // A non-finite duration cannot be represented in the JSON this report is serialized into
        // (System.Text.Json throws on NaN/Infinity) and a non-finite window would produce a
        // non-finite windowSec for the same reason, so both are normalized to "unknown" here rather
        // than allowed to reach a serializer. A NEGATIVE duration is representable and is echoed
        // as-is; it yields no overflow, which is already the honest answer.
        double playDurationSec = double.IsFinite(line.PlayDurationSec) ? line.PlayDurationSec : 0.0;

        // A negative word count is nonsense and would propagate into a negative overflow count, so it
        // is clamped; the clamped value is what the report carries, so the arithmetic and the echo
        // can never disagree.
        int wordCount = Math.Max(0, line.WordCount);
        int? alignedWordCount = line.AlignedWordCount is int aligned ? Math.Max(0, aligned) : null;

        // The declared window is usable only when BOTH ends are present and the end really is after
        // the start. Anything else (a missing last line, a zero-length window, a reversed or
        // non-finite pair) is "no window declared" — reported as WindowSource "none" and counted in
        // UnknownWindowLineCount, never as an overflow.
        double? windowSec = null;
        if (line.WindowStartSec is double windowStart
            && line.WindowEndSec is double windowEnd
            && double.IsFinite(windowStart)
            && double.IsFinite(windowEnd)
            && windowEnd > windowStart)
        {
            double span = windowEnd - windowStart;
            if (double.IsFinite(span))
                windowSec = span;
        }

        // Words per second is the line's OWN rate, measured from what was synthesized, and it is 0
        // whenever it is undefined — never NaN, never Infinity. It is deliberately NOT rounded: the
        // rounding the report pins is on the two derived second values, and a rate is an input to the
        // overflow arithmetic rather than a displayed measurement.
        //
        // The `double.IsFinite` guard above fences the INPUT, not this division's own result: a
        // denormal-but-positive playDurationSec (< ~1.2e-299, which ffprobe cannot produce and the
        // executor's own DurationSec<=0 guard rejects, but which this pure method must still survive)
        // makes the quotient overflow to Infinity. That value then leaves through
        // `WordsPerSecond` and `System.Text.Json` refuses it with an ArgumentException — NOT a
        // JsonException — so the step's own catch cannot name it and the run dies as
        // UNEXPECTED_ERROR. This method's whole contract is "deterministic, never throws", so the
        // result is normalized rather than merely the operands: the rate is 0 whenever it is not a
        // usable finite number, exactly as it is when the inputs make it undefined.
        double wordsPerSecond =
            playDurationSec > 0 && wordCount > 0 ? wordCount / playDurationSec : 0.0;

        if (!double.IsFinite(wordsPerSecond))
            wordsPerSecond = 0.0;

        // A windowless line never contributes overflow: overflow is measured AGAINST a window, and
        // with none there is nothing to overrun. It counts as Fits for the same reason.
        double overflowSec = windowSec is double window
            ? Math.Round(Math.Max(0.0, playDurationSec - window), SecondsPrecision)
            : 0.0;

        // Ceiling, not rounding: a line that overruns by a fraction of a second still overruns by
        // at least one spoken word, and "0 words over" would read as a fit. Computed from the
        // ROUNDED overflowSec so the two reported numbers are consistent with each other.
        int overflowWords = (int)Math.Ceiling(overflowSec * wordsPerSecond);

        return new NarrationFitLineResult(
            Id: line.Id,
            AnchorId: line.AnchorId,
            WindowSec: windowSec,
            PlayDurationSec: playDurationSec,
            WordCount: wordCount,
            AlignedWordCount: alignedWordCount,
            WordsPerSecond: wordsPerSecond,
            OverflowSec: overflowSec,
            OverflowWords: overflowWords,
            Fits: overflowSec <= 0,
            WindowSource: windowSec is null ? WindowSourceNone : WindowSourceDeclaredStarts,
            WordBudget: windowSec is double budgetWindow ? WordBudget(budgetWindow) : null);
    }
}

/// <summary>
/// One narration line, as the solver sees it: an identity, the window it was written for, how long
/// the synthesized audio actually plays, and how many words were spoken.
///
/// <para>
/// <b>The window is the DECLARED narration window</b> — <c>[StartSec, nextLine.StartSec)</c> for the
/// voiceover step's line list — and it is deliberately distinct from the picture window the compile
/// step measures a line against (the containing output placement, after seam overlaps). This solver
/// answers "does the narration overrun the slot it was written for"; the compile answers "does it
/// overrun the shot it landed in". A consumer must not conflate the two, and this type must never
/// grow a "read the compile" path: the two measurements are of different things on purpose.
/// </para>
/// <para>
/// <see cref="WindowStartSec"/> and <see cref="WindowEndSec"/> are BOTH null when there is no window
/// to declare — the last declared line has no successor to end at. Anchoring is not implemented by
/// any current source kind, so <see cref="AnchorId"/> is null on every line a workflow can build
/// today; it is part of the shape because a narration line is id-anchored by design and the solver is
/// the last place that fact should be lost.
/// </para>
/// </summary>
public sealed record NarrationFitLine(
    string Id,
    string? AnchorId,
    double? WindowStartSec,
    double? WindowEndSec,
    double PlayDurationSec,
    int WordCount,
    int? AlignedWordCount = null);

/// <summary>
/// The solver's verdict on one line. Every member is always present in the serialized form — this is
/// a closed record whose key set a consumer reads unconditionally (the compile-side passthrough
/// requires <c>overflowWords</c> to always be there), and a null already means exactly one thing per
/// member: no window (<see cref="WindowSec"/> null, paired with
/// <see cref="NarrationFitSolver.WindowSourceNone"/> in <see cref="WindowSource"/>), no anchor
/// declared, or no alignment applied. Nothing here is omitted, so nothing here needs a null check to
/// discover WHY it is absent.
/// </summary>
public sealed record NarrationFitLineResult(
    string Id,
    string? AnchorId,
    double? WindowSec,
    double PlayDurationSec,
    int WordCount,
    int? AlignedWordCount,
    double WordsPerSecond,
    double OverflowSec,
    int OverflowWords,
    bool Fits,
    string WindowSource,
    // APPEND-ONLY members below. Omitted when null (no window / policy Report / no retime).
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] int? WordBudget = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] double? Tempo = null);

/// <summary>
/// The whole measurement: the per-line verdicts plus the totals a review loop gates on without
/// walking the array. <see cref="OverflowTotalSec"/> and <see cref="MaxOverflowSec"/> are
/// <c>0.0</c> — never null, never NaN — for an empty or entirely windowless input, so a gate can
/// read them unconditionally.
/// </summary>
public sealed record NarrationFitReport(
    IReadOnlyList<NarrationFitLineResult> Lines,
    int MeasuredLineCount,
    int OverflowLineCount,
    int OverflowWords,
    double OverflowTotalSec,
    double MaxOverflowSec,
    int UnknownWindowLineCount);
