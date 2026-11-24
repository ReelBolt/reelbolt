using System;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Assistant;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// T-024 (QA-P0-8, S-13, S-14): what a tenant reads of a step's output or error never carries a storage key,
/// an internal address, a server path, a stack trace or another tenant's budget figures.
/// </summary>
public class StepOutputSanitizerTests
{
    private static readonly Guid P = Guid.Parse("8b0f7a52-3c1e-4d7e-9a51-2f1f3c8d4e10");
    private static readonly Guid E = Guid.Parse("1d6c0e3a-5b7f-4a2e-8c9d-0e1f2a3b4c5d");

    // The real VideoCompile success summary (VideoCompileStepExecutor outputSummary).
    private static string CompileOutput() => $$"""
        {"status":"completed","outputStorageKey":"projects/{{P}}/outputFiles/{{E}}/final.mp4","artifactStorageKey":"projects/{{P}}/agentFiles/video-analysis/{{E}}/step-4-edl.json","timelineStorageKey":"projects/{{P}}/agentFiles/video-analysis/{{E}}/step-4-timeline.json","segments":7,"outputDurationSec":61.2,"retainedRatio":0.42,"droppedSegmentsOverCap":0}
        """;

    // The real TimelineRender report (TimelineRenderStepExecutor.BuildOutput), including a nested source list.
    private static string TimelineOutput() => $$$"""
        {"timelineId":"tl-1","revision":3,"outputStorageKey":"projects/{{{P}}}/outputFiles/{{{E}}}/timeline.mp4","timelineStorageKey":"projects/{{{P}}}/agentFiles/timelines/{{{E}}}/timeline.json","artifactStorageKey":null,"assets":[{"id":"a1","source":"projects/{{{P}}}/userFiles/clip.mp4"}],"note":"rendered from projects/{{{P}}}/userFiles/clip.mp4 at 1080p","outputResolution":{"requested":"1080p","width":1920,"height":1080,"honoured":true}}
        """;

    /// <summary>S-13 (round 2): a legacy VideoGenerate failure output carries raw exception text in meta.error and in a
    /// clip's detail (VideoGenerateStepExecutor before the source fix); outputPreview cleans both on read.</summary>
    [Fact]
    public void A_video_generate_failure_preview_cleans_raw_exception_text_in_meta_error_and_clip_detail()
    {
        const string legacy = """
            {"clips":[{"clipId":"clip-1","status":"failed","failureReason":"DELIVERY_FAILED","detail":"The provider finished the clip but download/normalize/upload failed (HttpRequestException: Connection refused (minio.internal:9000))."}],"meta":{"failed":"UNEXPECTED_ERROR","error":"Npgsql.NpgsqlException: Failed to connect to 10.0.4.7:5432\n   at Npgsql.Internal.NpgsqlConnector.Open()"}}
            """;

        string preview = StepOutputSanitizer.Preview(legacy, 1500)!;

        preview.Should().NotContain("minio.internal");
        preview.Should().NotContain("9000");
        preview.Should().NotContain("10.0.4.7");
        preview.Should().NotContain("Exception");
        preview.Should().NotContain("Npgsql");
        using JsonDocument doc = JsonDocument.Parse(preview);
        JsonElement clip = doc.RootElement.GetProperty("clips")[0];
        clip.GetProperty("clipId").GetString().Should().Be("clip-1");
        clip.GetProperty("failureReason").GetString().Should().Be("DELIVERY_FAILED");
        clip.GetProperty("detail").GetString().Should().StartWith("The provider finished the clip");
        doc.RootElement.GetProperty("meta").GetProperty("failed").GetString().Should().Be("UNEXPECTED_ERROR");
    }

    [Fact]
    public void A_compile_preview_drops_every_storage_key_property_and_keeps_the_facts()
    {
        string preview = StepOutputSanitizer.Preview(CompileOutput(), 1500)!;

        preview.Should().NotContain("projects/");
        preview.Should().NotContain("StorageKey");
        using JsonDocument doc = JsonDocument.Parse(preview);
        doc.RootElement.GetProperty("segments").GetInt32().Should().Be(7);
        doc.RootElement.GetProperty("outputDurationSec").GetDouble().Should().Be(61.2);
    }

