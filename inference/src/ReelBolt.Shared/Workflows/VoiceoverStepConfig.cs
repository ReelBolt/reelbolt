using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// The source kind for a voiceover step.
/// </summary>
/// <remarks>
/// <b>APPEND ONLY.</b> Every member is serialized into <c>voiceover_config_json</c> (jsonb) and read
/// back by the execute path AND by the frontend's TypeScript mirror
/// (<c>web/lib/types/workflow.ts</c>), so a member inserted before <see cref="ScriptScenes"/> would
/// silently reinterpret every stored <see cref="Inline"/> or <see cref="ScriptScenes"/> config.
/// <see cref="NarrationPlan"/> is therefore appended LAST, and the ordinal values of the two members
/// that preceded it are unchanged.
/// </remarks>
public enum VoiceoverSourceKind
{
    /// <summary>Lines are provided directly in the config.</summary>
    Inline,

    /// <summary>Lines come from a ScriptScenes step (not yet implemented, T-010).</summary>
    ScriptScenes,

    /// <summary>
    /// Lines come from the <c>NarrationWriter</c> step named by
    /// <see cref="VoiceoverSource.NarrationWriterStepOrder"/>, whose output is a
    /// <c>NarrationPlanOutput</c>. Each plan line contributes its spoken <c>Text</c> plus its opaque
    /// cut-anchor id, and nothing else — the plan is structurally incapable of carrying a start
    /// time, so the voiceover step declares the line positions itself.
    /// </summary>
    NarrationPlan,

    /// <summary>
    /// Lines come from the <c>PickupPlanner</c> step named by
    /// <see cref="VoiceoverSource.PickupPlannerStepOrder"/>, whose output is a
    /// <c>PickupPlanOutput</c>: each pickup contributes its corrected <c>Text</c> plus the
    /// transcript-segment id it repairs as the line's anchor. The compile step's pickup path
    /// (<c>EnablePickups</c>) places each line over that segment's own window. APPENDED LAST.
    /// </summary>
    PickupPlan
}

/// <summary>
/// A single voiceover line to synthesize.
/// </summary>
/// <param name="Text">The words to speak.</param>
/// <param name="StartSec">
/// The line's DECLARED start. For <see cref="VoiceoverSourceKind.Inline"/> and
/// <see cref="VoiceoverSourceKind.ScriptScenes"/> this is what the config/script declared; for
/// <see cref="VoiceoverSourceKind.NarrationPlan"/> it is assigned by the executor's post-loop
/// placement pass (back-to-back in plan order, line 0 at 0.0) because the plan carries no start of
/// its own.
/// </param>
/// <param name="VoiceId">Per-line voice override; the step config's default applies when null.</param>
/// <param name="AnchorId">
/// The opaque cut-anchor id the line was written for (<c>s{n}</c>/<c>g{n}</c>/<c>t{n}</c>), present
/// ONLY for a <see cref="VoiceoverSourceKind.NarrationPlan"/> line — no other source kind can
/// declare an anchor. It is threaded through to the narration-fit facts so an overflow stays
/// attributable to the moment the line was written for; it is never spoken and never affects
/// synthesis or the content-addressed artifact key. APPENDED LAST: this record round-trips through
/// <c>voiceover_config_json</c>, so an Inline line stored before this member existed still
/// deserializes with the same first three values.
/// </param>
/// <param name="Speed">
/// Per-line speaking rate (0.5..2.0; Fish prosody) set by a step change request, or null for the provider's
/// normal rate. APPENDED LAST with a null default for the same round-trip reason; a null value changes nothing
/// on the wire and nothing in the content key.
/// </param>
public sealed record VoiceoverLine(
    string Text,
    double StartSec,
    string? VoiceId = null,
    string? AnchorId = null,
    double? Speed = null);

