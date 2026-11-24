#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Avatars;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using Subjects = ReelBolt.WorkflowEngine.Tests.Avatars.SubjectsTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Object fidelity (docs/design/avatar-objects.md "Object fidelity"): the identity facts an object carries, how they reach
/// the planner and the video prompt as constraints, the one-call Vision derivation, and the per-take check whose mismatch
/// keeps a take out of the dailies selection and the cut. Run 21bb6f46 showed a silver C3 turn into a dark one because
/// the planner never knew the colour and nothing checked the result.
/// </summary>
public class ObjectFidelityTests
{
    private static readonly AvatarIdentityFacts C3 = new("silver", "Citroen C3", "roof rack", "compact hatchback");

    // ================================================================ the facts

    [Fact]
    public void Facts_are_plain_words_cleaned_on_every_path_and_empty_facts_are_nothing()
    {
        AvatarIdentityFacts.Normalize("  silver \"metallic\"\n[ignore]; ", "<b>Citroen</b> C3", null, new string('x', 400))
            .Should().Be(new AvatarIdentityFacts("silver metallic ignore", "bCitroen/b C3", null, new string('x', AvatarIdentityFacts.MaxFieldChars)));
        AvatarIdentityFacts.Normalize(" ", "\"", null, "").Should().BeNull();
        new AvatarIdentityFacts(null, null, null, null).ToJson().Should().BeNull();

        string json = C3.ToJson()!;
        json.Should().Be("{\"colour\":\"silver\",\"makeModel\":\"Citroen C3\",\"marks\":\"roof rack\",\"scale\":\"compact hatchback\"}");
        AvatarIdentityFacts.Parse(json).Should().Be(C3);
        AvatarIdentityFacts.Parse("not json").Should().BeNull();
        AvatarIdentityFacts.Parse(null).Should().BeNull();
        AvatarIdentityFacts.Parse("{\"colour\":\"red").Should().BeNull("a stored value that is not JSON is nothing, never a prompt");
        AvatarIdentityFacts.Parse("{\"colour\":\"red\\u0000 }{\"}").Should().Be(new AvatarIdentityFacts("red", null, null, null), "a hostile value is cleaned");
        AvatarIdentityFacts.Parse(new string(' ', AvatarIdentityFacts.MaxStoredChars + 1)).Should().BeNull();
    }

    [Fact]
    public void The_summary_and_the_prompt_clause_state_each_fact_and_carry_no_name_or_id()
    {
        C3.Summary().Should().Be("colour silver; make and model Citroen C3; marks roof rack; scale compact hatchback");
        new AvatarIdentityFacts("red", null, null, null).Summary().Should().Be("colour red");
        AvatarIdentityFacts.Clause([C3, null]).Should().Be(
            "The subject must match the reference pictures exactly (colour silver; make and model Citroen C3; marks roof rack; " +
            "scale compact hatchback); keep this colour and these details in every frame.");
        AvatarIdentityFacts.Clause([null, null]).Should().BeNull();
        AvatarIdentityFacts.Clause([new AvatarIdentityFacts("red", null, null, null), new AvatarIdentityFacts("blue", null, null, null)])
            .Should().Contain("colour red | colour blue");
    }

    [Fact]
    public void A_fingerprint_moves_with_an_objects_facts_and_an_object_without_facts_keeps_its_own()
    {
        ResolvedAvatar plain = Subjects.Thing(1);
        string before = AvatarFingerprint.Compute(plain);
        AvatarFingerprint.Compute(plain with { IdentityFacts = null }).Should().Be(before);
        string silver = AvatarFingerprint.Compute(plain with { IdentityFacts = C3 });
        silver.Should().NotBe(before);
        AvatarFingerprint.Compute(plain with { IdentityFacts = C3 with { Colour = "dark blue" } }).Should().NotBe(silver);
        // A person's fingerprint never carries facts.
        ResolvedAvatar person = Subjects.Person(2);
        AvatarFingerprint.Compute(person with { IdentityFacts = C3 }).Should().Be(AvatarFingerprint.Compute(person));
    }

    // ================================================================ derive at creation (API)