    [Fact]
    public void A_timeline_preview_replaces_keys_held_under_other_names_and_inside_sentences()
    {
        string preview = StepOutputSanitizer.Preview(TimelineOutput(), 1500)!;

        preview.Should().NotContain("projects/");
        using JsonDocument doc = JsonDocument.Parse(preview);
        doc.RootElement.GetProperty("assets")[0].GetProperty("source").GetString().Should().Be(StepOutputSanitizer.StoredFile);
        doc.RootElement.GetProperty("note").GetString().Should().Be($"rendered from {StepOutputSanitizer.StoredFile} at 1080p");
        doc.RootElement.GetProperty("outputResolution").GetProperty("honoured").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void The_preview_is_cleaned_before_it_is_cut_so_a_key_near_the_cut_never_survives()
    {
        string padded = $$"""{"pad":"{{new string('x', 1480)}}","outputStorageKey":"projects/{{P}}/outputFiles/{{E}}/final.mp4"}""";

        string preview = StepOutputSanitizer.Preview(padded, 1500)!;

        preview.Should().NotContain("projects/");
        preview.Should().NotContain(P.ToString());
    }

    [Fact]
    public void Plain_text_output_has_embedded_keys_replaced()
    {
        string preview = StepOutputSanitizer.Preview($"Wrote projects/{P}/outputFiles/{E}/final.mp4 successfully.", 1500)!;
        preview.Should().Be($"Wrote {StepOutputSanitizer.StoredFile} successfully.");
    }

    [Fact]
    public void A_legacy_budget_failure_with_platform_figures_reads_as_the_tenant_safe_sentence()
    {
        string legacy = """{"clips":[],"meta":{"failed":"BUDGET_EXCEEDED","error":"This needs about 40 credits of video generation but the platform's daily video budget is 5000 credits and 4980 are already used today; it resets at 00:00 UTC (in 3 h). Generate fewer clips or wait."}}""";

        string preview = StepOutputSanitizer.Preview(legacy, 1500)!;

        using JsonDocument doc = JsonDocument.Parse(preview);
        doc.RootElement.GetProperty("meta").GetProperty("error").GetString().Should().Be(StepOutputSanitizer.LegacyBudgetSentence);
        preview.Should().NotContain("5000").And.NotContain("4980");
    }

    [Fact]
    public void A_budget_failure_on_the_tenants_own_cap_keeps_its_sentence()
    {
        const string own = "This needs about 40 credits of video generation but the project's daily video budget is 100 credits and 80 are already used today; it resets at 00:00 UTC (in 3 h). Generate fewer clips or wait.";

        StepOutputSanitizer.Error("BUDGET_EXCEEDED", own, 8000).Should().Be("BUDGET_EXCEEDED: " + own);
    }

    [Fact]
    public void A_legacy_budget_error_with_global_figures_is_replaced()
    {
        StepOutputSanitizer.Error("BUDGET_EXCEEDED", "BUDGET_EXCEEDED: global daily budget 5000 exceeded (spent 4990, requested 40)", 8000)
            .Should().Be("BUDGET_EXCEEDED: " + StepOutputSanitizer.LegacyBudgetSentence);
    }

    [Fact]
    public void Error_text_loses_internal_addresses_paths_stack_frames_and_exception_types()
    {
        const string raw = """
            System.Net.Http.HttpRequestException: Connection refused (minio:9000) while uploading /tmp/reelbolt/ws-12/out.mp4 to http://10.0.3.7:9000/reelbolt/x
               at System.Net.Http.HttpConnectionPool.ConnectAsync(HttpRequestMessage request)
               at ReelBolt.WorkflowEngine.Execution.Foo.BarAsync() in /src/inference/Foo.cs:line 42
            --- End of stack trace from previous location ---
            """;

        string cleaned = StepOutputSanitizer.Error("ENCODE_FAILED", raw, 8000)!;

        cleaned.Should().StartWith("ENCODE_FAILED: ");
        cleaned.Should().Contain("Connection refused");
        cleaned.Should().NotContain("minio:9000").And.NotContain("10.0.3.7").And.NotContain("/tmp/")
            .And.NotContain("HttpRequestException").And.NotContain(" at ").And.NotContain("End of stack trace")
            .And.NotContain("Foo.cs");
    }

    [Fact]
    public void Error_text_keeps_a_time_of_day_and_ordinary_sentences()
    {
        StepOutputSanitizer.Error(null, "Retry after 10:30 UTC; the clip is 12/30 frames short.", 8000)
            .Should().Be("Retry after 10:30 UTC; the clip is 12/30 frames short.");
    }

    [Fact]
    public void Error_text_has_storage_keys_replaced_and_is_capped()
    {
        StepOutputSanitizer.Error(null, $"missing projects/{P}/outputFiles/{E}/final.mp4", 8000)
            .Should().Be($"missing {StepOutputSanitizer.StoredFile}");
        StepOutputSanitizer.Error(null, new string('d', 9000), 8000)!.Should().StartWith(new string('d', 8000)).And.Contain("truncated");
    }

    [Fact]
    public void A_code_already_leading_the_text_is_not_repeated()
    {
        StepOutputSanitizer.Error("CONFIG_INVALID", "CONFIG_INVALID: Source=StepResult requires StepResultId.", 8000)
            .Should().Be("CONFIG_INVALID: Source=StepResult requires StepResultId.");
    }

    [Fact]
    public void Nothing_in_is_nothing_out()
    {
        StepOutputSanitizer.Preview(null, 1500).Should().BeNull();
        StepOutputSanitizer.Error("X", null, 8000).Should().BeNull();
    }

    [Fact]
    public void An_old_source_limit_error_loses_the_byte_count_and_the_setting_name()
    {
        string cleaned = StepOutputSanitizer.Error("INPUT_TOO_LARGE", "Source video is 3951234567 bytes, exceeding MaxInputBytes=2000000000.", 8000)!;

        cleaned.Should().StartWith("INPUT_TOO_LARGE: ").And.Contain("Trim it or export a smaller copy");
        cleaned.Should().NotContain("bytes").And.NotContain("MaxInputBytes").And.NotContain("3951234567");
    }
}
