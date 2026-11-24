using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Avatars;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Several subjects per clip on the inline path (docs/design/avatar-objects.md "Subjects in a video generation step"):
/// the per-kind switches, the one-person rule, the path the model's own declarations choose, the fair sharing of
/// reference slots, the composed first frame, what the step records, the ledger, and the re-check of EVERY subject
/// before each take. Fakes only.
/// </summary>
public class VideoGenerateSubjectsExecutorTests
{
    private static string Cfg(IEnumerable<Guid> subjects, bool egress = true, int takes = 1, string extra = "") =>
        "{\"maxSpendCredits\":2000.0,\"takes\":" + takes + ",\"maxClips\":" + takes
        + ",\"inlinePrompt\":\"Anna pours a coffee at the machine\",\"timeoutSeconds\":60,\"pollIntervalSeconds\":1,\"subjects\":["
        + string.Join(",", subjects.Select(id => "{\"avatarId\":\"" + id.ToString("D") + "\"}"))
        + "],\"allowSourceMediaEgress\":" + (egress ? "true" : "false") + extra + "}";

    private static string Failed(StepExecutionResult r) => JsonNode.Parse(r.Output)!["meta"]!["failed"]!.GetValue<string>();

    private static string Error(StepExecutionResult r) => JsonNode.Parse(r.Output)!["meta"]!["error"]!.GetValue<string>();

    private static void NothingReserved(SubjectsTestHarness h) => h.Reservations.Should().BeEmpty();

    private static readonly VideoReferenceInputSupport NoReferences = VideoReferenceInputSupport.None;

    // ---------------------------------------------------------------- References: sharing, records, ledger