/// <summary>
/// The source specification for voiceover lines.
/// </summary>
/// <param name="Kind">Where the lines come from.</param>
/// <param name="Lines">The declared lines, used by (and only meaningful for) <see cref="VoiceoverSourceKind.Inline"/>.</param>
/// <param name="ScriptwriterStepOrder">The <c>Scriptwriter</c> step to read scenes from, required by <see cref="VoiceoverSourceKind.ScriptScenes"/>.</param>
/// <param name="NarrationWriterStepOrder">
/// The <c>NarrationWriter</c> step to read a <c>NarrationPlanOutput</c> from, required by
/// <see cref="VoiceoverSourceKind.NarrationPlan"/>. A DISTINCT member rather than a reuse of
/// <paramref name="ScriptwriterStepOrder"/>: the two name different agents with different output
/// schemas, and overloading one member would make a stored config's meaning depend on which kind it
/// happens to carry. APPENDED LAST for the same reason the enum member was — see
/// <see cref="VoiceoverSourceKind"/>.
/// </param>
public sealed record VoiceoverSource(
    VoiceoverSourceKind Kind,
    IReadOnlyList<VoiceoverLine>? Lines = null,
    int? ScriptwriterStepOrder = null,
    int? NarrationWriterStepOrder = null,
    /// <summary>The <c>PickupPlanner</c> step to read a <c>PickupPlanOutput</c> from, required by <see cref="VoiceoverSourceKind.PickupPlan"/>. APPENDED LAST, distinct member for the same reason as <see cref="NarrationWriterStepOrder"/>.</summary>
    int? PickupPlannerStepOrder = null,
    /// <summary>
    /// <see cref="VoiceoverSourceKind.Inline"/> only: the lines are one block from 0 s, placed back to back in their order
    /// (each next line at the previous line's start plus its measured length, as a narration plan is), so their own
    /// <c>startSec</c> is not read. Written by an app's "speak exactly this" field (<c>@spokenText</c>,
    /// docs/design/app-inputs.md 27). APPENDED LAST, false (default) keeps every stored config byte-identical.
    /// </summary>
    bool Sequential = false);

/// <summary>
/// JSON configuration for a StepType.Voiceover workflow step.
/// Deserialized from WorkflowStep.VoiceoverConfigJson.
///
/// A closed, versioned, strongly-typed record — deterministic, non-LLM, never throws.
/// Every failure mode is representable in the resulting JSON output rather than as an exception.
/// </summary>
[StepCard(
    "Speaks the lines an earlier writer step produced, one clip per line, with the chosen voice.",
    ["Lines to speak: written inline, or from an earlier writer step", "A voice (a project voice also needs live consent)"],
    "One narration audio clip per line, with timings",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCreditsAndVoice,
    ReadableBy = ["VideoCompile"],
    PairsWith = ["VideoCompile: mixes the narration in when its voiceover switch is on"])]
