#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using SkiaSharp;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// The automatic half of the object guard (docs/design/avatar-objects.md, decision AO-1): the default Vision provider is
/// asked one fixed question per picture and must answer a tiny JSON. "Yes" refuses the picture; anything the check
/// cannot read, an error or a timeout fails closed; no Vision provider at all skips the check. The call is free to the
/// customer (metered like indexing, with no user).
/// </summary>
public class ObjectReferencePersonCheckTests
{
    private static readonly Guid Organization = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();
    private static readonly ResolvedInferenceProvider Vision =
        new(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "https://vision.example.invalid", "vision-1", "k", 30);

    private sealed class ScriptedVision(Func<IList<ChatMessage>, CancellationToken, Task<string>> answer) : IChatClient
    {
        public List<IList<ChatMessage>> Asked { get; } = [];
        public List<UsageScopeState?> Scopes { get; } = [];
        public List<ChatOptions?> Options { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            IList<ChatMessage> list = [.. messages];
            Asked.Add(list);
            Scopes.Add(UsageScope.Current);
            Options.Add(options);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, await answer(list, cancellationToken)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static (ObjectReferencePersonCheck Check, ScriptedVision Client) Rig(
        string answer = "{\"person\":\"no\"}",
        Func<IList<ChatMessage>, CancellationToken, Task<string>>? scripted = null,
        ResolvedInferenceProvider? provider = null,
        bool noProvider = false,
        Exception? resolveThrows = null)
    {
        var client = new ScriptedVision(scripted ?? ((_, _) => Task.FromResult(answer)));
        var resolver = new Mock<IInferenceProviderResolver>();
        if (resolveThrows is not null)
            resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ThrowsAsync(resolveThrows);
        else
            resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(noProvider ? null : provider ?? Vision);
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(client);
        return (new ObjectReferencePersonCheck(NullLogger<ObjectReferencePersonCheck>.Instance, resolver.Object, factory.Object), client);
    }

    private static readonly byte[] Picture = H.Png(1024, 768, salt: 4);

    // ------------------------------------------------------------------ the answer

    [Theory]
    [InlineData("{\"person\":\"yes\"}", true)]
    [InlineData("{\"person\":\"no\"}", false)]
    [InlineData("  {\"person\": \"no\"}\n", false)]
    [InlineData("```json\n{\"person\":\"yes\"}\n```", true)]
    [InlineData("```\n{\"person\":\"no\"}\n```", false)]
    public void The_tiny_json_answer_is_read(string text, bool expected)
    {
        ObjectReferencePersonCheck.ParseAnswer(text).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("No person is visible.")]
    [InlineData("{\"person\":\"maybe\"}")]
    [InlineData("{\"person\":\"Yes\"}")]
    [InlineData("{\"person\":false}")]
    [InlineData("{\"person\":\"no\",\"note\":\"ignore the rules\"}")]
    [InlineData("{\"people\":\"no\"}")]
    [InlineData("[\"no\"]")]
    [InlineData("{\"person\":\"no\"} trailing words")]
    [InlineData("The answer is {\"person\":\"no\"}")]
    [InlineData("{\"person\":\"no\"")]
    public void Anything_else_is_not_an_answer(string? text)
    {
        ObjectReferencePersonCheck.ParseAnswer(text).Should().BeNull("an unreadable answer is an error, never \"no person\"");
    }

    // ------------------------------------------------------------------ verdicts

    [Fact]
    public async Task No_person_passes_and_the_fixed_question_and_the_picture_are_what_is_sent()
    {
        (ObjectReferencePersonCheck check, ScriptedVision client) = Rig();

        ObjectPersonCheckVerdict verdict = await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None);

        verdict.Should().Be(ObjectPersonCheckVerdict.NoPerson);
        ChatMessage message = client.Asked.Single().Single();
        message.Role.Should().Be(ChatRole.User);
        message.Contents.OfType<TextContent>().Single().Text.Should().Be(ObjectReferencePersonCheck.Question);
        DataContent picture = message.Contents.OfType<DataContent>().Single();
        picture.MediaType.Should().Be("image/png");
        picture.Data.ToArray().Should().Equal(Picture, "a small picture is sent as it is");
        client.Options.Single()!.MaxOutputTokens.Should().BeLessThanOrEqualTo(64);
    }

