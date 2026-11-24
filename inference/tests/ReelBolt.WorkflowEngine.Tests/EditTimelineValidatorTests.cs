using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="EditTimelineValidator"/> — the structural and cross-tenant invariants the editing
/// contract depends on.
///
/// The suite is hostile-fixture-first: every rule has a document that VIOLATES it and is asserted
/// to produce exactly the expected violation (not merely "some violation"), plus a valid document
/// that must produce none. The cross-tenant <c>AssetRef</c> cases are the review focus: a
/// reference that resolves to another project's file or step result is a cross-tenant data leak,
/// so each kind gets its own dedicated rejection test rather than a shared positive-path check.
/// <c>Provenance.StepResultId</c> is the SECOND field that can name another project's row and gets
/// the same treatment, including a test that proves the rule reaches every item kind.
/// </summary>
public class EditTimelineValidatorTests
{
    // ---------------------------------------------------------------------------------------
    // Valid document — the control case every hostile fixture is contrasted against.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Valid_document_produces_no_violations()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                fixture.MediaClip(0, 100, fixture.OwnedFileRef),
                new Transition(Guid.NewGuid(), "Dissolve", 100, 110, "dissolve"),
                fixture.MediaClip(110, 300, fixture.OwnedFileRef, sourceIn: 200),
                new TitleClip(Guid.NewGuid(), "Title", 300, 400, "Jane Doe - Engineer"),
                new Caption(Guid.NewGuid(), "Caption", 400, 500, "It's a wrap, folks"),
                fixture.InsertClip(500, 600, fixture.OwnedStepResultRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeTrue(
            $"a well-formed document must produce no violations, but got: {Describe(result)}");
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Text_containing_allowlist_stripped_characters_is_valid()
    {
        // Deliberate: an apostrophe, a percent sign, a colon and an emoji are all STRIPPED by the
        // sanitizer but do not empty the string — real ASR captions contain apostrophes, so a
        // "text must equal its sanitized form" rule would be a functional defect, not hardening.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                new Caption(Guid.NewGuid(), "C1", 0, 10, "It's 50% done: really"),
                new Caption(Guid.NewGuid(), "C2", 10, 20, "Great shot \U0001F44D")));

        EditTimelineValidator.Validate(timeline, fixture.Context()).IsValid.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------
    // Hostile fixtures — one per rule.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Out_of_order_track_is_rejected()
    {
        // The second item starts BEFORE the first yet ends before the first starts, so this is
        // purely an ordering defect: it must be reported once, and the overlap rule must not
        // fabricate a second violation for it.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                fixture.MediaClip(100, 200, fixture.OwnedFileRef),
                fixture.MediaClip(50, 60, fixture.OwnedFileRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[1].recordIn");
        result.Violations[0].Message.Should().Contain("sorted by recordIn");
    }

    [Fact]
    public void Overlapping_items_are_rejected()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                fixture.MediaClip(0, 100, fixture.OwnedFileRef),
                fixture.MediaClip(50, 150, fixture.OwnedFileRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[1].recordIn");
        result.Violations[0].Message.Should().Contain("overlaps");
    }

    [Fact]
    public void Transition_off_the_boundary_is_rejected()
    {
        // A transition between two clips must sit exactly at the cut: its recordIn must equal the
        // predecessor's recordOut. Here it starts one frame late, leaving an unintended gap.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                fixture.MediaClip(0, 100, fixture.OwnedFileRef),
                new Transition(Guid.NewGuid(), "Dissolve", 101, 110, "dissolve"),
                fixture.MediaClip(110, 150, fixture.OwnedFileRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[1].recordIn");
        result.Violations[0].Message.Should().Contain("boundary");
    }

    [Fact]
    public void MediaClip_overrunning_its_source_asset_is_rejected()
    {
        var fixture = new Fixture();

        // sourceIn 800 + duration 200 = 1000 frames needed, but the asset is only 900 long.
        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 200, fixture.OwnedFileRef, sourceIn: 800)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].sourceIn");
        result.Violations[0].Message.Should().Contain("900");
    }

