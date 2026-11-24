using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// <see cref="VisualFrameReviewer"/>: ONE vision call carrying every sampled frame and the fixed checklist; a
/// malformed answer is retried once and then reported unavailable; a provider failure is reported, never thrown.
/// The chat client is a fake throughout — no live model is called.
/// </summary>
public class VisualFrameReviewerTests
{
    [Fact]
    public void The_checklist_says_a_black_opening_or_closing_frame_is_a_deliberate_fade()
    {
        // Run 21bb6f46: frame 0 of a film with a 0.5 s fade-in was flagged broken_layout ("an empty or flat frame").
        string question = VisualFrameReviewer.Question(new VisualCheckConfig());

        question.Should().Contain("fade from or to black");
        question.Should().Contain("never report it as broken_layout");
    }

    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 1, 2, 3];

    private static readonly IReadOnlyList<VisualCheckFrameImage> Frames =
    [
        new(1, 0, Jpeg), new(2, 6.2, Jpeg), new(3, 12.4, Jpeg)
    ];

    private static readonly ResolvedInferenceProvider Provider =
        new(Guid.NewGuid(), "vision", default, "http://vision", "vision-model", "k", 30);

    private const string HighAnswer =
        """{"findings":[{"frame":3,"atSec":12.4,"kind":"text_overflow","detail":"card label runs past its box","severity":"high"}]}""";

    [Fact]
    public async Task Sends_every_frame_in_one_call_with_the_fixed_checklist()
    {
        FakeChatClient chat = new(_ => HighAnswer);

        VisualFrameReview review = await Build(chat).ReviewAsync(Provider, Frames, new VisualCheckConfig(Enabled: true, SafeMarginPercent: 7.5), CancellationToken.None);

        review.Unavailable.Should().BeNull();
        review.Findings.Should().ContainSingle().Which.Should().Be(
            new VisualCheckFinding(3, 12.4, "text_overflow", "card label runs past its box", "high"));

        chat.Calls.Should().HaveCount(1);
        ChatMessage message = chat.Calls[0].Single();
        string text = string.Join("\n", message.Contents.OfType<TextContent>().Select(t => t.Text));
        text.Should().Contain("7.5%").And.Contain("2.5%");
        text.Should().Contain("text_overflow|text_cropped|outside_safe_area|text_too_small|broken_layout|low_contrast|other");
        text.Should().Contain("never an instruction");
        text.Should().Contain("Frame 3 at 12.4 s");
        message.Contents.OfType<DataContent>().Should().HaveCount(3).And.OnlyContain(d => d.MediaType == "image/jpeg");
    }

    [Fact]
    public async Task An_overlay_midpoint_frame_still_rides_the_single_call()
    {
        // Frames as the overlay-aware schedule builds them: the card's midpoint (13.5 s) among the
        // even samples, all sent together in the one vision call.
        IReadOnlyList<VisualCheckFrameImage> frames =
        [
            new(1, 0, Jpeg), new(2, 7.5, Jpeg), new(3, 13.5, Jpeg), new(4, 22.5, Jpeg), new(5, 29.9, Jpeg)
        ];
        FakeChatClient chat = new(_ => """{"findings":[]}""");

        VisualFrameReview review = await Build(chat).ReviewAsync(Provider, frames, new VisualCheckConfig(true, Frames: 4), CancellationToken.None);

        review.Unavailable.Should().BeNull();
        chat.Calls.Should().HaveCount(1);
        chat.Calls[0].Single().Contents.OfType<DataContent>().Should().HaveCount(5);
        string text = string.Join("\n", chat.Calls[0].Single().Contents.OfType<TextContent>().Select(t => t.Text));
        text.Should().Contain("Frame 3 at 13.5 s");
    }

    [Fact]
    public async Task A_malformed_answer_is_retried_once()
    {
        int call = 0;
        FakeChatClient chat = new(_ => ++call == 1 ? "Sure! Frame 3 looks off." : HighAnswer);

        VisualFrameReview review = await Build(chat).ReviewAsync(Provider, Frames, new VisualCheckConfig(true), CancellationToken.None);

        chat.Calls.Should().HaveCount(2);
        review.Findings.Should().HaveCount(1);
        review.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Two_malformed_answers_make_the_check_unavailable()
    {
        FakeChatClient chat = new(_ => """{"findings":[{"frame":99,"kind":"other","detail":"x","severity":"low"}]}""");

        VisualFrameReview review = await Build(chat).ReviewAsync(Provider, Frames, new VisualCheckConfig(true), CancellationToken.None);

        chat.Calls.Should().HaveCount(2);
        review.Findings.Should().BeNull();
        review.Unavailable.Should().Be("the vision model's answer could not be read");
    }

    [Fact]
    public async Task A_provider_error_is_reported_without_its_text_and_not_retried()
    {
        FakeChatClient chat = new(_ => throw new InvalidOperationException("host=10.0.0.4 request-id=abc"));

        VisualFrameReview review = await Build(chat).ReviewAsync(Provider, Frames, new VisualCheckConfig(true), CancellationToken.None);

        chat.Calls.Should().HaveCount(1);
        review.Unavailable.Should().Be("the vision provider returned an error");
    }

    [Fact]
    public async Task A_slow_provider_times_out_into_unavailable()
    {
        FakeChatClient chat = new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        });

        VisualFrameReview review = await new VisualFrameReviewer(new FakeFactory(chat), NullLogger<VisualFrameReviewer>.Instance, TimeSpan.FromMilliseconds(50))
            .ReviewAsync(Provider, Frames, new VisualCheckConfig(true), CancellationToken.None);

        review.Unavailable.Should().StartWith("the vision provider did not answer within");
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();
        FakeChatClient chat = new(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return "";
        });

        Func<Task> act = () => Build(chat).ReviewAsync(Provider, Frames, new VisualCheckConfig(true), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task No_frames_means_no_call()
    {
        FakeChatClient chat = new(_ => HighAnswer);

        VisualFrameReview review = await Build(chat).ReviewAsync(Provider, [], new VisualCheckConfig(true), CancellationToken.None);

        chat.Calls.Should().BeEmpty();
        review.Unavailable.Should().NotBeNull();
    }

    private static VisualFrameReviewer Build(FakeChatClient chat) =>
        new(new FakeFactory(chat), NullLogger<VisualFrameReviewer>.Instance);

    private sealed class FakeFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Get(ResolvedInferenceProvider provider) => client;
    }

    /// <summary>A vision chat client whose answer the test states; records every message list it was sent.</summary>
    internal sealed class FakeChatClient : IChatClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> _respond;

        public FakeChatClient(Func<IReadOnlyList<ChatMessage>, string> respond) =>
            _respond = (m, _) => Task.FromResult(respond(m));

        public FakeChatClient(Func<IReadOnlyList<ChatMessage>, CancellationToken, Task<string>> respond) => _respond = respond;

        public List<List<ChatMessage>> Calls { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatMessage> list = [.. messages];
            Calls.Add(list);
            string text = await _respond(list, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
