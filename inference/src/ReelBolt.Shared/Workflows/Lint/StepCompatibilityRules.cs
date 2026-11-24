using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows.Lint;

/// <summary>How serious a lint finding is. Only <see cref="Error"/> can ever block a run start (docs/workflow-linter.md).</summary>
public enum LintSeverity
{
    /// <summary>Would make the run fail (or fail silently) at run time today; the only severity the run gate enforces.</summary>
    Error,

    /// <summary>Probably not what the author wants, but the run proceeds.</summary>
    Warning,

    /// <summary>Worth knowing; never blocks.</summary>
    Info
}

/// <summary>The kind of fix a finding offers. Names are the wire values of <c>fix.kind</c>.</summary>
public enum LintFixKind
{
    /// <summary>No machine-describable fix beyond the message.</summary>
    None,

    /// <summary>Point a reference at the right earlier step.</summary>
    RetargetReference,

    /// <summary>Change a step setting.</summary>
    ChangeSetting,

    /// <summary>Add a step (closed patch id, for example <c>addVoiceover</c>).</summary>
    AddStep,

    /// <summary>Upload the missing media.</summary>
    UploadFiles,

    /// <summary>Connect or choose a provider.</summary>
    ChooseProvider,

    /// <summary>Choose a different plan, resolution or step.</summary>
    ChangePlanOrChoice,

    /// <summary>Approve the project's design system.</summary>
    ApproveDesignSystem,

    /// <summary>Pick a usable voice (consent recorded).</summary>
    ChooseVoice,

    /// <summary>Allow source media to be sent to the vendor on the step.</summary>
    AllowEgress,

    /// <summary>Remove the input or control that points at nothing.</summary>
    RemoveReference,

    /// <summary>Fill in (or correct) an app input on the run page (docs/design/app-inputs.md 11.2). Appended.</summary>
    FillInput,

    /// <summary>Choose another model for a model input (docs/design/app-inputs.md 11.2). Appended.</summary>
    ChooseModel
}

/// <summary>
/// The fix a finding suggests: a kind, a label for the button, and a closed patch id when one exists. The label
/// keeps today's English; <see cref="LabelId"/> + <see cref="Args"/> let a client translate the button the same way
/// it translates the message. <see cref="LabelId"/> is looked up as <c>lint.fix.&lt;LabelId&gt;</c> (the prefix is a
/// catalogue namespace, not part of the wire value) and is <c>null</c> when the finding was not built by a rule.
/// <see cref="PatchId"/> stays the third positional member so every existing two/three-argument call site still
/// compiles untouched.
/// </summary>
public sealed record LintFix(
    LintFixKind Kind,
    string Label,
    string? PatchId = null,
    string? LabelId = null,
    IReadOnlyDictionary<string, string>? Args = null);

/// <summary>
/// A sentence plus the tokens a client needs to render it in another language. <see cref="Text"/> is the English
/// sentence, byte-identical to the pre-i18n linter; <see cref="Args"/> holds <b>tokens only</b> — digits, user
/// labels/names, enum names and short codes (<c>comesLater</c>, <c>notSet</c>) — never a sentence or a clause.
/// </summary>
public sealed record LintMessage(string Text, IReadOnlyDictionary<string, string> Args)
{
    /// <summary>The empty token set (a sentence with no placeholders).</summary>
    public static IReadOnlyDictionary<string, string> NoArgs { get; } = new Dictionary<string, string>();

    /// <summary>
    /// A plain string is a whole English sentence with no tokens. This is what keeps a rule that only calls
    /// <see cref="StepCompatibilityRule.Format"/> (for example a future <c>WL022</c>) compiling and localisable: its
    /// args come from the format pairs and its key falls back to the rule id.
    /// </summary>
    public static implicit operator LintMessage(string text) => new(text, NoArgs);
}

