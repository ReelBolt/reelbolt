using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Follow;

/// <summary>followSubject config checks (run time and save time) and failure classes.</summary>
public class FollowSubjectConfigTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static VideoCompileStepConfig Config(
        IReadOnlyList<FollowSubjectRequest>? follow, VideoCompileMode mode = VideoCompileMode.Reencode, IReadOnlyList<VideoReframe>? hand = null) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Mode: mode, Reframes: hand, FollowSubject: follow);

    private static IReadOnlyList<FollowSubjectRequest> One(FollowSubjectRequest r) => [r];

    [Fact]
    public void No_follow_is_valid_and_unchanged()
    {
        VideoReframe.CheckConfig(Config(null)).IsValid.Should().BeTrue();
        VideoReframe.CheckConfig(Config([], VideoCompileMode.StreamCopy)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("r1", true)]
    [InlineData("g2", true)]
    [InlineData("r9999", true)]
    [InlineData("r0", true)]
    [InlineData("g0", true)]
    [InlineData("g10", true)]
    [InlineData("r01", false)]
    [InlineData("g00", false)]
    [InlineData("r10000", false)]
    [InlineData("x1", false)]
    [InlineData("r", false)]
    [InlineData("R1", false)]
    [InlineData("r1;", false)]
    public void The_track_id_must_look_like_r_or_g_and_a_number(string id, bool valid)
    {
        VideoReframeValidation check = VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest(id))));
        check.IsValid.Should().Be(valid);
        if (!valid)
            check.ErrorCode.Should().Be("FOLLOW_TRACK_ID_INVALID");
    }

    [Theory]
    [InlineData(-1.0, 5.0)]
    [InlineData(5.0, 5.0)]
    [InlineData(6.0, 5.0)]
    [InlineData(double.NaN, 5.0)]
    [InlineData(0.0, double.PositiveInfinity)]
    public void A_bad_range_is_refused(double from, double to)
    {
        VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest("r1", FromSec: from, ToSec: to))))
            .ErrorCode.Should().Be("FOLLOW_RANGE_INVALID");
    }

    [Fact]
    public void Eight_entries_are_fine_and_nine_are_not()
    {
        IReadOnlyList<FollowSubjectRequest> Many(int n) => Enumerable.Range(1, n).Select(i => new FollowSubjectRequest($"r{i}")).ToList();
        VideoReframe.CheckConfig(Config(Many(8))).IsValid.Should().BeTrue();
        VideoReframe.CheckConfig(Config(Many(9))).ErrorCode.Should().Be("FOLLOW_TOO_MANY");
    }

    [Fact]
    public void Following_a_stream_copy_is_refused()
    {
        VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest("r1")), VideoCompileMode.StreamCopy))
            .ErrorCode.Should().Be("FOLLOW_REQUIRES_REENCODE");
    }

    [Fact]
    public void A_source_has_one_owner()
    {
        VideoReframe hand = new(0, 0, 2, [new ReframeKeyframe(0, 2)]);
        VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest("r1", SourceIndex: 0)), hand: [hand]))
            .ErrorCode.Should().Be("FOLLOW_CONFLICTS_REFRAME");
        VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest("r1", SourceIndex: 1)), hand: [hand]))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void Every_follow_code_is_a_user_input_failure()
    {
        foreach (string code in FollowSubjectCodes.All)
        {
            FailureClassifier.TryGetCode(code, out FailureClass failureClass).Should().BeTrue(code);
            failureClass.Should().Be(FailureClass.UserInput, code);
        }
    }

    [Fact]
    public void The_config_round_trips_camel_case_with_enums_by_name()
    {
        const string json = """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"followSubject":[{"trackId":"g3","tightness":"Tight","onLost":"Widen"}]}""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>(json, options)!;
        config.FollowSubject.Should().ContainSingle().Which.Should().Be(
            new FollowSubjectRequest("g3", Tightness: FollowTightness.Tight, OnLost: FollowOnLost.Widen));
    }

    private static CreateWorkflowStepRequest CompileStep(JsonObject config) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["stepOrder"] = 3,
            ["label"] = "Compile",
            ["stepType"] = nameof(StepType.VideoCompile),
            ["videoCompileConfigJson"] = config.ToJsonString()
        }.ToJsonString(), WebJson)!;

    private static JsonObject SaveConfig(JsonNode? follow, string? mode = null)
    {
        var config = new JsonObject
        {
            ["version"] = 1,
            ["decision"] = new JsonObject { ["from"] = "Previous" },
            ["analysisStepOrder"] = 1
        };
        if (follow is not null)
            config["followSubject"] = follow;
        if (mode is not null)
            config["mode"] = mode;
        return config;
    }

    [Fact]
    public void A_valid_followSubject_saves()
    {
        var follow = new JsonArray(new JsonObject { ["trackId"] = "r1", ["tightness"] = "Tight" });
        StepConfigSaveValidator.ValidateStep(CompileStep(SaveConfig(follow))).Should().BeEmpty();
    }

    [Fact]
    public void A_bad_track_id_is_refused_at_save()
    {
        var follow = new JsonArray(new JsonObject { ["trackId"] = "banana" });
        StepConfigSaveValidator.ValidateStep(CompileStep(SaveConfig(follow)))
            .Should().ContainSingle().Which.Should().Contain("FOLLOW_TRACK_ID_INVALID");
    }

    [Fact]
    public void Following_a_stream_copy_is_refused_at_save()
    {
        var follow = new JsonArray(new JsonObject { ["trackId"] = "r1" });
        StepConfigSaveValidator.ValidateStep(CompileStep(SaveConfig(follow, "StreamCopy")))
            .Should().ContainSingle().Which.Should().Contain("FOLLOW_REQUIRES_REENCODE");
    }

    [Theory]
    [InlineData(0.0, 1e12)]
    [InlineData(1e12, null)]
    [InlineData(null, 14401.0)]
    public void A_range_past_the_follow_cap_is_refused(double? from, double? to)
    {
        VideoReframeValidation v = VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest("r1", FromSec: from, ToSec: to))));

        v.ErrorCode.Should().Be("FOLLOW_RANGE_INVALID");
    }

    [Fact]
    public void A_range_at_the_follow_cap_is_accepted()
    {
        VideoReframe.CheckConfig(Config(One(new FollowSubjectRequest("r1", FromSec: 0, ToSec: SubjectCameraPath.MaxRangeSeconds)))).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Two_entries_on_one_source_over_overlapping_ranges_are_refused()
    {
        VideoReframe.CheckConfig(Config([new FollowSubjectRequest("r1", SourceIndex: 0), new FollowSubjectRequest("r2", SourceIndex: 0)]))
            .ErrorCode.Should().Be("FOLLOW_OVERLAP");
        VideoReframe.CheckConfig(Config([
            new FollowSubjectRequest("r1", SourceIndex: 0, FromSec: 0, ToSec: 5),
            new FollowSubjectRequest("r2", SourceIndex: 0, FromSec: 4, ToSec: 8)])).ErrorCode.Should().Be("FOLLOW_OVERLAP");
    }

    [Fact]
    public void Disjoint_ranges_other_sources_and_unresolved_sources_pass_the_config_check()
    {
        VideoReframe.CheckConfig(Config([
            new FollowSubjectRequest("r1", SourceIndex: 0, FromSec: 0, ToSec: 5),
            new FollowSubjectRequest("r2", SourceIndex: 0, FromSec: 5, ToSec: 8)])).IsValid.Should().BeTrue();
        VideoReframe.CheckConfig(Config([new FollowSubjectRequest("r1", SourceIndex: 0), new FollowSubjectRequest("r2", SourceIndex: 1)])).IsValid.Should().BeTrue();
        VideoReframe.CheckConfig(Config([new FollowSubjectRequest("r1"), new FollowSubjectRequest("r2")])).IsValid.Should().BeTrue();
    }
}