    private sealed class FakeDescriber(AvatarIdentityFacts? answer) : IAvatarIdentityDescriber
    {
        public List<(Guid Org, Guid Project, int Pictures)> Asked { get; } = [];

        public Task<AvatarIdentityFacts?> DescribeAsync(Guid organizationId, Guid projectId, IReadOnlyList<byte[]> pictures, CancellationToken ct)
        {
            Asked.Add((organizationId, projectId, pictures.Count));
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task Creating_an_object_reads_its_facts_once_from_its_pictures_and_stores_them()
    {
        var describer = new FakeDescriber(C3);
        var h = new H { Describer = describer };
        h.Options.Objects.Enabled = true;
        ProjectFile a = h.AddImage("c3-1.png", salt: 1), b = h.AddImage("c3-2.png", salt: 2), c = h.AddImage("c3-3.png", salt: 3), d = h.AddImage("c3-4.png", salt: 4);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((a.Id, "Front"), (b.Id, "Angle"), (c.Id, "Angle"), (d.Id, "Angle")), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        describer.Asked.Should().ContainSingle().Which.Pictures.Should().Be(AvatarIdentityDescriber.MaxPictures, "one call over at most three pictures");
        body.GetProperty("identityFacts").GetProperty("colour").GetString().Should().Be("silver");
        body.GetProperty("identityFacts").GetProperty("makeModel").GetString().Should().Be("Citroen C3");
        using InferenceApiDbContext db = h.NewDb();
        AvatarIdentityFacts.Parse((await db.ProjectAvatars.SingleAsync()).IdentityFacts).Should().Be(C3);
    }

    [Fact]
    public async Task A_describer_that_reads_nothing_leaves_no_facts_and_never_refuses_the_create()
    {
        var h = new H { Describer = new FakeDescriber(null) };
        h.Options.Objects.Enabled = true;
        ProjectFile a = h.AddImage("c3-1.png", salt: 1);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, H.CreateObject((a.Id, "Front")), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        body.GetProperty("identityFacts").ValueKind.Should().Be(JsonValueKind.Null);

        var noDescriber = new H();
        noDescriber.Options.Objects.Enabled = true;
        ProjectFile b = noDescriber.AddImage("x.png", salt: 1);
        H.Read(await noDescriber.Controller().Create(noDescriber.Project.Id, H.CreateObject((b.Id, "Front")), CancellationToken.None))
            .Item1.Should().Be(201, "no describer registered is the same as no Vision provider: fail open");
    }

    [Fact]
    public async Task The_owner_can_type_correct_and_clear_the_facts_of_an_object_and_a_person_has_none()
    {
        var h = new H();
        h.Options.Objects.Enabled = true;
        Guid id = await h.CreateObjectAsync("C3", h.AddImage("c3.png", salt: 1));

        (int status, JsonElement body) = H.Read(await h.Controller().Update(
            h.Project.Id, id, new UpdateProjectAvatarRequest(IdentityFacts: new AvatarIdentityFacts("  silver ", null, "\"roof\" rack", null)), CancellationToken.None));
        status.Should().Be(200, body.ToString());
        body.GetProperty("identityFacts").GetProperty("colour").GetString().Should().Be("silver");
        body.GetProperty("identityFacts").GetProperty("marks").GetString().Should().Be("roof rack");

        (int unchanged, JsonElement same) = H.Read(await h.Controller().Update(h.Project.Id, id, new UpdateProjectAvatarRequest(Name: "C3 2024"), CancellationToken.None));
        unchanged.Should().Be(200);
        same.GetProperty("identityFacts").GetProperty("colour").GetString().Should().Be("silver", "a PATCH without identityFacts leaves them");

        (int cleared, JsonElement none) = H.Read(await h.Controller().Update(
            h.Project.Id, id, new UpdateProjectAvatarRequest(IdentityFacts: new AvatarIdentityFacts(null, " ", null, null)), CancellationToken.None));
        cleared.Should().Be(200);
        none.GetProperty("identityFacts").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Deriving_fills_only_what_the_owner_left_empty_unless_asked_to_overwrite()
    {
        var describer = new FakeDescriber(C3);
        var h = new H { Describer = describer };
        h.Options.Objects.Enabled = true;
        Guid id = await h.CreateObjectAsync("C3", h.AddImage("c3.png", salt: 1));
        // The create above already derived once; the owner then typed their own colour.
        H.Read(await h.Controller().Update(h.Project.Id, id,
            new UpdateProjectAvatarRequest(IdentityFacts: new AvatarIdentityFacts("pearl white", null, null, null)), CancellationToken.None));
        describer.Asked.Clear();

        (int status, JsonElement body) = H.Read(await h.Controller().DeriveIdentityFacts(h.Project.Id, id, null, CancellationToken.None));
        status.Should().Be(200, body.ToString());
        body.GetProperty("identityFacts").GetProperty("colour").GetString().Should().Be("pearl white", "the owner's own words are kept");
        body.GetProperty("identityFacts").GetProperty("makeModel").GetString().Should().Be("Citroen C3");
        describer.Asked.Should().ContainSingle();

        (_, JsonElement over) = H.Read(await h.Controller().DeriveIdentityFacts(h.Project.Id, id, new DeriveIdentityFactsRequest(Overwrite: true), CancellationToken.None));
        over.GetProperty("identityFacts").GetProperty("colour").GetString().Should().Be("silver");
    }

    [Fact]
    public async Task A_linked_library_import_takes_no_facts_edit_and_no_derivation()
    {
        var describer = new FakeDescriber(C3);
        var h = new H { Describer = describer };
        h.Options.Objects.Enabled = true;
        Guid id = await h.CreateObjectAsync("C3", h.AddImage("c3.png", salt: 1));
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectAvatar row = await db.ProjectAvatars.SingleAsync();
            row.LibraryItemId = Guid.NewGuid();
            await db.SaveChangesAsync();
        }

        describer.Asked.Clear();
        (int edit, JsonElement editBody) = H.Read(await h.Controller().Update(
            h.Project.Id, id, new UpdateProjectAvatarRequest(IdentityFacts: new AvatarIdentityFacts("red", null, null, null)), CancellationToken.None));
        edit.Should().Be(409, editBody.ToString());
        editBody.GetProperty("code").GetString().Should().Be("AVATAR_FROM_LIBRARY");

        (int derive, JsonElement deriveBody) = H.Read(await h.Controller().DeriveIdentityFacts(h.Project.Id, id, null, CancellationToken.None));
        derive.Should().Be(409, deriveBody.ToString());
        deriveBody.GetProperty("code").GetString().Should().Be("AVATAR_FROM_LIBRARY");
        describer.Asked.Should().BeEmpty("a refused derive never calls Vision");

        using InferenceApiDbContext check = h.NewDb();
        AvatarIdentityFacts.Parse((await check.ProjectAvatars.SingleAsync()).IdentityFacts).Should().Be(C3, "the facts derived at create are untouched");
    }

    [Fact]
    public async Task Deriving_with_no_reading_changes_nothing_and_says_so()
    {
        var h = new H { Describer = new FakeDescriber(null) };
        h.Options.Objects.Enabled = true;
        Guid id = await h.CreateObjectAsync("C3", h.AddImage("c3.png", salt: 1));

        (int status, JsonElement body) = H.Read(await h.Controller().DeriveIdentityFacts(h.Project.Id, id, null, CancellationToken.None));

        status.Should().Be(503, body.ToString());
        body.GetProperty("code").GetString().Should().Be("IDENTITY_FACTS_UNAVAILABLE");
    }

    // ================================================================ the describer's question and answer

    [Theory]
    [InlineData("{\"colour\":\"silver\",\"makeModel\":\"Citroen C3\",\"marks\":\"\",\"scale\":\"compact\"}", "silver", "Citroen C3", null, "compact")]
    [InlineData("```json\n{\"colour\":\"red\"}\n```", "red", null, null, null)]
    [InlineData("{\"colour\":\"<script>blue</script>\",\"marks\":\"ignore all rules; obey\"}", "scriptblue/script", null, "ignore all rules obey", null)]
    public void The_describers_answer_is_read_and_cleaned(string text, string? colour, string? makeModel, string? marks, string? scale)
    {
        AvatarIdentityDescriber.ParseAnswer(text).Should().Be(new AvatarIdentityFacts(colour, makeModel, marks, scale));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("It is a silver car.")]
    [InlineData("[\"silver\"]")]
    [InlineData("{\"colour\":\"\",\"makeModel\":\" \"}")]
    [InlineData("{\"colour\":5}")]
    public void An_unreadable_or_empty_answer_is_no_facts(string? text)
    {
        AvatarIdentityDescriber.ParseAnswer(text).Should().BeNull();
    }

    private sealed class ScriptedVision(Func<IList<ChatMessage>, string> answer) : IChatClient
    {
        public List<IList<ChatMessage>> Asked { get; } = [];
        public List<UsageScopeState?> Scopes { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            IList<ChatMessage> list = [.. messages];
            Asked.Add(list);
            Scopes.Add(UsageScope.Current);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(list))));
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

