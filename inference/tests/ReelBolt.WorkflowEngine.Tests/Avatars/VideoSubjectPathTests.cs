using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The path table of docs/design/avatar-objects.md "Subjects in a video generation step", read from each client's own
/// declarations: which model family carries subjects how, the sibling every text-only route names, the fair sharing of
/// reference slots, and the exact body of a fal.ai reference-to-video request. No vendor is called.
/// </summary>
public class VideoSubjectPathTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    // ---------------------------------------------------------------- the table, per provider and model

    public static IEnumerable<object[]> Models() =>
    [
        // MiniMax direct: its own reference mode; one composed first frame when the step asks for it.
        [InferenceProviderKind.MiniMax, "MiniMax-H3", VideoSubjectMode.Auto, VideoSubjectPath.References],
        [InferenceProviderKind.MiniMax, "MiniMax-H3-Max", VideoSubjectMode.References, VideoSubjectPath.References],
        [InferenceProviderKind.MiniMax, "MiniMax-H3-Lite", VideoSubjectMode.FirstFrame, VideoSubjectPath.FirstFrame],
        // fal.ai reference-to-video routes: native reference pictures.
        [InferenceProviderKind.FalAi, "alibaba/wan-3.0/reference-to-video", VideoSubjectMode.Auto, VideoSubjectPath.References],
        [InferenceProviderKind.FalAi, "fal-ai/veo3.1/reference-to-video", VideoSubjectMode.Auto, VideoSubjectPath.References],
        [InferenceProviderKind.FalAi, "fal-ai/veo3.1/fast/reference-to-video", VideoSubjectMode.Auto, VideoSubjectPath.References],
        // fal.ai image-to-video routes: one composed first frame.
        [InferenceProviderKind.FalAi, "fal-ai/vidu/q4/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.FalAi, "minimax/h3-max-turbo/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.FalAi, "fal-ai/kling-video/v3/turbo/pro/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.FalAi, "alibaba/wan-3.0/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.FalAi, "fal-ai/veo3.1/fast/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.FalAi, "xai/grok-imagine-video/v1.5/lite/image-to-video", VideoSubjectMode.FirstFrame, VideoSubjectPath.FirstFrame],
        // Higgsfield Kling image-to-video and the ComfyUI image-to-video graph: one composed first frame.
        [InferenceProviderKind.Higgsfield, "kling-video/v3.0/std/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.Higgsfield, "kling-video/v3.0-turbo/image-to-video", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
        [InferenceProviderKind.ComfyUI, "wan2.2-ti2v-5b-i2v", VideoSubjectMode.Auto, VideoSubjectPath.FirstFrame],
    ];

    [Theory]
    [MemberData(nameof(Models))]
    public void Each_model_family_carries_subjects_the_way_its_own_declarations_say(
        InferenceProviderKind kind, string model, VideoSubjectMode mode, VideoSubjectPath expected)
    {
        VideoSubjectPathDecision? decision = VideoSubjectPaths.DecideOffline(kind, model, mode);

        decision.Should().NotBeNull();
        decision!.Value.Refusal.Should().BeNull();
        decision.Value.Path.Should().Be(expected);
    }

    [Fact]
    public void The_offline_table_and_the_clients_own_declarations_are_the_same_answer()
    {
        var minimax = new MiniMaxVideoGenerationClient(new HttpClient(), "key");
        var fal = new FalAiVideoGenerationClient(new HttpClient { BaseAddress = new Uri("https://queue.fal.run") }, "key");

        foreach (string model in MiniMaxVideoGenerationClient.ModelIds)
        {
            VideoSubjectPaths.Decide(minimax, InferenceProviderKind.MiniMax, model)
                .Should().Be(VideoSubjectPaths.DecideOffline(InferenceProviderKind.MiniMax, model)!.Value, model);
        }

        foreach (FalVideoEndpoint endpoint in FalVideoCatalog.Instance.All)
        {
            bool talkingHead = endpoint.Job == FalVideoJob.TalkingHead;
            VideoSubjectPaths.Decide(fal, InferenceProviderKind.FalAi, endpoint.Id, hasDrivingAudio: talkingHead)
                .Should().Be(VideoSubjectPaths.DecideOffline(InferenceProviderKind.FalAi, endpoint.Id, hasDrivingAudio: talkingHead)!.Value, endpoint.Id);
        }
    }

    [Fact]
    public void A_talking_head_model_is_the_talking_head_path_only_with_speech()
    {
        const string model = "minimax/h3-max/lip-sync/image-to-video";

        VideoSubjectPaths.DecideOffline(InferenceProviderKind.FalAi, model, hasDrivingAudio: true)!.Value.Path
            .Should().Be(VideoSubjectPath.TalkingHead);
        VideoSubjectRefusal noSpeech = VideoSubjectPaths.DecideOffline(InferenceProviderKind.FalAi, model)!.Value.Refusal!;
        noSpeech.Code.Should().Be("AVATAR_NOT_SUPPORTED_BY_MODEL");
        noSpeech.Message.Should().Contain("names none");
        // Driving speech on a model that only borrows a voice is refused, as it always was.
        VideoSubjectPaths.DecideOffline(InferenceProviderKind.MiniMax, "MiniMax-H3", hasDrivingAudio: true)!.Value.Refusal!.Message
            .Should().Contain("not known to make a person say the words");
    }

    [Fact]
    public void References_mode_is_refused_on_a_model_that_only_takes_a_first_frame()
    {
        VideoSubjectRefusal refusal = VideoSubjectPaths.DecideOffline(
            InferenceProviderKind.FalAi, "fal-ai/vidu/q4/image-to-video", VideoSubjectMode.References)!.Value.Refusal!;

        refusal.Code.Should().Be("AVATAR_NOT_SUPPORTED_BY_MODEL");
        refusal.Message.Should().Contain("has no reference mode");
        refusal.Sibling.Should().BeNull("the model takes pictures; only the mode is wrong");
    }

    [Fact]
    public void An_unknown_model_or_a_kind_without_video_is_unknown_and_never_a_refusal()
    {
        VideoSubjectPaths.DecideOffline(InferenceProviderKind.FalAi, "fal-ai/not-a-model").Should().BeNull();
        VideoSubjectPaths.DecideOffline(InferenceProviderKind.MiniMax, "MiniMax-H9").Should().BeNull();
        VideoSubjectPaths.DecideOffline(InferenceProviderKind.ComfyUI, "some-other-graph").Should().BeNull();
        VideoSubjectPaths.DecideOffline(InferenceProviderKind.Anthropic, "claude").Should().BeNull();
    }

    // ---------------------------------------------------------------- text-only routes and their siblings

    public static IEnumerable<object[]> TextOnly() =>
    [
        [InferenceProviderKind.FalAi, "fal-ai/kling-video/v3/turbo/pro/text-to-video", "fal-ai/kling-video/v3/turbo/pro/image-to-video"],
        [InferenceProviderKind.FalAi, "minimax/h3-max-turbo/text-to-video", "minimax/h3-max-turbo/image-to-video"],
        [InferenceProviderKind.FalAi, "alibaba/wan-3.0/text-to-video", "alibaba/wan-3.0/image-to-video"],
        [InferenceProviderKind.FalAi, "fal-ai/veo3.1", "fal-ai/veo3.1/reference-to-video"],
        [InferenceProviderKind.Higgsfield, "kling-video/v3.0/std/text-to-video", "kling-video/v3.0/std/image-to-video"],
        [InferenceProviderKind.Higgsfield, "kling-video/v3.0/pro/text-to-video", "kling-video/v3.0/pro/image-to-video"],
        [InferenceProviderKind.Higgsfield, "kling-video/v3.0/4k/text-to-video", "kling-video/v3.0/4k/image-to-video"],
        [InferenceProviderKind.Higgsfield, "kling-video/v3.0-turbo/text-to-video", "kling-video/v3.0-turbo/image-to-video"],
        [InferenceProviderKind.ComfyUI, "wan2.2-ti2v-5b-t2v", "wan2.2-ti2v-5b-i2v"],
    ];

    [Theory]
    [MemberData(nameof(TextOnly))]
    public void A_text_only_route_is_refused_with_the_image_capable_model_of_its_family(
        InferenceProviderKind kind, string model, string sibling)
    {
        VideoSubjectSiblings.For(kind, model).Should().Be(sibling);

        VideoSubjectRefusal refusal = VideoSubjectPaths.DecideOffline(kind, model)!.Value.Refusal!;
        refusal.Code.Should().Be("AVATAR_NOT_SUPPORTED_BY_MODEL");
        refusal.Sibling.Should().Be(sibling, "a caller can hand the sibling on as data, not only as words");
        refusal.Message.Should().Be($"'{model}' takes no pictures. Use '{sibling}' to show an avatar or an object.");

        // The sibling really takes pictures, on a path of its own.
        VideoSubjectPaths.DecideOffline(kind, sibling)!.Value.Path.Should().NotBeNull();
    }

    [Fact]
    public void Every_text_only_route_the_platform_offers_has_a_sibling()
    {
        foreach (FalVideoEndpoint endpoint in FalVideoCatalog.Instance.All)
        {
            VideoSubjectModels.TryDeclared(InferenceProviderKind.FalAi, endpoint.Id, out var references, out var images).Should().BeTrue();
            if (references.AcceptsAvatar || images != VideoImageInputSupport.None)
            {
                VideoSubjectSiblings.For(InferenceProviderKind.FalAi, endpoint.Id).Should().BeNull(endpoint.Id + " takes pictures itself");
                continue;
            }

            string? sibling = VideoSubjectSiblings.For(InferenceProviderKind.FalAi, endpoint.Id);
            sibling.Should().NotBeNull(endpoint.Id + " is a text-only route and must name the model to use instead");
            FalVideoCatalog.Instance.Find(sibling).Should().NotBeNull(sibling + " must be a catalogued endpoint");
        }

        foreach (string endpoint in HiggsfieldVideoGenerationClient.KnownEndpointIds.Where(id => id.EndsWith("/text-to-video", StringComparison.Ordinal)))
        {
            VideoSubjectSiblings.For(InferenceProviderKind.Higgsfield, endpoint).Should().NotBeNull(endpoint);
        }

        foreach (string template in ComfyUiWorkflowTemplates.Names)
        {
            ComfyUiWorkflowTemplates.TryParse(template, out ComfyUiWorkflowTemplate parsed).Should().BeTrue();
            if (ComfyUiWorkflowTemplates.ImageInputSupport(parsed) == VideoImageInputSupport.None)
            {
                VideoSubjectSiblings.For(InferenceProviderKind.ComfyUI, template).Should().NotBeNull(template);
            }
        }
    }

    [Fact]
    public void A_model_with_no_known_sibling_is_still_refused_in_kind_neutral_words()
    {
        VideoSubjectPaths.TakesNoPictures("some/text-model", null).Should().Be(
            "'some/text-model' takes no pictures, so it cannot show an avatar or an object. Choose a model that takes pictures.");
        VideoSubjectSiblings.For(InferenceProviderKind.MiniMax, "MiniMax-H3").Should().BeNull();
        VideoSubjectSiblings.For(InferenceProviderKind.Higgsfield, "kling-video/v9/text-to-video").Should().BeNull("no such image endpoint is mapped");
    }

    [Fact]
    public void The_refusals_of_a_model_without_reference_material_name_no_kind()
    {
        string refusal = VideoReferenceInput.Refusal(VideoReferenceInputSupport.None, "m", hasFrames: false, wantsDrivingAudio: false)!;
        refusal.Should().Contain("an avatar or an object").And.NotContain("person");

        Action carry = () => VideoReferenceInput.EnsureNoReferences(new VideoGenerationRequest("m", "p", 5, "720p", "16:9",
            References: [new VideoGenerationReference(AvatarReferenceKind.Image, AvatarReferenceRole.Front, "image/png", Png)]));
        carry.Should().Throw<NotSupportedException>().WithMessage("*an avatar or an object*");
    }

    // ---------------------------------------------------------------- sharing the reference slots

    private static VideoGenerationReference Ref(AvatarReferenceKind kind, int? ms = null, int bytes = 10) =>
        new(kind, kind == AvatarReferenceKind.Image ? AvatarReferenceRole.Face : kind == AvatarReferenceKind.Audio ? AvatarReferenceRole.Speech : AvatarReferenceRole.Motion,
            kind == AvatarReferenceKind.Image ? "image/png" : kind == AvatarReferenceKind.Audio ? "audio/wav" : "video/mp4", new byte[bytes], ms);

    [Fact]
    public void One_subject_is_selected_exactly_as_before()
    {
        VideoReferenceInputSupport support = MiniMaxVideoGenerationClient.ReferenceSupport;
        List<VideoGenerationReference> candidates =
        [
            .. Enumerable.Repeat(Ref(AvatarReferenceKind.Image), 11),
            Ref(AvatarReferenceKind.Audio, 4_000), Ref(AvatarReferenceKind.Audio, 9_000), Ref(AvatarReferenceKind.Audio, 9_000),
            Ref(AvatarReferenceKind.Video, 5_000), Ref(AvatarReferenceKind.Video), Ref(AvatarReferenceKind.Video, 14_000),
        ];

        VideoReferenceSelection legacy = VideoReferenceInput.Select(support, candidates, long.MaxValue);
        VideoReferenceSelection shared = VideoReferenceInput.SelectShared(support, [candidates], long.MaxValue).Single();

        shared.Chosen.Should().Equal(legacy.Chosen);
        shared.Skipped.Should().Equal(legacy.Skipped);
    }

    [Fact]
    public void Pictures_are_shared_in_turns_and_the_body_ceiling_is_one_for_all_subjects()
    {
        var support = new VideoReferenceInputSupport(5, 1, 1, 6, 15_000, 15_000, AudioDrivesSpeech: false, ExclusiveWithFrames: true);
        IReadOnlyList<VideoGenerationReference> person = [.. Enumerable.Repeat(Ref(AvatarReferenceKind.Image), 4), Ref(AvatarReferenceKind.Audio, 4_000)];
        IReadOnlyList<VideoGenerationReference> thing = [.. Enumerable.Repeat(Ref(AvatarReferenceKind.Image), 4), Ref(AvatarReferenceKind.Video, 5_000)];

        IReadOnlyList<VideoReferenceSelection> picked = VideoReferenceInput.SelectShared(support, [person, thing], long.MaxValue);

        // Five pictures in turns: person 1, thing 1, person 2, thing 2, person 3. Then the speech sample (the sixth
        // file); the object's video finds no file slot left.
        picked[0].Chosen.Should().Equal(0, 1, 2, 4);
        picked[1].Chosen.Should().Equal(0, 1);
        picked[0].Skipped.Should().Equal((3, VideoReferenceInput.SkipKindLimit));
        picked[1].Skipped.Should().Equal(
            (2, VideoReferenceInput.SkipKindLimit), (3, VideoReferenceInput.SkipKindLimit), (4, VideoReferenceInput.SkipFileLimit));

        // A body that holds two pictures: one each, never two of the first subject.
        long room = 64 * 1024 + 2 * (VideoReferenceInput.Base64Length(10) + 128);
        IReadOnlyList<VideoReferenceSelection> tight = VideoReferenceInput.SelectShared(support, [person, thing], room);
        tight[0].Chosen.Should().Equal(0);
        tight[1].Chosen.Should().Equal(0);
        tight[0].Skipped.Should().Contain((1, VideoReferenceInput.SkipBodySize));
    }

    // ---------------------------------------------------------------- fal.ai reference-to-video bodies

    private static VideoGenerationRequest ReferenceRequest(string model, int pictures, int duration, string resolution, string ratio) =>
        new(model, "The machine steams on a marble counter", duration, resolution, ratio,
            References: [.. Enumerable.Range(0, pictures).Select(_ =>
                new VideoGenerationReference(AvatarReferenceKind.Image, AvatarReferenceRole.Front, "image/png", Png))]);

    private static string Body(string model, VideoGenerationRequest request) =>
        JsonSerializer.Serialize(FalAiVideoGenerationClient.BuildBody(FalVideoCatalog.Instance.Find(model)!, request));

    private static readonly string Uri = "data:image/png;base64," + Convert.ToBase64String(Png);

    [Fact]
    public void Wan_reference_to_video_sends_the_pictures_in_reference_image_urls_and_nothing_it_does_not_declare()
    {
        const string model = "alibaba/wan-3.0/reference-to-video";

        Body(model, ReferenceRequest(model, 2, 5, "720p", "16:9")).Should().Be(
            "{\"reference_image_urls\":[\"" + Uri + "\",\"" + Uri + "\"],\"prompt\":\"The machine steams on a marble counter\"," +
            "\"duration\":5,\"resolution\":\"720p\",\"aspect_ratio\":\"16:9\"}");
    }

    [Theory]
    [InlineData("fal-ai/veo3.1/reference-to-video", "1080p")]
    [InlineData("fal-ai/veo3.1/fast/reference-to-video", "720p")]
    public void Veo_reference_to_video_sends_the_pictures_in_image_urls_and_its_one_duration_as_a_string(string model, string resolution)
    {
        Body(model, ReferenceRequest(model, 3, 8, resolution, "9:16")).Should().Be(
            "{\"image_urls\":[\"" + Uri + "\",\"" + Uri + "\",\"" + Uri + "\"],\"prompt\":\"The machine steams on a marble counter\"," +
            "\"duration\":\"8s\",\"resolution\":\"" + resolution + "\",\"aspect_ratio\":\"9:16\"}");
    }

    [Fact]
    public void A_reference_route_refuses_what_its_schema_and_the_platforms_limits_do_not_cover()
    {
        const string wan = "alibaba/wan-3.0/reference-to-video";
        const string veoFast = "fal-ai/veo3.1/fast/reference-to-video";
        FalVideoEndpoint Endpoint(string id) => FalVideoCatalog.Instance.Find(id)!;

        Action none = () => FalAiVideoGenerationClient.BuildBody(Endpoint(wan), ReferenceRequest(wan, 0, 5, "720p", "16:9"));
        none.Should().Throw<ArgumentException>().WithMessage("*none*was given*");

        Action tooMany = () => FalAiVideoGenerationClient.BuildBody(Endpoint(wan), ReferenceRequest(wan, 11, 5, "720p", "16:9"));
        tooMany.Should().Throw<ArgumentException>().WithMessage("*at most 10*");

        Action fourOnVeo = () => FalAiVideoGenerationClient.BuildBody(Endpoint(veoFast), ReferenceRequest(veoFast, 4, 8, "720p", "16:9"));
        fourOnVeo.Should().Throw<ArgumentException>().WithMessage("*at most 3*");

        Action video = () => FalAiVideoGenerationClient.BuildBody(Endpoint(wan), ReferenceRequest(wan, 1, 5, "720p", "16:9") with
        {
            References = [new VideoGenerationReference(AvatarReferenceKind.Video, AvatarReferenceRole.Motion, "video/mp4", Png, 5_000)],
        });
        video.Should().Throw<ArgumentException>().WithMessage("*PICTURES only*");

        Action frame = () => FalAiVideoGenerationClient.BuildBody(Endpoint(wan), ReferenceRequest(wan, 1, 5, "720p", "16:9") with
        {
            Images = [new VideoGenerationImageInput("image/png", Png)],
        });
        frame.Should().Throw<ArgumentException>("a reference route documents no frame");

        Action sixSeconds = () => FalAiVideoGenerationClient.BuildBody(Endpoint(veoFast), ReferenceRequest(veoFast, 1, 6, "720p", "16:9"));
        sixSeconds.Should().Throw<Exception>("only the schema's default duration, 8s, is offered");

        Action fourK = () => FalAiVideoGenerationClient.BuildBody(Endpoint(veoFast), ReferenceRequest(veoFast, 1, 8, "4k", "16:9"));
        fourK.Should().Throw<ArgumentException>("the page states no 4k price for the Fast tier, so it is not offered");

        Action oversized = () => FalAiVideoGenerationClient.BuildBody(Endpoint(wan), ReferenceRequest(wan, 1, 5, "720p", "16:9") with
        {
            References = [new VideoGenerationReference(AvatarReferenceKind.Image, AvatarReferenceRole.Front, "image/png",
                new byte[(int)(FalAiVideoGenerationClient.MaxReferenceRequestBytes * 3 / 4) + 1024])],
        });
        oversized.Should().Throw<ArgumentException>().WithMessage("*bound this platform keeps a request under*");
    }

    [Fact]
    public void A_reference_route_declares_pictures_only_no_frame_and_no_input_charge()
    {
        var fal = new FalAiVideoGenerationClient(new HttpClient { BaseAddress = new Uri("https://queue.fal.run") }, "key");

        foreach ((string model, int pictures) in new[]
                 {
                     ("alibaba/wan-3.0/reference-to-video", 10), ("fal-ai/veo3.1/reference-to-video", 3),
                     ("fal-ai/veo3.1/fast/reference-to-video", 3),
                 })
        {
            VideoReferenceInputSupport support = fal.ReferenceInputSupport(model);
            (support.MaxImages, support.MaxVideos, support.MaxAudio, support.MaxFiles).Should().Be((pictures, 0, 0, pictures), model);
            support.ExclusiveWithFrames.Should().BeTrue();
            support.AnimatesStill.Should().BeFalse();
            fal.ImageInputSupport(model).Should().Be(VideoImageInputSupport.None, model + " takes no frame");
            fal.InputMaterialCostUsd(model, "720p", [new VideoReferenceMaterial(AvatarReferenceKind.Image, null)]).Should().Be(0m);
        }

        FalVideoCatalog.Instance.DefaultFor(FalVideoJob.ReferenceToVideo).Id.Should().Be("alibaba/wan-3.0/reference-to-video");
        fal.Describe("fal-ai/veo3.1/fast/reference-to-video").PricePerSecondByResolution.Keys.Should().BeEquivalentTo(["720p", "1080p"]);
        fal.Describe("fal-ai/veo3.1/reference-to-video").PricePerSecondByResolution["4k"].Should().Be(0.60m);
        fal.Describe("alibaba/wan-3.0/reference-to-video").PricePerSecondByResolution["480p"].Should().Be(0.05m);
    }

    [Fact]
    public void A_request_that_carries_reference_pictures_is_sent_on_the_queue_that_keeps_nothing()
    {
        FalAiVideoGenerationClient.CarriesPersonalMedia(ReferenceRequest("alibaba/wan-3.0/reference-to-video", 1, 5, "720p", "16:9"))
            .Should().BeTrue("a person may be among the subjects, and an object's pictures lose nothing by not being kept");
    }
}