/// <summary>
/// A <see cref="StepCompatibilityRule.Format"/> value that renders one way and tokenises another:
/// <see cref="Text"/> fills the English template, <see cref="Tokens"/> become <see cref="LintMessage.Args"/>.
/// A plain string or number is both at once (a label is text and token); a clause must never be one.
/// </summary>
public sealed record LintPart(string Text, IReadOnlyList<KeyValuePair<string, string>> Tokens)
{
    /// <summary>
    /// Builds a part from its English text and its tokens. A token whose value is null/blank (a step with no label,
    /// a reference that does not exist) is omitted, so the arg set says exactly what is known.
    /// </summary>
    public static LintPart Of(string text, params (string Name, string? Value)[] tokens)
    {
        List<KeyValuePair<string, string>> kept = [];
        foreach ((string name, string? value) in tokens)
        {
            if (!string.IsNullOrWhiteSpace(value))
                kept.Add(new KeyValuePair<string, string>(name, value));
        }

        return new LintPart(text, kept);
    }
}

/// <summary>Builds a finding's token dictionary, dropping the tokens that are not known.</summary>
public static class LintArgs
{
    /// <summary>The named tokens that are not null/blank.</summary>
    public static IReadOnlyDictionary<string, string> Of(params (string Name, string? Value)[] tokens)
    {
        Dictionary<string, string> args = new(StringComparer.Ordinal);
        foreach ((string name, string? value) in tokens)
        {
            if (!string.IsNullOrWhiteSpace(value))
                args[name] = value;
        }

        return args;
    }

    /// <summary>
    /// A list as <c>&lt;prefix&gt;Count</c> plus <c>&lt;prefix&gt;1</c> … <c>&lt;prefix&gt;N</c>, in order: the flat
    /// shape a message template cannot loop over but a client can render as a list.
    /// </summary>
    public static (string Name, string? Value)[] Indexed(string prefix, IReadOnlyList<string> values)
    {
        List<(string, string?)> tokens = [(prefix + "Count", values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        for (int i = 0; i < values.Count; i++)
            tokens.Add((prefix + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), values[i]));
        return [.. tokens];
    }
}

/// <summary>
/// One lint finding: <c>{ruleId, severity, stepOrder?, message, messageKey, args, fix?}</c> on the wire.
/// <see cref="Message"/> stays the English string (the assistant, MCP and the run gate read it); <see cref="Args"/>
/// carries the tokens and <see cref="MessageKey"/> names the sentence the client looks up.
/// </summary>
/// <remarks>
/// <para>
/// <b>MessageKey scheme.</b> A rule with one sentence uses its id (<c>"WL010"</c>); a rule with variants appends
/// <c>.</c> and the variant code (<c>"WL001.comesLater"</c>, <c>"WL018.unreadField"</c>). The client looks it up as
/// <c>lint.&lt;MessageKey&gt;</c> in <c>web/messages/en/apps.json</c>: the <c>lint.</c> prefix is a catalogue
/// namespace and is <b>not</b> part of the wire value. The default is <see cref="RuleId"/>, so a rule that only
/// calls <see cref="StepCompatibilityRule.Format"/> localises without any change to its own call site.
/// </para>
/// <para>
/// <b>Arg names.</b> A step is <c>stepOrder</c> + <c>stepLabel</c> (the label is omitted when the step has none); a
/// referenced step is <c>targetOrder</c> + <c>targetLabel</c> (or <c>sourceOrder</c>/<c>sourceLabel</c> where the
/// template calls it a source); a review is <c>reviewOrder</c>/<c>reviewLabel</c>, a loop <c>loopOrder</c>, the final
/// cut <c>lastOrder</c>. Lists are <c>&lt;prefix&gt;Count</c> + <c>&lt;prefix&gt;1</c>… as built by
/// <see cref="LintArgs.Indexed"/>. The names each rule emits are documented next to its emission.
/// </para>
/// </remarks>
public sealed record LintFinding
{
    /// <summary>Builds a finding. <paramref name="message"/>'s text is the English message; its args the tokens.</summary>
    /// <param name="ruleId">The rule id, <c>WL000</c>…<c>WL034</c>.</param>
    /// <param name="severity">The severity.</param>
    /// <param name="stepOrder">The step the finding is about, or null for a workflow-level finding.</param>
    /// <param name="message">The sentence and its tokens.</param>
    /// <param name="fix">The suggested fix, when the rule offers one.</param>
    /// <param name="messageKey">The catalogue key; null = <paramref name="ruleId"/>.</param>
    public LintFinding(
        string ruleId,
        LintSeverity severity,
        int? stepOrder,
        LintMessage message,
        LintFix? fix = null,
        string? messageKey = null)
    {
        RuleId = ruleId;
        Severity = severity;
        StepOrder = stepOrder;
        Message = message.Text;
        MessageKey = string.IsNullOrWhiteSpace(messageKey) ? ruleId : messageKey;
        Args = message.Args;
        Fix = fix;
    }

