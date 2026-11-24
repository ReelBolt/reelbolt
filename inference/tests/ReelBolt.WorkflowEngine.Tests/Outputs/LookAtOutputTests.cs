#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Outputs;

/// <summary>
/// <c>LookAtOutput</c> (T-101): look at a render or project file with the vision service — stills of a
/// video (asked for, then answered once they exist), or a png/jpeg directly — and GetOutput's stills.
/// No live model: the chat client is a recording fake.
/// </summary>
public class LookAtOutputTests
{
    private readonly IConfirmationTokenService _tokens = ConfirmationTokenService.CreateEphemeral();
    private readonly List<MediaDerivativesRequested> _published = [];
    private readonly Mock<IInferenceProviderResolver> _resolver = new();

    private sealed class RecordingVision(string answer) : IChatClientFactory
    {
        public List<ChatMessage> Sent { get; } = [];
        public List<ChatOptions?> Options { get; } = [];

        public IChatClient Get(ResolvedInferenceProvider provider)
        {
            Mock<IChatClient> client = new();
            client
                .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
                .Returns<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((messages, options, _) =>
                {
                    Sent.AddRange(messages);
                    Options.Add(options);
                    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
                });
            return client.Object;
        }

        public DataContent[] Images => [.. Sent.SelectMany(m => m.Contents).OfType<DataContent>()];
        public string Text => string.Join("\n", Sent.SelectMany(m => m.Contents).OfType<TextContent>().Select(t => t.Text));
    }

    /// <summary>A vision factory whose model call fails with text that names an internal host and a storage key.</summary>
    private sealed class FailingVision : IChatClientFactory
    {
        public const string Detail = "HTTP 502 from vision.internal:8443 for projects/p1/outputFiles/x/still-01.jpg";

