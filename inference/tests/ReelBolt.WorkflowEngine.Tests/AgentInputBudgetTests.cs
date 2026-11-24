using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.Context;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Covers <see cref="AgentInputBudget"/> directly. The under-budget/single-part cases build the
/// expected "legacy" concatenation BY HAND rather than by calling any production helper — those
/// are the regression guard that a budgeted call and an unbudgeted call produce the exact same
/// string whenever nothing actually needs shrinking (the single most important behavior of this
/// feature, per its safety contract).
/// </summary>
public class AgentInputBudgetTests
{
    private static AgentInputBudgetOptions DefaultOptions() => new()
    {
        Enabled = true,
        MaxInputChars = 1000,
        MaxDigestedStepChars = 200,
        RecentStepsVerbatim = 1,
        DigestMaxStringChars = 50,
        DigestMaxArrayItems = 5,
        DigestMaxDepth = 6
    };

    private static string LegacyFormat(IReadOnlyList<(int StepOrder, string StepLabel, string Output)> parts)
    {
        if (parts.Count == 0)
            return "[\"Begin analysis of the project.\"]";
        if (parts.Count == 1)
            return parts[0].Output;

        return string.Join("\n\n---\n\n", parts.Select(p => $"## Step {p.StepOrder}: {p.StepLabel}\n{p.Output}"));
    }