    [Fact]
    public void MediaClip_fitting_exactly_in_its_source_asset_is_valid()
    {
        var fixture = new Fixture();

        // sourceIn 800 + duration 100 = exactly the 900 frames the asset has: the boundary itself
        // is exclusive-end-legal and must NOT be rejected.
        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, fixture.OwnedFileRef, sourceIn: 800)));

        EditTimelineValidator.Validate(timeline, fixture.Context()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void MediaClip_whose_asset_duration_is_unknown_is_not_a_violation()
    {
        // The ref resolves and is owned by the right project, but the caller has no duration for
        // it. No caller in this phase can supply one (ProjectFile has no duration column and the
        // MediaDerivative table has no writer yet), so "unknown duration" must mean UNVALIDATED,
        // never "provably wrong" — otherwise every real document would be rejected.
        var fixture = new Fixture();
        var context = new EditTimelineValidationContext(
            fixture.ProjectId,
            new Dictionary<AssetRef, long>(),
            fixture.ProjectFileOwners,
            fixture.StepResultOwners);

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, fixture.OwnedFileRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, context);

        result.IsValid.Should().BeTrue(
            $"an unknown asset duration must skip the source-extent check, but got: {Describe(result)}");
        result.Violations.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_duration_is_unvalidated_while_ownership_stays_strict()
    {
        // The asymmetry this fix is about, proved directly on ONE document shape:
        //
        //   duration ABSENT from the lookup      -> valid   (unvalidated, not rejected)
        //   duration PRESENT but too small       -> violation (the unchanged overrun check)
        //   foreign-project AssetRef, either way -> violation (ownership is unchanged)
        //
        // The third case is what keeps the cross-tenant Review Focus guarantee intact: relaxing
        // the DURATION lookup must not relax the OWNERSHIP lookups by one inch.
        var fixture = new Fixture();

        // A clip that needs 1000 frames from its asset but is handed a context that says the
        // asset is only 900 long: sourceIn 800 + duration 200 = 1000 > 900.
        EditTimeline overrunning = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 200, fixture.OwnedFileRef, sourceIn: 800)));

        EditTimelineValidationContext withoutDuration = new(
            fixture.ProjectId,
            new Dictionary<AssetRef, long>(),
            fixture.ProjectFileOwners,
            fixture.StepResultOwners);

        EditTimelineValidationContext withTooSmallDuration = new(
            fixture.ProjectId,
            new Dictionary<AssetRef, long> { [fixture.OwnedFileRef] = 900 },
            fixture.ProjectFileOwners,
            fixture.StepResultOwners);

        // 1. Same document, absence of the duration entry: no violation at all.
        ValidationResult unknownDuration =
            EditTimelineValidator.Validate(overrunning, withoutDuration);
        unknownDuration.IsValid.Should().BeTrue(
            $"a missing duration entry must simply skip the check, but got: {Describe(unknownDuration)}");

        // 2. Same document, a too-small duration present: the overrun check still fires.
        ValidationResult knownDuration =
            EditTimelineValidator.Validate(overrunning, withTooSmallDuration);
        knownDuration.Violations.Should().ContainSingle($"got: {Describe(knownDuration)}");
        knownDuration.Violations[0].Path.Should().Be("tracks[0].items[0].sourceIn");
        knownDuration.Violations[0].Message.Should().Contain("900");

        // 3. A foreign-project ref still violates under BOTH contexts — durational leniency must
        //    never leak into the ownership rule.
        EditTimeline foreign = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, fixture.ForeignFileRef)));

        foreach (EditTimelineValidationContext context in new[] { withoutDuration, withTooSmallDuration })
        {
            ValidationResult foreignResult = EditTimelineValidator.Validate(foreign, context);
            foreignResult.Violations.Should().ContainSingle($"got: {Describe(foreignResult)}");
            foreignResult.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
            foreignResult.Violations[0].Message.Should().Contain("not found in this project");
        }
    }

    [Fact]
    public void IsNotFoundInProject_recognises_every_unresolved_reference_and_nothing_structural()
    {
        var fixture = new Fixture();
        var unknownFile = new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid());
        var unknownStep = new AssetRef(AssetRefKind.StepResult, StepResultId: Guid.NewGuid());

        foreach (AssetRef reference in new[] { fixture.ForeignFileRef, fixture.ForeignStepResultRef, unknownFile, unknownStep })
        {
            ValidationResult result = EditTimelineValidator.Validate(
                Timeline(fixture, VideoTrack(fixture.MediaClip(0, 100, reference))), fixture.Context());
            result.Violations.Should().ContainSingle($"got: {Describe(result)}");
            EditTimelineValidator.IsNotFoundInProject(result.Violations[0].Message).Should().BeTrue(result.Violations[0].Message);
        }

        EditTimelineValidator.IsNotFoundInProject("sequence 1 does not exist in this timeline").Should().BeFalse();
    }

    [Fact]
    public void Foreign_and_unknown_ids_produce_the_same_message_and_never_name_the_owner()
    {
        var fixture = new Fixture();
        var unknownFile = new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid());
        var unknownStep = new AssetRef(AssetRefKind.StepResult, StepResultId: Guid.NewGuid());

        string Msg(AssetRef r)
        {
            ValidationResult res = EditTimelineValidator.Validate(
                Timeline(fixture, VideoTrack(fixture.MediaClip(0, 100, r))), fixture.Context());
            res.Violations.Should().ContainSingle();
            return res.Violations[0].Message;
        }

        string ProvMsg(Guid id)
        {
            ValidationResult res = EditTimelineValidator.Validate(
                Timeline(fixture, VideoTrack(new MediaClip(
                    Guid.NewGuid(), "Clip", 0, 100, fixture.OwnedFileRef,
                    Provenance: fixture.FromStepResult(id)))),
                fixture.Context());
            res.Violations.Should().ContainSingle();
            return res.Violations[0].Message;
        }

        (string Foreign, Guid ForeignId, string Unknown, Guid UnknownId)[] cases =
        [
            (Msg(fixture.ForeignFileRef), fixture.ForeignFileId, Msg(unknownFile), unknownFile.ProjectFileId!.Value),
            (Msg(fixture.ForeignStepResultRef), fixture.ForeignStepResultId, Msg(unknownStep), unknownStep.StepResultId!.Value),
            (ProvMsg(fixture.ForeignStepResultId), fixture.ForeignStepResultId, ProvMsg(Guid.NewGuid()), Guid.Empty),
        ];

        foreach (var c in cases)
        {
            c.Foreign.Should().NotContain(fixture.OtherProjectId.ToString());
            c.Foreign.Should().NotContain("belongs to project");
            c.Unknown.Should().NotContain("belongs to project");
        }

        // Same text once the (caller-supplied) id is masked.
        cases[0].Foreign.Replace(cases[0].ForeignId.ToString(), "ID")
            .Should().Be(cases[0].Unknown.Replace(cases[0].UnknownId.ToString(), "ID"));
        cases[1].Foreign.Replace(cases[1].ForeignId.ToString(), "ID")
            .Should().Be(cases[1].Unknown.Replace(cases[1].UnknownId.ToString(), "ID"));
        cases[2].Foreign.Replace(fixture.ForeignStepResultId.ToString(), "ID")
            .Should().MatchRegex("^stepResultId ID not found in this project$");
        cases[2].Unknown.Should().MatchRegex("^stepResultId [0-9a-f-]{36} not found in this project$");
    }

    [Fact]
    public void MediaClip_referencing_a_foreign_projects_file_is_rejected()
    {
        // REVIEW FOCUS. This is the cross-tenant leak: the document is being validated for
        // fixture.ProjectId, but its MediaClip points at a ProjectFile owned by another project.
        // It must be rejected, and rejected ONCE — the source-extent check must not pile a
        // second, unrelated violation on top of the security-relevant one.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, fixture.ForeignFileRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
        result.Violations[0].Message.Should().Contain("not found in this project");
        result.Violations[0].Message.Should().NotContain(fixture.OtherProjectId.ToString());
        result.Violations[0].Message.Should().NotContain("belongs to project");
    }

    [Fact]
    public void MediaClip_referencing_a_foreign_projects_step_result_is_rejected()
    {
        // Same leak, the other AssetRefKind.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, fixture.ForeignStepResultRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
        result.Violations[0].Message.Should().Contain("not found in this project");
        result.Violations[0].Message.Should().NotContain(fixture.OtherProjectId.ToString());
        result.Violations[0].Message.Should().NotContain("belongs to project");
    }

    [Fact]
    public void InsertClip_referencing_a_foreign_projects_step_result_is_rejected()
    {
        // Every AssetRef resolves within the expected project — the rule is not MediaClip-only.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.InsertClip(0, 100, fixture.ForeignStepResultRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
        result.Violations[0].Message.Should().Contain("not found in this project");
        result.Violations[0].Message.Should().NotContain(fixture.OtherProjectId.ToString());
        result.Violations[0].Message.Should().NotContain("belongs to project");
    }

    [Fact]
    public void MediaClip_referencing_an_unknown_file_id_is_rejected()
    {
        var fixture = new Fixture();
        var unknownRef = new AssetRef(AssetRefKind.ProjectFile, Guid.NewGuid());

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, unknownRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
        result.Violations[0].Message.Should().Contain("not found in this project");
    }

    [Fact]
    public void AssetRef_with_a_null_id_for_its_kind_is_rejected()
    {
        var fixture = new Fixture();
        var malformed = new AssetRef(AssetRefKind.ProjectFile, ProjectFileId: null);

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, malformed)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
        result.Violations[0].Message.Should().Contain("projectFileId is null");
    }

    // ---------------------------------------------------------------------------------------
    // Provenance.StepResultId — the SECOND way a document can name another project's row.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Provenance_naming_a_foreign_projects_step_result_is_rejected()
    {
        // REVIEW FOCUS, the same leak as the AssetRef cases above through a different field: the
        // document is validated FOR fixture.ProjectId, but an item's Provenance names a
        // WorkflowStepResult owned by another project. Provenance is declared on the base Item, so
        // it is reachable on EVERY item kind and must resolve through the SAME StepResultOwners
        // map — a foreign provenance id is a cross-tenant data leak, not harmless metadata.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new MediaClip(
                Guid.NewGuid(),
                "Clip",
                0,
                100,
                fixture.OwnedFileRef,
                Provenance: fixture.FromStepResult(fixture.ForeignStepResultId))));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeFalse();
        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].provenance.stepResultId");
        result.Violations[0].Message.Should().Contain("not found in this project");
        result.Violations[0].Message.Should().NotContain(fixture.OtherProjectId.ToString());
        result.Violations[0].Message.Should().NotContain("belongs to project");
    }

    [Fact]
    public void Provenance_naming_an_unknown_step_result_is_rejected()
    {
        // "Unknown" must never be read as "trusted" here either: an id resolving to no row at all
        // is a violation, exactly as it is for an AssetRef.
        var fixture = new Fixture();
        var unknown = Guid.NewGuid();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new MediaClip(
                Guid.NewGuid(),
                "Clip",
                0,
                100,
                fixture.OwnedFileRef,
                Provenance: fixture.FromStepResult(unknown))));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].provenance.stepResultId");
        result.Violations[0].Message.Should().Contain("not found in this project");
        result.Violations[0].Message.Should().Contain(unknown.ToString());
    }

    [Fact]
    public void Provenance_naming_a_step_result_owned_by_this_project_is_valid()
    {
        // The control case: the same shape as the hostile fixture, with an id the project really
        // owns. Without this, "reject every provenance id" would pass the tests above.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new MediaClip(
                Guid.NewGuid(),
                "Clip",
                0,
                100,
                fixture.OwnedFileRef,
                Provenance: fixture.FromStepResult(fixture.OwnedStepResultId))));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeTrue(
            $"an owned provenance step result is a legitimate reference, but got: {Describe(result)}");
    }

    [Fact]
    public void Provenance_step_result_is_validated_on_every_item_kind()
    {
        // Provenance is declared on the base Item rather than on a subclass, so a check written
        // inside the per-kind switch would silently skip every kind it has no case for. These four
        // cover the MediaClip path, the InsertClip path, a text path and the switch's default path.
        var fixture = new Fixture();

        (string Label, Item Item)[] items =
        [
            ("mediaClip", new MediaClip(
                Guid.NewGuid(),
                "Clip",
                0,
                100,
                fixture.OwnedFileRef,
                Provenance: fixture.FromStepResult(fixture.ForeignStepResultId))),
            ("insertClip", new InsertClip(
                Guid.NewGuid(),
                "Insert",
                0,
                100,
                fixture.OwnedStepResultRef,
                Provenance: fixture.FromStepResult(fixture.ForeignStepResultId))),
            ("titleClip", new TitleClip(
                Guid.NewGuid(),
                "Title",
                0,
                100,
                "Jane Doe",
                Provenance: fixture.FromStepResult(fixture.ForeignStepResultId))),
            ("generatorClip", new GeneratorClip(
                Guid.NewGuid(),
                "Bars",
                0,
                100,
                "bars",
                Provenance: fixture.FromStepResult(fixture.ForeignStepResultId))),
        ];

        foreach ((string label, Item item) in items)
        {
            ValidationResult result = EditTimelineValidator.Validate(
                Timeline(fixture, VideoTrack(item)),
                fixture.Context());

            result.Violations.Should().ContainSingle(
                $"the {label} kind must have its provenance validated too, but got: {Describe(result)}");
            result.Violations[0].Path.Should().Be("tracks[0].items[0].provenance.stepResultId");
        }
    }

    [Fact]
    public void Provenance_without_a_step_result_id_is_not_a_violation()
    {
        // Provenance is OPTIONAL metadata. A hand-authored title, a v1 seed item with no recorded
        // step result and an analyzed generator clip all legitimately name none, and demanding one
        // would reject valid documents. The rule bites only once a step result is actually named.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                new TitleClip(Guid.NewGuid(), "Title", 0, 100, "Jane Doe"),
                new Caption(
                    Guid.NewGuid(),
                    "C",
                    100,
                    200,
                    "It's a wrap, folks",
                    Provenance: new Provenance(ProvenanceOrigin.Manual)),
                new GeneratorClip(
                    Guid.NewGuid(),
                    "Bars",
                    200,
                    300,
                    "bars",
                    Provenance: new Provenance(ProvenanceOrigin.Analyzed))));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeTrue(
            $"provenance is optional and a null stepResultId is not a violation, but got: {Describe(result)}");
    }

    [Fact]
    public void Provenance_agent_and_provider_ids_are_not_resolved_against_the_ownership_maps()
    {
        // The deliberate half of the decision, proved rather than asserted in prose:
        // AgentDefinitionId and ProviderId name rows in GLOBAL, non-project-scoped catalogues
        // (agent_definitions and inference_providers carry no ProjectId column), so there is no
        // cross-project boundary to enforce and no map in this context that could resolve them.
        // Both ids below are absent from BOTH ownership maps — if the validator ever started
        // routing them through one, they would read as "unknown" and this document would be
        // rejected. A future consumer of these fields must resolve-and-check at its own point of
        // use instead of relying on validation here.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new MediaClip(
                Guid.NewGuid(),
                "Clip",
                0,
                100,
                fixture.OwnedFileRef,
                Provenance: new Provenance(
                    Origin: ProvenanceOrigin.Generated,
                    AgentDefinitionId: Guid.NewGuid(),
                    ProviderId: Guid.NewGuid(),
                    OfferedId: "s7"))));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeTrue(
            $"a global-catalogue id has no project to belong to, but got: {Describe(result)}");
    }

    [Fact]
    public void AssetRef_populating_both_ids_is_rejected()
    {
        var fixture = new Fixture();

        var contradictory = new AssetRef(
            AssetRefKind.ProjectFile,
            ProjectFileId: fixture.OwnedFileId,
            StepResultId: fixture.OwnedStepResultId);

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.MediaClip(0, 100, contradictory)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].assetRef");
        result.Violations[0].Message.Should().Contain("stepResultId is also set");
    }

    [Fact]
    public void Whitespace_only_title_text_is_rejected()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new TitleClip(Guid.NewGuid(), "T", 0, 100, "   \n\t  ")));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].text");
    }

    [Fact]
    public void Text_that_sanitizes_away_to_nothing_is_rejected()
    {
        // Emoji fall outside the \p{L}/\p{N}/\p{M} allowlist, so an emoji-only caption renders as
        // nothing at all — it is "unsanitizable" in the sense this validator cares about.
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new Caption(Guid.NewGuid(), "C", 0, 100, "\U0001F44D\U0001F600")));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].text");
        result.Violations[0].Message.Should().Contain("sanitization");
    }

    [Fact]
    public void Oversized_keyframe_list_is_rejected()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.InsertClip(
                0, 100, fixture.OwnedStepResultRef,
                keyframeCount: EditTimelineValidator.MaxKeyframes + 1)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].quad.keys");
        result.Violations[0].Message.Should().Contain(
            EditTimelineValidator.MaxKeyframes.ToString());
    }

    [Fact]
    public void Keyframe_list_at_exactly_the_bound_is_valid()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(fixture.InsertClip(
                0, 100, fixture.OwnedStepResultRef,
                keyframeCount: EditTimelineValidator.MaxKeyframes)));

        EditTimelineValidator.Validate(timeline, fixture.Context()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RecordOut_at_or_before_recordIn_is_rejected()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(new GeneratorClip(Guid.NewGuid(), "Bars", 500, 500, "bars")));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items[0].recordOut");
    }

    [Fact]
    public void Too_many_tracks_is_rejected()
    {
        var fixture = new Fixture();
        Track[] tracks = Enumerable
            .Range(0, EditTimelineValidator.MaxTracks + 1)
            .Select(_ => VideoTrack())
            .ToArray();

        ValidationResult result = EditTimelineValidator.Validate(Timeline(fixture, tracks), fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks");
        result.Violations[0].Message.Should().Contain("64");
    }

    [Fact]
    public void Too_many_items_in_a_track_is_rejected()
    {
        var fixture = new Fixture();
        Item[] items = Enumerable
            .Range(0, EditTimelineValidator.MaxItemsPerTrack + 1)
            .Select(i => (Item)new GeneratorClip(Guid.NewGuid(), "Bars", i * 2L, (i * 2L) + 1, "bars"))
            .ToArray();

        ValidationResult result = EditTimelineValidator.Validate(
            Timeline(fixture, new Track(
                Guid.NewGuid(), TrackKind.Video, "V1", null, 100, false, true, false, false, null, items)),
            fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks[0].items");
        result.Violations[0].Message.Should().Contain("5000");
    }

    [Fact]
    public void Null_tracks_collection_is_rejected_without_throwing()
    {
        var fixture = new Fixture();

        EditTimeline timeline = new(
            id: Guid.NewGuid(),
            projectId: fixture.ProjectId,
            name: "Broken",
            settings: Settings(),
            tracks: null!,
            markers: Array.Empty<Marker>());

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Should().ContainSingle($"got: {Describe(result)}");
        result.Violations[0].Path.Should().Be("tracks");
    }

    // ---------------------------------------------------------------------------------------
    // Completeness — every violation, not just the first.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Every_violation_is_reported_not_just_the_first()
    {
        var fixture = new Fixture();

        EditTimeline timeline = Timeline(
            fixture,
            VideoTrack(
                // item 0: foreign-project ref (one violation)
                fixture.MediaClip(0, 100, fixture.ForeignFileRef),
                // item 1: overlaps item 0 (one violation)
                fixture.MediaClip(50, 120, fixture.OwnedFileRef),
                // item 2: source overrun (one violation)
                fixture.MediaClip(200, 400, fixture.OwnedFileRef, sourceIn: 800)),
            VideoTrack(
                // item 0: unsanitizable text (one violation)
                new Caption(Guid.NewGuid(), "C", 0, 100, "%%%:::"),
                // item 1: oversized keyframes (one violation)
                fixture.InsertClip(
                    100, 200, fixture.OwnedStepResultRef,
                    keyframeCount: EditTimelineValidator.MaxKeyframes + 1)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeFalse();
        result.Violations.Should().HaveCount(5, $"got: {Describe(result)}");

        var paths = result.Violations.Select(v => v.Path).ToList();
        paths.Should().BeEquivalentTo(new[]
        {
            "tracks[0].items[0].assetRef",
            "tracks[0].items[1].recordIn",
            "tracks[0].items[2].sourceIn",
            "tracks[1].items[0].text",
            "tracks[1].items[1].quad.keys",
        });
    }

    [Fact]
    public void Oversized_track_does_not_suppress_other_tracks_violations()
    {
        // The bounds rule must not short-circuit the rest of the document: a reviewer fixing the
        // 400 body should see every problem in one round trip.
        var fixture = new Fixture();

        Item[] oversized = Enumerable
            .Range(0, EditTimelineValidator.MaxItemsPerTrack + 1)
            .Select(i => (Item)new GeneratorClip(Guid.NewGuid(), "Bars", i * 2L, (i * 2L) + 1, "bars"))
            .ToArray();

        EditTimeline timeline = Timeline(
            fixture,
            new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 100, false, true, false, false, null, oversized),
            VideoTrack(fixture.MediaClip(0, 100, fixture.ForeignFileRef)));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.Violations.Select(v => v.Path).Should().Equal(
            "tracks[0].items",
            "tracks[1].items[0].assetRef");
    }

    // ---------------------------------------------------------------------------------------
    // Bounds are named public constants (acceptance 5).
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Size_bounds_are_named_public_constants_with_the_agreed_values()
    {
        EditTimelineValidator.MaxTracks.Should().Be(64);
        EditTimelineValidator.MaxItemsPerTrack.Should().Be(5000);
        EditTimelineValidator.MaxKeyframes.Should().Be(256);
    }

    // ---------------------------------------------------------------------------------------
    // Fixtures and helpers.
    // ---------------------------------------------------------------------------------------

    // ── P5: clip depth, track audio, catalogues ──────────────────────────────

    [Fact]
    public void P5_fields_inside_their_ranges_are_valid()
    {
        var fixture = new Fixture();
        MediaClip clip = fixture.MediaClip(0, 100, fixture.OwnedFileRef);
        MediaClip deep = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, 30, 1.0, true, null,
            new Keyframed<double>(-6, [new Keyframe<double>(0, -12, InterpKind.Linear), new Keyframe<double>(50, 0, InterpKind.Step)]),
            new ClipTransform(PositionX: new Keyframed<double>(0.25), Scale: new Keyframed<double>(0, [new Keyframe<double>(0, 1, InterpKind.Linear), new Keyframe<double>(99, 2, InterpKind.Linear)]), Opacity: new Keyframed<double>(0.8)),
            new ClipCrop(0.1, 0, 0.2, 0.3), Reverse: true, FadeInFrames: 10, FadeOutFrames: 20);
        Track track = VideoTrack(deep, new Transition(Guid.NewGuid(), "x", 100, 110, "wipeleft", "easeInOutQuad"), fixture.MediaClip(110, 200, fixture.OwnedFileRef, sourceIn: 300)) with
        {
            GainDb = new Keyframed<double>(-3, [new Keyframe<double>(0, 0, InterpKind.Linear), new Keyframe<double>(10, -12, InterpKind.Linear)]),
            Pan = -0.5,
            Eq = new TrackEq(80, 12000, [new EqBand(3000, 2, 1.4)])
        };
        EditTimeline timeline = Timeline(fixture, track, VideoTrack(new GeneratorClip(Guid.NewGuid(), "g", 0, 50, "color", new Dictionary<string, object> { ["color"] = "#FF8800" })));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        result.IsValid.Should().BeTrue(Describe(result));
    }

    [Fact]
    public void P5_out_of_range_numbers_and_catalogue_misses_are_each_reported_at_their_path()
    {
        var fixture = new Fixture();
        MediaClip clip = fixture.MediaClip(0, 100, fixture.OwnedFileRef);
        MediaClip bad = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, 0, 1.0, true, null,
            new Keyframed<double>(40),
            new ClipTransform(Opacity: new Keyframed<double>(1.5), Scale: new Keyframed<double>(0, [new Keyframe<double>(10, 1, InterpKind.Linear), new Keyframe<double>(10, 2, InterpKind.Linear)])),
            new ClipCrop(0.6, 0, 0.5, 0), Reverse: true, Hold: true, FadeInFrames: 60, FadeOutFrames: 60);
        Track track = VideoTrack(bad, new Transition(Guid.NewGuid(), "x", 100, 110, "sparkle", "bouncy"), fixture.MediaClip(110, 200, fixture.OwnedFileRef)) with { Pan = 2, Eq = new TrackEq(5, null, [new EqBand(100, 30)]) };
        EditTimeline timeline = Timeline(fixture, track, VideoTrack(new GeneratorClip(Guid.NewGuid(), "g", 0, 50, "plasma", new Dictionary<string, object> { ["color"] = "red" })));

        ValidationResult result = EditTimelineValidator.Validate(timeline, fixture.Context());

        string[] paths = result.Violations.Select(v => v.Path).ToArray();
        paths.Should().Contain("tracks[0].items[0].gainDb.value");
        paths.Should().Contain("tracks[0].items[0].transform.opacity.value");
        paths.Should().Contain("tracks[0].items[0].transform.scale.keys[1].frame");
        paths.Should().Contain("tracks[0].items[0].crop");
        paths.Should().Contain("tracks[0].items[0].reverse");
        paths.Should().Contain("tracks[0].items[0].fadeInFrames");
        paths.Should().Contain("tracks[0].items[1].transitionType");
        paths.Should().Contain("tracks[0].items[1].easing");
        paths.Should().Contain("tracks[0].pan");
        paths.Should().Contain("tracks[0].eq.lowCutHz");
        paths.Should().Contain("tracks[0].eq.bands[0].gainDb");
        paths.Should().Contain("tracks[1].items[0].generatorType");
        paths.Should().Contain("tracks[1].items[0].generatorConfig.color");
        result.Violations.Should().Contain(v => v.Path == "tracks[0].items[0].reverse" && v.Message.Contains("both reversed and held"));
    }

    [Fact]
    public void P5_a_reversed_clip_longer_than_the_ceiling_is_rejected()
    {
        var fixture = new Fixture();
        MediaClip clip = fixture.MediaClip(0, EditTimelineValidator.MaxReverseFrames + 1, fixture.OwnedFileRef);
        MediaClip reversed = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, Reverse: true);
        ValidationResult result = EditTimelineValidator.Validate(Timeline(fixture, VideoTrack(reversed)), fixture.Context());
        result.Violations.Should().Contain(v => v.Path == "tracks[0].items[0].reverse" && v.Message.Contains("at most"), Describe(result));
    }

    private static string Describe(ValidationResult result) =>
        result.Violations.Count == 0
            ? "no violations"
            : string.Join("; ", result.Violations.Select(v => $"{v.Path}: {v.Message}"));

    private static EditTimelineSettings Settings() => new(
        Width: 1920,
        Height: 1080,
        Rate: new Rational(30, 1),
        SampleRate: 48000,
        PixelAspect: new Rational(1, 1),
        AspectPolicy: AspectPolicy.Fit,
        StartTimecodeFrames: 0);

    private static EditTimeline Timeline(Fixture fixture, params Track[] tracks) => new(
        id: Guid.NewGuid(),
        projectId: fixture.ProjectId,
        name: "Timeline",
        settings: Settings(),
        tracks: tracks,
        markers: Array.Empty<Marker>());

    private static Track VideoTrack(params Item[] items) => new(
        Id: Guid.NewGuid(),
        Kind: TrackKind.Video,
        Name: "V1",
        Color: null,
        Height: 100,
        Locked: false,
        Enabled: true,
        Mute: false,
        Solo: false,
        BusId: null,
        Items: items);

    /// <summary>
    /// The ownership/duration lookups a caller (T-015) builds from the database, plus the ids the
    /// hostile fixtures reference. Two projects exist so a "foreign" ref is a real, resolvable ref
    /// that simply belongs to somebody else — the case a naive "does this id exist?" check passes.
    /// </summary>
    private sealed class Fixture
    {
        public const long KnownDurationFrames = 900;

        public Guid ProjectId { get; } = Guid.NewGuid();

        public Guid OtherProjectId { get; } = Guid.NewGuid();

        public Guid OwnedFileId { get; } = Guid.NewGuid();

        public Guid ForeignFileId { get; } = Guid.NewGuid();

        public Guid OwnedStepResultId { get; } = Guid.NewGuid();

        public Guid ForeignStepResultId { get; } = Guid.NewGuid();

        public AssetRef OwnedFileRef => new(AssetRefKind.ProjectFile, OwnedFileId);

        public AssetRef ForeignFileRef => new(AssetRefKind.ProjectFile, ForeignFileId);

        public AssetRef OwnedStepResultRef => new(AssetRefKind.StepResult, StepResultId: OwnedStepResultId);

        public AssetRef ForeignStepResultRef => new(AssetRefKind.StepResult, StepResultId: ForeignStepResultId);

        /// <summary>
        /// A provenance naming a step result, for the fixtures that exercise the second
        /// cross-tenant lookup. <paramref name="stepResultId"/> is deliberately allowed to be an id
        /// that is in NEITHER ownership map: "unknown" is a case the rule has to handle.
        /// </summary>
        public Provenance FromStepResult(Guid? stepResultId) =>
            new(Origin: ProvenanceOrigin.Generated, StepResultId: stepResultId);

        public IReadOnlyDictionary<Guid, Guid> ProjectFileOwners => new Dictionary<Guid, Guid>
        {
            [OwnedFileId] = ProjectId,
            [ForeignFileId] = OtherProjectId,
        };

        public IReadOnlyDictionary<Guid, Guid> StepResultOwners => new Dictionary<Guid, Guid>
        {
            [OwnedStepResultId] = ProjectId,
            [ForeignStepResultId] = OtherProjectId,
        };

        public EditTimelineValidationContext Context() => new(
            ProjectId,
            new Dictionary<AssetRef, long>
            {
                [OwnedFileRef] = KnownDurationFrames,
                [OwnedStepResultRef] = KnownDurationFrames,
            },
            ProjectFileOwners,
            StepResultOwners);

        public MediaClip MediaClip(long recordIn, long recordOut, AssetRef assetRef, long sourceIn = 0) =>
            new(
                Id: Guid.NewGuid(),
                Name: "Clip",
                RecordIn: recordIn,
                RecordOut: recordOut,
                AssetRef: assetRef,
                SourceIn: sourceIn);

        public InsertClip InsertClip(
            long recordIn,
            long recordOut,
            AssetRef assetRef,
            int keyframeCount = 3) =>
            new(
                Id: Guid.NewGuid(),
                Name: "Insert",
                RecordIn: recordIn,
                RecordOut: recordOut,
                AssetRef: assetRef,
                Quad: new Keyframed<QuadCorners>(
                    Value: null,
                    Keys: Enumerable
                        .Range(0, keyframeCount)
                        .Select(i => new Keyframe<QuadCorners>(i, SampleQuad(), InterpKind.Linear))
                        .ToList()));
    }

    private static QuadCorners SampleQuad() => new(
        new QuadPoint(0, 0),
        new QuadPoint(1, 0),
        new QuadPoint(1, 1),
        new QuadPoint(0, 1));

    [Fact]
    public void ScaleY_and_a_title_transform_are_range_checked_like_scale()
    {
        var fixture = new Fixture();
        MediaClip clip = fixture.MediaClip(0, 100, fixture.OwnedFileRef);
        MediaClip bad = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, 0, 1.0, true, null,
            Transform: new ClipTransform(ScaleY: new Keyframed<double>(-1)));
        TitleClip title = new(Guid.NewGuid(), "t", 100, 150, "Hi", Template: "lowerThird",
            Transform: new ClipTransform(ScaleY: new Keyframed<double>(-1), Scale: new Keyframed<double>(9)));
        EditTimeline timeline = Timeline(fixture, VideoTrack(bad), VideoTrack(title));

        string[] paths = EditTimelineValidator.Validate(timeline, fixture.Context()).Violations.Select(v => v.Path).ToArray();

        paths.Should().Contain("tracks[0].items[0].transform.scaleY.value");
        paths.Should().Contain("tracks[1].items[0].transform.scaleY.value");
        paths.Should().Contain("tracks[1].items[0].transform.scale.value");
    }

    [Fact]
    public void A_valid_scaleY_and_a_title_transform_on_a_template_are_accepted()
    {
        var fixture = new Fixture();
        MediaClip clip = fixture.MediaClip(0, 100, fixture.OwnedFileRef);
        MediaClip free = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, 0, 1.0, true, null,
            Transform: new ClipTransform(Scale: new Keyframed<double>(2), ScaleY: new Keyframed<double>(0.5)));
        TitleClip title = new(Guid.NewGuid(), "t", 100, 150, "Hi", Template: "card",
            Transform: new ClipTransform(PositionX: new Keyframed<double>(0.1), ScaleY: new Keyframed<double>(1.5), Rotation: new Keyframed<double>(10)));

        EditTimelineValidator.Validate(Timeline(fixture, VideoTrack(free), VideoTrack(title)), fixture.Context()).IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_zero_scaleY_or_scale_reads_as_absent_and_a_negative_one_is_refused()
    {
        var fixture = new Fixture();
        MediaClip clip = fixture.MediaClip(0, 100, fixture.OwnedFileRef);
        MediaClip zero = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, 0, 1.0, true, null,
            Transform: new ClipTransform(Scale: new Keyframed<double>(0), ScaleY: new Keyframed<double>(0)));
        EditTimelineValidator.Validate(Timeline(fixture, VideoTrack(zero)), fixture.Context()).IsValid.Should().BeTrue();

        MediaClip negative = new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, 0, 1.0, true, null,
            Transform: new ClipTransform(Scale: new Keyframed<double>(-1)));
        EditTimelineValidator.Validate(Timeline(fixture, VideoTrack(negative)), fixture.Context()).Violations
            .Select(v => v.Path).Should().Contain("tracks[0].items[0].transform.scale.value");
    }
}
