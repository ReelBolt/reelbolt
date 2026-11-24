#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Lane L6 of docs/design/avatar-objects.md, the assistant half: <c>ProposeRunPictureTool</c> /
/// <c>ApplyRunPictureTool</c> take <c>subjects</c> (the NAMES of avatars and objects) for <c>generate</c> and
/// <c>instruct</c>. Names are resolved in the picture's project, the token is bound to the avatars they resolved to,
/// the card names each of them from the database, and the tools never allow sending media themselves.
/// </summary>
public class AssistantPictureSubjectToolTests
{
    private sealed class Caller(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Member;
        public bool EmailVerified => true;
    }

    /// <summary>Records the operation the tool would run, instead of calling a vendor.</summary>
    private sealed class RecordingCommands : IImageDocumentCommands
    {
        public List<EditImageOperationRequest> Operations { get; } = [];

        private static Task<ImageCommandOutcome> Ok(string body = "{}") =>
            Task.FromResult(new ImageCommandOutcome(200, JsonDocument.Parse(body).RootElement.Clone()));

        public Task<ImageCommandOutcome> CreateDocumentAsync(Guid projectId, CreateEditImageDocumentRequest request, CancellationToken ct) => Ok();
        public Task<ImageCommandOutcome> CreateTextLayerAsync(Guid projectId, Guid documentId, CreateTextLayerRequest request, CancellationToken ct) => Ok();
        public Task<ImageCommandOutcome> UpdateLayerAsync(Guid projectId, Guid documentId, Guid layerId, UpdateEditImageLayerRequest request, CancellationToken ct) => Ok();
        public Task<ImageCommandOutcome> DeleteLayerAsync(Guid projectId, Guid documentId, Guid layerId, CancellationToken ct) => Ok();
        public Task<ImageCommandOutcome> UpdateDocumentAsync(Guid projectId, Guid documentId, UpdateEditImageDocumentRequest request, CancellationToken ct) => Ok();
        public Task<ImageCommandOutcome> SegmentSubjectAsync(Guid projectId, Guid documentId, SegmentSubjectRequest request, CancellationToken ct) => Ok("[]");

        public Task<ImageCommandOutcome> RunOperationAsync(Guid projectId, Guid documentId, EditImageOperationRequest request, CancellationToken ct)
        {
            Operations.Add(request);
            return Ok("{\"layers\":[],\"operation\":\"" + request.Operation + "\",\"provider\":{\"kind\":\"FalAi\",\"model\":\"m\",\"capability\":\"ImageEdit\"},\"revision\":2}");
        }

        public Task<string> StoreMaskAsync(Guid projectId, Guid organizationId, Guid documentId, byte[] png, CancellationToken ct) =>
            Task.FromResult("mask");
    }

    private sealed class World : IDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Guid Me { get; } = Guid.NewGuid();
        public Caller User { get; }
        public Project Project { get; }
        public EditImageDocument Document { get; }
        public EditImageLayer Source { get; }
        public FakeFileStorage Storage { get; } = new();
        public RecordingCommands Commands { get; } = new();
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public AvatarOptions Avatars { get; } = new() { Enabled = true, Objects = new AvatarObjectOptions { Enabled = true } };
        public IConfirmationTokenService Tokens { get; } = ConfirmationTokenService.CreateEphemeral();