    [Fact]
    public async Task A_person_and_two_objects_share_the_models_reference_slots_and_the_step_records_each_of_them()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1, pictures: 2, speech: true);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2, pictures: 2, motion: true);
        ResolvedAvatar cup = SubjectsTestHarness.Thing(3, pictures: 1, name: "Cup");
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id, cup.Id]), [anna, machine, cup]);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        VideoGenerationRequest sent = h.Sent.Should().ContainSingle().Subject;
        sent.Images.Should().BeNull("reference mode sends no frame");
        // Subject by subject, each in its own order: Anna's two pictures and her speech, the machine's two pictures and
        // its motion video, the cup's picture. Only the person brings a speech sample.
        sent.References!.Select(r => (r.Kind, r.Role)).Should().Equal(
            (AvatarReferenceKind.Image, AvatarReferenceRole.Face),
            (AvatarReferenceKind.Image, AvatarReferenceRole.HalfBody),
            (AvatarReferenceKind.Audio, AvatarReferenceRole.Speech),
            (AvatarReferenceKind.Image, AvatarReferenceRole.Front),
            (AvatarReferenceKind.Image, AvatarReferenceRole.Angle),
            (AvatarReferenceKind.Video, AvatarReferenceRole.Motion),
            (AvatarReferenceKind.Image, AvatarReferenceRole.Front));

        JsonObject meta = JsonNode.Parse(result.Output)!["meta"]!.AsObject();
        meta.ContainsKey(AvatarOutputFacts.MetaKey).Should().BeFalse("a step that names Subjects records meta.subjects, not meta.avatar");
        meta[SubjectOutputFacts.PathKey]!.GetValue<string>().Should().Be("references");
        JsonArray subjects = meta[SubjectOutputFacts.MetaKey]!.AsArray();
        subjects.Select(s => (s!["name"]!.GetValue<string>(), s["kind"]!.GetValue<string>())).Should().Equal(
            ("Anna presenter", "person"), ("Espresso machine", "object"), ("Cup", "object"));
        subjects.Select(s => s!["fingerprint"]!.GetValue<string>()).Should().Equal(
            AvatarFingerprint.Compute(anna), AvatarFingerprint.Compute(machine), AvatarFingerprint.Compute(cup));
        subjects[0]!["referencesSent"]!.ToJsonString().Should().Be("""{"images":2,"videos":0,"audio":1}""");
        subjects[1]!["referencesSent"]!.ToJsonString().Should().Be("""{"images":2,"videos":1,"audio":0}""");
        subjects[2]!["referencesSent"]!.ToJsonString().Should().Be("""{"images":1,"videos":0,"audio":0}""");

        string identity = SubjectsFingerprint.Compute([anna, machine, cup]);
        JsonObject facts = meta["subjectFacts"]!.AsObject();
        facts["fingerprint"]!.GetValue<string>().Should().Be(identity);
        facts["showsPerson"]!.GetValue<bool>().Should().BeTrue();
        facts["marking"]!.GetValue<string>().Should().Be("metadata+visibleLabel", "a person among the subjects = that avatar's disclosure");

        // The ledger: avatar_id holds the person; subject_ids_json every avatar the take showed.
        VideoGenerationReservationRequest reservation = h.Reservations.Should().ContainSingle().Subject;
        reservation.AvatarId.Should().Be(anna.Id);
        reservation.SubjectIds.Should().Equal(anna.Id, machine.Id, cup.Id);
        await using var db = h.Factory.CreateDbContext();
        ExternalGenerationJob row = await db.Set<ExternalGenerationJob>().SingleAsync();
        row.AvatarId.Should().Be(anna.Id);
        ExternalGenerationJob.ReadSubjectIds(row.SubjectIdsJson).Should().Equal(anna.Id, machine.Id, cup.Id);

        // Each clip carries the fingerprint of the SET, under its own key.
        JsonNode clip = JsonNode.Parse(result.Output)!["clips"]![0]!;
        clip[SubjectOutputFacts.ClipFingerprintKey]!.GetValue<string>().Should().Be(identity);
        clip.AsObject().ContainsKey(AvatarOutputFacts.ClipFingerprintKey).Should().BeFalse();
    }

    [Fact]
    public async Task Every_subject_gets_one_picture_before_any_subject_gets_a_second()
    {
        // A model that takes four reference pictures, three subjects with three pictures each: 2 + 1 + 1.
        var four = new VideoReferenceInputSupport(4, 0, 0, 4, 0, 0, AudioDrivesSpeech: false, ExclusiveWithFrames: true);
        ResolvedAvatar anna = SubjectsTestHarness.Person(1, pictures: 3, speech: false);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2, pictures: 3);
        ResolvedAvatar cup = SubjectsTestHarness.Thing(3, pictures: 3, name: "Cup");
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id, cup.Id]), [anna, machine, cup], references: four);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Single().References!.Count.Should().Be(4);
        JsonArray subjects = JsonNode.Parse(result.Output)!["meta"]![SubjectOutputFacts.MetaKey]!.AsArray();
        subjects.Select(s => s!["referencesSent"]!["images"]!.GetValue<int>()).Should().Equal(2, 1, 1);
        // What was left out is listed per subject, with the reason.
        subjects[0]!["referencesSkipped"]!.AsArray().Should().ContainSingle()
            .Which!["reason"]!.GetValue<string>().Should().Be(VideoReferenceInput.SkipKindLimit);
        subjects[1]!["referencesSkipped"]!.AsArray().Count.Should().Be(2);
        subjects[2]!["referencesSkipped"]!.AsArray().Count.Should().Be(2);
    }

    [Fact]
    public async Task A_subject_left_with_no_picture_refuses_the_step_before_anything_is_reserved()
    {
        // Two reference pictures, three subjects: the third would be shown from nothing.
        var two = new VideoReferenceInputSupport(2, 0, 0, 2, 0, 0, AudioDrivesSpeech: false, ExclusiveWithFrames: true);
        ResolvedAvatar a = SubjectsTestHarness.Thing(1), b = SubjectsTestHarness.Thing(2), c = SubjectsTestHarness.Thing(3);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([a.Id, b.Id, c.Id]), [a, b, c], references: two);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(VideoReferenceInput.NotSupportedCode);
        Error(result).Should().Contain("Show fewer avatars");
        NothingReserved(h);
        h.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_subjects_own_roles_choose_what_of_it_is_sent()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1, pictures: 2, speech: true);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2, pictures: 2);
        string config = Cfg([anna.Id]).Replace(
            "\"subjects\":[{\"avatarId\":\"" + anna.Id.ToString("D") + "\"}]",
            "\"subjects\":[{\"avatarId\":\"" + anna.Id.ToString("D") + "\",\"roles\":[\"Face\"]},{\"avatarId\":\""
            + machine.Id.ToString("D") + "\",\"roles\":[\"Angle\"]}]", StringComparison.Ordinal);
        SubjectsTestHarness h = SubjectsTestHarness.Build(config, [anna, machine]);

        await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Single().References!.Select(r => r.Role).Should().Equal(AvatarReferenceRole.Face, AvatarReferenceRole.Angle);
    }

    // ---------------------------------------------------------------- switches, the one-person rule, egress

    [Fact]
    public async Task The_generation_switch_is_judged_for_each_subjects_kind()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);

        // Objects on, people off: the object alone runs, the pair does not, and nothing of either avatar is read.
        var objectsOnly = new AvatarGenerationOptions { Enabled = false, Generation = AvatarGenerationMode.Off, ObjectsEnabled = true };
        SubjectsTestHarness alone = SubjectsTestHarness.Build(Cfg([machine.Id]), [anna, machine], options: objectsOnly);
        await alone.Executor.ExecuteAsync(alone.Context);
        alone.Sent.Should().ContainSingle();

        SubjectsTestHarness pair = SubjectsTestHarness.Build(Cfg([machine.Id, anna.Id]), [anna, machine], options: objectsOnly);
        StepExecutionResult refused = await pair.Executor.ExecuteAsync(pair.Context);
        Failed(refused).Should().Be(AvatarFailureCodes.GenerationDisabled);
        pair.Subjects.Resolved.Should().BeEmpty("only the rows' kinds were read");
        NothingReserved(pair);

        // People on, objects off: the object stops the pair.
        var peopleOnly = new AvatarGenerationOptions { Enabled = true, Generation = AvatarGenerationMode.On, ObjectsEnabled = false };
        SubjectsTestHarness other = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id]), [anna, machine], options: peopleOnly);
        Failed(await other.Executor.ExecuteAsync(other.Context)).Should().Be(AvatarFailureCodes.GenerationDisabled);
    }

    [Fact]
    public async Task A_second_person_is_refused_by_name_of_the_rule()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar ben = SubjectsTestHarness.Person(2, name: "Ben");
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, ben.Id]), [anna, ben]);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be("CONFIG_INVALID");
        Error(result).Should().Contain("at most one person");
        h.Subjects.Resolved.Should().BeEmpty("the rule is judged from the rows' kinds, before any avatar is resolved");
        NothingReserved(h);
    }

    [Fact]
    public async Task The_permission_to_send_media_is_checked_once_for_every_subject()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id], egress: false), [anna, machine]);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be("EGRESS_REFUSED");
        h.Subjects.Resolved.Should().BeEmpty();
        NothingReserved(h);
    }

    [Fact]
    public async Task One_unusable_subject_fails_the_step_with_its_own_code()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([machine.Id, anna.Id]), [anna, machine]);
        h.Subjects.Revoke(anna.Id);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.ConsentRevoked);
        NothingReserved(h);
        h.Sent.Should().BeEmpty("a step never shows the usable subjects without the one that is not");
    }

    // ---------------------------------------------------------------- the per-take re-check

    [Fact]
    public async Task Every_subject_is_re_resolved_before_each_take_and_a_change_to_any_of_them_stops_the_next_take()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        // After the first take was submitted, the OBJECT's pictures change (a new digest = a new fingerprint).
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id], takes: 2), [anna, machine],
            afterSubmit: (subjects, submitted) =>
            {
                if (submitted == 1)
                {
                    ResolvedAvatar now = subjects.Avatars[machine.Id];
                    subjects.Avatars[machine.Id] = now with
                    {
                        References = [.. now.References.Select(r => r with { ContentSha256 = new string('0', 64) })],
                    };
                }
            });

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle("the second take was not submitted");
        result.Status.Should().Be(StepStatus.Failed);
        Failed(result).Should().Be(AvatarFailureCodes.ConsentMissing);
        // Both subjects were asked again before each of the two takes (after the two resolutions of the preparation).
        h.Subjects.Resolved.Count(id => id == anna.Id).Should().Be(3);
        h.Subjects.Resolved.Count(id => id == machine.Id).Should().Be(3);
        await using var db = h.Factory.CreateDbContext();
        (await db.Set<ExternalGenerationJob>().CountAsync(j => j.Status == ExternalGenerationJobStatus.Cancelled)).Should().Be(1);
    }

    [Fact]
    public async Task A_revoked_person_stops_the_take_of_a_clip_that_also_shows_objects()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([machine.Id, anna.Id], takes: 2), [anna, machine],
            afterSubmit: (subjects, submitted) => { if (submitted == 1) subjects.Revoke(anna.Id); });

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle();
        Failed(result).Should().Be(AvatarFailureCodes.ConsentRevoked);
    }

    // ---------------------------------------------------------------- FirstFrame: one composed still

    [Fact]
    public async Task A_model_with_an_image_input_and_no_reference_mode_is_shown_its_subjects_through_one_composed_first_frame()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1, pictures: 4, speech: true, motion: true);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2, pictures: 2, description: "matte black, chrome lever");
        var stills = new SubjectsTestHarness.FakeSceneStills();
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id]), [anna, machine],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi, stills: stills);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        // ONE still is made, from at most three pictures of each subject, at the step's ratio, with the step's prompt.
        SubjectSceneStillRequest made = stills.Requests.Should().ContainSingle().Subject;
        made.Prompt.Should().Be("Anna pours a coffee at the machine");
        made.Ratio.Should().Be("16:9");
        made.Subjects.Select(s => (s.Kind, s.Pictures.Count, s.Description)).Should().Equal(
            (AvatarSubjectKind.Person, 3, (string?)null), (AvatarSubjectKind.Object, 2, "matte black, chrome lever"));
        made.OwnerAvatarId.Should().Be(anna.Id, "a still with a person in it lives under that person's stills folder");

        // The video model receives the still as its first frame and NO reference item; a person's still is not kept.
        VideoGenerationRequest sent = h.Sent.Should().ContainSingle().Subject;
        sent.References.Should().BeNull();
        sent.Images!.Should().ContainSingle().Which.Bytes.Should().Equal(SubjectsTestHarness.FakeSceneStills.StillBytes);
        sent.NoVendorRetention.Should().BeTrue();
        sent.PromptText.Should().Be("Anna pours a coffee at the machine");

        JsonObject meta = JsonNode.Parse(result.Output)!["meta"]!.AsObject();
        meta[SubjectOutputFacts.PathKey]!.GetValue<string>().Should().Be("firstFrame");
        JsonArray subjects = meta[SubjectOutputFacts.MetaKey]!.AsArray();
        subjects[0]!["referencesSent"]!.ToJsonString().Should().Be("""{"images":3,"videos":0,"audio":0}""");
        subjects[0]!["referencesSkipped"]!.AsArray().Select(s => s!["reason"]!.GetValue<string>())
            .Should().BeEquivalentTo(["not_a_picture", "not_a_picture", VideoReferenceInput.SkipKindLimit]);
        meta["subjectFacts"]!["still"]!["made"]!.GetValue<bool>().Should().BeTrue();
        meta["subjectFacts"]!["inputMaterialCostPerTake"]!.GetValue<decimal>().Should().Be(0m);

        // The take is priced as a plain clip: the video model bills no input material on this path.
        h.Reservations.Single().SubjectIds.Should().Equal(anna.Id, machine.Id);
    }

    [Fact]
    public async Task Objects_only_get_the_neutral_marking_and_their_still_lives_under_the_first_objects_folder()
    {
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        ResolvedAvatar cup = SubjectsTestHarness.Thing(3, name: "Cup");
        var stills = new SubjectsTestHarness.FakeSceneStills();
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([machine.Id, cup.Id]), [machine, cup],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi, stills: stills);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        stills.Requests.Single().OwnerAvatarId.Should().Be(machine.Id);
        h.Sent.Single().NoVendorRetention.Should().BeFalse("no person's media is in the request");
        JsonObject facts = JsonNode.Parse(result.Output)!["meta"]!["subjectFacts"]!.AsObject();
        facts["showsPerson"]!.GetValue<bool>().Should().BeFalse();
        facts["marking"]!.GetValue<string>().Should().Be("metadata");
        facts["disclosure"]!.GetValue<string>().Should().Be(nameof(AvatarDisclosure.MetadataOnly));
        h.Reservations.Single().AvatarId.Should().Be(machine.Id, "avatar_id holds the first object when no person is shown");
    }

    [Fact]
    public async Task SubjectMode_FirstFrame_uses_one_composed_still_even_on_a_model_with_a_reference_mode()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        var stills = new SubjectsTestHarness.FakeSceneStills();
        SubjectsTestHarness h = SubjectsTestHarness.Build(
            Cfg([anna.Id], extra: ",\"subjectMode\":\"FirstFrame\""), [anna], stills: stills);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        stills.Requests.Should().ContainSingle();
        h.Sent.Single().References.Should().BeNull();
        JsonNode.Parse(result.Output)!["meta"]![SubjectOutputFacts.PathKey]!.GetValue<string>().Should().Be("firstFrame");
    }

    [Fact]
    public async Task SubjectMode_References_is_refused_on_a_model_without_a_reference_mode()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id], extra: ",\"subjectMode\":\"References\""), [anna],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, stills: new SubjectsTestHarness.FakeSceneStills());

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(VideoReferenceInput.NotSupportedCode);
        Error(result).Should().Contain("has no reference mode");
        NothingReserved(h);
    }

    [Fact]
    public async Task A_subject_that_changes_while_the_first_frame_is_made_removes_the_still_and_fails_the_step()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        var stills = new SubjectsTestHarness.FakeSceneStills();
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([anna.Id, machine.Id]), [anna, machine],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi, stills: stills);
        stills.AfterMake = () => h.Subjects.Revoke(anna.Id);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.ConsentRevoked);
        h.Workspace.Verify(w => w.DeleteArtifactAsync(
            SubjectsTestHarness.ProjectId,
            It.Is<string>(k => k.StartsWith(AvatarStillStorage.ObjectPrefix(SubjectsTestHarness.ProjectId, anna.Id), StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        NothingReserved(h);
        h.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reused_still_is_not_removed_when_a_subject_changed()
    {
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        var stills = new SubjectsTestHarness.FakeSceneStills { Reused = true };
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([machine.Id]), [machine],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi, stills: stills);
        stills.AfterMake = () => h.Subjects.Avatars.Remove(machine.Id);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.NotFound);
        h.Workspace.Verify(w => w.DeleteArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_still_that_cannot_be_made_refuses_the_step_with_the_makers_code_and_buys_no_video()
    {
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        var stills = new SubjectsTestHarness.FakeSceneStills
        {
            Answer = AvatarStillOutcome.Refused(AvatarStillCodes.StillProviderMissing, "no picture-edit model"),
        };
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([machine.Id]), [machine],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi, stills: stills);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarStillCodes.StillProviderMissing);
        NothingReserved(h);
    }

    [Fact]
    public async Task An_engine_without_a_still_maker_refuses_the_first_frame_path_instead_of_showing_nothing()
    {
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Cfg([machine.Id]), [machine],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(AvatarFailureCodes.GenerationDisabled);
        NothingReserved(h);
    }

    // ---------------------------------------------------------------- a text-only route

    [Theory]
    [InlineData(InferenceProviderKind.FalAi, "alibaba/wan-3.0/text-to-video", "alibaba/wan-3.0/image-to-video")]
    [InlineData(InferenceProviderKind.Higgsfield, "kling-video/v3.0/std/text-to-video", "kling-video/v3.0/std/image-to-video")]
    [InlineData(InferenceProviderKind.ComfyUI, "wan2.2-ti2v-5b-t2v", "wan2.2-ti2v-5b-i2v")]
    public async Task A_text_only_model_is_refused_before_anything_is_read_with_the_sibling_named(
        InferenceProviderKind kind, string model, string sibling)
    {
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        string config = Cfg([machine.Id], extra: ",\"model\":\"" + model + "\",\"maxGpuMinutes\":10");
        SubjectsTestHarness h = SubjectsTestHarness.Build(config, [machine],
            references: NoReferences, images: VideoImageInputSupport.None, kind: kind, stills: new SubjectsTestHarness.FakeSceneStills());

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        Failed(result).Should().Be(VideoReferenceInput.NotSupportedCode);
        Error(result).Should().Be($"'{model}' takes no pictures. Use '{sibling}' to show an avatar or an object. Nothing was submitted.");
        h.Subjects.Resolved.Should().BeEmpty("the model is judged before any avatar is resolved");
        NothingReserved(h);
    }

    // ---------------------------------------------------------------- the legacy single avatar on the new paths

    [Fact]
    public async Task A_legacy_object_avatar_on_a_first_frame_model_keeps_meta_avatar_with_its_kind_and_the_path_word()
    {
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        var stills = new SubjectsTestHarness.FakeSceneStills();
        string config = "{\"maxSpendCredits\":1000.0,\"takes\":1,\"maxClips\":1,\"inlinePrompt\":\"The machine steams\",\"timeoutSeconds\":60,\"pollIntervalSeconds\":1,\"avatarId\":\""
            + machine.Id.ToString("D") + "\",\"allowSourceMediaEgress\":true}";
        SubjectsTestHarness h = SubjectsTestHarness.Build(config, [machine],
            references: NoReferences, images: VideoImageInputSupport.FirstFrame, kind: InferenceProviderKind.FalAi, stills: stills);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        JsonObject meta = JsonNode.Parse(result.Output)!["meta"]!.AsObject();
        meta.ContainsKey(SubjectOutputFacts.MetaKey).Should().BeFalse("the legacy avatarId keeps the legacy record");
        meta.ContainsKey(SubjectOutputFacts.PathKey).Should().BeFalse();
        JsonObject avatar = meta[AvatarOutputFacts.MetaKey]!.AsObject();
        avatar["kind"]!.GetValue<string>().Should().Be("object");
        avatar["path"]!.GetValue<string>().Should().Be("firstFrame");
        avatar["fingerprint"]!.GetValue<string>().Should().Be(AvatarFingerprint.Compute(machine));
        avatar["still"]!["sha256"]!.GetValue<string>().Should().Be(SubjectsTestHarness.Sha(SubjectsTestHarness.FakeSceneStills.StillBytes));
        h.Reservations.Single().SubjectIds.Should().BeNull("the legacy ledger row is what it was");
        JsonNode.Parse(result.Output)!["clips"]![0]![AvatarOutputFacts.ClipFingerprintKey]!.GetValue<string>()
            .Should().Be(AvatarFingerprint.Compute(machine));
    }
}