public sealed record VoiceoverStepConfig(
    VoiceoverSource Source,
    Guid? ProviderId = null,
    string? Model = null,
    string? DefaultVoiceId = null,
    long MaxBytes = 2_000_000,
    // -- Phase 3 (see docs/voiceover.md "Pronunciation dictionary"). Off (default) sends the
    //    byte-identical phase-1 request and leaves every per-line content key unchanged. --
    /// <summary>
    /// <c>Auto</c> derives a pronunciation dictionary from the code-analysis outputs already in the
    /// run's history (dependency names, framework, component names) matched against each line's
    /// text, through a FIRST-PARTY phoneme table — a term the table does not know is simply not
    /// in the dictionary. <c>Off</c> (default) sends none.
    /// </summary>
    PronunciationDictionaryMode PronunciationDictionary = PronunciationDictionaryMode.Off,
    /// <summary>Cap on dictionary entries per line (clamped 1..64); terms beyond it are dropped most-generic-first, never the whole dictionary.</summary>
    int MaxPronunciationEntries = 16,
    // -- Phase 4 (see docs/voiceover.md "Voices", "Dubs", "Voice casting"). All defaults are
    //    byte-identical to the phase-1 request. --
    /// <summary>
    /// The project voice (<c>project_voices</c> row) every line speaks in unless it names its own
    /// voice. Resolved by the executor through the consent guard: a voice whose consent is not
    /// live, or that belongs to another project, fails the step with <c>VOICE_CONSENT_MISSING</c>
    /// rather than falling back to anything. Takes precedence over <see cref="DefaultVoiceId"/>.
    /// </summary>
    Guid? ProjectVoiceId = null,
    /// <summary>
    /// BCP-47 language tag of the narration (dubs). Recorded on the output and used to label the
    /// per-language track; the speech providers themselves detect the language from the text.
    /// </summary>
    string? Language = null,
    /// <summary>
    /// <c>Decision</c> asks the configured Decision provider ONE <c>Choice</c> question over the
    /// project's consented voice ids (the offered set) with the narration plan's rationale as
    /// state, and speaks every line in the chosen voice; any failure, a missing provider or an
    /// answer outside the offered set falls back to the configured voice. <c>Off</c> (default)
    /// asks nothing.
    /// </summary>
    VoiceCastingMode VoiceCasting = VoiceCastingMode.Off,
    /// <summary>
    /// Author-supplied pronunciations (docs/voiceover.md "Italian (and other non-English) narration").
    /// Each entry is sent for every line containing the term as a whole word, regardless of
    /// <see cref="PronunciationDictionary"/>, and wins over the built-in table. Null (default) keeps
    /// the request and every content key byte-identical.
    /// </summary>
    IReadOnlyList<PronunciationOverride>? PronunciationOverrides = null,
    /// <summary>
    /// <c>Enforce</c> (default when absent) acts on a line that does not fit its scene: speeds it up
    /// (pitch-preserving, at most 1.15x) or fails the step clearly; two voices never overlap.
    /// <c>Report</c> only measures, as before.
    /// </summary>
    NarrationFitPolicy NarrationFitPolicy = NarrationFitPolicy.Enforce,
    /// <summary>
    /// Avatars phase 3: speak in the voice linked to the run's avatar (the run brief's <c>avatar</c>), so a talking
    /// head says its words in that person's own consented voice. An explicit brief voice still wins. No avatar in the
    /// brief, or an avatar with no linked voice, fails the step (<c>AVATAR_VOICE_MISSING</c>); the linked voice then goes
    /// through the voice consent guard like any <see cref="ProjectVoiceId"/>. False (default) keeps every existing step
    /// byte-identical.
    /// </summary>
    bool VoiceFromAvatar = false)
{
    /// <summary>
    /// Save-time / run-time check of <see cref="PronunciationOverrides"/>: the field-naming errors,
    /// empty when valid or absent.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        List<string> errors = [.. PronunciationOverride.ValidateAll(PronunciationOverrides)];
        IReadOnlyList<VoiceoverLine>? lines = Source?.Lines;
        if (lines is not null)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i]?.Speed is double rate && !(rate >= 0.5 && rate <= 2.0))
                    errors.Add($"source.lines[{i}].speed must be between 0.5 and 2.0.");
            }
        }

        return errors;
    }

    /// <summary>
    /// The one set of options <c>VoiceoverConfigJson</c> is deserialized with — by the executor at
    /// run time and by the Inference API's save-time validation — so both read a config identically
    /// (Web defaults: camelCase, case-insensitive; enums accepted as strings). The same shape
    /// <see cref="VideoGenerateStepConfig.JsonOptions"/> uses, for the same reason: two private
    /// copies of "how a config is read" is how a save-time check and a run-time read drift apart.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// THE way a stored <c>VoiceoverConfigJson</c> is read (typed, <see cref="JsonOptions"/>, last duplicate key wins).
    /// The executor and the voice-reuse guard both call this, so a config with duplicate or differently-cased keys
    /// means the same thing to the check as to the run. Throws <see cref="JsonException"/> on malformed JSON;
    /// returns null for a JSON <c>null</c>.
    /// </summary>
    public static VoiceoverStepConfig? Parse(string json) => JsonSerializer.Deserialize<VoiceoverStepConfig>(json, JsonOptions);
}