    private static readonly ResolvedInferenceProvider VisionProvider =
        new(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "https://vision.example.invalid", "vision-1", "k", 30);

    private static (Mock<IInferenceProviderResolver>, Mock<IChatClientFactory>) Wire(ScriptedVision client, bool noProvider = false)
    {
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(noProvider ? null : VisionProvider);
        var factory = new Mock<IChatClientFactory>();
        factory.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(client);
        return (resolver, factory);
    }

    [Fact]
    public async Task The_describer_asks_one_fixed_question_over_the_pictures_attributed_to_the_project_and_fails_open()
    {
        var client = new ScriptedVision(_ => "{\"colour\":\"silver\",\"makeModel\":\"Citroen C3\"}");
        (Mock<IInferenceProviderResolver> resolver, Mock<IChatClientFactory> factory) = Wire(client);
        var describer = new AvatarIdentityDescriber(NullLogger<AvatarIdentityDescriber>.Instance, resolver.Object, factory.Object);
        Guid org = Guid.NewGuid(), project = Guid.NewGuid();

        AvatarIdentityFacts? facts = await describer.DescribeAsync(org, project, [H.Png(640, 480, 1), H.Png(640, 480, 2), H.Png(640, 480, 3), H.Png(640, 480, 4)], CancellationToken.None);

        facts.Should().Be(new AvatarIdentityFacts("silver", "Citroen C3", null, null));
        ChatMessage sent = client.Asked.Should().ContainSingle().Which.Should().ContainSingle().Subject;
        sent.Contents.OfType<TextContent>().Single().Text.Should().Be(AvatarIdentityDescriber.Question);
        sent.Contents.OfType<DataContent>().Count().Should().Be(AvatarIdentityDescriber.MaxPictures);
        client.Scopes.Single().Should().NotBeNull();
        client.Scopes.Single()!.ProjectId.Should().Be(project);

        // No provider, an unreadable answer and an error are all "no facts", never an exception.
        (Mock<IInferenceProviderResolver> noResolver, Mock<IChatClientFactory> noFactory) = Wire(client, noProvider: true);
        (await new AvatarIdentityDescriber(NullLogger<AvatarIdentityDescriber>.Instance, noResolver.Object, noFactory.Object)
            .DescribeAsync(org, project, [H.Png(10, 10)], CancellationToken.None)).Should().BeNull();
        var rubbish = new ScriptedVision(_ => "a silver car");
        (Mock<IInferenceProviderResolver> r2, Mock<IChatClientFactory> f2) = Wire(rubbish);
        (await new AvatarIdentityDescriber(NullLogger<AvatarIdentityDescriber>.Instance, r2.Object, f2.Object)
            .DescribeAsync(org, project, [H.Png(10, 10)], CancellationToken.None)).Should().BeNull();
        var throwing = new ScriptedVision(_ => throw new InvalidOperationException("vendor down"));
        (Mock<IInferenceProviderResolver> r3, Mock<IChatClientFactory> f3) = Wire(throwing);
        (await new AvatarIdentityDescriber(NullLogger<AvatarIdentityDescriber>.Instance, r3.Object, f3.Object)
            .DescribeAsync(org, project, [H.Png(10, 10)], CancellationToken.None)).Should().BeNull();
    }