    [Fact]
    public void Apply_under_budget_is_byte_identical_to_the_legacy_format()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Analyze", "analysis-output"),
            (2, "Dependency", "dependency-output"),
            (3, "Inventory", "inventory-output")
        };
        AgentInputBudgetOptions options = DefaultOptions(); // MaxInputChars = 1000, far above these tiny parts

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.Text.Should().Be(LegacyFormat(parts));
        result.OriginalChars.Should().Be(result.Text.Length);
        result.FinalChars.Should().Be(result.Text.Length);
        result.DigestedStepCount.Should().Be(0);
        result.DroppedStepCount.Should().Be(0);
    }

    [Fact]
    public void Apply_single_part_returns_its_output_verbatim_with_no_header()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Analyze", "solo-output")
        };

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, DefaultOptions());

        result.Text.Should().Be("solo-output");
        result.Text.Should().Be(LegacyFormat(parts));
    }

    [Fact]
    public void Apply_empty_parts_returns_the_placeholder()
    {
        BudgetedConcatenation result = AgentInputBudget.Apply(
            new List<(int StepOrder, string StepLabel, string Output)>(), DefaultOptions());

        result.Text.Should().Be("[\"Begin analysis of the project.\"]");
        result.DigestedStepCount.Should().Be(0);
        result.DroppedStepCount.Should().Be(0);
    }

    [Fact]
    public void Apply_disabled_is_a_no_op_even_when_hugely_over_budget()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Analyze", new string('a', 5000)),
            (2, "Dependency", new string('b', 5000))
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.Enabled = false;
        options.MaxInputChars = 100; // would be hugely over budget if enabled

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.Text.Should().Be(LegacyFormat(parts));
        result.DigestedStepCount.Should().Be(0);
        result.DroppedStepCount.Should().Be(0);
    }

    [Fact]
    public void Apply_negative_or_zero_MaxInputChars_disables_the_cap()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Analyze", new string('a', 5000))
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.MaxInputChars = 0;

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.Text.Should().Be(LegacyFormat(parts));
        result.DigestedStepCount.Should().Be(0);
    }

    [Fact]
    public void Apply_digests_older_steps_once_over_budget_but_keeps_the_most_recent_verbatim()
    {
        string bigJson = "{\"summary\":\"" + new string('x', 500) + "\"}";
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Old A", bigJson),
            (2, "Old B", bigJson),
            (3, "Recent", bigJson) // RecentStepsVerbatim = 1, so only this one is protected
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.MaxInputChars = 700; // plain concatenation of 3 big parts is well over this

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.DigestedStepCount.Should().Be(2); // steps 1 and 2
        result.Text.Should().Contain(bigJson); // step 3's untouched body is still present verbatim
        result.Text.Should().Contain("## Step 3: Recent");
        result.FinalChars.Should().BeLessThan(result.OriginalChars);
    }

    [Fact]
    public void Apply_recent_N_steps_are_never_digested_even_though_they_are_JSON()
    {
        string bigJson = "{\"summary\":\"" + new string('x', 500) + "\"}";
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Old", bigJson),
            (2, "Recent A", bigJson),
            (3, "Recent B", bigJson)
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.RecentStepsVerbatim = 2; // steps 2 and 3 protected
        options.MaxInputChars = 700;

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.Text.Should().Contain($"## Step 2: Recent A\n{bigJson}");
        result.Text.Should().Contain($"## Step 3: Recent B\n{bigJson}");
        result.DigestedStepCount.Should().Be(1); // only step 1
    }

    [Fact]
    public void Apply_drops_oldest_first_with_a_marker_once_digesting_alone_is_not_enough()
    {
        // Each part's digested form is still ~200 chars (DigestMaxDepth JSON structural digest
        // caps strings but the overall shape survives), so three digested parts plus one
        // protected part can still exceed a very small MaxInputChars — forcing the drop phase.
        string bigJson = "{\"summary\":\"" + new string('x', 5000) + "\"}";
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Oldest", bigJson),
            (2, "Middle", bigJson),
            (3, "Recent", bigJson)
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.RecentStepsVerbatim = 1;
        options.MaxDigestedStepChars = 300;
        options.MaxInputChars = 400; // small enough that even two digested + one protected won't fit

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.DroppedStepCount.Should().BeGreaterThan(0);
        result.Text.Should().Contain("## Step 1: Oldest");
        result.Text.Should().Contain("Step 1 output omitted by context budget");
        result.Text.Should().Contain($"{bigJson.Length} chars");
        // Step 2 (also non-protected) should be dropped before step 3 (protected) is ever touched,
        // since drops proceed oldest-first.
        result.Text.Should().Contain("## Step 3: Recent");
        result.Text.Should().Contain(bigJson); // the protected step 3 body, still fully intact
    }

    [Fact]
    public void Apply_protected_only_overflow_returns_protected_parts_intact_and_reports_the_overflow()
    {
        string hugeJson = "{\"summary\":\"" + new string('x', 5000) + "\"}";
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Recent A", hugeJson),
            (2, "Recent B", hugeJson)
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.RecentStepsVerbatim = 2; // both parts protected — nothing left to digest or drop
        options.MaxInputChars = 200; // impossible to hit with two ~5000-char protected parts

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.Text.Should().Contain(hugeJson);
        result.DigestedStepCount.Should().Be(0);
        result.DroppedStepCount.Should().Be(0);
        result.FinalChars.Should().BeGreaterThan(options.MaxInputChars); // still over budget — never corrupted
    }

    [Fact]
    public void Apply_stats_reflect_original_and_final_character_counts()
    {
        string bigJson = "{\"summary\":\"" + new string('x', 2000) + "\"}";
        List<(int StepOrder, string StepLabel, string Output)> parts = new()
        {
            (1, "Old", bigJson),
            (2, "Recent", bigJson)
        };
        AgentInputBudgetOptions options = DefaultOptions();
        options.RecentStepsVerbatim = 1;
        options.MaxInputChars = 1500;

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.OriginalChars.Should().Be(LegacyFormat(parts).Length);
        result.FinalChars.Should().Be(result.Text.Length);
        result.FinalChars.Should().BeLessThan(result.OriginalChars);
    }

    // --- Relevance ordering (context relevance, issue #95 §3j) -------------------------------
    //
    // Every test below uses the same deliberately flat shape: three EQUAL-SIZED droppable parts
    // (so "which one was dropped" is the only thing that can differ) plus one protected part, with
    // MaxInputChars set to "legacy length minus 100". Digesting is a no-op at that size (every
    // part is already at or under MaxDigestedStepChars), and dropping any single part frees well
    // over 100 characters — so every run drops EXACTLY one part, and the marker's location names
    // the drop order unambiguously.

    private static List<(int StepOrder, string StepLabel, string Output)> DropOrderParts() =>
    [
        (1, "Oldest", new string('a', 300)),
        (2, "Middle", new string('b', 300)),
        (3, "Newest Droppable", new string('c', 300)),
        (4, "Protected", new string('d', 100))
    ];

    private static AgentInputBudgetOptions DropOrderOptions(
        List<(int StepOrder, string StepLabel, string Output)> parts)
    {
        AgentInputBudgetOptions options = new()
        {
            Enabled = true,
            MaxDigestedStepChars = 300, // every part is already <= this, so digesting changes nothing
            RecentStepsVerbatim = 1,
            DigestMaxStringChars = 50,
            DigestMaxArrayItems = 5,
            DigestMaxDepth = 6
        };
        options.MaxInputChars = LegacyFormat(parts).Length - 100;
        return options;
    }

    private static string DropMarker(int stepOrder, int outputChars) =>
        $"_[Step {stepOrder} output omitted by context budget ({outputChars} chars) " +
        "— re-read it with the project/file tools if you need it.]_";

    [Fact]
    public void Apply_with_a_relevance_ordering_drops_the_least_relevant_part_first()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);

        // Step 3 is the model's least relevant, then 1, then 2.
        ContextRelevanceOrdering ordering = new([3, 1, 2]);

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options, ordering);

        result.DroppedStepCount.Should().Be(1);
        result.Text.Should().Contain($"## Step 3: Newest Droppable\n{DropMarker(3, 300)}");
        result.Text.Should().Contain($"## Step 1: Oldest\n{new string('a', 300)}");
        result.Text.Should().Contain($"## Step 2: Middle\n{new string('b', 300)}");
        result.Text.Should().NotContain(DropMarker(1, 300));
    }

    [Fact]
    public void Apply_without_a_relevance_ordering_still_drops_oldest_first()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options);

        result.DroppedStepCount.Should().Be(1);
        result.Text.Should().Contain($"## Step 1: Oldest\n{DropMarker(1, 300)}");
        result.Text.Should().Contain($"## Step 3: Newest Droppable\n{new string('c', 300)}");
    }

    [Fact]
    public void Apply_ignores_relevance_step_orders_that_are_protected_or_absent()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);

        // 4 is protected at this budget and 99 does not exist: the model may name anything, but the
        // budget alone decides what it is allowed to drop. The remaining ranking still applies.
        ContextRelevanceOrdering ordering = new([4, 99, 3, 1, 2]);

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options, ordering);

        result.DroppedStepCount.Should().Be(1);
        result.Text.Should().Contain($"## Step 3: Newest Droppable\n{DropMarker(3, 300)}");
        result.Text.Should().Contain($"## Step 4: Protected\n{new string('d', 100)}");
    }

    [Fact]
    public void Apply_with_a_partial_relevance_ordering_drops_the_listed_parts_first_and_the_rest_oldest_first()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);

        // Only step 3 was ranked; 1 and 2 keep today's oldest-first treatment after it.
        ContextRelevanceOrdering ordering = new([3]);

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options, ordering);

        result.Text.Should().Contain(DropMarker(3, 300));
        result.Text.Should().NotContain(DropMarker(1, 300));

        // Same setup, but now TWO drops are needed: the ranked step goes first, then the oldest.
        AgentInputBudgetOptions tighter = DropOrderOptions(parts);
        tighter.MaxInputChars = LegacyFormat(parts).Length - 350;

        BudgetedConcatenation twoDrops = AgentInputBudget.Apply(parts, tighter, ordering);

        twoDrops.DroppedStepCount.Should().Be(2);
        twoDrops.Text.Should().Contain(DropMarker(3, 300));
        twoDrops.Text.Should().Contain(DropMarker(1, 300));
        twoDrops.Text.Should().NotContain(DropMarker(2, 300));
    }

    [Fact]
    public void Apply_with_an_empty_relevance_ordering_is_identical_to_no_ordering()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);

        BudgetedConcatenation withoutOrdering = AgentInputBudget.Apply(parts, options);
        BudgetedConcatenation withEmptyOrdering = AgentInputBudget.Apply(parts, options, new ContextRelevanceOrdering([]));

        withEmptyOrdering.Text.Should().Be(withoutOrdering.Text);
        withEmptyOrdering.DroppedStepCount.Should().Be(withoutOrdering.DroppedStepCount);
    }

    [Fact]
    public void Apply_under_budget_ignores_a_relevance_ordering_entirely()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);
        options.MaxInputChars = 1_000_000; // far above the legacy concatenation

        BudgetedConcatenation result = AgentInputBudget.Apply(parts, options, new ContextRelevanceOrdering([3, 1, 2]));

        result.Text.Should().Be(LegacyFormat(parts));
        result.DroppedStepCount.Should().Be(0);
        result.DigestedStepCount.Should().Be(0);
    }

    [Fact]
    public void Apply_relevance_ordering_changes_only_the_order_not_what_survives()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();

        // A budget so tight that every droppable part must go: the two orderings must land on the
        // SAME final text (rule 6), differing only in the order they got there.
        AgentInputBudgetOptions options = DropOrderOptions(parts);
        options.MaxInputChars = 400;

        BudgetedConcatenation oldestFirst = AgentInputBudget.Apply(parts, options);
        BudgetedConcatenation relevanceFirst = AgentInputBudget.Apply(parts, options, new ContextRelevanceOrdering([3, 1, 2]));

        oldestFirst.DroppedStepCount.Should().Be(3);
        relevanceFirst.DroppedStepCount.Should().Be(3);
        relevanceFirst.Text.Should().Be(oldestFirst.Text);
    }

    [Fact]
    public void SelectDroppableParts_returns_only_the_non_protected_parts_oldest_first()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);
        options.RecentStepsVerbatim = 2;

        IReadOnlyList<(int StepOrder, string StepLabel, string Output)> droppable =
            AgentInputBudget.SelectDroppableParts(parts, options);

        droppable.Select(p => p.StepOrder).Should().Equal(1, 2);
    }

    [Fact]
    public void SelectDroppableParts_returns_nothing_when_every_part_is_protected()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();
        AgentInputBudgetOptions options = DropOrderOptions(parts);
        options.RecentStepsVerbatim = 4;

        AgentInputBudget.SelectDroppableParts(parts, options).Should().BeEmpty();
    }

    [Fact]
    public void MeasureConcatenatedChars_matches_the_legacy_format_length()
    {
        List<(int StepOrder, string StepLabel, string Output)> parts = DropOrderParts();

        AgentInputBudget.MeasureConcatenatedChars(parts).Should().Be(LegacyFormat(parts).Length);
    }
}
