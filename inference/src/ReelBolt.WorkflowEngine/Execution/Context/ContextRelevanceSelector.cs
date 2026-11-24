using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// The one implementation of <see cref="IContextRelevanceSelector"/>: composes a bounded,
/// descriptor-only state from the droppable prior-step parts (enriched with the project's own
/// files) and asks a Decision model, in ONE
/// <see cref="IDecisionClient.DecideAsync"/> call, one two-option
/// <see cref="DecisionKind.Choice"/> question per candidate ("Keep" / "Drop").
///
/// <para>
/// <b>Why several <see cref="DecisionKind.Choice"/> questions and not one multi-choice
/// question:</b> <see cref="DecisionKind"/> is closed — there is no multi-choice kind — and
/// adding one would change a wire format two working clients already speak. Several two-option
/// questions in a single call express the same thing with zero protocol change, and give a
/// per-candidate probability (<c>P(Keep)</c>) that is directly usable as a relevance score.
/// </para>
///
/// <para>
/// <b>Soft-failure is the contract</b> (see <see cref="IContextRelevanceSelector"/>): every
/// failure mode — disabled, no provider, transport error, malformed answer, cancellation —
/// returns <c>null</c> and the budget falls back to its original oldest-first dropping. Nothing
/// here may throw into a step.
/// </para>
/// </summary>
public sealed class ContextRelevanceSelector : IContextRelevanceSelector
{
    private const string KeepOption = "Keep";
    private const string DropOption = "Drop";
    private const string AbbreviationMarker = "…";
    private const string NoSummaryPlaceholder = "(no summary)";

    /// <summary>
    /// Hard, in-code ceiling on the question count for a single request — independent of
    /// <see cref="ContextRelevanceOptions.MaxCandidates"/>. The logprob decision client issues one
    /// completion per question, so an unbounded list would be an unbounded cost; a mistyped
    /// configuration must not be able to produce one.
    /// </summary>
    private const int HardMaxCandidates = 32;

    /// <summary>
    /// Sanity floor for <see cref="ContextRelevanceOptions.MaxStateChars"/>: below this there is
    /// no room for the header plus a couple of real descriptors, so a mistyped tiny value is
    /// raised rather than silently disabling the feature. A cap that genuinely leaves fewer than
    /// two candidates described still degrades to "no ordering" — the requested state size is
    /// honoured, it just buys nothing.
    /// </summary>
    private const int MinStateChars = 1_024;
    private const int HardMaxStateChars = 32_000;

    /// <summary>Longest preview taken from any single candidate's descriptor text.</summary>
    private const int MaxDescriptorChars = 240;

    /// <summary>
    /// A candidate whose preview cannot be at least this long is left out of BOTH the state and
    /// the question set entirely — asking a model about an item it cannot actually see would only
    /// produce noise.
    /// </summary>
    private const int MinDescriptorChars = 24;

    private const int MaxLabelChars = 80;

    /// <summary>
    /// How many project files may enrich the state. Deliberately a code constant rather than
    /// another configuration knob: files are context for the ranking, never candidates that can be
    /// dropped, so their count is a readability/cost concern and not an operator decision.
    /// </summary>
    private const int MaxFileContextItems = 8;

    /// <summary>
    /// Deliberately a few dozen words, not a paragraph: this text is billed against
    /// <see cref="ContextRelevanceOptions.MaxStateChars"/> for every request, and a long preamble
    /// would leave a small-but-legitimate cap with no room for a single candidate.
    /// </summary>
    private const string StateHeader =
        "Context items offered to an agent whose prompt exceeded its size budget. Answer \"Keep\" " +
        "(helps the current step) or \"Drop\" (least relevant; give it up first) for each.\n";

    private readonly IInferenceProviderResolver _resolver;
    private readonly IDecisionClientFactory _clientFactory;
    private readonly IProjectFileWorkspace _files;
    private readonly IOptions<ContextRelevanceOptions> _options;
    private readonly ILogger<ContextRelevanceSelector> _logger;

    public ContextRelevanceSelector(
        IInferenceProviderResolver resolver,
        IDecisionClientFactory clientFactory,
        IProjectFileWorkspace files,
        IOptions<ContextRelevanceOptions> options,
        ILogger<ContextRelevanceSelector> logger)
    {
        _resolver = resolver;
        _clientFactory = clientFactory;
        _files = files;
        _options = options;
        _logger = logger;
    }