    /// <summary>The rule id.</summary>
    public string RuleId { get; init; }

    /// <summary>The severity.</summary>
    public LintSeverity Severity { get; init; }

    /// <summary>The step the finding is about, or null for a workflow-level finding.</summary>
    public int? StepOrder { get; init; }

    /// <summary>The English sentence (unchanged from before the i18n work).</summary>
    public string Message { get; init; }

    /// <summary>The catalogue key: <c>lint.&lt;MessageKey&gt;</c>. Never null or empty.</summary>
    public string MessageKey { get; init; }

    /// <summary>The tokens the sentence's template takes. Never null; empty for a sentence with no placeholders.</summary>
    public IReadOnlyDictionary<string, string> Args { get; init; }

    /// <summary>The suggested fix, when the rule offers one.</summary>
    public LintFix? Fix { get; init; }
}

/// <summary>A brief field a step may read (docs/design/workflow-apps.md section 3.8).</summary>
public enum LintBriefField
{
    Language,
    SourceLanguage,
    LengthSec,
    Voice,
    /// <summary>Style preset, saved style, one-off style and tone: they reach agents through the style snapshot.</summary>
    Style,
    Format,
    Resolution,
    DesignSystem,
    SourceFiles,

    /// <summary>The run's avatar (avatars phase 2); read only by a VideoGenerate step. Appended.</summary>
    Avatar
}

/// <summary>
/// One row of the rule table: the id, name, default severity, what it needs and produces (the facts the
/// step catalog reads), the plain-language message and the fix kind.
/// </summary>
/// <param name="Id">Stable id, <c>WL001</c> to <c>WL034</c>. Append only.</param>
/// <param name="Name">Short kebab-case name.</param>
/// <param name="Severity">The severity the rule reports (WL012 downgrades its brief-only variant, see the linter).</param>
/// <param name="Description">When the rule fires, in one sentence of plain words.</param>
/// <param name="Needs">What a step needs for the rule to hold (a catalog fact: "an earlier Scriptwriter step").</param>
/// <param name="Produces">What a step produces that other rules rely on (a catalog fact).</param>
/// <param name="MessageTemplate">The plain-language message, with <c>{name}</c> placeholders.</param>
/// <param name="FixKind">The fix kind the rule offers.</param>
public sealed record StepCompatibilityRule(
    string Id,
    string Name,
    LintSeverity Severity,
    string Description,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> Produces,
    string MessageTemplate,
    LintFixKind FixKind)
{
    /// <summary>
    /// Fills <c>{name}</c> placeholders of <see cref="MessageTemplate"/> in order of the pairs given and hands back the
    /// tokens too, so a call site never states its args twice. A <see cref="LintPart"/> carries its own tokens; a plain
    /// value (a label, a number) is its own token under the placeholder's name; a null value is neither.
    /// </summary>
    public LintMessage Format(params (string Name, object? Value)[] values)
    {
        string text = MessageTemplate;
        Dictionary<string, string> args = new(StringComparer.Ordinal);
        foreach ((string name, object? value) in values)
        {
            string rendered;
            if (value is LintPart part)
            {
                rendered = part.Text;
                foreach (KeyValuePair<string, string> token in part.Tokens)
                    args[token.Key] = token.Value;
            }
            else
            {
                rendered = value is null
                    ? string.Empty
                    : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (value is not null)
                    args[name] = rendered;
            }

            text = text.Replace("{" + name + "}", rendered, StringComparison.Ordinal);
        }

        return new LintMessage(text, args);
    }
}

/// <summary>
/// THE rule table of the pre-run linter (docs/design/workflow-apps.md section 6.3): one source consumed by the
/// <c>WorkflowLinter</c> (the checks) and by the step catalog (needs / produces / pairs with), so the facts the
/// assistant reads and the checks the platform enforces cannot diverge. It also owns the section 3.8 consumption
/// matrix (<see cref="StepReads"/>, a view of <see cref="BriefFieldConsumers"/>) that rules WL011 and WL016 need.
/// </summary>
public static class StepCompatibilityRules
{
    private static readonly string[] None = [];

