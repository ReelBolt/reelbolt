using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Avatars;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// What subjects add to a <c>VideoGenerate</c> step's hold (docs/design/avatar-objects.md "Subjects in a video
/// generation step", Money), read from the model's own declarations exactly as the engine chooses the path: the input
/// material of every subject on a reference model, ONE still on a first-frame model, nothing on a text-only route, and
/// the same per planned shot while planned subjects can apply. Plus the identity of a subject set for the cache key and
/// the reuse guard. The real clients' declarations and price tables are used; nothing is called.
/// </summary>
public class VideoGenerateSubjectsPricingTests
{
    private const long MillisPerVideoSecond = 1_000;
    private const long StillMillis = 630;

    private static readonly MiniMaxVideoGenerationClient MiniMax = new(new HttpClient(), "key");
    private static readonly FalAiVideoGenerationClient Fal = new(new HttpClient { BaseAddress = new Uri("https://queue.fal.run") }, "key");

    /// <summary>Prices a video second at a round figure, a quoted USD amount at 10,000 millis per dollar, and remembers what it was asked.</summary>
    private sealed class Prices : IModelPriceService
    {
        public List<decimal> QuotedUsd { get; } = [];

        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new PriceQuote((long)(request.Quantity * MillisPerVideoSecond), 0m, Guid.NewGuid()));

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode,
            DateTime? at = null, CancellationToken ct = default)
        {
            QuotedUsd.Add(costUsd);
            long millis = unit == UsageKind.ImageEditImages ? StillMillis : (long)(costUsd * 10_000m);
            return ValueTask.FromResult(new PriceQuote(millis, costUsd, Guid.NewGuid()));
        }

        public void Invalidate()
        {
        }
    }

    private static async Task<(long? Hold, Prices Prices)> Hold(string config, InferenceProviderKind kind)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedVideoGenerationProvider(Guid.NewGuid(), "p", kind, "https://x", "m", "key", 60));
        var clients = new Mock<IVideoGenerationClientFactory>();
        clients.Setup(f => f.Get(It.IsAny<ResolvedVideoGenerationProvider>()))
            .Returns(kind == InferenceProviderKind.FalAi ? Fal : MiniMax);
        var prices = new Prices();
        return (await VideoGenerateCreditPricing.TryHoldMillisAsync(config, resolver.Object, clients.Object, prices), prices);
    }

    private static string Ids(int count) =>
        string.Join(",", Enumerable.Range(1, count).Select(n => "{\"avatarId\":\"" + SubjectsTestHarness.Id(n, 1).ToString("D") + "\"}"));

    private static string MiniMaxInline(string subjects, int takes = 1, string extra = "") =>
        "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"model\":\"MiniMax-H3\",\"resolution\":\"768P\",\"durationSeconds\":6,\"ratio\":\"16:9\",\"takes\":"
        + takes + ",\"maxClips\":" + takes + ",\"maxSpendCredits\":900,\"allowSourceMediaEgress\":true" + subjects + extra + "}";

    private static string FalInline(string model, string subjects, int takes = 1, string resolution = "720p", string ratio = "16:9", int seconds = 5) =>
        "{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"model\":\"" + model + "\",\"resolution\":\"" + resolution + "\",\"durationSeconds\":" + seconds
        + ",\"ratio\":\"" + ratio + "\",\"takes\":" + takes + ",\"maxClips\":" + takes + ",\"maxSpendCredits\":900,\"allowSourceMediaEgress\":true" + subjects + "}";

    // ---------------------------------------------------------------- References

    [Fact]
    public async Task One_subject_on_a_reference_model_holds_what_a_single_avatar_always_held()
    {
        string legacy = MiniMaxInline(",\"avatarId\":\"" + SubjectsTestHarness.Id(1, 1).ToString("D") + "\"", takes: 2);
        string named = MiniMaxInline(",\"subjects\":[" + Ids(1) + "]", takes: 2);

        (long? legacyHold, Prices legacyPrices) = await Hold(legacy, InferenceProviderKind.MiniMax);
        (long? namedHold, _) = await Hold(named, InferenceProviderKind.MiniMax);

        // Eight pictures (three beyond the five free ones at $0.04) and the whole 15 s of input video at $0.08, per take.
        decimal perTake = (3 * 0.04m) + (15 * 0.08m);
        legacyPrices.QuotedUsd.Should().Equal(perTake * 2);
        legacyHold.Should().Be((12 * MillisPerVideoSecond) + (long)(perTake * 2 * 10_000m));
        namedHold.Should().Be(legacyHold);
        VideoGenerateCreditPricing.MaxInputMaterialUsd(MiniMax, "MiniMax-H3", "768P").Should().Be(perTake, "the default is one subject");
    }

    [Fact]
    public async Task Several_subjects_fill_the_models_own_picture_count_and_the_hold_only_grows()
    {
        (long? one, _) = await Hold(MiniMaxInline(",\"subjects\":[" + Ids(1) + "]"), InferenceProviderKind.MiniMax);
        (long? three, Prices prices) = await Hold(MiniMaxInline(",\"subjects\":[" + Ids(3) + "]"), InferenceProviderKind.MiniMax);

        // Three subjects could bring 24 pictures; the model takes nine (four beyond the free five).
        prices.QuotedUsd.Should().Equal((4 * 0.04m) + (15 * 0.08m));
        three.Should().BeGreaterThan(one!.Value);
        VideoGenerateCreditPricing.MaxInputMaterialUsd(MiniMax, "MiniMax-H3", "768P", subjects: 3).Should().Be((4 * 0.04m) + (15 * 0.08m));
        VideoGenerateCreditPricing.MaxInputMaterialUsd(MiniMax, "MiniMax-H3", "768P", subjects: 99)
            .Should().Be(VideoGenerateCreditPricing.MaxInputMaterialUsd(MiniMax, "MiniMax-H3", "768P", subjects: 3), "at most three subjects are ever shown");
    }

    [Fact]
    public async Task A_fal_reference_route_bills_no_input_material_so_subjects_add_nothing_to_its_hold()
    {
        const string wan = "alibaba/wan-3.0/reference-to-video";
        (long? plain, _) = await Hold(FalInline(wan, ""), InferenceProviderKind.FalAi);
        (long? shown, Prices prices) = await Hold(FalInline(wan, ",\"subjects\":[" + Ids(3) + "]"), InferenceProviderKind.FalAi);

        shown.Should().Be(plain);
        prices.QuotedUsd.Should().BeEmpty("pictures only are sent, and the page prices only the seconds of video");
    }

    // ---------------------------------------------------------------- FirstFrame

    [Fact]
    public async Task A_first_frame_model_holds_one_still_for_the_inline_step_whatever_its_takes()
    {
        const string vidu = "fal-ai/vidu/q4/image-to-video";
        (long? plain, _) = await Hold(FalInline(vidu, "", takes: 3, ratio: "native"), InferenceProviderKind.FalAi);
        (long? shown, Prices prices) = await Hold(FalInline(vidu, ",\"subjects\":[" + Ids(2) + "]", takes: 3, ratio: "native"), InferenceProviderKind.FalAi);

        shown.Should().Be(plain + StillMillis, "the still is made once and every take starts from it");
        prices.QuotedUsd.Should().Equal(VideoGenerateCreditPricing.AvatarStillHoldUsd);
    }

    [Fact]
    public async Task SubjectMode_FirstFrame_swaps_the_reference_material_for_one_still_on_a_reference_model()
    {
        (long? shown, Prices prices) = await Hold(
            MiniMaxInline(",\"subjects\":[" + Ids(2) + "]", extra: ",\"subjectMode\":\"FirstFrame\""), InferenceProviderKind.MiniMax);

        shown.Should().Be((6 * MillisPerVideoSecond) + StillMillis);
        prices.QuotedUsd.Should().Equal(VideoGenerateCreditPricing.AvatarStillHoldUsd);
    }

    [Fact]
    public async Task A_text_only_model_adds_nothing_the_step_is_refused_before_anything_is_reserved()
    {
        const string textOnly = "alibaba/wan-3.0/text-to-video";
        (long? plain, _) = await Hold(FalInline(textOnly, ""), InferenceProviderKind.FalAi);
        (long? shown, Prices prices) = await Hold(FalInline(textOnly, ",\"subjects\":[" + Ids(1) + "]"), InferenceProviderKind.FalAi);

        shown.Should().Be(plain);
        prices.QuotedUsd.Should().BeEmpty();
    }

    [Fact]
    public async Task The_run_briefs_avatar_fills_only_a_step_that_names_no_subject()
    {
        using IDisposable brief = BriefAvatarPricing.Enter(Guid.NewGuid());
        const string vidu = "fal-ai/vidu/q4/image-to-video";

        (long? bare, _) = await Hold(FalInline(vidu, "", ratio: "native"), InferenceProviderKind.FalAi);
        (long? named, Prices prices) = await Hold(FalInline(vidu, ",\"subjects\":[" + Ids(2) + "]", ratio: "native"), InferenceProviderKind.FalAi);

        bare.Should().Be((5 * MillisPerVideoSecond) + StillMillis, "the brief's avatar is shown through one composed first frame");
        named.Should().Be(bare, "one still either way: the brief adds no subject to a step that names its own");
        prices.QuotedUsd.Should().HaveCount(1);
    }

    // ---------------------------------------------------------------- planned shots

    private static string Planned(string model, bool egress, string extra = "", int maxClips = 3) =>
        "{\"planSource\":\"StepRef\",\"plan\":{\"from\":\"Previous\"},\"model\":\"" + model + "\",\"resolution\":\""
        + (model.StartsWith("MiniMax", StringComparison.Ordinal) ? "768P" : "720p") + "\",\"ratio\":\""
        + (model.Contains("vidu", StringComparison.Ordinal) ? "native" : "16:9") + "\",\"takes\":1,\"maxClips\":" + maxClips
        + ",\"maxSpendCredits\":900,\"allowSourceMediaEgress\":" + (egress ? "true" : "false") + extra + "}";

    [Fact]
    public async Task A_planned_step_holds_one_still_per_planned_shot_only_while_planned_subjects_can_apply()
    {
        const string vidu = "fal-ai/vidu/q4/image-to-video";
        (long? egressOff, _) = await Hold(Planned(vidu, egress: false), InferenceProviderKind.FalAi);
        long plain = egressOff!.Value;

        // Nobody read the project (unknown): held as if it may hold an avatar.
        (await Hold(Planned(vidu, egress: true), InferenceProviderKind.FalAi)).Hold.Should().Be(plain + (3 * StillMillis));

        using (ProjectSubjectsPricing.Enter(true))
        {
            (await Hold(Planned(vidu, egress: true), InferenceProviderKind.FalAi)).Hold.Should().Be(plain + (3 * StillMillis));
            (await Hold(Planned(vidu, egress: true, ",\"plannedSubjects\":\"Off\""), InferenceProviderKind.FalAi)).Hold
                .Should().Be(plain, "the planner is offered no subject");
            (await Hold(Planned(vidu, egress: false), InferenceProviderKind.FalAi)).Hold.Should().Be(plain, "nothing may leave");
        }

        using (ProjectSubjectsPricing.Enter(false))
        {
            (await Hold(Planned(vidu, egress: true), InferenceProviderKind.FalAi)).Hold
                .Should().Be(plain, "a project with no avatar offers none, so its runs are held exactly as before");
        }

        ProjectSubjectsPricing.Current.Should().BeNull("the fact is scoped to its using block");
    }

    [Fact]
    public async Task A_planned_step_on_a_reference_model_holds_the_input_material_of_up_to_three_subjects_per_shot()
    {
        using IDisposable project = ProjectSubjectsPricing.Enter(true);
        (long? off, _) = await Hold(Planned("MiniMax-H3", egress: true, ",\"plannedSubjects\":\"Off\"", maxClips: 2), InferenceProviderKind.MiniMax);
        (long? auto, Prices prices) = await Hold(Planned("MiniMax-H3", egress: true, maxClips: 2), InferenceProviderKind.MiniMax);

        decimal perShot = (4 * 0.04m) + (15 * 0.08m);
        prices.QuotedUsd.Should().Equal(perShot * 2);
        auto.Should().Be(off + (long)(perShot * 2 * 10_000m));
    }

    [Theory]
    [InlineData("{\"planSource\":\"StepRef\",\"plan\":{\"from\":\"Previous\"},\"allowSourceMediaEgress\":true}", true)]
    [InlineData("{\"planSource\":\"ProjectFile\",\"planProjectFileId\":\"aaaaaaaa-0001-4000-8000-000000000001\",\"allowSourceMediaEgress\":true}", true)]
    [InlineData("{\"planSource\":\"StepRef\",\"plan\":{\"from\":\"Previous\"},\"allowSourceMediaEgress\":false}", false)]
    [InlineData("{\"planSource\":\"StepRef\",\"plan\":{\"from\":\"Previous\"},\"allowSourceMediaEgress\":true,\"plannedSubjects\":\"Off\"}", false)]
    [InlineData("{\"planSource\":\"Inline\",\"inlinePrompt\":\"x\",\"allowSourceMediaEgress\":true}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void The_project_is_read_only_for_a_step_whose_hold_depends_on_it(string? config, bool matters)
    {
        ProjectSubjectsPricing.Matters(config).Should().Be(matters);
    }

    // ---------------------------------------------------------------- the identity of a set

    [Fact]
    public async Task The_cache_component_and_the_reuse_guard_follow_every_subject_in_order()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        var subjects = new SubjectsTestHarness.FakeSubjects();
        subjects.Avatars[anna.Id] = anna;
        subjects.Avatars[machine.Id] = machine;
        string json = "{\"maxSpendCredits\":5,\"inlinePrompt\":\"x\",\"subjects\":[{\"avatarId\":\"" + anna.Id.ToString("D") + "\"},{\"avatarId\":\""
            + machine.Id.ToString("D") + "\"}],\"allowSourceMediaEgress\":true}";
        VideoGenerateStepConfig config = System.Text.Json.JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, VideoGenerateStepConfig.JsonOptions)!;

        string component = (await SubjectsCacheComponent.ComputeAsync(subjects, SubjectsTestHarness.ProjectId, config, default))!;
        component.Should().Be("subjects:" + AvatarFingerprint.Compute(anna) + "," + AvatarFingerprint.Compute(machine));

        string Output(params (Guid Id, string Fingerprint)[] recorded) =>
            "{\"meta\":{\"subjects\":[" + string.Join(",", recorded.Select(r =>
                "{\"avatarId\":\"" + r.Id.ToString("D") + "\",\"fingerprint\":\"" + r.Fingerprint + "\"}")) + "]}}";
        var guard = new AvatarReuseGuard(subjects);
        string good = Output((anna.Id, AvatarFingerprint.Compute(anna)), (machine.Id, AvatarFingerprint.Compute(machine)));

        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json, good, default)).Should().BeTrue();
        // Another order, a subject less, a result made before Subjects, or a result with no record at all: a miss.
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json,
            Output((machine.Id, AvatarFingerprint.Compute(machine)), (anna.Id, AvatarFingerprint.Compute(anna))), default)).Should().BeFalse();
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json, Output((anna.Id, AvatarFingerprint.Compute(anna))), default)).Should().BeFalse();
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json,
            "{\"meta\":{\"avatar\":{\"avatarId\":\"" + anna.Id.ToString("D") + "\",\"fingerprint\":\"" + AvatarFingerprint.Compute(anna) + "\"}}}", default))
            .Should().BeFalse();
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json, "{\"meta\":{}}", default)).Should().BeFalse();
        // A step that names no subject never reuses a result that showed some.
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, "{\"maxSpendCredits\":5,\"inlinePrompt\":\"x\"}", good, default)).Should().BeFalse();
        // A malformed record is a miss, never a guess.
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json, "{\"meta\":{\"subjects\":[{\"avatarId\":\"x\"}]}}", default)).Should().BeFalse();

        // The OBJECT changes (a new picture): the component moves and the old result is no longer reused.
        subjects.Avatars[machine.Id] = machine with
        {
            References = [.. machine.References.Select(r => r with { ContentSha256 = new string('0', 64) })],
        };
        (await SubjectsCacheComponent.ComputeAsync(subjects, SubjectsTestHarness.ProjectId, config, default)).Should().NotBe(component);
        (await guard.IsReuseAllowedAsync(SubjectsTestHarness.ProjectId, json, good, default)).Should().BeFalse();

        // The person is revoked: the component says so and nothing is reused.
        subjects.Revoke(anna.Id);
        (await SubjectsCacheComponent.ComputeAsync(subjects, SubjectsTestHarness.ProjectId, config, default))
            .Should().StartWith("subjects:unusable:" + ProjectAvatar.ReasonConsentRevoked + ",");
    }

    [Fact]
    public void The_fingerprint_of_a_set_depends_on_its_members_and_their_order_and_is_never_a_members_own()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);

        string set = SubjectsFingerprint.Compute([anna, machine]);
        set.Should().MatchRegex("^[0-9a-f]{64}$");
        set.Should().Be(SubjectsFingerprint.Compute([AvatarFingerprint.Compute(anna), AvatarFingerprint.Compute(machine)]));
        set.Should().NotBe(SubjectsFingerprint.Compute([machine, anna]));
        SubjectsFingerprint.Compute([anna]).Should().NotBe(AvatarFingerprint.Compute(anna), "a set of one is still a set");
        // Pinned: this value is in ledger request hashes and on stored clips.
        SubjectsFingerprint.Compute(["a", "bb"]).Should().Be(SetOfAAndBb);
    }

    private const string SetOfAAndBb = "fbe0b051d6958472090e80ab7bda50c6e0721dfcea102f1f450f621d9e09d273";

    [Fact]
    public void An_offer_id_is_stable_and_names_one_subject_or_none()
    {
        ResolvedAvatar anna = SubjectsTestHarness.Person(1);
        ResolvedAvatar machine = SubjectsTestHarness.Thing(2);
        ResolvedAvatar twin = machine with { Id = Guid.Parse("aaaaaaaa-0002-4000-8000-0000000000ff") };

        SubjectOfferId.For(anna.Id).Should().Be("aaaaaaaaa");
        SubjectOfferId.For(Guid.Parse("12345678-9abc-4def-8000-000000000001")).Should().Be("a12345678");
        SubjectOfferId.Resolve("a12345678", [anna, machine]).Should().BeNull("unknown");
        SubjectOfferId.Resolve(SubjectOfferId.For(Guid.Parse("12345678-9abc-4def-8000-000000000001")),
            [anna with { Id = Guid.Parse("12345678-9abc-4def-8000-000000000001") }, machine])!.Name.Should().Be("Anna presenter");
        SubjectOfferId.Resolve(SubjectOfferId.For(machine.Id), [machine, twin]).Should().BeNull("two avatars share the id: never guessed");
        SubjectOfferId.Resolve("  ", [anna]).Should().BeNull();
        SubjectOfferId.Resolve(null, [anna]).Should().BeNull();
    }
}
