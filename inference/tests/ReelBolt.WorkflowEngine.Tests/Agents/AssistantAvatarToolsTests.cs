#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// <c>ListProjectAvatars</c> (docs/avatars.md "Assistant and MCP"): the kill switch answers a plain result, the rule's
/// reasons come back in plain words, the bytes of a held reference are never read, nothing id-shaped, no storage key and
/// no subject name reaches the result, a foreign project is refused, and the tool is free, read-only and closed-world.
/// </summary>
public class AssistantAvatarToolsTests
{
    private static readonly byte[] FaceBytes = Encoding.UTF8.GetBytes("face-of-anna");

    private sealed class Caller(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email => "test@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool EmailVerified => true;
    }

    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; }
        public Guid User { get; } = Guid.NewGuid();
        public Project Project { get; }
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];

        public Rig()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase($"avatar-tools-{Guid.NewGuid()}").Options);
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch Teaser", OwnerId = User, OrganizationId = TestTenancy.OrgOf(User), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.SaveChanges();
        }

        public ProjectFile AddFile(string name, string mime, byte[]? bytes)
        {
            ProjectFile file = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = name,
                MimeType = mime,
                StorageKey = $"projects/{Project.Id}/userFiles/{Guid.NewGuid():N}-{name}",
                CsamStatus = CsamScanStatus.Clean,
                UploadedAt = DateTime.UtcNow.AddDays(-2),
            };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            if (bytes is not null)
                Objects[file.StorageKey] = bytes;
            return file;
        }

        public ProjectAvatar AddAvatar(string name, Action<ProjectAvatar>? change = null, params (ProjectFile File, AvatarReferenceKind Kind, AvatarReferenceRole Role, byte[] Attested)[] refs)
        {
            ProjectAvatar avatar = new()
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                Name = name,
                Description = "Presenter for the launch",
                ConsentAttestedByUserId = User,
                ConsentAttestedAt = DateTime.UtcNow.AddDays(-1),
                ConsentStatement = "I confirm ...",
                ConsentStatementVersion = "avatar-consent-2026-11",
                ConsentLocale = "en",
                ConsentSubjectName = "Anna Secret-Surname",
                ConsentBasis = AvatarConsentBasis.Self,
                ConsentSubjectAdult = true,
                RetentionUntil = new DateTime(2027, 11, 4, 0, 0, 0, DateTimeKind.Utc),
                CreatedByUserId = User,
            };
            change?.Invoke(avatar);
            Db.ProjectAvatars.Add(avatar);
            int ordinal = 0;
            foreach ((ProjectFile file, AvatarReferenceKind kind, AvatarReferenceRole role, byte[] attested) in refs)
            {
                Db.ProjectAvatarReferences.Add(new ProjectAvatarReference
                {
                    Id = Guid.NewGuid(),
                    AvatarId = avatar.Id,
                    ProjectFileId = file.Id,
                    Kind = kind,
                    Role = role,
                    Ordinal = ordinal++,
                    ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(attested)),
                });
            }
            Db.SaveChanges();
            return avatar;
        }

        /// <summary>The objects switch (<c>Avatars:Objects:Enabled</c>); off unless a test turns it on.</summary>
        public bool ObjectsEnabled { get; set; }

        /// <summary>A raw value for <c>Avatars:Enabled</c> that replaces the true/false derived from <c>enabled</c> (for an unreadable switch).</summary>
        public string? RawPeopleSwitch { get; set; }

        public AssistantAvatarTools Tools(bool enabled, Guid? user = null)
        {
            Caller caller = new(user ?? User);
            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((Guid _, string key, CancellationToken _) =>
                {
                    Reads.Add(key);
                    return Objects.TryGetValue(key, out byte[]? bytes)
                        ? Task.FromResult<Stream>(new MemoryStream(bytes))
                        : throw new FileNotFoundException("gone");
                });
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Avatars:Enabled"] = RawPeopleSwitch ?? (enabled ? "true" : "false"),
                    ["Avatars:Objects:Enabled"] = ObjectsEnabled ? "true" : "false"
                })
                .Build();
            return new AssistantAvatarTools(Db, TestTenancy.Access(Db, caller), storage.Object, configuration, NullLogger<AssistantAvatarTools>.Instance);
        }

        public async Task<(JsonElement Json, string Raw)> CallAsync(bool enabled = true, Guid? user = null, Guid? projectId = null)
        {
            Guid who = user ?? User;
            AIFunction fn = Tools(enabled, who).GetTools(new AssistantToolContext(who, Project.Id)).Single(t => t.Name == "ListProjectAvatars");
            Dictionary<string, object?> args = [];
            if (projectId is not null) args["projectId"] = projectId;
            object? raw = await fn.InvokeAsync(new AIFunctionArguments(args));
            string text = raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw is JsonElement e ? e.GetRawText() : raw!.ToString()!;
            return (JsonDocument.Parse(text).RootElement.Clone(), text);
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task With_the_kill_switch_off_it_answers_a_plain_not_available_result_and_reads_nothing()
    {
        using Rig rig = new();
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        rig.AddAvatar("Anna", null, (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes));

        (JsonElement result, _) = await rig.CallAsync(enabled: false);

        result.GetProperty("available").GetBoolean().Should().BeFalse();
        result.GetProperty("message").GetString().Should().Be("Avatars are not available in this workspace.");
        result.TryGetProperty("error", out _).Should().BeFalse("a switched-off feature is a result, not an error");
        result.TryGetProperty("avatars", out _).Should().BeFalse();
        rig.Reads.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ objects (docs/design/avatar-objects.md)

    private static void MakeObject(ProjectAvatar avatar)
    {
        avatar.SubjectKind = AvatarSubjectKind.Object;
        avatar.Description = "Chrome, two groups";
        avatar.ConsentStatement = ReelBolt.Shared.Avatars.AvatarObjectStatement.English;
        avatar.ConsentStatementVersion = ReelBolt.Shared.Avatars.AvatarObjectStatement.Version;
        avatar.ConsentSubjectName = string.Empty;
        avatar.ConsentSubjectAdult = false;
        avatar.RetentionUntil = ProjectAvatar.NoRetention;
        avatar.DisclosureLabel = AvatarDisclosure.MetadataOnly;
    }

    [Fact]
    public async Task It_lists_people_and_objects_with_their_kind_and_whether_each_covers_pictures()
    {
        using Rig rig = new() { ObjectsEnabled = true };
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        rig.AddAvatar("Anna", null, (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes));
        ProjectFile bea = rig.AddFile("bea.jpg", "image/jpeg", Encoding.UTF8.GetBytes("bea"));
        rig.AddAvatar("Bea", a => a.ConsentStatementVersion = "avatar-consent-2026-12",
            (bea, AvatarReferenceKind.Image, AvatarReferenceRole.Face, Encoding.UTF8.GetBytes("bea")));
        ProjectFile machine = rig.AddFile("machine.jpg", "image/jpeg", Encoding.UTF8.GetBytes("machine"));
        rig.AddAvatar("Machine", MakeObject, (machine, AvatarReferenceKind.Image, AvatarReferenceRole.Front, Encoding.UTF8.GetBytes("machine")));
        ProjectFile lamp = rig.AddFile("lamp.jpg", "image/jpeg", Encoding.UTF8.GetBytes("changed"));
        rig.AddAvatar("Lamp", MakeObject, (lamp, AvatarReferenceKind.Image, AvatarReferenceRole.Front, Encoding.UTF8.GetBytes("original")));

        (JsonElement result, string raw) = await rig.CallAsync();

        result.GetProperty("available").GetBoolean().Should().BeTrue();
        result.GetProperty("mediaConsent").GetBoolean().Should().BeFalse();
        JsonElement[] avatars = [.. result.GetProperty("avatars").EnumerateArray()];
        avatars.Select(a => (a.GetProperty("name").GetString(), a.GetProperty("kind").GetString(), a.GetProperty("coversPictures").GetBoolean()))
            .Should().Equal(("Anna", "person", false), ("Bea", "person", true), ("Lamp", "object", true), ("Machine", "object", true));

        JsonElement theMachine = avatars[3];
        theMachine.GetProperty("selectable").GetBoolean().Should().BeTrue();
        theMachine.GetProperty("description").GetString().Should().Be("Chrome, two groups");
        theMachine.GetProperty("keepUntil").ValueKind.Should().Be(JsonValueKind.Null, "an object has no keep-until date");
        theMachine.GetProperty("voice").ValueKind.Should().Be(JsonValueKind.Null);
        theMachine.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("Front");
        avatars[2].GetProperty("selectable").GetBoolean().Should().BeFalse();
        avatars[2].GetProperty("unselectableReason").GetString().Should().Be("A picture or video of it is missing or was changed after it was added.");
        raw.Should().NotContain("9999").And.NotContain(rig.Project.Id.ToString());
    }

    [Theory]
    [InlineData(true, false, "Anna")]
    [InlineData(false, true, "Machine")]
    [InlineData(true, true, "Anna,Machine")]
    public async Task It_lists_only_the_kinds_whose_switch_is_on(bool people, bool objects, string expected)
    {
        using Rig rig = new() { ObjectsEnabled = objects };
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        rig.AddAvatar("Anna", null, (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes));
        ProjectFile machine = rig.AddFile("machine.jpg", "image/jpeg", Encoding.UTF8.GetBytes("machine"));
        rig.AddAvatar("Machine", MakeObject, (machine, AvatarReferenceKind.Image, AvatarReferenceRole.Front, Encoding.UTF8.GetBytes("machine")));

        (JsonElement result, _) = await rig.CallAsync(enabled: people);

        result.GetProperty("available").GetBoolean().Should().BeTrue("one switch is enough for the tool to answer");
        result.GetProperty("avatars").EnumerateArray().Select(a => a.GetProperty("name").GetString()).Should().Equal(expected.Split(','));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("tru e")]
    public async Task An_unreadable_people_switch_reads_as_off(string raw)
    {
        using Rig rig = new() { ObjectsEnabled = false, RawPeopleSwitch = raw };

        (JsonElement result, _) = await rig.CallAsync(enabled: true);

        result.GetProperty("available").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task With_both_switches_off_it_is_not_available()
    {
        using Rig rig = new() { ObjectsEnabled = false };

        (JsonElement result, _) = await rig.CallAsync(enabled: false);

        result.GetProperty("available").GetBoolean().Should().BeFalse();
        result.TryGetProperty("mediaConsent", out _).Should().BeFalse();
    }

    [Fact]
    public async Task It_reports_the_media_consent_it_can_never_give()
    {
        using Rig rig = new() { ObjectsEnabled = true };
        rig.Project.SubjectMediaConsentAt = DateTime.UtcNow;
        rig.Project.SubjectMediaConsentByUserId = rig.User;
        rig.Db.SaveChanges();

        (JsonElement result, _) = await rig.CallAsync();

        result.GetProperty("mediaConsent").GetBoolean().Should().BeTrue();
        string[] tools = [.. rig.Tools(enabled: true).GetTools(new AssistantToolContext(rig.User, rig.Project.Id)).Select(t => t.Name)];
        tools.Should().NotContain(n => n.Contains("Consent") || n.Contains("Revoke") || n.Contains("Delete") || n.Contains("Reattest") || n.Contains("Publish"),
            "there is no tool that records the consent, revokes, deletes, re-attests or publishes to the library");
        tools.Should().NotContain(n => n.Contains("Person"), "no tool makes a person's avatar");
    }

    [Fact]
    public void Every_reason_an_object_can_have_has_plain_words_of_its_own()
    {
        AssistantAvatarTools.ObjectReasonWords.Keys.Should().BeEquivalentTo(
        [
            ProjectAvatar.ReasonConsentMissing, ProjectAvatar.ReasonReferenceMissing,
            ProjectAvatar.ReasonReferenceHeld, ProjectAvatar.ReasonNoImageReference
        ]);
        AssistantAvatarTools.ObjectReasonWords.Values.Should().OnlyContain(words => !words.Contains("person's consent") && !words.Contains("withdrawn"));
    }

    [Fact]
    public void The_tool_is_registered_whether_or_not_the_switch_is_on_so_the_counts_stay_fixed()
    {
        using Rig rig = new();

        rig.Tools(enabled: false).GetTools(new AssistantToolContext(rig.User, rig.Project.Id)).Select(t => t.Name)
            .Should().Equal(["ListProjectAvatars", "ListAvatarLibrary",
                "ProposeCreateObjectAvatar", "ApplyCreateObjectAvatar", "ProposeExtractObjectFromVideo", "ApplyExtractObjectFromVideo",
                "ProposeCreateAvatarVariant", "ApplyCreateAvatarVariant", "ProposeImportLibraryAvatar", "ApplyImportLibraryAvatar",
                "ProposeGenerateSubjectClip", "ApplyGenerateSubjectClip"]);
        rig.Tools(enabled: true).GetTools(new AssistantToolContext(rig.User, rig.Project.Id)).Select(t => t.Name)
            .Should().Equal(["ListProjectAvatars", "ListAvatarLibrary",
                "ProposeCreateObjectAvatar", "ApplyCreateObjectAvatar", "ProposeExtractObjectFromVideo", "ApplyExtractObjectFromVideo",
                "ProposeCreateAvatarVariant", "ApplyCreateAvatarVariant", "ProposeImportLibraryAvatar", "ApplyImportLibraryAvatar",
                "ProposeGenerateSubjectClip", "ApplyGenerateSubjectClip"]);
    }

    [Fact]
    public async Task It_lists_each_avatar_with_the_rule_answer_in_plain_words_counts_roles_voice_and_keep_until()
    {
        using Rig rig = new();
        ProjectVoice voice = new() { Id = Guid.NewGuid(), ProjectId = rig.Project.Id, Name = "Anna's voice", Source = ProjectVoiceSource.Library, RemoteVoiceId = "fish-anna", CreatedAt = DateTime.UtcNow };
        rig.Db.ProjectVoices.Add(voice);
        rig.Db.SaveChanges();
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        ProjectFile motion = rig.AddFile("anna.mp4", "video/mp4", Encoding.UTF8.GetBytes("motion"));
        rig.AddAvatar("Anna", a => a.ProjectVoiceId = voice.Id,
            (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes),
            (motion, AvatarReferenceKind.Video, AvatarReferenceRole.Motion, Encoding.UTF8.GetBytes("motion")));
        ProjectFile bruno = rig.AddFile("bruno.jpg", "image/jpeg", Encoding.UTF8.GetBytes("bruno"));
        rig.AddAvatar("Bruno", a => a.ConsentRevokedAt = DateTime.UtcNow.AddHours(-1),
            (bruno, AvatarReferenceKind.Image, AvatarReferenceRole.Face, Encoding.UTF8.GetBytes("bruno")));
        ProjectFile carla = rig.AddFile("carla.jpg", "image/jpeg", Encoding.UTF8.GetBytes("replaced bytes"));
        rig.AddAvatar("Carla", null, (carla, AvatarReferenceKind.Image, AvatarReferenceRole.Face, Encoding.UTF8.GetBytes("original bytes")));

        (JsonElement result, _) = await rig.CallAsync();

        result.GetProperty("available").GetBoolean().Should().BeTrue();
        result.GetProperty("projectName").GetString().Should().Be("Launch Teaser");
        JsonElement[] avatars = [.. result.GetProperty("avatars").EnumerateArray()];
        avatars.Select(a => a.GetProperty("name").GetString()).Should().Equal("Anna", "Bruno", "Carla");

        JsonElement anna = avatars[0];
        anna.GetProperty("selectable").GetBoolean().Should().BeTrue();
        anna.GetProperty("unselectableReason").ValueKind.Should().Be(JsonValueKind.Null);
        anna.GetProperty("references").GetProperty("images").GetInt32().Should().Be(1);
        anna.GetProperty("references").GetProperty("videos").GetInt32().Should().Be(1);
        anna.GetProperty("references").GetProperty("audio").GetInt32().Should().Be(0);
        anna.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).Should().Equal("Face", "Motion");
        anna.GetProperty("voice").GetString().Should().Be("Anna's voice");
        anna.GetProperty("keepUntil").GetString().Should().Be("2027-11-04");

        avatars[1].GetProperty("selectable").GetBoolean().Should().BeFalse();
        avatars[1].GetProperty("unselectableReason").GetString().Should().Be("Consent was withdrawn.");
        avatars[2].GetProperty("selectable").GetBoolean().Should().BeFalse();
        avatars[2].GetProperty("unselectableReason").GetString()
            .Should().Be(AssistantAvatarTools.ReasonWords[ProjectAvatar.ReasonReferenceMissing]);
        result.GetProperty("note").GetString().Should().Contain("Avatars tab");
        rig.Reads.Should().NotContain(bruno.StorageKey, "a revoked avatar's media is never read");
    }

    [Fact]
    public async Task A_quarantined_reference_is_reported_held_and_its_bytes_are_never_read()
    {
        using Rig rig = new();
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        face.ModerationStatus = FileModerationStatus.Quarantined;
        rig.Db.SaveChanges();
        rig.AddAvatar("Anna", null, (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes));

        (JsonElement result, _) = await rig.CallAsync();

        JsonElement anna = result.GetProperty("avatars")[0];
        anna.GetProperty("selectable").GetBoolean().Should().BeFalse();
        anna.GetProperty("unselectableReason").GetString().Should().Be(AssistantAvatarTools.ReasonWords[ProjectAvatar.ReasonReferenceHeld]);
        rig.Reads.Should().BeEmpty();
    }

    [Fact]
    public async Task The_result_carries_no_id_no_storage_key_and_not_the_consent_subjects_name()
    {
        using Rig rig = new();
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        ProjectAvatar avatar = rig.AddAvatar("Anna", null, (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes));

        (_, string raw) = await rig.CallAsync();

        raw.Should().NotContain(avatar.Id.ToString()).And.NotContain(face.Id.ToString()).And.NotContain(rig.Project.Id.ToString())
            .And.NotContain(face.StorageKey).And.NotContain("Secret-Surname").And.NotContain("projects/");
        System.Text.RegularExpressions.Regex.IsMatch(raw, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-").Should().BeFalse();
        raw.Should().NotContain(Convert.ToHexStringLower(SHA256.HashData(FaceBytes)), "digests stay server-side");
    }

    [Fact]
    public async Task Every_rule_reason_has_plain_words()
    {
        foreach (string reason in ProjectAvatar.UnselectableReasons)
            AssistantAvatarTools.ReasonWords.Should().ContainKey(reason);
        AssistantAvatarTools.ReasonWords.Values.Should().OnlyContain(w => !w.Contains('_'));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_project_the_caller_cannot_see_is_refused()
    {
        using Rig rig = new();

        (JsonElement result, _) = await rig.CallAsync(user: Guid.NewGuid());

        result.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        result.TryGetProperty("avatars", out _).Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_project_says_how_avatars_are_made()
    {
        using Rig rig = new();

        (JsonElement result, _) = await rig.CallAsync();

        result.GetProperty("avatars").GetArrayLength().Should().Be(0);
        result.GetProperty("note").GetString().Should().Contain("no avatars yet").And.Contain("Avatars tab");
    }

    // ------------------------------------------------------------------ variants and builds (docs/design/avatar-objects.md)

    [Fact]
    public async Task A_variant_is_listed_with_its_originals_name_its_label_and_what_sets_it_apart()
    {
        using Rig rig = new() { ObjectsEnabled = true };
        byte[] bytes = Encoding.UTF8.GetBytes("car");
        ProjectFile front = rig.AddFile("car.jpg", "image/jpeg", bytes);
        ProjectAvatar car = rig.AddAvatar("Car", a => { MakeObject(a); a.Description = "A small city car"; },
            (front, AvatarReferenceKind.Image, AvatarReferenceRole.Front, bytes));
        ProjectFile red = rig.AddFile("car-red.jpg", "image/jpeg", bytes);
        rig.AddAvatar("Car · Red", a =>
            {
                MakeObject(a);
                a.VariantOfAvatarId = car.Id;
                a.VariantLabel = "Red";
                a.VariantDescription = "matte red paint, black rims";
            },
            (red, AvatarReferenceKind.Image, AvatarReferenceRole.Front, bytes));

        (JsonElement result, string raw) = await rig.CallAsync(enabled: false);

        JsonElement[] avatars = [.. result.GetProperty("avatars").EnumerateArray()];
        JsonElement original = avatars.Single(a => a.GetProperty("name").GetString() == "Car");
        JsonElement variant = avatars.Single(a => a.GetProperty("name").GetString() == "Car · Red");
        original.GetProperty("variantOf").ValueKind.Should().Be(JsonValueKind.Null);
        original.GetProperty("variantLabel").ValueKind.Should().Be(JsonValueKind.Null);
        variant.GetProperty("variantOf").GetString().Should().Be("Car", "the original by name, never by id");
        variant.GetProperty("variantLabel").GetString().Should().Be("Red");
        variant.GetProperty("variantDescription").GetString().Should().Be("matte red paint, black rims");
        variant.GetProperty("selectable").GetBoolean().Should().BeTrue();
        raw.Should().NotContain(car.Id.ToString()).And.NotContain(car.Id.ToString("N"));
        result.GetProperty("builds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Builds_still_being_made_and_recently_failed_are_listed_by_name_and_nothing_else_is()
    {
        using Rig rig = new() { ObjectsEnabled = true };
        DateTime now = DateTime.UtcNow;
        AvatarBuildJob Job(string name, string kind, string state, string? stage, string? message, DateTime updatedAt) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = rig.Project.Id,
            OrganizationId = rig.Project.OrganizationId,
            RequestedByUserId = rig.User,
            Kind = kind,
            Name = name,
            State = state,
            Stage = stage,
            Message = message,
            StatementLocale = "en",
            ParamsJson = "{\"holdMillis\":8000}",
            HoldId = Guid.NewGuid(),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt
        };
        AvatarBuildJob foreign = Job("Elsewhere", "variant", "queued", null, null, now);
        foreign.ProjectId = Guid.NewGuid();
        rig.Db.AvatarBuildJobs.AddRange(
            Job("Car · Red", "variant", "pictures", "pictures", null, now),
            Job("Kettle", "extraction", "queued", null, null, now.AddMinutes(-1)),
            Job("Car · Blue", "variant", "failed", "pictures", "None of the pictures could be changed.", now.AddHours(-2)),
            Job("Old failure", "variant", "failed", "pictures", "x", now.AddDays(-2)),
            Job("Done", "variant", "ready", "creating", null, now),
            Job("Stopped", "variant", "cancelled", "pictures", "Cancelled.", now),
            foreign);
        rig.Db.SaveChanges();

        (JsonElement result, string raw) = await rig.CallAsync(enabled: false);

        JsonElement[] builds = [.. result.GetProperty("builds").EnumerateArray()];
        builds.Select(b => b.GetProperty("name").GetString()).Should().BeEquivalentTo(["Car · Red", "Kettle", "Car · Blue"]);
        JsonElement running = builds.Single(b => b.GetProperty("name").GetString() == "Car · Red");
        (running.GetProperty("kind").GetString(), running.GetProperty("state").GetString(), running.GetProperty("stage").GetString())
            .Should().Be(("variant", "pictures", "pictures"));
        JsonElement failed = builds.Single(b => b.GetProperty("name").GetString() == "Car · Blue");
        failed.GetProperty("message").GetString().Should().Be("None of the pictures could be changed.");
        builds.Single(b => b.GetProperty("name").GetString() == "Kettle").GetProperty("kind").GetString().Should().Be("extraction");
        builds.Should().OnlyContain(b => b.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "name", "kind", "state", "stage", "message" }),
            "a build is told by name, kind, state, stage and message: no id, no hold, no notes");
        raw.Should().NotContain("holdMillis").And.NotContain("jobId");

        // With objects switched off no build is listed (builds only ever make objects).
        rig.ObjectsEnabled = false;
        (JsonElement people, _) = await rig.CallAsync(enabled: true);
        people.GetProperty("builds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void It_is_free_read_only_closed_world_and_not_gated()
    {
        AssistantToolAnnotation annotation = AssistantToolAnnotations.For("ListProjectAvatars");

        annotation.ReadOnlyHint.Should().BeTrue();
        annotation.DestructiveHint.Should().BeFalse();
        annotation.IdempotentHint.Should().BeTrue();
        annotation.OpenWorldHint.Should().BeFalse();
        annotation.Title.Should().Be("List the project's avatars");
        AssistantApprovalCatalog.IsGated("ListProjectAvatars").Should().BeFalse();
        AssistantPlatformTools.Classify("ListProjectAvatars")!.Kind.Should().Be(AssistantToolCostKind.Free);
    }

    [Fact]
    public async Task It_marks_a_linked_import_of_a_workspace_library_item_and_shows_no_id_of_it()
    {
        // docs/design/avatar-objects.md "The workspace avatar library": ListProjectAvatars marks an import (fromLibrary).
        using Rig rig = new();
        Guid itemId = Guid.NewGuid();
        ProjectFile face = rig.AddFile("anna.jpg", "image/jpeg", FaceBytes);
        rig.AddAvatar("Anna", a => a.LibraryItemId = itemId, (face, AvatarReferenceKind.Image, AvatarReferenceRole.Face, FaceBytes));
        ProjectFile bea = rig.AddFile("bea.jpg", "image/jpeg", Encoding.UTF8.GetBytes("bea"));
        rig.AddAvatar("Bea", null, (bea, AvatarReferenceKind.Image, AvatarReferenceRole.Face, Encoding.UTF8.GetBytes("bea")));

        (JsonElement result, string raw) = await rig.CallAsync();

        JsonElement[] avatars = [.. result.GetProperty("avatars").EnumerateArray()];
        avatars.Single(a => a.GetProperty("name").GetString() == "Anna").GetProperty("fromLibrary").GetBoolean().Should().BeTrue();
        avatars.Single(a => a.GetProperty("name").GetString() == "Bea").GetProperty("fromLibrary").GetBoolean().Should().BeFalse();
        raw.Should().NotContain(itemId.ToString(), "names, never ids");
    }
}
