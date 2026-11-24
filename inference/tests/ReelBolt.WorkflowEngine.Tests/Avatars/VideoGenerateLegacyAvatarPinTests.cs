using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Avatars;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The legacy step: ONE person named by <c>avatarId</c> on a reference-capable model. Everything it produces was
/// RECORDED ON THE CODE AS IT WAS before several subjects per clip existed (docs/design/avatar-objects.md "Subjects in
/// a video generation step": "a single-person legacy AvatarId step keeps its exact hashes and meta.avatar"), with fixed
/// ids, and is pinned here: the request the vendor receives, the ledger row (request hash, avatar id, estimate), and
/// <c>meta.avatar</c> byte for byte. A change to any of them replays or re-buys clips of real people, so a failure here
/// is never fixed by editing the pinned value.
/// </summary>
public class VideoGenerateLegacyAvatarPinTests
{
    private const string Config =
        """{"maxSpendCredits":1000.0,"takes":1,"maxClips":1,"inlinePrompt":"Anna waves at the camera","timeoutSeconds":60,"pollIntervalSeconds":1,"avatarId":"aaaaaaaa-0001-4000-8000-000000000001","allowSourceMediaEgress":true}""";

    private const string PinnedFingerprint = "f6d62ef67387e11b4e51487833d3d4e11642700d7b3edf29de4b12535fd2c08c";
    private const string PinnedRequestHash = "5C0F5084346BF98FA8CD51FE5ACC3BF03817C4B6CEE008070045F956C3221021";
    private const string PinnedMetaAvatar =
        """{"avatarId":"aaaaaaaa-0001-4000-8000-000000000001","name":"Anna presenter","fingerprint":"f6d62ef67387e11b4e51487833d3d4e11642700d7b3edf29de4b12535fd2c08c","aiGenerated":true,"disclosure":"Visible","marking":"metadata\u002BvisibleLabel","c2pa":"not_available","referencesSent":{"images":2,"videos":1,"audio":1},"referencesSkipped":[],"inputVideoSeconds":5,"inputMaterialCostPerTake":66.666666666666666666666666667}""";
    private const decimal PinnedCostEstimate = 146.66666666666666666666666667m;

    [Fact]
    public async Task One_person_named_by_avatar_id_produces_exactly_what_it_always_did()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1, pictures: 2, speech: true, motion: true);
        SubjectsTestHarness h = SubjectsTestHarness.Build(Config, [anna]);

        StepExecutionResult result = await h.Executor.ExecuteAsync(h.Context);

        string fingerprint = AvatarFingerprint.Compute(anna);
        VideoGenerationRequest sent = h.Sent.Should().ContainSingle().Subject;
        ExternalGenerationJob row;
        await using (var db = h.Factory.CreateDbContext())
        {
            row = await db.Set<ExternalGenerationJob>().SingleAsync();
        }

        string metaAvatar = JsonNode.Parse(result.Output)!["meta"]![AvatarOutputFacts.MetaKey]!.ToJsonString();

        fingerprint.Should().Be(PinnedFingerprint);

        // The request: the prompt and the step's own figures, no frame, and the references in the avatar's order within
        // each kind (pictures, then the speech sample, then the motion video), as the vendor always received them.
        sent.PromptText.Should().Be("Anna waves at the camera");
        (sent.Model, sent.DurationSeconds, sent.Resolution, sent.Ratio).Should().Be(("MiniMax-H3", 6, "768P", "16:9"));
        sent.Images.Should().BeNull();
        sent.NoVendorRetention.Should().BeFalse();
        sent.References!.Select(r => (r.Kind, r.Role, r.ContentType, r.DurationMs)).Should().Equal(
            (AvatarReferenceKind.Image, AvatarReferenceRole.Face, "image/png", (int?)null),
            (AvatarReferenceKind.Image, AvatarReferenceRole.HalfBody, "image/png", (int?)null),
            (AvatarReferenceKind.Audio, AvatarReferenceRole.Speech, "audio/wav", (int?)4_000),
            (AvatarReferenceKind.Video, AvatarReferenceRole.Motion, "video/mp4", (int?)5_000));

        // The ledger row.
        row.RequestHash.Should().Be(PinnedRequestHash);
        row.AvatarId.Should().Be(anna.Id);
        row.CostEstimate.Should().Be(PinnedCostEstimate);
        row.SubjectIdsJson.Should().BeNull("only a step that names Subjects writes subject_ids_json");
        h.Reservations.Single().SubjectIds.Should().BeNull();

        // meta.avatar, byte for byte, and no trace of the newer shape.
        metaAvatar.Should().Be(PinnedMetaAvatar);
        JsonObject meta = JsonNode.Parse(result.Output)!["meta"]!.AsObject();
        meta.ContainsKey("subjects").Should().BeFalse();
        meta.ContainsKey("subjectPath").Should().BeFalse();
        meta.ContainsKey("subjectFacts").Should().BeFalse();

        // The clip keeps its own key, with the avatar's own fingerprint.
        JsonNode clip = JsonNode.Parse(result.Output)!["clips"]![0]!;
        clip[AvatarOutputFacts.ClipFingerprintKey]!.GetValue<string>().Should().Be(PinnedFingerprint);
        clip.AsObject().ContainsKey(SubjectOutputFacts.ClipFingerprintKey).Should().BeFalse();
    }

    [Fact]
    public async Task The_step_cache_component_of_one_avatar_is_still_that_avatars_fingerprint_and_the_reuse_guard_still_reads_meta_avatar()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1, pictures: 2, speech: true, motion: true);
        var subjects = new SubjectsTestHarness.FakeSubjects();
        subjects.Avatars[anna.Id] = anna;
        VideoGenerateStepConfig config = System.Text.Json.JsonSerializer.Deserialize<VideoGenerateStepConfig>(Config, VideoGenerateStepConfig.JsonOptions)!;

        (await SubjectsCacheComponent.ComputeAsync(subjects, SubjectsTestHarness.ProjectId, config, default)).Should().Be(PinnedFingerprint);

        string output = "{\"meta\":{\"avatar\":" + PinnedMetaAvatar + "}}";
        var guard = new AvatarReuseGuard(subjects);
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, Config, output, default)).Should().BeTrue();

        subjects.Revoke(anna.Id);
        (await SubjectsCacheComponent.ComputeAsync(subjects, SubjectsTestHarness.ProjectId, config, default))
            .Should().Be("unusable:" + ProjectAvatar.ReasonConsentRevoked);
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, Config, output, default)).Should().BeFalse();
    }
}