    /// <summary>Every rule, ascending by id.</summary>
    public static IReadOnlyList<StepCompatibilityRule> All { get; } =
    [
        new("WL001", "voiceover-source-missing", LintSeverity.Error,
            "A Voiceover step reads its lines from a step that does not exist, comes later, or is not the kind of step that writes them.",
            ["when the lines are not written inline: an earlier Scriptwriter, NarrationWriter or PickupPlanner step, named by the Voiceover source"], None,
            "The {step} speaks lines from {source}, which {problem}. The voiceover would fail when it runs.",
            LintFixKind.RetargetReference),
        new("WL002", "compile-voiceover-unwired", LintSeverity.Error,
            "VideoCompile turns on voiceover (or pickups) but no earlier Voiceover step sits at the order it names.",
            ["an earlier Voiceover step at the named order"], None,
            "The {step} is set to mix in the narration, but {problem}. The film would come out silent.",
            LintFixKind.RetargetReference),
        new("WL003", "reviewloop-target-invalid", LintSeverity.Error,
            "A review loop has no step to go back to, points forward, or points at a step no model writes.",
            ["an earlier step written by a model, as the loop target"], None,
            "The review at {step} goes back to {target}, which {problem}. The loop would never improve anything.",
            LintFixKind.RetargetReference),
        new("WL004", "compile-ref-invalid", LintSeverity.Error,
            "VideoCompile reads a decision, analysis, plan or take selection from a step that is missing, later, or of the wrong kind.",
            ["earlier steps of the right kind for each input"], None,
            "The {step} reads its {input} from {target}, which {problem}. The cut would be wrong or the step would stop.",
            LintFixKind.RetargetReference),
        new("WL005", "compile-mode-conflict", LintSeverity.Error,
            "VideoCompile is set to copy streams without re-encoding while also asking for music, voice, graphics, sound effects, colour, effects or captions.",
            ["Mode = Reencode when adding picture or sound"], None,
            "The {step} copies the footage without re-encoding, which cannot also add {features}. The step would stop.",
            LintFixKind.ChangeSetting),
        new("WL006", "provider-missing-for-capability", LintSeverity.Error,
            "A step needs a capability (voice, video generation, music generation, transcription, vision) and no provider is chosen or set as the default for it.",
            ["a provider for the capability, chosen on the step or set as the organization default"], None,
            "The {step} needs {capability}, but {problem}. It would stop after earlier steps had already been paid for.",
            LintFixKind.ChooseProvider),
        new("WL007", "plan-not-entitled", LintSeverity.Error,
            "A step feature or the chosen resolution is not part of the organization's plan.",
            ["a plan that includes the feature or resolution"], None,
            "{what} is not included in your plan. It would be refused when the run starts.",
            LintFixKind.ChangePlanOrChoice),
        new("WL008", "voice-consent-missing", LintSeverity.Error,
            "The chosen project voice cannot be used: consent is missing, revoked or expired, or the voice is not in this project.",
            ["when a project voice is chosen: that voice with live consent"], None,
            "The voice {voice} cannot be used ({reason}). The narration step would stop.",
            LintFixKind.ChooseVoice),
        new("WL009", "voice-language-mismatch", LintSeverity.Warning,
            "The film's language is not one of the languages the chosen voice or speech provider is known to speak.",
            ["a voice that speaks the film's language"], None,
            "The film is in {language}, but the chosen voice speaks {languages}. The narration may sound wrong.",
            LintFixKind.ChooseVoice),
        new("WL010", "source-media-missing", LintSeverity.Error,
            "A step needs media (footage, a code archive, a music track, sound effects) and the project has none of that kind.",
            ["project files of the role the step reads"], None,
            "The {step} needs {role}, but there are none in this project. The run would be empty or fail.",
            LintFixKind.UploadFiles),
        new("WL011", "brief-field-unread", LintSeverity.Info,
            "A brief field is set but no step in this workflow reads it.",
            ["at least one step that reads the field (see the consumption matrix)"], None,
            "The {field} you set is not used by any step in this workflow, so it will not change the result.",
            LintFixKind.None),
        new("WL012", "design-system-missing", LintSeverity.Error,
            "A DesignSystem Require step, or a brief that expects the project's design system, finds no approved version.",
            ["an approved design system version on the project"], None,
            "{what} needs the project's approved design system, and none is approved yet.",
            LintFixKind.ApproveDesignSystem),
        new("WL013", "paid-step-in-loop", LintSeverity.Warning,
            "A video-generation or voiceover step sits inside a review loop's repeat span, so every extra round pays for it again.",
            ["paid steps outside the repeat span of a review loop"], None,
            "The {step} costs money and sits inside the review loop at step {loop}; every extra round pays for it again.",
            LintFixKind.ChangeSetting),
        new("WL014", "forward-step-reference", LintSeverity.Error,
            "A step refers to a step that comes after it (or to itself).",
            ["references only to earlier steps"], None,
            "The {step} reads from step {target}, which comes later, so the input does not exist yet.",
            LintFixKind.RetargetReference),
        new("WL015", "egress-permission-missing", LintSeverity.Error,
            "A step sends source media to an outside vendor (region effects, keyframes, an avatar, audio for AI cleanup) and neither the step's own switch nor the run gives the permission to send media.",
            ["the permission to send media, given for the run on the app page (the field \"Allow sending your media to AI services\")"], None,
            "The {step} would send your footage to an outside service, but the permission is off. It would be refused.",
            LintFixKind.FillInput),
        new("WL016", "silent-output", LintSeverity.Warning,
            "The brief asks for a narrated film but the workflow has no voice step.",
            ["a Voiceover step when the brief says narrated"], ["narration"],
            "You asked for a narrated film, but this workflow has no voice step, so the result would be silent.",
            LintFixKind.AddStep),
        new("WL017", "length-unreachable", LintSeverity.Warning,
            "The requested length is longer than the source footage can fill.",
            ["source footage at least as long as the requested length"], None,
            "You asked for {length} seconds, but your footage only adds up to about {available} seconds.",
            LintFixKind.ChangeSetting),
        new("WL018", "manifest-orphan", LintSeverity.Error,
            "An app input points at a field, file slot or step the workflow no longer has.",
            ["every app input bound to something that still exists"], None,
            "The app control {input} points at {target}, which this workflow no longer has.",
            LintFixKind.RemoveReference),
        new("WL019", "captions-without-text-source", LintSeverity.Error,
            "VideoCompile burns captions but neither an applied Voiceover step nor a transcript caption source (an analysis step that transcribes the speech) gives it text to burn.",
            ["EnableVoiceover with a Voiceover step, or transcribed speech"], None,
            "The {step} burns captions, but no narration and no transcribed speech gives it any text. Nothing would appear.",
            LintFixKind.ChangeSetting),
        new("WL020", "review-before-final-cut", LintSeverity.Warning,
            "A ReviewLoop sits before a Voiceover or VideoCompile step that runs later in the workflow, so it judges a film without its final voice and mix.",
            ["a ReviewLoop that runs after every Voiceover, AudioGenerate and VideoCompile step"], None,
            "The review at step {review} runs before {later}, so it checks a silent picture. Move the review after step {last}.",
            LintFixKind.RetargetReference),
        new("WL021", "voiceover-over-narrated-film", LintSeverity.Error,
            "A VideoCompile lays a voiceover over a film that already carries narration (a revision, a continued composition, a render made after a Voiceover step, or a compile with its own voiceover) and keeps the source sound.",
            ["source sound muted when the film already speaks"], None,
            "The {step} lays a voiceover over {source}, a film that already has its own narration, so the narration would be heard twice. Turn on muteSourceAudio so only the new voiceover, music and sound effects play, or switch this compile's voiceover off to keep the film's own narration.",
            LintFixKind.ChangeSetting),
        new("WL022", "censor-needs-feature-track", LintSeverity.Warning,
            "A VideoCompile censors a label that its analysis step tracks only with method Segmentation. The censor reads feature tracks only, so it silently matches nothing.",
            ["censor labels on feature tracks"], None,
            "The censor won't apply to {label}: censoring needs a feature track, and this label is tracked with segmentation only. Use a region effect on that track instead.",
            LintFixKind.ChangeSetting),
        new("WL023", "media-overlay-source-missing", LintSeverity.Error,
            "A picture or video the compile is set to show is missing, is not an image or video, is quarantined, or names an earlier step that cannot produce one.",
            ["the picture or video the overlay names, usable"], None,
            "The {step} is set to show a picture or video, but it cannot be used. The step would stop.",
            LintFixKind.RetargetReference),
        new("WL024", "media-overlay-span-invalid", LintSeverity.Error,
            "A configured picture or video overlay starts or ends at a moment the film cannot have.",
            ["a start and an end inside the film"], None,
            "The {step} is set to show a picture or video from {start} to {end}, but that is not a valid moment in the film. The step would stop.",
            LintFixKind.ChangeSetting),
        new("WL025", "media-overlay-card-too-long", LintSeverity.Warning,
            "A full-screen card stays on screen longer than a quarter of a known film or longer than 30 seconds.",
            ["a card under a quarter of the film or 30 seconds"], None,
            "The {step} keeps a full-screen card on screen for {seconds} seconds. A card should stay under 25% of the film or 30 seconds.",
            LintFixKind.ChangeSetting),
        new("WL026", "voiceover-ignores-script", LintSeverity.Warning,
            "A Voiceover types its lines inline even though an earlier Scriptwriter, NarrationWriter or NarrationTranslator step writes the script or narration, so that script is never spoken.",
            ["a Voiceover wired to the step that writes the lines, unless the user supplied the words exactly"], None,
            "The narration at {step} is typed inline even though {script} writes the lines, so that script is never spoken. Point the voiceover at the script.",
            LintFixKind.ChangeSetting),
        new("WL027", "avatar-unusable", LintSeverity.Error,
            "A VideoGenerate step shows an avatar whose consent is missing, was withdrawn or has expired, or that is not an avatar of this project. The step fails closed; it never shows nobody or someone else instead.",
            ["an avatar with consent on record, in this project"], None,
            "The {step} shows the avatar {avatar}, but {reason}. The step would stop.",
            LintFixKind.ChangeSetting),
        // ---- app inputs (docs/design/app-inputs.md 11.2), appended ----
        new("WL028", "app-input-missing", LintSeverity.Error,
            "A required app input that is shown on the run page has no value and no default.",
            ["a value for every required, visible app input"], None,
            "The app field {input} is required and has no value. The run would be refused.",
            LintFixKind.FillInput),
        new("WL029", "app-input-unfit", LintSeverity.Error,
            "An app input's value, default or choice no longer fits the step setting it fills (the setting's limits changed since the app was saved).",
            ["values that fit every setting they fill"], None,
            "The value of {input} does not fit the setting it fills. The run would be refused.",
            LintFixKind.FillInput),
        new("WL030", "model-input-unavailable", LintSeverity.Error,
            "The model chosen (or recommended) for a model input is no longer selectable, priced, on the plan or able to do the step's job.",
            ["a model the organization can use for the step's job"], None,
            "The model chosen for {input} can no longer be used. The run would be refused.",
            LintFixKind.ChooseModel),
        new("WL031", "branch-skips-step", LintSeverity.Info,
            "A Conditional step decided by app inputs leaves some steps out of this run.",
            ["nothing: it says which steps a switch or choice leaves out"], None,
            "When {input} is {value}, {count} step(s) do not run.",
            LintFixKind.None),
        new("WL032", "agent-model-shared", LintSeverity.Info,
            "A model input changes the model of an agent that several steps use, so all of them use the chosen model.",
            ["nothing: one agent model applies to every step of that agent"], None,
            "{input} changes the model of an agent that {count} steps use.",
            LintFixKind.None),
        new("WL033", "input-range-over-spend-cap", LintSeverity.Warning,
            "With the dearest values its app inputs allow (length, takes), a generation step could cost more than its own spend cap, so such a run would stop at the cap.",
            ["app input ranges whose dearest choice fits the step's spend cap"], None,
            "With the dearest choice of {input}, the {step} could cost about {credits} credits, more than its cap of {cap}.",
            LintFixKind.ChangeSetting),
        // ---- several subjects per clip (docs/design/avatar-objects.md), appended ----
        new("WL034", "subject-model-unsupported", LintSeverity.Error,
            "A VideoGenerate step shows an avatar or an object, and its model takes no pictures at all (a text-only route). The step is refused before anything is charged; the model is never changed for you.",
            ["a model that takes pictures, to show an avatar"], None,
            "The {step} shows an avatar or an object, but its model {model} takes no pictures. {hint}",
            LintFixKind.ChooseModel)
    ];