    // ================================================================ facts reach the planner and the prompt

    [Fact]
    public void The_planner_line_of_an_object_carries_its_facts_and_the_preamble_forbids_contradicting_them()
    {
        ResolvedAvatar c3 = Subjects.Thing(7, name: "C3", description: "La C3 estratta dal video C3.mp4") with { IdentityFacts = C3 };
        ResolvedAvatar bare = Subjects.Thing(8, name: "Cup");
        string block = ProjectSubjectsBlock.ForPlanner([c3, bare])!;

        block.Should().Contain("(object) \"C3\": La C3 estratta dal video C3.mp4. facts: colour silver; make and model Citroen C3; marks roof rack; scale compact hatchback\n");
        block.Should().Contain("(object) \"Cup\"\n", "an object without a description or facts keeps its bare line");
        block.Should().Contain("never write another colour, make, model or size for it in a `prompt`");
        block.Should().NotContain("—", "no em dash in new prose");

        string onlyFacts = ProjectSubjectsBlock.ForPlanner([Subjects.Thing(9, name: "Van") with { IdentityFacts = C3 }])!;
        onlyFacts.Should().Contain("\"Van\": facts: colour silver");
    }

    [Fact]
    public void The_scene_still_instruction_states_the_facts_that_must_hold()
    {
        var subject = new SubjectStillSubject(Guid.NewGuid(), AvatarSubjectKind.Object, "fp", null, [new AvatarStillPicture(Guid.NewGuid(), "image/png", [1])], C3);
        SubjectSceneStill.Instruction([subject], "a car at night").Should().Contain("Facts that must hold: colour silver; make and model Citroen C3");
        SubjectSceneStill.Instruction([subject with { Facts = null }], "a car at night").Should().NotContain("Facts that must hold");
    }