    public async Task<ContextRelevanceOrdering?> SelectDropOrderAsync(
        Guid projectId,
        IReadOnlyList<ContextRelevanceCandidate> stepCandidates,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stepCandidates);

        ContextRelevanceOptions options = _options.Value;

        // Fewer than two droppable parts means the ranking cannot change anything: a single
        // droppable part is dropped first under either rule, so paying for a decision would buy
        // nothing.
        if (!options.Enabled || stepCandidates.Count < 2)
            return null;

        try
        {
            ResolvedDecisionProvider? provider = await _resolver.ResolveDecisionAsync(options.ProviderId, ct);
            if (provider is null)
            {
                // "No Decision provider configured" is a first-class state, not an error: the
                // budget simply keeps its original drop order.
                return null;
            }

            IDecisionClient client = _clientFactory.Get(provider);

            List<ContextRelevanceCandidate> candidates =
                await BuildCandidateListAsync(projectId, stepCandidates, options, ct);

            (string state, List<ContextRelevanceCandidate> asked) = ComposeState(candidates, options.MaxStateChars);
            if (asked.Count < 2)
                return null;

            List<DecisionQuestion> questions = asked.Select(BuildQuestion).ToList();

            // ONE call. Several questions, one request — see the type's doc comment.
            DecisionResult result = await client.DecideAsync(new DecisionState(state), questions, ct);

            return BuildOrdering(asked, result, options.AcceptAt);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The execution is winding down; the agent run this prompt feeds observes the same
            // token and will unwind on its own. Swallowing here is what keeps cancellation from
            // being reported as a relevance failure.
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Context relevance selection failed for project {ProjectId}; the context budget falls back to its oldest-first drop order",
                projectId);
            return null;
        }
    }

    /// <summary>
    /// The droppable STEP candidates first (they are the only things that can actually be
    /// dropped, so they always get a question while budget remains — oldest-first, so the parts
    /// the default rule would have given up first are the ones the model gets to re-rank), then as
    /// many project files as the candidate cap and the state cap allow. File listing is
    /// best-effort: a storage/database failure costs context, never the selection.
    /// </summary>
    private async Task<List<ContextRelevanceCandidate>> BuildCandidateListAsync(
        Guid projectId,
        IReadOnlyList<ContextRelevanceCandidate> stepCandidates,
        ContextRelevanceOptions options,
        CancellationToken ct)
    {
        int maxCandidates = Math.Clamp(options.MaxCandidates, 1, HardMaxCandidates);

        List<ContextRelevanceCandidate> all = stepCandidates.Take(maxCandidates).ToList();

        if (all.Count >= maxCandidates)
            return all;

        try
        {
            IReadOnlyList<ProjectWorkspaceFile> files = await _files.ListFilesAsync(projectId, ct);

            foreach (ProjectWorkspaceFile file in files)
            {
                if (all.Count >= maxCandidates || all.Count - stepCandidates.Count >= MaxFileContextItems)
                    break;

                all.Add(new ContextRelevanceCandidate(
                    Key: $"file:{file.Id}",
                    Kind: ContextRelevanceCandidateKind.ProjectFile,
                    StepOrder: 0,
                    Label: file.OriginalFileName,
                    Meta: $"{file.MimeType}, {file.SizeBytes} bytes",
                    Descriptor: string.IsNullOrWhiteSpace(file.AgentSummary) ? NoSummaryPlaceholder : file.AgentSummary));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not list project files for {ProjectId} while composing the context-relevance state; continuing with step descriptors only",
                projectId);
        }

        return all;
    }

    /// <summary>
    /// Renders exactly the candidates that fit under <paramref name="maxStateChars"/> and returns
    /// both the text and that same subset — the two are produced together on purpose, because a
    /// question about an item missing from the state would be unanswerable. The character cap is
    /// therefore upheld structurally (a candidate is skipped whole, never half-written); the
    /// trailing clamp is belt-and-braces and unreachable.
    /// </summary>
    private static (string State, List<ContextRelevanceCandidate> Asked) ComposeState(
        IReadOnlyList<ContextRelevanceCandidate> candidates,
        int maxStateChars)
    {
        int cap = Math.Clamp(maxStateChars, MinStateChars, HardMaxStateChars);

        StringBuilder builder = new(StateHeader);
        List<ContextRelevanceCandidate> asked = new(candidates.Count);

        foreach (ContextRelevanceCandidate candidate in candidates)
        {
            string label = Abbreviate(candidate.Label, MaxLabelChars);
            string prefix = $"- [{candidate.Key}] {label} ({candidate.Meta}): ";

            // +1 for the newline that terminates this candidate's line.
            int available = cap - builder.Length - prefix.Length - 1;
            if (available < MinDescriptorChars)
                break;

            string preview = BuildPreview(candidate.Descriptor, Math.Min(MaxDescriptorChars, available));
            if (string.IsNullOrEmpty(preview))
                preview = NoSummaryPlaceholder;

            builder.Append(prefix).Append(preview).Append('\n');
            asked.Add(candidate);
        }

        string state = builder.ToString();
        if (state.Length > cap)
            state = state[..cap];

        return (state, asked);
    }

    private static DecisionQuestion BuildQuestion(ContextRelevanceCandidate candidate) =>
        new(
            Name: candidate.Key,
            Kind: DecisionKind.Choice,
            Instructions:
                $"Context item [{candidate.Key}] \"{Abbreviate(candidate.Label, MaxLabelChars)}\" " +
                $"({candidate.Meta}) is available to the next step. Keep it, or drop it first if " +
                "the prompt has to shrink?",
            Options: [KeepOption, DropOption]);

    /// <summary>
    /// Turns the answers into a drop order. Only STEP candidates are ranked (files cannot be
    /// dropped), and only answers whose confidence reaches <paramref name="acceptAt"/> are trusted
    /// enough to reorder anything — everything else keeps today's oldest-first treatment, which
    /// <see cref="AgentInputBudget"/> applies to any part this ordering does not list. Returns
    /// <c>null</c> when nothing was trusted, which is the "degrade to exactly today's behaviour"
    /// path.
    /// </summary>
    private static ContextRelevanceOrdering? BuildOrdering(
        IReadOnlyList<ContextRelevanceCandidate> asked,
        DecisionResult result,
        double acceptAt)
    {
        List<(int StepOrder, double KeepScore)> trusted = [];

        foreach (ContextRelevanceCandidate candidate in asked)
        {
            if (candidate.Kind != ContextRelevanceCandidateKind.Step)
                continue;

            if (!result.Answers.TryGetValue(candidate.Key, out DecisionAnswer? answer))
                continue;

            if (answer.Confidence is not double confidence || confidence < acceptAt)
                continue;

            if (!TryReadKeepScore(answer, out double keepScore))
                continue;

            trusted.Add((candidate.StepOrder, keepScore));
        }

        if (trusted.Count == 0)
            return null;

        List<int> ordered = trusted
            .OrderBy(t => t.KeepScore)
            .ThenBy(t => t.StepOrder)
            .Select(t => t.StepOrder)
            .ToList();

        return new ContextRelevanceOrdering(ordered);
    }

    /// <summary>
    /// P(Keep) for one Choice answer: the renormalized probability of the "Keep" option when the
    /// client reports probabilities, otherwise the chosen option itself (1 / 0). Both are
    /// deliberate: a client that only reports a discrete choice still yields a usable ordering,
    /// and any answer of an unexpected kind is discarded rather than guessed at.
    /// </summary>
    private static bool TryReadKeepScore(DecisionAnswer answer, out double keepScore)
    {
        keepScore = 0.0;

        if (answer.Kind != DecisionKind.Choice)
            return false;

        if (answer.Probabilities.TryGetValue(KeepOption, out double keepProbability))
        {
            keepScore = keepProbability;
            return true;
        }

        if (string.Equals(answer.Choice, KeepOption, StringComparison.Ordinal))
        {
            keepScore = 1.0;
            return true;
        }

        if (string.Equals(answer.Choice, DropOption, StringComparison.Ordinal))
        {
            keepScore = 0.0;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A single-line, whitespace-collapsed preview of at most <paramref name="maxChars"/>
    /// characters (including the abbreviation marker when something was cut). Collapsing happens
    /// AFTER truncation and on the short slice only, so a megabyte-scale step output is never
    /// normalized in full just to show 240 characters of it.
    /// </summary>
    private static string BuildPreview(string descriptor, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(descriptor) || maxChars <= 0)
            return string.Empty;

        string slice = descriptor.Length <= maxChars ? descriptor : Abbreviate(descriptor, maxChars);
        return NormalizeWhitespace(slice);
    }

    private static string Abbreviate(string value, int maxChars)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.Length <= maxChars)
            return value;

        int keep = Math.Min(value.Length, maxChars - AbbreviationMarker.Length);
        if (keep <= 0)
            return value[..Math.Min(value.Length, Math.Max(0, maxChars))];

        return string.Concat(value.AsSpan(0, keep), AbbreviationMarker);
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