        public IChatClient Get(ResolvedInferenceProvider provider)
        {
            Mock<IChatClient> client = new();
            client
                .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new System.Net.Http.HttpRequestException(Detail));
            return client.Object;
        }
    }

    /// <summary>Records every log entry, so a test can prove the detail went to the server and the answer did not.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<AssistantOutputTools>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception) + " | " + exception?.ToString());
    }

    private void WithVision() =>
        _resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(
                Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "https://vision.example/v1", "gpt-4o", "placeholder", 30));

    private OutputLinkService Links(OutputsWorld w) => new(_tokens, new ConfigurationBuilder().Build(), w.Clock);

    private AssistantOutputTools Tools(OutputsWorld w, IChatClientFactory? vision, StillsExtractionGate? stillsGate = null)
    {
        var publish = new Mock<IPublishEndpoint>();
        publish.Setup(p => p.Publish(It.IsAny<MediaDerivativesRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MediaDerivativesRequested, CancellationToken>((m, _) => _published.Add(m))
            .Returns(Task.CompletedTask);
        var access = TestTenancy.Access(w.Db, new OutputsUser(w.UserId, w.OrgId));
        return new AssistantOutputTools(
            w.Db, _tokens, access, new OutputDescriber(w.Db, w.Storage, Links(w), publish.Object), w.Storage,
            Mock.Of<IStorageObjectCopier>(), publish.Object, new RecordingSummarizationQueue(), null, null,
            _resolver.Object, vision, stillsGate ?? new StillsExtractionGate(w.Clock));
    }

    private static async Task<JsonElement> Invoke(AIFunction tool, params (string Key, object? Value)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach ((string k, object? v) in args) arguments[k] = v;
        object? raw = await tool.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> Look(AssistantOutputTools tools, OutputsWorld w, params (string Key, object? Value)[] args) =>
        Invoke(tools.GetLookAtOutputTool(new AssistantToolContext(w.UserId, w.Project.Id)), args);

    private static void AssertNoStorageSecrets(OutputsWorld w, string body)
    {
        body.Should().NotContain("storageKey").And.NotContain("StorageKey").And.NotContain("storage_key");
        body.Should().NotContain(OutputsWorld.Bucket);
        body.Should().NotContain("X-Amz-");
        body.Should().NotContain("projects/");
        body.Should().NotContain("outputFiles").And.NotContain("agentFiles");
    }

    /// <summary>A stills set of the render: <paramref name="timesSec"/> stills, requested at <paramref name="requestedMs"/> (null = the even set).</summary>
    private static MediaDerivative AddStills(OutputsWorld w, double[] timesSec, int[]? requestedMs = null)
    {
        string dir = $"projects/{w.Project.Id}/agentFiles/media-derivatives/s{Guid.NewGuid():N}";
        string paramsJson = requestedMs is null
            ? "{\"evenCount\":12,\"longSide\":1280,\"quality\":3,\"videoCodec\":\"mjpeg\"}"
            : "{\"longSide\":1280,\"quality\":3,\"timesMs\":[" + string.Join(',', requestedMs) + "],\"videoCodec\":\"mjpeg\"}";
        var row = new MediaDerivative
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            SourceKind = MediaSourceKind.StepResult,
            SourceStepResultId = w.Render.Id,
            SourceStorageKey = w.RenderKey,
            Kind = MediaDerivativeKind.Stills,
            ParamsJson = paramsJson,
            StorageKey = $"{dir}/still-01.jpg",
            IndexStorageKey = $"{dir}/stills.json",
            StorageBucket = OutputsWorld.Bucket,
            MimeType = "image/jpeg",
            Width = 1280,
            Height = 720,
            CreatedAt = OutputsWorld.Now.UtcDateTime
        };
        w.Db.MediaDerivatives.Add(row);
        w.Db.SaveChanges();

        var entries = new List<object>();
        for (int i = 0; i < timesSec.Length; i++)
        {
            string leaf = $"still-{i + 1:00}.jpg";
            w.Storage.Objects[$"{dir}/{leaf}"] = Encoding.UTF8.GetBytes($"jpeg-{i}");
            entries.Add(new { atSec = timesSec[i], index = i, storageLeaf = leaf });
        }

        w.Storage.Objects[row.IndexStorageKey] = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { count = timesSec.Length, stills = entries }));
        return row;
    }

    // ---------------------------------------------------------------- pending

    [Fact]
    public async Task Without_stills_the_look_asks_for_them_once_and_says_pending()
    {
        using OutputsWorld w = new();
        WithVision();
        var vision = new RecordingVision("unused");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", w.Render.Id));

        r.GetProperty("status").GetString().Should().Be("pending");
        r.GetProperty("retryAfterSeconds").GetInt32().Should().Be(10);
        MediaDerivativesRequested request = _published.Should().ContainSingle().Subject;
        request.Kinds.Should().Equal(MediaDerivativeKind.Stills);
        request.SourceRef.Should().Be(new MediaSourceRef(MediaSourceKind.StepResult, w.Render.Id));
        request.SourceStorageKey.Should().Be(w.RenderKey);
        (request.StillTimesSec ?? []).Should().BeEmpty("no times asked: the evenly spaced set");
        vision.Sent.Should().BeEmpty("nothing is sent to the model until the stills exist");
        AssertNoStorageSecrets(w, r.GetRawText());
    }

    [Fact]
    public async Task Asking_at_times_with_only_the_even_set_requests_those_times()
    {
        using OutputsWorld w = new();
        WithVision();
        AddStills(w, [0, 5.5, 11]);

        JsonElement r = await Look(Tools(w, new RecordingVision("unused")), w,
            ("outputId", w.Render.Id), ("atSeconds", new[] { 12.04, 3.0 }));

        r.GetProperty("status").GetString().Should().Be("pending");
        _published.Should().ContainSingle().Which.StillTimesSec.Should().Equal(3.0, 12.0);
    }

    // ---------------------------------------------------------------- S-74: the stills ask is bounded

    /// <summary>
    /// S-74: the times are clamped to the source's own length BEFORE they are keyed on, so asking at 70 s and 80 s of
    /// a 61.2 s render is one ask for the last frame, not two extractions over the same pixels — and the answer says
    /// how long the video is and which times it read as what.
    /// </summary>
    [Fact]
    public async Task Times_beyond_the_video_are_clamped_to_its_length_and_the_answer_states_it()
    {
        using OutputsWorld w = new();
        WithVision();

        JsonElement r = await Look(Tools(w, new RecordingVision("unused")), w,
            ("outputId", w.Render.Id), ("atSeconds", new[] { 70.0, 80.0 }));

        r.GetProperty("status").GetString().Should().Be("pending");
        r.GetProperty("durationSec").GetDouble().Should().Be(61.2, "the render reports outputDurationSec");
        JsonElement[] clamped = [.. r.GetProperty("clampedTimes").EnumerateArray()];
        clamped.Should().HaveCount(2);
        clamped.Select(c => c.GetProperty("used").GetDouble()).Should().AllBeEquivalentTo(61.2);
        r.GetProperty("message").GetString().Should().Contain("61.2 s");

        MediaDerivativesRequested request = _published.Should().ContainSingle().Subject;
        request.StillTimesSec.Should().Equal([61.2], "both out-of-range times are the last frame, once");
    }

    /// <summary>
    /// S-74: a set already extracted for one out-of-range ask answers a DIFFERENT out-of-range ask, because both are
    /// the same clamped set — so the pixels are reused instead of a second extraction being minted for them.
    /// </summary>
    [Fact]
    public async Task A_clamped_ask_matches_the_set_already_extracted_for_another_out_of_range_time()
    {
        using OutputsWorld w = new();
        WithVision();
        AddStills(w, [61.2], requestedMs: [61200]);
        var vision = new RecordingVision("The last frame.");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", w.Render.Id), ("atSeconds", new[] { 999.0 }));

        r.GetProperty("answer").GetString().Should().Be("The last frame.");
        r.GetProperty("lookedAt").EnumerateArray().Single().GetProperty("atSec").GetDouble().Should().Be(61.2);
        _published.Should().BeEmpty("the set taken at the clamped time IS the ask");
        vision.Images.Should().ContainSingle();
    }

    /// <summary>
    /// S-74: the same ask made twice while the extraction is still in flight publishes ONE request — the second call
    /// reads as pending too, which is what it would have got anyway — while a genuinely different ask still publishes.
    /// </summary>
    [Fact]
    public async Task An_identical_ask_while_the_stills_are_in_flight_publishes_nothing_again()
    {
        using OutputsWorld w = new();
        WithVision();
        AssistantOutputTools tools = Tools(w, new RecordingVision("unused"));

        JsonElement first = await Look(tools, w, ("outputId", w.Render.Id), ("atSeconds", new[] { 5.0, 10.0 }));
        JsonElement second = await Look(tools, w, ("outputId", w.Render.Id), ("atSeconds", new[] { 10.04, 5.0 }));
        JsonElement other = await Look(tools, w, ("outputId", w.Render.Id), ("atSeconds", new[] { 30.0 }));

        first.GetProperty("status").GetString().Should().Be("pending");
        second.GetProperty("status").GetString().Should().Be("pending");
        other.GetProperty("status").GetString().Should().Be("pending");
        _published.Should().HaveCount(2, "the identical ask is deduped; the different one is a new extraction");
        _published[0].StillTimesSec.Should().Equal(5.0, 10.0);
        _published[1].StillTimesSec.Should().Equal(30.0);
    }

    /// <summary>
    /// S-74: one caller may start a bounded number of extractions in the window; past it the tool refuses plainly
    /// (nothing is published, so nothing is spent) instead of driving unbounded engine work from a free tool.
    /// </summary>
    [Fact]
    public async Task A_caller_past_its_window_bound_is_refused_and_publishes_nothing()
    {
        using OutputsWorld w = new();
        WithVision();
        AssistantOutputTools tools = Tools(w, new RecordingVision("unused"));

        for (int i = 0; i < StillsExtractionGate.MaxRequestsPerUserPerWindow; i++)
        {
            JsonElement ok = await Look(tools, w, ("outputId", w.Render.Id), ("atSeconds", new[] { i * 0.5 }));
            ok.GetProperty("status").GetString().Should().Be("pending");
        }

        JsonElement refused = await Look(tools, w, ("outputId", w.Render.Id), ("atSeconds", new[] { 59.5 }));

        refused.GetProperty("code").GetString().Should().Be("STILLS_REQUEST_BUDGET_EXHAUSTED");
        refused.GetProperty("error").GetString().Should().Contain("limit for one person");
        refused.GetProperty("retryAfterSeconds").GetInt32().Should().Be((int)StillsExtractionGate.Window.TotalSeconds);
        _published.Should().HaveCount(StillsExtractionGate.MaxRequestsPerUserPerWindow);

        // The window is rolling, not permanent: once it has passed the caller asks again normally.
        w.Clock.Now = OutputsWorld.Now + StillsExtractionGate.Window + TimeSpan.FromSeconds(1);
        JsonElement again = await Look(tools, w, ("outputId", w.Render.Id), ("atSeconds", new[] { 59.5 }));
        again.GetProperty("status").GetString().Should().Be("pending");
        _published.Should().HaveCount(StillsExtractionGate.MaxRequestsPerUserPerWindow + 1);
    }

    // ---------------------------------------------------------------- ready

    [Fact]
    public async Task With_stills_the_vision_model_gets_at_most_six_images_a_legend_and_the_default_question()
    {
        using OutputsWorld w = new();
        WithVision();
        AddStills(w, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
        var vision = new RecordingVision("A title card; the word 'LAUNCH' is cut off at the right edge.");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", w.Render.Id));

        vision.Images.Should().HaveCount(6);
        vision.Images.Should().OnlyContain(i => i.MediaType == "image/jpeg");
        vision.Text.Should().Contain(AssistantOutputTools.DefaultLookOutputQuestion);
        vision.Text.Should().Contain("image 1 = 0.0 s").And.Contain("image 6 = 11.0 s");
        vision.Options.Should().ContainSingle().Which!.MaxOutputTokens.Should().Be(1024);

        r.GetProperty("projectName").GetString().Should().Be("Launch Promo");
        r.GetProperty("output").GetProperty("id").GetGuid().Should().Be(w.Render.Id);
        r.GetProperty("output").GetProperty("name").GetString().Should().Be("final-cut.mp4");
        JsonElement[] looked = [.. r.GetProperty("lookedAt").EnumerateArray()];
        looked.Should().HaveCount(6);
        looked[0].GetProperty("index").GetInt32().Should().Be(1);
        looked[0].GetProperty("atSec").GetDouble().Should().Be(0);
        looked[5].GetProperty("atSec").GetDouble().Should().Be(11);
        r.GetProperty("model").GetString().Should().Be("gpt-4o");
        r.GetProperty("answer").GetString().Should().Contain("cut off");
        r.GetProperty("truncated").GetBoolean().Should().BeFalse();
        _published.Should().BeEmpty();
        AssertNoStorageSecrets(w, r.GetRawText());
    }

    [Fact]
    public async Task Asking_at_times_uses_the_set_taken_at_those_times_and_the_question_asked()
    {
        using OutputsWorld w = new();
        WithVision();
        AddStills(w, [0, 6, 12]);
        AddStills(w, [3, 12], requestedMs: [3000, 12000]);
        var vision = new RecordingVision("Two frames.");

        JsonElement r = await Look(Tools(w, vision), w,
            ("outputId", w.Render.Id), ("atSeconds", new[] { 12.0, 3.0 }), ("question", "Is the logo visible?"));

        vision.Images.Should().HaveCount(2);
        vision.Text.Should().Contain("Is the logo visible?").And.Contain("image 1 = 3.0 s").And.Contain("image 2 = 12.0 s");
        r.GetProperty("lookedAt").EnumerateArray().Select(e => e.GetProperty("atSec").GetDouble()).Should().Equal(3, 12);
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_long_answer_is_capped_and_flagged()
    {
        using OutputsWorld w = new();
        WithVision();
        AddStills(w, [0]);

        JsonElement r = await Look(Tools(w, new RecordingVision(new string('x', 5000))), w, ("outputId", w.Render.Id));

        r.GetProperty("answer").GetString().Should().HaveLength(3000);
        r.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task An_image_file_is_sent_directly()
    {
        using OutputsWorld w = new();
        WithVision();
        string imageKey = $"projects/{w.Project.Id}/userFiles/{Guid.NewGuid()}.png";
        var image = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            OriginalFileName = "poster.png",
            OriginalPath = "poster.png",
            Category = "userFiles",
            StorageKey = imageKey,
            StorageBucket = OutputsWorld.Bucket,
            MimeType = "image/png",
            SizeBytes = 8
        };
        w.Db.ProjectFiles.Add(image);
        w.Db.SaveChanges();
        w.Storage.Objects[imageKey] = Encoding.UTF8.GetBytes("png-body");
        var vision = new RecordingVision("A poster.");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", image.Id));

        vision.Images.Should().ContainSingle().Which.MediaType.Should().Be("image/png");
        r.GetProperty("output").GetProperty("name").GetString().Should().Be("poster.png");
        r.GetProperty("lookedAt").EnumerateArray().Should().ContainSingle();
        _published.Should().BeEmpty();
        AssertNoStorageSecrets(w, r.GetRawText());
    }

    // ---------------------------------------------------------------- bounds (C1, S-76)

    /// <summary>A PNG whose IHDR declares <paramref name="width"/> x <paramref name="height"/> (the body is not a real image).</summary>
    private static byte[] PngHeader(int width, int height)
    {
        byte[] b = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 6, 0, 0, 0, 0, 0, 0, 0];
        return b;
    }

    private static ProjectFile AddImage(OutputsWorld w, byte[] body, string name = "poster.png")
    {
        string imageKey = $"projects/{w.Project.Id}/userFiles/{Guid.NewGuid()}.png";
        var image = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = w.Project.Id,
            OriginalFileName = name,
            OriginalPath = name,
            Category = "userFiles",
            StorageKey = imageKey,
            StorageBucket = OutputsWorld.Bucket,
            MimeType = "image/png",
            SizeBytes = body.Length
        };
        w.Db.ProjectFiles.Add(image);
        w.Db.SaveChanges();
        w.Storage.Objects[imageKey] = body;
        return image;
    }

    [Fact]
    public async Task An_image_over_the_look_pixel_cap_is_refused_and_nothing_is_sent()
    {
        using OutputsWorld w = new();
        WithVision();
        ProjectFile huge = AddImage(w, PngHeader(8000, 6000)); // 48 MP > MaxLookPixels
        var vision = new RecordingVision("unused");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", huge.Id));

        r.GetProperty("error").GetString().Should().Contain("too large").And.Contain("megapixels");
        vision.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task An_image_within_the_cap_is_still_sent()
    {
        using OutputsWorld w = new();
        WithVision();
        ProjectFile ok = AddImage(w, PngHeader(1920, 1080));
        var vision = new RecordingVision("A poster.");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", ok.Id));

        r.GetProperty("answer").GetString().Should().Be("A poster.");
        vision.Images.Should().ContainSingle();
    }

    [Fact]
    public async Task A_question_over_the_length_cap_is_refused_before_anything_is_requested_or_sent()
    {
        using OutputsWorld w = new();
        WithVision();
        var vision = new RecordingVision("unused");

        JsonElement r = await Look(Tools(w, vision), w,
            ("outputId", w.Render.Id), ("question", new string('q', AssistantOutputTools.MaxLookQuestionChars + 1)));

        r.GetProperty("error").GetString().Should().Contain(AssistantOutputTools.MaxLookQuestionChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
        vision.Sent.Should().BeEmpty();
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task The_sizer_reports_what_the_look_will_send()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w, null);
        var context = new AssistantToolContext(w.UserId, w.Project.Id);
        ProjectFile image = AddImage(w, PngHeader(10, 10));

        LookOutputSizing? even = await tools.SizeLookOutputAsync(new Dictionary<string, object?> { ["outputId"] = w.Render.Id }, context, CancellationToken.None);
        LookOutputSizing? timed = await tools.SizeLookOutputAsync(new Dictionary<string, object?>
        {
            ["outputId"] = w.Render.Id.ToString(),
            ["atSeconds"] = JsonDocument.Parse("[1, 2.5]").RootElement,
            ["question"] = "Is it ok?"
        }, context, CancellationToken.None);
        LookOutputSizing? picture = await tools.SizeLookOutputAsync(new Dictionary<string, object?> { ["outputId"] = image.Id }, context, CancellationToken.None);
        (Project _, WorkflowStepResult theirs) = w.AddForeignProject();
        LookOutputSizing? foreign = await tools.SizeLookOutputAsync(new Dictionary<string, object?> { ["outputId"] = theirs.Id }, context, CancellationToken.None);

        (even!.Images, even.PixelsPerImage).Should().Be((AssistantOutputTools.MaxLookImages, AssistantOutputTools.MaxStillPixels));
        even.PromptChars.Should().BeGreaterThan(AssistantOutputTools.DefaultLookOutputQuestion.Length + "final-cut.mp4".Length);
        (timed!.Images, timed.PixelsPerImage).Should().Be((2, AssistantOutputTools.MaxStillPixels));
        timed.PromptChars.Should().BeLessThan(even.PromptChars, "a short question and two images");
        (picture!.Images, picture.PixelsPerImage).Should().Be((1, 100L), "an image is sized from its header");
        foreign.Should().BeNull();
    }

    // ---------------------------------------------------------------- S-75: the tool boundary

    /// <summary>
    /// S-75: the vision call, the stills lookup and the source lookup all fail sometimes, and an exception's own text
    /// names internal hosts and storage keys. The tool answers ONE fixed sentence with its own code, and the detail is
    /// logged server-side — never in the answer, never in the model's input.
    /// </summary>
    [Fact]
    public async Task A_vision_call_that_fails_answers_a_fixed_sentence_and_logs_the_detail()
    {
        using OutputsWorld w = new();
        WithVision();
        AddStills(w, [0, 5]);
        var logger = new RecordingLogger();
        var publish = new Mock<IPublishEndpoint>();
        var access = TestTenancy.Access(w.Db, new OutputsUser(w.UserId, w.OrgId));
        var tools = new AssistantOutputTools(
            w.Db, _tokens, access, new OutputDescriber(w.Db, w.Storage, Links(w), publish.Object), w.Storage,
            Mock.Of<IStorageObjectCopier>(), publish.Object, new RecordingSummarizationQueue(), null, null,
            _resolver.Object, new FailingVision(), new StillsExtractionGate(w.Clock), logger);

        JsonElement r = await Look(tools, w, ("outputId", w.Render.Id));

        r.GetProperty("code").GetString().Should().Be("LOOK_AT_OUTPUT_FAILED");
        string error = r.GetProperty("error").GetString()!;
        error.Should().StartWith("The output could not be looked at just now");
        error.Should().NotContain("vision.internal").And.NotContain("projects/").And.NotContain("HttpRequestException")
            .And.NotContain("502");
        r.GetRawText().Should().NotContain("vision.internal");

        logger.Entries.Should().ContainSingle("one failure, one log line")
            .Which.Should().Contain(FailingVision.Detail, "the operator diagnoses it from the log");
    }

    // ---------------------------------------------------------------- refusals

    [Fact]
    public async Task Without_a_vision_service_it_refuses_plainly_and_publishes_nothing()
    {
        using OutputsWorld w = new();

        JsonElement r = await Look(Tools(w, new RecordingVision("unused")), w, ("outputId", w.Render.Id));

        r.GetProperty("error").GetString().Should().Contain("No vision service");
        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_output_is_refused_and_nothing_is_published_or_sent()
    {
        using OutputsWorld w = new();
        WithVision();
        (Project theirs, WorkflowStepResult theirRender) = w.AddForeignProject();
        var vision = new RecordingVision("unused");
        AssistantOutputTools tools = Tools(w, vision);

        (await Look(tools, w, ("outputId", theirRender.Id))).GetProperty("error").GetString().Should().Be("output not found");
        (await Look(tools, w, ("outputId", theirRender.Id), ("projectId", theirs.Id))).GetProperty("error").GetString()
            .Should().Be("project not found");
        _published.Should().BeEmpty();
        vision.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Audio_is_refused_without_a_model_call()
    {
        using OutputsWorld w = new();
        WithVision();
        var vision = new RecordingVision("unused");

        JsonElement r = await Look(Tools(w, vision), w, ("outputId", w.File.Id));

        r.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        vision.Sent.Should().BeEmpty();
        _published.Should().BeEmpty();
    }

    [Fact]
    public void The_look_is_offered_separately_with_its_caption_for_the_registering_ticket()
    {
        using OutputsWorld w = new();
        AssistantOutputTools tools = Tools(w, null);

        tools.GetTools(new AssistantToolContext(w.UserId)).Select(t => t.Name).Should().NotContain("LookAtOutput");
        tools.GetLookAtOutputTool(new AssistantToolContext(w.UserId)).Name.Should().Be("LookAtOutput");
        AssistantOutputTools.LookCaptions.Should().ContainKey("LookAtOutput");
    }

    // ---------------------------------------------------------------- GetOutput stills

    [Fact]
    public async Task GetOutput_lists_the_stills_as_signed_links_that_resolve_to_each_still()
    {
        using OutputsWorld w = new();
        AddStills(w, [0, 4.5]);
        AssistantOutputTools tools = Tools(w, null);

        JsonElement r = await Invoke(tools.GetTools(new AssistantToolContext(w.UserId, w.Project.Id)).Single(t => t.Name == "GetOutput"),
            ("outputId", w.Render.Id));

        JsonElement frames = r.GetProperty("frames");
        frames.GetProperty("status").GetString().Should().Be("ready");
        JsonElement[] stills = [.. frames.GetProperty("stills").EnumerateArray()];
        stills.Select(s => s.GetProperty("atSec").GetDouble()).Should().Equal(0, 4.5);
        AssertNoStorageSecrets(w, r.GetRawText());

        string url = stills[1].GetProperty("url").GetString()!;
        url.Should().StartWith("/api/v1/output-links/");
        Links(w).TryVerify(url[(url.LastIndexOf('/') + 1)..], w.Clock.Now, out OutputLinkClaims claims).Should().BeTrue();
        ResolvedOutputObject? resolved = await new OutputLinkResolver(w.Db, w.Storage).ResolveAsync(claims, CancellationToken.None);
        resolved!.StorageKey.Should().EndWith("/still-02.jpg");
        resolved.ContentType.Should().Be("image/jpeg");
    }
}