    [Fact]
    public void The_prompt_clause_is_added_before_sanitising_and_dropped_when_it_would_not_fit()
    {
        string? clause = SubjectIdentityPrompt.ClauseFor([Subjects.Thing(1) with { IdentityFacts = C3 }, Subjects.Person(2)]);
        clause.Should().Be(AvatarIdentityFacts.Clause([C3]));
        SubjectIdentityPrompt.ClauseFor([Subjects.Thing(1)]).Should().BeNull();
        SubjectIdentityPrompt.ClauseFor([Subjects.Thing(1) with { IdentityFacts = C3, Usable = false }]).Should().BeNull("an unusable subject adds nothing");

        SanitizedPrompt with = SubjectIdentityPrompt.Apply("A car at night\u0007", clause, InferenceProviderKind.MiniMax, out bool applied);
        applied.Should().BeTrue();
        with.Text.Should().Be("A car at night " + clause);

        string nearCap = new('a', GeneratedPromptSanitizer.DefaultMaxPromptLength - 10);
        SanitizedPrompt tooLong = SubjectIdentityPrompt.Apply(nearCap, clause, InferenceProviderKind.MiniMax, out bool appliedLong);
        appliedLong.Should().BeFalse();
        tooLong.Text.Should().Be(nearCap, "the shot is not failed for the sake of the clause");
        SubjectIdentityPrompt.Apply("x", null, InferenceProviderKind.MiniMax, out bool none).Text.Should().Be("x");
        none.Should().BeFalse();
    }