        public World(bool consent = true)
        {
            User = new Caller(Me);
            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Coffee launch",
                OwnerId = Me,
                OrganizationId = TestTenancy.OrgOf(Me),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                SubjectMediaConsentAt = consent ? DateTime.UtcNow : null
            };
            ProjectFile file = new() { Id = Guid.NewGuid(), ProjectId = Project.Id, OriginalFileName = "cover.png", MimeType = "image/png", StorageKey = $"projects/{Project.Id}/userFiles/cover.png", Category = "userFiles" };
            Document = new EditImageDocument { Id = Guid.NewGuid(), ProjectId = Project.Id, SourceProjectFileId = file.Id, CanvasWidth = 1280, CanvasHeight = 720, Revision = 1 };
            Source = new EditImageLayer { Id = Guid.NewGuid(), DocumentId = Document.Id, ZOrder = 1, Kind = EditImageLayerKind.Source, Role = "background", Label = "cover.png", StorageKey = file.StorageKey, Width = 1280, Height = 720, TransformJson = "{}" };
            Db.Projects.Add(Project);
            Db.ProjectFiles.Add(file);
            Db.EditImageDocuments.Add(Document);
            Db.EditImageLayers.Add(Source);
            Db.SaveChanges();
            Db.ChangeTracker.Clear();

            Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedImageProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi,
                    InferenceProviderCapability.ImageEdit, "https://queue.fal.run", "", "k", 60));
        }

        public Guid AddAvatar(string name, AvatarSubjectKind kind, Action<ProjectAvatar>? tweak = null)
        {
            bool isObject = kind == AvatarSubjectKind.Object;
            byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Guid.NewGuid().ToByteArray()];
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = name + ".png",
                OriginalPath = name + ".png",
                MimeType = "image/png",
                StorageKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid():N}.png",
                StorageBucket = "fake-bucket",
                Category = "userFiles",
                SizeBytes = bytes.Length,
                CsamStatus = CsamScanStatus.Clean,
                ModerationStatus = FileModerationStatus.Clean
            };
            Storage.Objects[file.StorageKey] = bytes;
            ProjectAvatar avatar = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                SubjectKind = kind,
                Name = name,
                ConsentAttestedByUserId = Me,
                ConsentAttestedAt = DateTime.UtcNow,
                ConsentStatement = isObject ? AvatarObjectStatement.English : AvatarConsentStatement.English,
                ConsentStatementVersion = isObject ? AvatarObjectStatement.Version : AvatarConsentStatement.Version,
                ConsentLocale = "en",
                ConsentSubjectName = isObject ? string.Empty : name,
                ConsentSubjectAdult = !isObject,
                RetentionUntil = isObject ? ProjectAvatar.NoRetention : DateTime.UtcNow.AddDays(90),
                DisclosureLabel = isObject ? AvatarDisclosure.MetadataOnly : AvatarDisclosure.Visible,
                CreatedByUserId = Me
            };
            tweak?.Invoke(avatar);
            Db.ProjectFiles.Add(file);
            Db.ProjectAvatars.Add(avatar);
            Db.ProjectAvatarReferences.Add(new ProjectAvatarReference
            {
                Id = Guid.NewGuid(),
                AvatarId = avatar.Id,
                ProjectFileId = file.Id,
                Kind = AvatarReferenceKind.Image,
                Role = isObject ? AvatarReferenceRole.Front : AvatarReferenceRole.Face,
                Ordinal = 0,
                ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            });
            Db.SaveChanges();
            Db.ChangeTracker.Clear();
            return avatar.Id;
        }

        public AssistantPictureTools Tools() => AssistantToolTestFactory.PictureTools(
            Db, User, Tokens, Storage, Commands, Resolver.Object,
            subjectReader: new ProjectSubjectReader(Db, Storage, Microsoft.Extensions.Options.Options.Create(Avatars), NullLogger<ProjectSubjectReader>.Instance),
            avatarOptions: Avatars);

        public AssistantApprovalCardBuilder Cards() => new(Db, TestTenancy.Access(Db, User), User);

        public AssistantToolContext Context() => new(Me, Project.Id, null, Document.Id);

        public void Dispose() => Db.Dispose();
    }

    private static async Task<JsonElement> Invoke(AssistantPictureTools tools, AssistantToolContext context, string name, params (string Key, object? Value)[] args)
    {
        AIFunction tool = tools.GetTools(context).Single(t => t.Name == name);
        AIFunctionArguments arguments = new();
        foreach ((string key, object? value) in args) arguments[key] = value;
        object? raw = await tool.InvokeAsync(arguments);
        string text = raw switch
        {
            JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
            JsonElement e => e.GetRawText(),
            _ => raw!.ToString()!
        };
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string? ErrorOf(JsonElement result) =>
        result.TryGetProperty("error", out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string Token(JsonElement proposal) => proposal.GetProperty("confirmationToken").GetString()!;

    // ------------------------------------------------------------------ propose and apply

    [Fact]
    public async Task Generate_with_subjects_is_proposed_on_the_picture_edit_model_and_runs_with_the_resolved_avatar_ids()
    {
        using World w = new();
        Guid anna = w.AddAvatar("Anna", AvatarSubjectKind.Person);
        Guid machine = w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        AssistantPictureTools tools = w.Tools();
        string[] subjects = ["anna", "Espresso machine"]; // a name is matched without regard to case

        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "at a cafe counter"), ("subjects", subjects));

        ErrorOf(proposal).Should().BeNull();
        proposal.GetProperty("summary").GetString().Should()
            .Contain("It shows \"Anna\" (avatar), \"Espresso machine\" (object), and sends their pictures to the picture service.");
        w.Resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        w.Resolver.Verify(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageGeneration, null, It.IsAny<CancellationToken>()), Times.Never,
            "a new picture with subjects is made, and priced, by the picture-edit model");
        w.Commands.Operations.Should().BeEmpty("a proposal runs nothing");

        JsonElement applied = await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "generate"), ("prompt", "at a cafe counter"), ("subjects", subjects), ("confirmationToken", Token(proposal)));

        ErrorOf(applied).Should().BeNull();
        applied.GetProperty("summary").GetString().Should().Be("ran generate, showing \"Anna\" (avatar), \"Espresso machine\" (object)");
        EditImageOperationRequest run = w.Commands.Operations.Should().ContainSingle().Subject;
        run.Operation.Should().Be("generate");
        run.SubjectIds.Should().Equal(anna, machine);
        run.AllowMediaEgress.Should().BeNull("the assistant never allows sending media itself");
    }

    [Fact]
    public async Task Instruct_takes_subjects_and_every_other_tool_refuses_them()
    {
        using World w = new();
        Guid machine = w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        AssistantPictureTools tools = w.Tools();
        string[] subjects = ["Espresso machine"];

        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "instruct"), ("layerId", w.Source.Id), ("prompt", "put the machine on the table"), ("subjects", subjects));
        ErrorOf(proposal).Should().BeNull();
        await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "instruct"), ("layerId", w.Source.Id), ("prompt", "put the machine on the table"), ("subjects", subjects),
            ("confirmationToken", Token(proposal)));
        w.Commands.Operations.Single().SubjectIds.Should().Equal(machine);

        JsonElement refused = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "relight"), ("layerId", w.Source.Id), ("prompt", "warm light"), ("subjects", subjects));
        ErrorOf(refused).Should().Be("relight cannot show an avatar or object. Only generate (a new picture) and instruct (an edit) take subjects.");
        refused.TryGetProperty("confirmationToken", out _).Should().BeFalse();
    }

    [Fact]
    public async Task The_token_is_bound_to_the_avatars_the_names_resolved_to()
    {
        using World w = new();
        Guid anna = w.AddAvatar("Anna", AvatarSubjectKind.Person);
        w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        AssistantPictureTools tools = w.Tools();
        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Anna" }));

        // other subjects, or none, with the same token
        ErrorOf(await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine" }), ("confirmationToken", Token(proposal))))
            .Should().Be(AssistantPictureTools.StaleTokenMessage);
        ErrorOf(await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("confirmationToken", Token(proposal))))
            .Should().Be(AssistantPictureTools.StaleTokenMessage);

        // the same NAME now means another avatar: the old one was withdrawn and a new "Anna" was made
        ProjectAvatar old = await w.Db.ProjectAvatars.SingleAsync(a => a.Id == anna);
        old.ConsentRevokedAt = DateTime.UtcNow;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        w.AddAvatar("Anna", AvatarSubjectKind.Person);

        ErrorOf(await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Anna" }), ("confirmationToken", Token(proposal))))
            .Should().Be(AssistantPictureTools.StaleTokenMessage);
        w.Commands.Operations.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task An_unknown_or_ambiguous_name_is_a_plain_error_that_lists_the_names_that_can_be_used()
    {
        using World w = new();
        w.AddAvatar("Anna", AvatarSubjectKind.Person);
        w.AddAvatar("anna", AvatarSubjectKind.Object);
        w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        AssistantPictureTools tools = w.Tools();

        JsonElement unknown = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Bruno" }));
        ErrorOf(unknown).Should().StartWith("no avatar or object named \"Bruno\" is in this project. Names that can be used: ")
            .And.Contain("\"Anna\" (person)").And.Contain("\"Espresso machine\" (object)");
        unknown.TryGetProperty("confirmationToken", out _).Should().BeFalse();

        ErrorOf(await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "ANNA" }))).Should().Contain("could mean more than one");

        ErrorOf(await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine", "espresso machine" })))
            .Should().Contain("named twice");

        // a kind that is switched off is not offered and not found
        w.Avatars.Objects.Enabled = false;
        ErrorOf(await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine" })))
            .Should().StartWith("no avatar or object named \"Espresso machine\"").And.NotContain("(object)");
    }

    [Fact]
    public async Task Without_the_projects_recorded_permission_or_the_word_for_this_call_the_tool_refuses()
    {
        using World w = new(consent: false);
        w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        AssistantPictureTools tools = w.Tools();
        const string refusal = AssistantPictureTools.NoMediaAgreementMessage;

        JsonElement proposal = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine" }));
        ErrorOf(proposal).Should().Be(refusal);
        proposal.TryGetProperty("confirmationToken", out _).Should().BeFalse();

        // The permission is re-read at apply: withdrawn after the proposal, the run does not start.
        Project project = await w.Db.Projects.SingleAsync(p => p.Id == w.Project.Id);
        project.SubjectMediaConsentAt = DateTime.UtcNow;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        JsonElement allowed = await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine" }));
        ErrorOf(allowed).Should().BeNull();

        project = await w.Db.Projects.SingleAsync(p => p.Id == w.Project.Id);
        project.SubjectMediaConsentAt = null;
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        ErrorOf(await Invoke(tools, w.Context(), "ApplyRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine" }), ("confirmationToken", Token(allowed))))
            .Should().Be(refusal);
        w.Commands.Operations.Should().BeEmpty();
    }

    [Fact]
    public async Task An_avatar_that_cannot_be_used_in_a_picture_is_refused_at_the_proposal_with_the_rules_words()
    {
        using World w = new();
        w.AddAvatar("Anna", AvatarSubjectKind.Person, a => a.ConsentRevokedAt = DateTime.UtcNow);
        w.AddAvatar("Bruno", AvatarSubjectKind.Person, a => a.ConsentStatementVersion = AvatarConsentStatement.Version2026_11);
        w.AddAvatar("Carla", AvatarSubjectKind.Person);
        AssistantPictureTools tools = w.Tools();

        ErrorOf(await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Anna" })))
            .Should().Be("\"Anna\" cannot be used in a picture right now: consent for this avatar was withdrawn.");
        ErrorOf(await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Bruno" })))
            .Should().Be("Confirm this person's consent again in the Avatars tab before using them in a picture.");
        ErrorOf(await Invoke(tools, w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Bruno", "Carla" })))
            .Should().Contain("At most one person");
    }

    [Fact]
    public async Task A_picture_edit_model_that_takes_one_picture_is_refused_at_the_proposal()
    {
        using World w = new();
        w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedImageProvider(Guid.NewGuid(), "fal", InferenceProviderKind.FalAi,
                InferenceProviderCapability.ImageEdit, "https://queue.fal.run", "bria/fibo-edit/edit", "k", 60));

        JsonElement proposal = await Invoke(w.Tools(), w.Context(), "ProposeRunPictureTool",
            ("tool", "generate"), ("prompt", "a scene"), ("subjects", new[] { "Espresso machine" }));

        ErrorOf(proposal).Should().Contain("takes one picture at a time").And.Contain("google/nano-banana-2.1/edit");
    }

    // ------------------------------------------------------------------ the card, the schema, the worst case

    [Fact]
    public async Task The_card_adds_one_line_per_subject_from_the_database_and_refuses_a_name_it_cannot_place()
    {
        using World w = new();
        w.AddAvatar("Anna", AvatarSubjectKind.Person);
        w.AddAvatar("Espresso machine", AvatarSubjectKind.Object);
        AIFunctionArguments args = new()
        {
            ["tool"] = "generate",
            ["prompt"] = "a scene",
            ["subjects"] = JsonSerializer.SerializeToElement(new[] { "anna", "Espresso machine" }),
            ["confirmationToken"] = "t"
        };

        ApprovalVerdict verdict = await w.Cards().BuildAsync("ApplyRunPictureTool", args, w.Context(), null, CancellationToken.None);

        verdict.Card.Should().NotBeNull();
        // The card's own quoting (typographic quotes, as on every other card), by code point.
        string open = ((char)0x201C).ToString();
        string close = ((char)0x201D).ToString();
        verdict.Card!.Changes!.Select(c => c.Text).Should().Equal(
            $"Shows: {open}Anna{close} (avatar). Sends that person's pictures to the picture service.",
            $"Shows: {open}Espresso machine{close} (object). Sends its pictures to the picture service.");
        verdict.Card.Changes!.Should().OnlyContain(c => c.Kind == AssistantChangeKind.Info);

        args["subjects"] = JsonSerializer.SerializeToElement(new[] { "Somebody else" });
        ApprovalVerdict unplaced = await w.Cards().BuildAsync("ApplyRunPictureTool", args, w.Context(), null, CancellationToken.None);
        unplaced.Card.Should().BeNull("a call the builder cannot place is refused, never run");
        unplaced.Refusal.Should().Contain("no avatar or object named \"Somebody else\"");

        args["subjects"] = JsonSerializer.SerializeToElement("Anna");
        (await w.Cards().BuildAsync("ApplyRunPictureTool", args, w.Context(), null, CancellationToken.None)).Card.Should().BeNull();

        // a call without subjects keeps the card it always had
        args.Remove("subjects");
        ApprovalVerdict plain = await w.Cards().BuildAsync("ApplyRunPictureTool", args, w.Context(), null, CancellationToken.None);
        plain.Card!.Changes.Should().BeNull();
    }

    [Fact]
    public void Both_tools_declare_the_subjects_argument_and_the_tool_set_is_unchanged()
    {
        using World w = new();
        IReadOnlyList<AIFunction> tools = w.Tools().GetTools(w.Context());

        tools.Should().HaveCount(15, "the picture surface gained an argument, not a tool");
        foreach (string name in new[] { "ProposeRunPictureTool", "ApplyRunPictureTool" })
        {
            AIFunction tool = tools.Single(t => t.Name == name);
            tool.JsonSchema.GetProperty("properties").TryGetProperty("subjects", out JsonElement subjects).Should().BeTrue();
            tool.JsonSchema.GetProperty("properties").TryGetProperty("allowMediaEgress", out _).Should().BeTrue();
            subjects.GetRawText().Should().Contain("string");
        }

        tools.Single(t => t.Name == "ProposeRunPictureTool").Description.Should()
            .Contain("subjects is the NAMES of one to three avatars or objects").And.Contain("allowMediaEgress is optional, with subjects only");
    }

    [Fact]
    public async Task The_turn_guard_claims_the_picture_edit_models_worst_case_for_a_generate_with_subjects()
    {
        using World w = new();
        w.Resolver.Setup(r => r.ResolveImageJobAsync(InferenceProviderCapability.ImageGeneration, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedImageProvider(Guid.NewGuid(), "gen", InferenceProviderKind.FalAi,
                InferenceProviderCapability.ImageGeneration, "https://queue.fal.run", "", "k", 60));
        AssistantPictureTools tools = w.Tools();

        IReadOnlyList<ReelBolt.Shared.Metering.PriceRequest>? plain = await tools.RunPictureToolWorstCaseAsync(
            new Dictionary<string, object?> { ["tool"] = "generate" }, w.Context(), CancellationToken.None);
        plain!.Should().OnlyContain(r => r.Capability == InferenceProviderCapability.ImageGeneration);

        IReadOnlyList<ReelBolt.Shared.Metering.PriceRequest>? withSubjects = await tools.RunPictureToolWorstCaseAsync(
            new Dictionary<string, object?> { ["tool"] = "generate", ["subjects"] = JsonSerializer.SerializeToElement(new[] { "Anna" }) },
            w.Context(), CancellationToken.None);
        withSubjects!.Should().NotBeEmpty().And.OnlyContain(r => r.Capability == InferenceProviderCapability.ImageEdit);
    }
}