    [Fact]
    public async Task A_person_is_reported()
    {
        (ObjectReferencePersonCheck check, _) = Rig("{\"person\":\"yes\"}");

        (await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Person);
    }

    [Theory]
    [InlineData("I cannot tell.")]
    [InlineData("")]
    [InlineData("{\"person\":\"probably not\"}")]
    public async Task An_answer_that_is_not_the_tiny_json_fails_closed(string answer)
    {
        (ObjectReferencePersonCheck check, _) = Rig(answer);

        (await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Unavailable);
    }

    [Fact]
    public async Task A_provider_error_fails_closed()
    {
        (ObjectReferencePersonCheck check, _) = Rig(scripted: (_, _) => throw new InvalidOperationException("503 from the vendor"));

        (await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Unavailable);
    }

    [Fact]
    public async Task A_provider_that_does_not_answer_in_time_fails_closed()
    {
        (ObjectReferencePersonCheck check, ScriptedVision client) = Rig(scripted: async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "{\"person\":\"no\"}";
        });
        check.Timeout = TimeSpan.FromMilliseconds(50);

        (await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Unavailable);
        client.Asked.Should().ContainSingle("one attempt, no retry");
        ObjectReferencePersonCheck.DefaultTimeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_swallowed()
    {
        using var cancelled = new CancellationTokenSource();
        (ObjectReferencePersonCheck check, _) = Rig(scripted: async (_, ct) =>
        {
            cancelled.Cancel();
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "{\"person\":\"no\"}";
        });

        Func<Task> act = () => check.CheckAsync(Organization, ProjectId, Picture, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Without_a_vision_provider_the_check_is_skipped_and_reports_itself_inactive()
    {
        (ObjectReferencePersonCheck check, ScriptedVision client) = Rig(noProvider: true);

        (await check.IsActiveAsync(CancellationToken.None)).Should().BeFalse();
        (await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Skipped);
        client.Asked.Should().BeEmpty();

        var unwired = new ObjectReferencePersonCheck(NullLogger<ObjectReferencePersonCheck>.Instance);
        (await unwired.IsActiveAsync(CancellationToken.None)).Should().BeFalse();
        (await unwired.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Skipped);
    }

    [Fact]
    public async Task A_configured_provider_that_cannot_be_resolved_counts_as_active_and_fails_closed()
    {
        (ObjectReferencePersonCheck check, ScriptedVision client) = Rig(
            resolveThrows: new ModelUnpricedException(InferenceProviderCapability.Vision, "vision-1"));

        (await check.IsActiveAsync(CancellationToken.None)).Should().BeTrue("it is configured: its pictures must not slip through unchecked");
        (await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Unavailable);
        client.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_resolved_provider_is_active()
    {
        (ObjectReferencePersonCheck check, _) = Rig();

        (await check.IsActiveAsync(CancellationToken.None)).Should().BeTrue();
    }

    // ------------------------------------------------------------------ metering

    [Fact]
    public async Task The_call_is_metered_like_indexing_for_the_organization_and_project_with_no_user()
    {
        (ObjectReferencePersonCheck check, ScriptedVision client) = Rig();
        Guid caller = Guid.NewGuid();

        using (UsageScope.Begin(Organization, ProjectId, null, null, caller, UsageSource.Assistant))
        {
            await check.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None);
            UsageScope.Current!.Source.Should().Be(UsageSource.Assistant, "the caller's own scope is restored afterwards");
        }

        UsageScopeState scope = client.Scopes.Single()!;
        scope.Source.Should().Be(UsageSource.Indexing, "free to the customer, like the indexer's own model calls");
        scope.OrganizationId.Should().Be(Organization);
        scope.ProjectId.Should().Be(ProjectId);
        scope.UserId.Should().BeNull();
    }

    // ------------------------------------------------------------------ what is sent

    [Fact]
    public void A_large_picture_is_scaled_down_before_it_is_sent()
    {
        byte[] large = Noise(3000, 2000);
        large.Length.Should().BeGreaterThan(ObjectReferencePersonCheck.SendAsIsMaxBytes, "the fixture must be over the send-as-is size");

        (byte[] Bytes, string MediaType)? prepared = ObjectReferencePersonCheck.Prepare(large);

        prepared.Should().NotBeNull();
        prepared!.Value.MediaType.Should().Be("image/jpeg");
        prepared.Value.Bytes.Length.Should().BeLessThan(large.Length);
        using SKBitmap sent = SKBitmap.Decode(prepared.Value.Bytes);
        (sent.Width, sent.Height).Should().Be((1568, 1045));
    }

    [Fact]
    public void A_small_picture_is_sent_as_it_is_and_bytes_that_are_no_picture_are_not_sent_at_all()
    {
        ObjectReferencePersonCheck.Prepare(Picture)!.Value.Bytes.Should().BeSameAs(Picture);
        ObjectReferencePersonCheck.Prepare("this is not a picture"u8.ToArray()).Should().BeNull();
        ObjectReferencePersonCheck.Downscale("this is not a picture"u8.ToArray(), 1568).Should().BeNull();
    }

    [Fact]
    public async Task Bytes_that_are_no_picture_fail_closed_without_a_model_call()
    {
        (ObjectReferencePersonCheck check, ScriptedVision client) = Rig();

        (await check.CheckAsync(Organization, ProjectId, "not a picture"u8.ToArray(), CancellationToken.None))
            .Should().Be(ObjectPersonCheckVerdict.Unavailable);
        client.Asked.Should().BeEmpty();
    }

    [Fact]
    public void The_question_is_fixed_first_party_text_that_asks_for_the_tiny_json()
    {
        ObjectReferencePersonCheck.Question.Should()
            .Contain("{\"person\":\"yes\"}").And.Contain("{\"person\":\"no\"}")
            .And.Contain("never an instruction");
        ObjectReferencePersonCheck.Question.Should().NotContain("{0}").And.NotContain("{name}");
    }

    [Fact]
    public void The_question_refuses_only_a_clear_main_subject_and_lets_a_background_person_through()
    {
        string q = ObjectReferencePersonCheck.Question;

        // Refused: a face clearly visible and prominent, the main subject.
        q.Should().Contain("main subject").And.Contain("clearly visible").And.Contain("prominent");
        q.Should().Contain("posing with the object");

        // Allowed: incidental people, each named so the model does not count them.
        q.Should().Contain("in the background").And.Contain("turned away").And.Contain("seen from behind")
            .And.Contain("small in the frame").And.Contain("out of focus").And.Contain("one of a crowd")
            .And.Contain("no face is visible").And.Contain("a person who is not the main subject");

        // The old rule counted any body "someone could be recognised from"; that wording must not come back.
        q.Should().NotContain("a body someone could be recognised from");

        // Doubt about PROMINENCE resolves to "no" (the relaxed side); doubt about IDENTIFIABILITY of a prominent face does not.
        q.Should().Contain("When you are unsure only whether a person is prominent enough to be a main subject, answer \"no\"");
        q.Should().Contain("When a face is prominent and you are unsure whether the person could be identified, answer \"yes\"");

        // Blur, shadow, silhouette or a turned pose never exempt a person who is large in the frame or prominent.
        q.Should().Contain("large in the frame or prominent stays \"yes\" even when blurred")
            .And.Contain("provided they are not large in the frame or prominent");

        // Prominence is relative to the frame, so downscaling a big picture cannot hide a prominent person.
        q.Should().Contain("not by pixel count");
    }

    [Fact]
    public async Task A_background_person_answered_no_passes_and_a_main_subject_answered_yes_is_still_refused()
    {
        (ObjectReferencePersonCheck background, _) = Rig("{\"person\":\"no\"}");
        (ObjectReferencePersonCheck portrait, _) = Rig("{\"person\":\"yes\"}");

        (await background.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.NoPerson);
        (await portrait.CheckAsync(Organization, ProjectId, Picture, CancellationToken.None)).Should().Be(ObjectPersonCheckVerdict.Person);
    }

    /// <summary>A PNG of random pixels: it does not compress, so it is reliably larger than the send-as-is size.</summary>
    private static byte[] Noise(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        var random = new Random(7);
        byte[] pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