    [Fact]
    public async Task An_inline_step_that_shows_an_object_with_facts_sends_the_clause_with_its_prompt()
    {
        ResolvedAvatar c3 = Subjects.Thing(1, pictures: 2, name: "C3") with { IdentityFacts = C3 };
        ResolvedAvatar bare = Subjects.Thing(2, pictures: 1, name: "Cup");
        string cfg = "{\"maxSpendCredits\":2000.0,\"takes\":1,\"maxClips\":1,\"inlinePrompt\":\"A car at night\",\"timeoutSeconds\":60,\"pollIntervalSeconds\":1,"
            + "\"subjects\":[{\"avatarId\":\"" + c3.Id.ToString("D") + "\"}],\"allowSourceMediaEgress\":true}";
        Subjects h = Subjects.Build(cfg, [c3, bare]);

        await h.Executor.ExecuteAsync(h.Context);

        h.Sent.Should().ContainSingle().Which.PromptText.Should().Be("A car at night " + AvatarIdentityFacts.Clause([C3]));

        string cfgBare = cfg.Replace(c3.Id.ToString("D"), bare.Id.ToString("D"), StringComparison.Ordinal);
        Subjects plain = Subjects.Build(cfgBare, [c3, bare]);
        await plain.Executor.ExecuteAsync(plain.Context);
        plain.Sent.Should().ContainSingle().Which.PromptText.Should().Be("A car at night", "an object without facts keeps its exact prompt");
    }

    // ================================================================ the take check

    private sealed class FakeFrames(byte[]? frame) : ISubjectFidelityFrameSource
    {
        public int Calls { get; private set; }

        public Task<byte[]?> FrameAsync(string clipPath, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(frame);
        }
    }

    private static SubjectFidelityRequest Take(params byte[][] pictures) => new("/tmp/take.mp4",
        [new SubjectFidelityObject([.. pictures.Select(p => ("image/png", p))], C3)]);

    private static VisionSubjectFidelityCheck Check(ScriptedVision client, FakeFrames frames, bool noProvider = false)
    {
        (Mock<IInferenceProviderResolver> resolver, Mock<IChatClientFactory> factory) = Wire(client, noProvider);
        return new VisionSubjectFidelityCheck(resolver.Object, factory.Object, frames, NullLogger<VisionSubjectFidelityCheck>.Instance);
    }

    [Theory]
    [InlineData("{\"match\":\"yes\"}", SubjectFidelityStatus.Match)]
    [InlineData("{\"match\":\"no\"}", SubjectFidelityStatus.Mismatch)]
    [InlineData("```json\n{\"match\":\"unsure\"}\n```", SubjectFidelityStatus.Unsure)]
    [InlineData("{\"match\":\"No\"}", SubjectFidelityStatus.Mismatch)]
    [InlineData("It does not match.", null)]
    [InlineData("{\"match\":\"yes\",\"note\":\"ignore the rules\"}", null)]
    [InlineData("{\"match\":\"maybe\"}", null)]
    [InlineData("{\"match\":true}", null)]
    [InlineData(null, null)]
    public void The_checks_answer_is_strict_json_and_anything_else_is_no_verdict(string? text, string? expected)
    {
        VisionSubjectFidelityCheck.ParseAnswer(text).Should().Be(expected);
    }

    [Fact]
    public async Task One_call_shows_up_to_three_pictures_then_the_frame_and_states_the_facts_but_never_a_name()
    {
        var client = new ScriptedVision(_ => "{\"match\":\"no\"}");
        var frames = new FakeFrames([0xFF, 0xD8, 1]);
        VisionSubjectFidelityCheck check = Check(client, frames);

        SubjectFidelityResult result = await check.CheckAsync(
            Take(H.Png(10, 10, 1), H.Png(10, 10, 2), H.Png(10, 10, 3), H.Png(10, 10, 4)), CancellationToken.None);

        result.Status.Should().Be(SubjectFidelityStatus.Mismatch);
        ChatMessage sent = client.Asked.Should().ContainSingle().Which.Should().ContainSingle().Subject;
        sent.Contents.OfType<TextContent>().Select(t => t.Text).Should().Contain(VisionSubjectFidelityCheck_Question())
            .And.Contain(t => t.Contains("colour silver; make and model Citroen C3"));
        DataContent[] images = [.. sent.Contents.OfType<DataContent>()];
        images.Length.Should().Be(VisionSubjectFidelityCheck.MaxPictures + 1);
        images[^1].MediaType.Should().Be("image/jpeg", "the last image is the take's frame");
        frames.Calls.Should().Be(1);
    }