    /// <summary>
    /// The step types each rule applies to (the single place the step catalog reads, so a card's checks cannot
    /// disagree with the linter). Rules about the whole workflow or the brief apply to no single step type and
    /// are absent. Where the linter already has a source of truth (the plan gate), it is read, not retyped.
    /// </summary>
    public static IReadOnlySet<StepType> AppliesTo(string id) => Applies.TryGetValue(id, out IReadOnlySet<StepType>? set) ? set : NoSteps;

    private static readonly IReadOnlySet<StepType> NoSteps = new HashSet<StepType>();

    private static readonly Dictionary<string, IReadOnlySet<StepType>> Applies = new(StringComparer.Ordinal)
    {
        ["WL001"] = Of(StepType.Voiceover),
        ["WL002"] = Of(StepType.VideoCompile),
        ["WL003"] = Of(StepType.ReviewLoop),
        ["WL004"] = Of(StepType.VideoCompile),
        ["WL005"] = Of(StepType.VideoCompile),
        ["WL006"] = Of(StepType.Voiceover, StepType.VideoGenerate, StepType.AudioGenerate, StepType.VideoAnalyze, StepType.AudioCleanup, StepType.ImageGenerate),
        ["WL007"] = new HashSet<StepType>(Enum.GetValues<StepType>().Where(t => ReelBolt.Shared.Billing.PlanGate.FeatureFor(t) is not null)),
        ["WL008"] = Of(StepType.Voiceover),
        ["WL009"] = Of(StepType.Voiceover),
        ["WL010"] = Of(StepType.Agent, StepType.VideoAnalyze, StepType.VideoCompile),
        ["WL012"] = Of(StepType.DesignSystem),
        ["WL013"] = Of(StepType.VideoGenerate, StepType.Voiceover),
        ["WL015"] = Of(StepType.VideoAnalyze, StepType.VideoGenerate, StepType.AudioCleanup, StepType.VideoEnhance, StepType.ImageGenerate),
        ["WL019"] = Of(StepType.VideoCompile),
        ["WL020"] = Of(StepType.ReviewLoop),
        ["WL021"] = Of(StepType.VideoCompile),
        ["WL022"] = Of(StepType.VideoCompile),
        ["WL023"] = Of(StepType.VideoCompile),
        ["WL024"] = Of(StepType.VideoCompile),
        ["WL025"] = Of(StepType.VideoCompile),
        ["WL026"] = Of(StepType.Voiceover),
        ["WL027"] = Of(StepType.VideoGenerate),
        ["WL034"] = Of(StepType.VideoGenerate)
    };