/// <summary>Whether a Voiceover step sends a pronunciation dictionary — see <see cref="VoiceoverStepConfig.PronunciationDictionary"/>. APPEND ONLY.</summary>
public enum PronunciationDictionaryMode
{
    Off,
    Auto
}

/// <summary>Whether a Voiceover step asks a Decision provider to cast the voice — see <see cref="VoiceoverStepConfig.VoiceCasting"/>. APPEND ONLY.</summary>
public enum VoiceCastingMode
{
    Off,
    Decision
}

/// <summary>What a Voiceover step does about a line that overruns its scene — see <see cref="VoiceoverStepConfig.NarrationFitPolicy"/>. APPEND ONLY.</summary>
public enum NarrationFitPolicy
{
    /// <summary>Speed the line up within the tempo limit, otherwise fail the step. The default.</summary>
    Enforce,

    /// <summary>Measure only (the original behaviour).</summary>
    Report
}

/// <summary>One author-supplied pronunciation: <paramref name="Term"/> is spoken as the IPA <paramref name="Ipa"/> (stress marks allowed).</summary>
public sealed record PronunciationOverride(string Term, string Ipa, bool CaseSensitive = false)
{
    public const int MaxEntries = 64;

    /// <summary>The errors for a whole override list, naming the offending field; empty when valid.</summary>
    public static IReadOnlyList<string> ValidateAll(IReadOnlyList<PronunciationOverride>? overrides)
    {
        if (overrides is null)
            return [];

        List<string> errors = [];
        if (overrides.Count > MaxEntries)
            errors.Add($"pronunciationOverrides may hold at most {MaxEntries} entries.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < overrides.Count; i++)
        {
            PronunciationOverride? o = overrides[i];
            if (o is null)
            {
                errors.Add($"pronunciationOverrides[{i}] must be an object.");
                continue;
            }

            string term = (o.Term ?? string.Empty).Normalize(System.Text.NormalizationForm.FormC);
            if (term.Length < 2 || term.Length > 60 || string.IsNullOrWhiteSpace(term)
                || term.Any(c => char.IsControl(c) || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
                errors.Add($"pronunciationOverrides[{i}].term must be 2 to 60 characters, not blank, with no control or format characters.");
            else if (!seen.Add(term))
            {
                string shown = term.Length > 40 ? term[..40] : term;
                errors.Add($"pronunciationOverrides[{i}].term '{shown}' is a duplicate (terms are compared case-insensitively).");
            }

            string ipa = o.Ipa ?? string.Empty;
            if (ipa.Length < 1 || ipa.Length > 120 || !ipa.All(IsIpaChar))
                errors.Add($"pronunciationOverrides[{i}].ipa must be 1 to 120 characters of IPA letters, diacritics, stress marks, spaces or '.'.");
        }

        return errors;
    }

    private static bool IsIpaChar(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or ' ' or '.'
        || (c is >= '\u00C0' and <= '\u024F' && c != '\u00D7' && c != '\u00F7') // Latin-1 / Extended letters (æ ø ð ç ...)
        || c is >= '\u0250' and <= '\u02AF'   // IPA Extensions
        || c is >= '\u02B0' and <= '\u02FF'   // Spacing Modifier Letters (incl. stress marks)
        || c is >= '\u0300' and <= '\u036F'   // Combining Diacritical Marks
        || c is >= '\u1D00' and <= '\u1DBF'   // Phonetic Extensions (+ Supplement)
        || c is '\u03B2' or '\u03B8' or '\u03C7'; // Greek beta, theta, chi
}
