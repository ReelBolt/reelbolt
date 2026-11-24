using Microsoft.AspNetCore.Http;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// The assistant's <b>request-body seam</b>, asserted from the C# side against the ONE shared
/// fixture — <c>TestData/assistant-chat-ai-sdk-body.json</c> — that the e2e seam spec
/// (<c>e2e/tests/assistant-seam.contract.spec.ts</c>) also pins the real browser client to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a shared file and not a literal.</b> The critical QA finding on this branch was exactly a
/// disagreement across this seam: the client posted the AI-SDK <c>messages[]</c> body while the
/// endpoint required a flat <c>message</c>, so every assistant turn was a 400. A body written twice
/// — once in C# and once in the spec — can drift while both halves stay green, which is the same
/// failure with extra steps. So the body exists once, on disk, and both halves are asserted against
/// that single artifact: the e2e half proves the real adapter POSTs it, this half proves the server
/// binds and serves it.
/// </para>
/// <para>
/// <b>What this class covers.</b> It reads the fixture from the test output directory, binds it with
/// the same <see cref="JsonSerializerOptions"/> instance the MVC-shaped tests in
/// <see cref="AssistantChatControllerTests"/> already use (<c>MvcJson</c>), asserts the bound
/// values, and then drives the REAL <see cref="AssistantChatController.ChatStream"/> action with
/// that bound request: HTTP 200, an <c>X-Assistant-Thread-Id</c> header that parses as a non-empty
/// <see cref="Guid"/>, exactly one user <c>assistant_messages</c> row holding the resolved text
/// <c>hello</c>, and a <c>text/event-stream</c> content type.
/// </para>
/// <para>
/// <b>What it does NOT cover — stated plainly, because the difference is easy to overclaim.</b>
/// This is <b>not</b> an HTTP round trip. There is no Kestrel and no
/// <c>TestServer</c>/<c>WebApplicationFactory</c> harness in this repo, so the body never travels
/// through the ASP.NET Core input formatter, <c>[ApiController]</c>'s automatic model-state
/// validation, the <c>[Authorize]</c> filter or the route table: <c>ChatStream</c> is invoked
/// directly with an already-bound object. A defect living in that plumbing — a changed route
/// template, a changed authorization policy, a custom input-formatter registration that binds the
/// body differently from <c>MvcJson</c> — would <b>not</b> fail here. The route pair is pinned
/// separately, by the e2e spec, which reads the controller's own <c>[Route]</c>/<c>[HttpPost]</c>
/// attributes and compares them with the URL the client actually fetches.
/// </para>
/// <para>
/// <b>Nor is it a substitute for the e2e spec.</b> This half pins what the SERVER does with the
/// body; only the e2e half pins the exact bytes the real client puts on the wire. Both read the
/// same fixture, so a change to the shape fails on both sides at once.
/// </para>
/// </remarks>
public class AssistantChatSeamContractTests
{
    /// <summary>
    /// The fixture, relative to the test output directory it is copied into (see the
    /// <c>&lt;None Update … CopyToOutputDirectory="PreserveNewest" /&gt;</c> item in
    /// <c>ReelBolt.WorkflowEngine.Tests.csproj</c>).
    /// </summary>
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "assistant-chat-ai-sdk-body.json");

    /// <summary>
    /// The one shared AI-SDK request body. Read from disk rather than restated so it cannot drift
    /// from what the e2e spec deep-equals the real client's captured request against.
    /// </summary>
    internal static string ReadSharedFixture()
    {
        if (!File.Exists(FixturePath))
        {
            throw new FileNotFoundException(
                "The shared AI-SDK body fixture is missing from the test output directory. It is the "
                + "artifact that pins the client and the server to the SAME request body, so both "
                + "seam tests depend on it: check the <None Update … CopyToOutputDirectory> item in "
                + "ReelBolt.WorkflowEngine.Tests.csproj rather than inlining a literal.",
                FixturePath);
        }

        return File.ReadAllText(FixturePath);
    }

    private static AssistantChatRequest DeserializeSharedFixture() =>
        AssistantChatControllerTests.DeserializeRequest(ReadSharedFixture());

    /// <summary>
    /// The fixture binds to the seam contract the endpoint actually relies on: an ABSENT
    /// <c>threadId</c> (which reads as <see cref="Guid.Empty"/>, i.e. "allocate one"), no flat
    /// <c>message</c>, and the turn text resolvable out of the AI-SDK <c>messages[]</c> fallback.
    /// </summary>
    /// <remarks>
    /// Every one of these four is load-bearing and each fails differently in production: a
    /// <c>ThreadId</c> that stopped binding to <see cref="Guid.Empty"/> would 404 every first turn;
    /// a <c>Message</c> that stopped binding to <c>null</c> would silently shadow the fallback; a
    /// <see cref="AssistantChatRequest.ResolveMessageText"/> that stopped reading <c>messages[]</c>
    /// would 400 with "No user message was supplied" — the exact bug this seam is hardening.
    /// </remarks>
    [Fact]
    public void TheSharedFixture_BindsToTheSeamContract()
    {
        AssistantChatRequest request = DeserializeSharedFixture();

        Assert.Equal(Guid.Empty, request.ThreadId);
        Assert.Null(request.Message);
        Assert.Equal("hello", request.ResolveMessageText());
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), request.ProjectId);
    }

    /// <summary>
    /// The fixture binds <b>identically</b> under MVC's shipped default options
    /// (<see cref="JsonSerializerDefaults.Web"/>) and under the <c>MvcJson</c> instance the test
    /// suite shares.
    /// </summary>
    /// <remarks>
    /// The suite's <c>MvcJson</c> spells out camelCase naming plus case-insensitive matching rather
    /// than constructing from <see cref="JsonSerializerDefaults.Web"/>. Those two are what this DTO
    /// needs — its keys are camelCase and its members are a <see cref="Guid"/>, a
    /// <see cref="string"/> and a <see cref="Guid"/>? — and the defaults add only
    /// <see cref="System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString"/>, which
    /// no field here can exercise. Asserted rather than argued: if the two ever diverge on this body,
    /// the shared options are a weaker stand-in than they claim to be, and this fails instead of
    /// quietly passing.
    /// </remarks>
    [Fact]
    public void TheSharedFixture_BindsIdenticallyUnderMvcsShippedDefaultOptions()
    {
        string json = ReadSharedFixture();

        AssistantChatRequest shared = AssistantChatControllerTests.DeserializeRequest(json);
        AssistantChatRequest shippedDefault =
            JsonSerializer.Deserialize<AssistantChatRequest>(
                json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(shippedDefault.ThreadId, shared.ThreadId);
        Assert.Equal(shippedDefault.Message, shared.Message);
        Assert.Equal(shippedDefault.ProjectId, shared.ProjectId);
        Assert.Equal(shippedDefault.ResolveMessageText(), shared.ResolveMessageText());
    }

    /// <summary>
    /// The fixture's body is accepted end to end by the REAL action: 200, an allocated thread
    /// echoed in <c>X-Assistant-Thread-Id</c>, the resolved text persisted as the user turn, and a
    /// live SSE stream.
    /// </summary>
    /// <remarks>
    /// See the class remarks for what "real" does and does not mean here — this drives the real
    /// controller action, not the real HTTP pipeline.
    /// </remarks>
    [Fact]
    public async Task TheSharedFixture_DrivesTheRealChatStreamAction()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();

        (AssistantChatController controller, MemoryStream responseBody) =
            AssistantChatControllerTests.CreateController(
                db, userId, AssistantChatControllerTests.ChatProviderReturning("reply"));

        await controller.ChatStream(DeserializeSharedFixture(), CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, controller.HttpContext.Response.StatusCode);
        Assert.Equal("text/event-stream", controller.HttpContext.Response.ContentType);

        // The fixture carries no threadId, so the endpoint allocates a thread and must echo the id it
        // chose — that header is the only way a client can bind to a thread it never named.
        string header = controller.HttpContext.Response.Headers["X-Assistant-Thread-Id"].ToString();
        bool headerNamesAThread =
            Guid.TryParse(header, out Guid allocatedThreadId) && allocatedThreadId != Guid.Empty;
        Assert.True(
            headerNamesAThread,
            "X-Assistant-Thread-Id must name the thread the fixture's turn landed on, and must parse "
                + $"as a non-empty GUID. It was '{header}'. The shared fixture carries no threadId, so "
                + "a missing or unparseable header means the endpoint stopped allocating a thread for "
                + "the AI-SDK body — the client-side half of this seam is covered by "
                + "e2e/tests/assistant-seam.contract.spec.ts, this is the server-side half.");

        // Exactly one user turn, carrying the RESOLVED text and not an empty string, a JSON blob or
        // the raw content-part array.
        AssistantMessage userTurn = Assert.Single(db.AssistantMessages.Where(m => m.Role == "user"));
        Assert.Equal("hello", userTurn.Content);
        Assert.Equal(allocatedThreadId, userTurn.ThreadId);
        Assert.Single(db.AssistantMessages.Where(m => m.Content == "hello"));

        // The turn genuinely streamed rather than being persisted and dropped.
        Assert.Contains("[DONE]", AssistantChatControllerTests.ParseDataLines(responseBody));
    }
}