    private static IReadOnlySet<StepType> Of(params StepType[] types) => new HashSet<StepType>(types);

    private static readonly Dictionary<string, StepCompatibilityRule> ById =
        All.ToDictionary(r => r.Id, StringComparer.Ordinal);

    /// <summary>The rule with this id; throws for an unknown id (a programming error).</summary>
    public static StepCompatibilityRule Get(string id) => ById[id];

    // ------------------------------------------------------------------
    // The consumption matrix (docs/design/workflow-apps.md section 3.8)
    // ------------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="step"/> reads <paramref name="field"/>. The answer comes from
    /// <see cref="BriefFieldConsumers"/>, THE consumption table (also read by the app deriver and validator), so
    /// WL011 and the app page cannot disagree. A custom or unknown agent is assumed to read everything it could be
    /// told, so WL011 never claims a field is unread because of a role it cannot see.
    /// </summary>
    public static bool StepReads(WorkflowStep step, LintBriefField field)
    {
        AgentType? agent = step.AgentDefinition?.AgentType;
        if (step.StepType is StepType.Agent or StepType.Parallel or StepType.ForEach && (agent is null || agent == AgentType.Custom))
            return true;

        AppStepFacts facts = AppStepFacts.From(step.StepType, agent, step.VideoAnalyzeConfigJson, step.VideoGenerateConfigJson, step.VideoCompileConfigJson, step.ImageGenerateConfigJson);
        IReadOnlySet<string> read = BriefFieldConsumers.FieldsReadBy(facts);
        return field switch
        {
            LintBriefField.Language => read.Contains(BriefFieldConsumers.Language),
            LintBriefField.SourceLanguage => read.Contains(BriefFieldConsumers.SourceLanguage),
            LintBriefField.LengthSec => read.Contains(BriefFieldConsumers.LengthSec),
            LintBriefField.Voice => read.Contains(BriefFieldConsumers.Voice),
            LintBriefField.Style => read.Contains(BriefFieldConsumers.StylePreset) || read.Contains(BriefFieldConsumers.Tone),
            LintBriefField.Format => read.Contains(BriefFieldConsumers.Format),
            LintBriefField.Resolution => read.Contains(BriefFieldConsumers.Resolution),
            LintBriefField.DesignSystem => read.Contains(BriefFieldConsumers.DesignSystem) || step.StepType == StepType.DesignSystem,
            LintBriefField.SourceFiles => new[] { "video", "code", "music", "sfx", "image", "document" }
                .Any(role => BriefFieldConsumers.AcceptsFileRole(role, [facts])),
            LintBriefField.Avatar => read.Contains(BriefFieldConsumers.Avatar),
            _ => false
        };
    }
}