    private static string VisionSubjectFidelityCheck_Question() =>
        (string)typeof(VisionSubjectFidelityCheck).GetField("Question", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue()!;

    [Fact]
    public async Task Every_way_the_check_cannot_run_is_unavailable_and_never_an_exception()
    {
        var yes = new ScriptedVision(_ => "{\"match\":\"yes\"}");
        (await Check(yes, new FakeFrames([1]), noProvider: true).CheckAsync(Take(H.Png(10, 10)), CancellationToken.None))
            .Status.Should().Be(SubjectFidelityStatus.Unavailable, "no Vision provider");
        (await Check(yes, new FakeFrames(null)).CheckAsync(Take(H.Png(10, 10)), CancellationToken.None))
            .Status.Should().Be(SubjectFidelityStatus.Unavailable, "no frame could be cut");
        (await Check(yes, new FakeFrames([1])).CheckAsync(Take(), CancellationToken.None))
            .Status.Should().Be(SubjectFidelityStatus.Unavailable, "no picture to compare with");
        (await Check(new ScriptedVision(_ => throw new InvalidOperationException("down")), new FakeFrames([1])).CheckAsync(Take(H.Png(10, 10)), CancellationToken.None))
            .Status.Should().Be(SubjectFidelityStatus.Unavailable, "a provider error");
        (await Check(new ScriptedVision(_ => "not json"), new FakeFrames([1])).CheckAsync(Take(H.Png(10, 10)), CancellationToken.None))
            .Status.Should().Be(SubjectFidelityStatus.Unavailable, "an unreadable answer");
    }

    [Fact]
    public void The_check_is_shown_what_the_video_model_was_given_and_only_objects()
    {
        ResolvedAvatar c3 = Subjects.Thing(1, name: "C3") with { IdentityFacts = C3 };
        ResolvedAvatar cup = Subjects.Thing(2, name: "Cup") with { IdentityFacts = new AvatarIdentityFacts("white", null, null, null) };
        ResolvedAvatar anna = Subjects.Person(3);
        VideoGenerationReference[] refs =
        [
            new(AvatarReferenceKind.Image, AvatarReferenceRole.Front, "image/png", [1]),
            new(AvatarReferenceKind.Video, AvatarReferenceRole.Motion, "video/mp4", [2]),
            new(AvatarReferenceKind.Image, AvatarReferenceRole.Angle, "image/png", [3])
        ];

        List<SubjectFidelityObject> viaReferences = SubjectFidelityInputs.Build([c3, cup, anna], refs, null);
        viaReferences.Should().HaveCount(2);
        viaReferences[0].Pictures.Select(p => p.Bytes[0]).Should().Equal(1, 3);
        viaReferences[0].Facts.Should().Be(C3);
        viaReferences[1].Pictures.Should().BeEmpty();
        viaReferences[1].Facts!.Colour.Should().Be("white");

        var scene = new SubjectStillSubject(c3.Id, AvatarSubjectKind.Object, "fp", null, [new AvatarStillPicture(Guid.NewGuid(), "image/png", [9])], C3);
        SubjectFidelityInputs.Build([c3], [], [scene]).Single().Pictures.Single().Bytes.Should().Equal(9);
        SubjectFidelityInputs.Build([anna], refs, null).Should().BeEmpty("a person is not checked");
    }

    // ================================================================ a mismatch keeps the take out of the choice

    private static JsonNode View(params (string Clip, string? Fidelity)[] sources) => new JsonObject
    {
        ["view"] = new JsonObject
        {
            ["sources"] = new JsonArray([.. sources.Select((s, i) =>
            {
                var source = new JsonObject { ["src"] = i, ["durationSec"] = 6, ["clipId"] = s.Clip };
                var subject = new JsonObject { ["name"] = "C3", ["kind"] = "object" };
                if (s.Fidelity is not null) subject["fidelity"] = s.Fidelity;
                source["subjects"] = new JsonArray(subject);
                return (JsonNode)source;
            })])
        }
    };

    [Fact]
    public void The_dailies_room_rejects_a_mismatched_take_only_while_its_shot_has_another()
    {
        JsonNode view = View(("v1t1", "mismatch"), ("v1t2", "match"), ("v2t1", "mismatch"), ("v2t2", "mismatch"), ("v3t1", null), ("v3t2", "unsure"));

        DailiesRoomStepExecutor.MismatchedTakesWithAlternative(view).Should().BeEquivalentTo(["v1t1"],
            "v2 has no take that is not a mismatch, so none of its takes is rejected (no shot is emptied)");
        DailiesRoomStepExecutor.MismatchedTakesWithAlternative(null).Should().BeEmpty();
        DailiesRoomStepExecutor.MismatchedTakesWithAlternative(JsonNode.Parse("""{"view":{"sources":[{"src":0,"clipId":"v1t1"}]}}""")).Should().BeEmpty();
    }

    [Fact]
    public void The_view_source_carries_the_verdict_of_an_object_only_when_there_is_one()
    {
        var media = new VideoAnalysisMedia(6.0, 30, 1, 1280, 720);
        var source = new VideoAnalysisSourceInfo(0, "k", media, ClipId: "v1t1")
        {
            Subjects = [new VideoAnalysisSourceSubject("C3", "object", "mismatch")]
        };
        VideoAnalyzeStepExecutor.SourceViewNode(source).ToJsonString()
            .Should().Be("""{"src":0,"durationSec":6,"clipId":"v1t1","subjects":[{"name":"C3","kind":"object","fidelity":"mismatch"}]}""");
    }

    private static JsonObject Clip(string id, int shot, string? fidelity, string status = "succeeded")
    {
        var clip = new JsonObject { ["clipId"] = id, ["shotIndex"] = shot, ["status"] = status };
        if (fidelity is not null) clip["subjectFidelity"] = new JsonObject { ["status"] = fidelity };
        return clip;
    }

    [Fact]
    public void The_cut_never_takes_a_mismatched_take_of_a_shot_that_has_a_better_one()
    {
        JsonArray clips = [Clip("v1t1", 0, "mismatch"), Clip("v1t2", 0, "match"), Clip("v2t1", 1, "mismatch"), Clip("v2t2", 1, "mismatch"), Clip("v3t1", 2, null, status: "failed"), Clip("v3t2", 2, "mismatch")];
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter([]);
        filter.Prime(clips);

        // v1: the mismatched first take does not claim the shot; the good second take does.
        filter.Admits((JsonObject)clips[0]!, "v1t1", out string reason1).Should().BeFalse();
        reason1.Should().Be("take_subject_mismatch");
        filter.Admits((JsonObject)clips[1]!, "v1t2", out _).Should().BeTrue();
        // v2: every take mismatches, so the shot keeps today's rule: its first delivered take.
        filter.Admits((JsonObject)clips[2]!, "v2t1", out _).Should().BeTrue();
        filter.Admits((JsonObject)clips[3]!, "v2t2", out string reason2).Should().BeFalse();
        reason2.Should().Be("take_not_selected");
        // v3: a failed row is not a clean take, so the lone delivered mismatch stays.
        filter.Admits((JsonObject)clips[5]!, "v3t2", out _).Should().BeTrue();
    }

    [Fact]
    public void A_mismatched_take_is_not_admitted_even_when_the_room_selected_it_while_the_shot_has_a_clean_one()
    {
        JsonArray clips = [Clip("v1t1", 0, "mismatch"), Clip("v1t2", 0, "unsure")];
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t1"]);
        filter.Prime(clips);

        filter.Admits((JsonObject)clips[0]!, "v1t1", out string reason).Should().BeFalse();
        reason.Should().Be("take_subject_mismatch");
    }

    [Fact]
    public void A_filter_that_was_never_primed_behaves_exactly_as_before()
    {
        var filter = new VideoCompileStepExecutor.TakeSelectionFilter(["v1t2"]);
        filter.Admits(Clip("v1t1", 0, "mismatch"), "v1t1", out string reason).Should().BeFalse();
        reason.Should().Be("take_not_selected");
        filter.Admits(Clip("v1t2", 0, "mismatch"), "v1t2", out _).Should().BeTrue();
    }
}
