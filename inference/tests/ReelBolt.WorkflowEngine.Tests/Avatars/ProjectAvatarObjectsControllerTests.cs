#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L1 of docs/design/avatar-objects.md: avatars of OBJECTS on the avatar REST surface. An object is created under
/// the object statement (verbatim or nothing is written), takes nothing a person's avatar carries, is checked for
/// people, has its own switch, plan feature and limit, cannot be revoked, and keeps its files when it is deleted. A
/// person's avatar behaves as before, except that every new attestation is made under the current statement version.
/// </summary>
public class ProjectAvatarObjectsControllerTests
{
    private static H Objects(bool people = true, bool objects = true)
    {
        var h = new H();
        h.Options.Enabled = people;
        h.Options.Objects.Enabled = objects;
        return h;
    }

    // ------------------------------------------------------------------ statements

    [Fact]
    public void The_current_person_statement_is_the_old_one_with_pictures_added_and_nothing_else()
    {
        AvatarConsentStatement.Version.Should().Be("avatar-consent-2026-12");
        AvatarConsentStatement.English.Should().Be(AvatarConsentStatement.English2026_11.Replace(
            "to generate AI videos in this project", "to generate AI videos and pictures in this project", StringComparison.Ordinal));
        AvatarConsentStatement.Italian.Should().Be(AvatarConsentStatement.Italian2026_11.Replace(
            "per generare video con l'intelligenza artificiale in questo progetto",
            "per generare video e immagini con l'intelligenza artificiale in questo progetto", StringComparison.Ordinal));

        // Pinned: a statement is never edited in place (rows store these bytes under this version).
        Hex(AvatarConsentStatement.English).Should().Be("97d08a6f8ae0b18144a29808f2fb331a4c52743806da37bba91ea4f7e6b1baa4");
        Hex(AvatarConsentStatement.Italian).Should().Be("6f82cb1b9e2a8be6f9fe99f98622ab6665b9ce41107d78bdde3ae56b39f2cd67");
        Encoding.UTF8.GetByteCount(AvatarConsentStatement.English).Should().Be(469);
        Encoding.UTF8.GetByteCount(AvatarConsentStatement.Italian).Should().Be(564);

        // For and Matches answer the CURRENT text only.
        AvatarConsentStatement.For("en").Should().Be(AvatarConsentStatement.English);
        AvatarConsentStatement.For("it").Should().Be(AvatarConsentStatement.Italian);
        AvatarConsentStatement.Matches("en", AvatarConsentStatement.English2026_11).Should().BeFalse("a new attestation is never made under the old text");
        AvatarConsentStatement.Matches("it", AvatarConsentStatement.Italian2026_11).Should().BeFalse();
        AvatarConsentStatement.CoversPictures(AvatarConsentStatement.Version).Should().BeTrue();
        AvatarConsentStatement.CoversPictures(AvatarConsentStatement.Version2026_11).Should().BeFalse();
    }

    [Fact]
    public void The_object_statements_are_the_design_text_byte_for_byte()
    {
        AvatarObjectStatement.Version.Should().Be("avatar-object-2026-11");
        AvatarObjectStatement.English.Should().Be(
            "I confirm that these files show an object, a product or a place and no identifiable person, and that I have the right to use them to generate AI videos and pictures in this project and to send them for that purpose to the AI providers this project uses.");
        AvatarObjectStatement.Italian.Should().Be(
            "Confermo che questi file mostrano un oggetto, un prodotto o un luogo e nessuna persona identificabile, e che ho il diritto di usarli per generare video e immagini con l'intelligenza artificiale in questo progetto e di inviarli, a tale scopo, ai provider di intelligenza artificiale usati da questo progetto.");
        Hex(AvatarObjectStatement.English).Should().Be("a08dc0bf891603fb941ab7a296a57eadd47122bd8c360dd3589cd9f4e7a3a316");
        Hex(AvatarObjectStatement.Italian).Should().Be("6a2b67c946f75427c0a9efbdd3ba6f6aa84674bba4fdd96a41da2da455d2c826");
        Encoding.UTF8.GetByteCount(AvatarObjectStatement.English).Should().Be(253);
        Encoding.UTF8.GetByteCount(AvatarObjectStatement.Italian).Should().Be(307);
        AvatarObjectStatement.Locales.Should().Equal("en", "it");
        AvatarObjectStatement.For("de").Should().BeNull();
        AvatarObjectStatement.Matches("en", AvatarObjectStatement.English).Should().BeTrue();
        AvatarObjectStatement.Matches("en", AvatarObjectStatement.English + " ").Should().BeFalse("ordinal, untrimmed");
        AvatarObjectStatement.Matches("it", AvatarObjectStatement.English).Should().BeFalse("the statement must be the one of the locale shown");
        AvatarObjectStatement.Matches("en", AvatarConsentStatement.English).Should().BeFalse("a person's consent is not the object statement");
    }

    private static string Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Create_records_the_object_statement_links_ordinary_files_and_never_expires()
    {
        H h = Objects();
        ProjectFile front = h.AddImage("machine-front.png", salt: 1);
        ProjectFile side = h.AddImage("machine-side.png", salt: 2);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front"), (side.Id, "Angle")) with { Description = " Chrome, two groups " }, CancellationToken.None));

        status.Should().Be(201, body.ToString());
        body.GetProperty("subjectKind").GetString().Should().Be("Object");
        body.GetProperty("name").GetString().Should().Be("Espresso machine");
        body.GetProperty("description").GetString().Should().Be("Chrome, two groups");
        body.GetProperty("selectable").GetBoolean().Should().BeTrue();
        body.GetProperty("unselectableReason").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("coversPictures").GetBoolean().Should().BeTrue();
        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.Null, "an object has no keep-until date");
        body.GetProperty("disclosureLabel").GetString().Should().Be("MetadataOnly");
        body.GetProperty("projectVoiceId").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("consent").GetProperty("statement").GetString().Should().Be(AvatarObjectStatement.English);
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be("avatar-object-2026-11");
        body.GetProperty("consent").GetProperty("subjectName").GetString().Should().BeEmpty();
        body.GetProperty("consent").GetProperty("attestedByUserId").GetGuid().Should().Be(h.OwnerId);
        body.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("role").GetString()).Should().Equal("Front", "Angle");
        body.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("state").GetString()).Should().OnlyContain(s => s == "ok");

        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar row = await db.ProjectAvatars.SingleAsync();
        row.SubjectKind.Should().Be(AvatarSubjectKind.Object);
        row.RetentionUntil.Should().Be(ProjectAvatar.NoRetention);
        row.ConsentSubjectAdult.Should().BeFalse();
        row.ConsentRevokedAt.Should().BeNull();
        row.ConsentAttestedAt.Should().NotBeNull();
        row.ConsentDocumentFileId.Should().BeNull();
        ProjectAvatarReference first = await db.ProjectAvatarReferences.SingleAsync(r => r.ProjectFileId == front.Id);
        body.GetProperty("primaryReferenceId").GetGuid().Should().Be(first.Id, "the first Front picture is the object's primary picture");
        first.ContentSha256.Should().Be(H.Sha256(h.Objects[front.StorageKey]));
        (await db.ProjectFiles.Where(f => f.Id == front.Id || f.Id == side.Id).Select(f => f.SourceKind).ToListAsync())
            .Should().OnlyContain(kind => kind == null, "an object's files stay ordinary media, never marked avatar_reference");

        ProjectAvatarEvent created = await db.ProjectAvatarEvents.SingleAsync();
        created.Kind.Should().Be(AvatarEventKind.Created);
        created.ConsentStatementVersion.Should().Be("avatar-object-2026-11");
        JsonElement outcomes = JsonDocument.Parse(created.OutcomesJson!).RootElement;
        outcomes.GetProperty("subject_kind").GetString().Should().Be("object");
        outcomes.GetProperty("person_check").GetString().Should().Be("passed", "the check is mandatory and never skipped");
        outcomes.GetProperty("images").GetInt32().Should().Be(2);
        created.OutcomesJson.Should().NotContain("machine").And.NotContain("userFiles");
    }

    [Fact]
    public async Task The_italian_object_statement_is_stored_under_its_locale()
    {
        H h = Objects();
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id,
            H.CreateObject((front.Id, "Front")) with { ConsentStatement = AvatarObjectStatement.Italian, ConsentLocale = "it" },
            CancellationToken.None));

        status.Should().Be(201, body.ToString());
        body.GetProperty("consent").GetProperty("statement").GetString().Should().Be(AvatarObjectStatement.Italian);
        body.GetProperty("consent").GetProperty("locale").GetString().Should().Be("it");
    }

    [Theory]
    [InlineData("en", "person")]
    [InlineData("en", "padded")]
    [InlineData("en", "it")]
    [InlineData("it", "en")]
    [InlineData("de", "en")]
    [InlineData("en", "empty")]
    public async Task Anything_but_the_object_statement_of_the_locale_is_object_statement_required_and_writes_nothing(string locale, string statement)
    {
        H h = Objects();
        ProjectFile front = h.AddImage();
        string sent = statement switch
        {
            "person" => AvatarConsentStatement.English,
            "padded" => AvatarObjectStatement.English + "\n",
            "empty" => string.Empty,
            _ => AvatarObjectStatement.For(statement)!
        };

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front")) with { ConsentStatement = sent, ConsentLocale = locale }, CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("object_statement_required");
        body.GetProperty("error").GetString().Should().Be("object_statement_required");
        body.GetProperty("statementVersion").GetString().Should().Be("avatar-object-2026-11");
        body.GetProperty("requiredStatement").GetString().Should().Be(AvatarObjectStatement.For(locale) ?? AvatarObjectStatement.English);
        body.GetProperty("statements").GetProperty("en").GetString().Should().Be(AvatarObjectStatement.English);
        body.GetProperty("statements").GetProperty("it").GetString().Should().Be(AvatarObjectStatement.Italian);
        await AssertNoAvatarAsync(h);
    }

    public static TheoryData<string, Func<CreateProjectAvatarRequest, CreateProjectAvatarRequest>> PersonOnlyFields => new()
    {
        { "consentSubjectName", r => r with { ConsentSubjectName = "Anna Rossi" } },
        { "consentBasis", r => r with { ConsentBasis = "Self" } },
        { "subjectIsAdult", r => r with { SubjectIsAdult = true } },
        { "retentionDays", r => r with { RetentionDays = 365 } },
        { "consentDocumentFileId", r => r with { ConsentDocumentFileId = Guid.NewGuid() } },
        { "projectVoiceId", r => r with { ProjectVoiceId = Guid.NewGuid() } },
        { "disclosureLabel", r => r with { DisclosureLabel = "Visible" } },
        { "disclosesOtherwise", r => r with { DisclosesOtherwise = true } },
        { "subjectIsPublicFigure", r => r with { SubjectIsPublicFigure = true } },
    };

    [Theory]
    [MemberData(nameof(PersonOnlyFields))]
    public async Task A_field_of_a_persons_avatar_sent_for_an_object_is_refused_by_name(string field, Func<CreateProjectAvatarRequest, CreateProjectAvatarRequest> send)
    {
        H h = Objects();
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, send(H.CreateObject((front.Id, "Front"))), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("field_not_for_objects");
        body.GetProperty("field").GetString().Should().Be(field);
        await AssertNoAvatarAsync(h);
    }

    [Fact]
    public async Task Empty_and_false_person_fields_count_as_not_sent()
    {
        H h = Objects();
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id,
            H.CreateObject((front.Id, "Front")) with { ConsentSubjectName = "", ConsentBasis = " ", SubjectIsAdult = false },
            CancellationToken.None));

        status.Should().Be(201, body.ToString());
    }

    [Theory]
    [InlineData("object")]
    [InlineData("Thing")]
    [InlineData("1")]
    public async Task An_unknown_subject_kind_is_refused(string kind)
    {
        H h = Objects();
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front")) with { SubjectKind = kind }, CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("subject_kind_invalid");
        await AssertNoAvatarAsync(h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Person")]
    public async Task No_subject_kind_or_Person_creates_a_person_exactly_as_before(string? kind)
    {
        H h = Objects();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.Create((face.Id, "Face")) with { SubjectKind = kind }, CancellationToken.None));

        status.Should().Be(201, body.ToString());
        body.GetProperty("subjectKind").GetString().Should().Be("Person");
        body.GetProperty("coversPictures").GetBoolean().Should().BeTrue("a new attestation is under the version that covers pictures");
        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.String);
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be(AvatarConsentStatement.Version);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.FindAsync(face.Id))!.SourceKind.Should().Be(ProjectAvatarReference.ProjectFileSourceKind, "a person's reference is still badged");
    }

    [Fact]
    public async Task A_person_created_without_retention_days_keeps_the_default_period()
    {
        H h = Objects();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.Create((face.Id, "Face")) with { RetentionDays = null }, CancellationToken.None));

        status.Should().Be(201, body.ToString());
        DateTime until = body.GetProperty("retentionUntil").GetDateTime();
        until.Should().BeCloseTo(DateTime.UtcNow.AddDays(ProjectAvatarLimits.DefaultRetentionDays), TimeSpan.FromMinutes(1));
    }

    // ------------------------------------------------------------------ references of an object

    [Theory]
    [InlineData("Face")]
    [InlineData("HalfBody")]
    [InlineData("Speech")]
    public async Task A_persons_role_on_an_objects_picture_is_refused(string role)
    {
        H h = Objects();
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, role)), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("reference_role_not_allowed");
        await AssertNoAvatarAsync(h);
    }

    [Fact]
    public async Task An_object_role_on_a_persons_picture_is_refused()
    {
        H h = Objects();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.Create((face.Id, "Front")), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("reference_role_not_allowed");
    }

    [Fact]
    public async Task An_unknown_role_names_the_objects_own_roles()
    {
        H h = Objects();
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Sideways")), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("reference_role_invalid");
        body.GetProperty("message").GetString().Should().Contain("Front, Angle, Detail, Motion");
    }

    [Fact]
    public async Task An_object_takes_no_recording()
    {
        H h = Objects();
        ProjectFile front = h.AddImage();
        ProjectFile sound = h.AddProbedMedia("hum.wav", "audio/wav", 5);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front"), (sound.Id, "Motion")), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("reference_kind_not_allowed");
        body.GetProperty("fileId").GetGuid().Should().Be(sound.Id);
        await AssertNoAvatarAsync(h);
    }

    [Fact]
    public async Task An_object_needs_a_picture_and_holds_at_most_eight_pictures_and_three_videos()
    {
        H h = Objects();
        ProjectFile[] pictures = [.. Enumerable.Range(0, 9).Select(i => h.AddImage($"p{i}.png", salt: (byte)(i + 1)))];
        ProjectFile[] videos = [.. Enumerable.Range(0, 4).Select(i => h.AddProbedMedia($"spin{i}.mp4", "video/mp4", 5, "h264"))];
        foreach (ProjectFile video in videos) h.AddFilmstrip(video);

        (await CreateCode(h, [.. videos.Take(1).Select(v => (v.Id, "Motion"))])).Should().Be((400, "no_image_reference"));
        (await CreateCode(h)).Should().Be((400, "no_image_reference"));
        (await CreateCode(h, [.. pictures.Select(p => (p.Id, "Angle"))])).Should().Be((400, "too_many_references"));
        (await CreateCode(h, [(pictures[0].Id, "Front"), .. videos.Select(v => (v.Id, "Motion"))])).Should().Be((400, "too_many_references"));
        (await CreateCode(h, [.. pictures.Take(8).Select(p => (p.Id, "Detail")), .. videos.Take(3).Select(v => (v.Id, "Motion"))]))
            .Should().Be((201, null), "eight pictures and three videos is the most an object holds");
    }

    [Theory]
    [InlineData("github", 400)]
    [InlineData("local_folder", 400)]
    [InlineData("google_drive", 400)]
    [InlineData("onedrive", 400)]
    [InlineData("dropbox", 400)]
    [InlineData("some_future_writer", 400)]
    [InlineData("output_copy", 201)]
    [InlineData("zip", 201)]
    [InlineData("avatar_reference", 201)]
    public async Task An_object_may_use_a_platform_made_file_but_never_one_a_connected_source_keeps_in_step(string sourceKind, int expected)
    {
        H h = Objects();
        ProjectFile picture = h.AddImage(tweak: f => f.SourceKind = sourceKind);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((picture.Id, "Front")), CancellationToken.None));

        status.Should().Be(expected, body.ToString());
        if (expected == 400) body.GetProperty("code").GetString().Should().Be("reference_file_source_not_allowed");
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.FindAsync(picture.Id))!.SourceKind.Should().Be(sourceKind, "an object never re-marks a file");
    }

    [Fact]
    public async Task A_person_still_refuses_a_saved_output_copy()
    {
        H h = Objects();
        ProjectFile picture = h.AddImage(tweak: f => f.SourceKind = "output_copy");

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, H.Create((picture.Id, "Face")), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("reference_file_source_not_allowed");
    }

    [Fact]
    public async Task The_same_safety_checks_guard_an_objects_files()
    {
        H h = Objects();
        h.Moderation.Enabled = true;
        ProjectFile pending = h.AddImage("pending.png", salt: 1, tweak: f => f.ModerationStatus = FileModerationStatus.Scanning);
        ProjectFile quarantined = h.AddImage("bad.png", salt: 2, tweak: f => f.ModerationStatus = FileModerationStatus.Quarantined);
        ProjectFile tiny = h.AddImage("tiny.png", width: 100, height: 100, salt: 3);

        (await CreateCode(h, (pending.Id, "Front"))).Should().Be((409, "AVATAR_REFERENCE_PENDING"));
        (await CreateCode(h, (quarantined.Id, "Front"))).Should().Be((400, "reference_file_not_found"));
        (await CreateCode(h, (tiny.Id, "Front"))).Should().Be((400, "reference_out_of_limits"));
        (await CreateCode(h, (Guid.NewGuid(), "Front"))).Should().Be((400, "reference_file_not_found"));
        await AssertNoAvatarAsync(h);
    }

    // ------------------------------------------------------------------ the person check (AO-1)

    [Fact]
    public async Task With_a_vision_provider_every_picture_is_checked_and_the_event_says_passed()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck();
        ProjectFile front = h.AddImage("front.png", salt: 1);
        ProjectFile detail = h.AddImage("detail.png", salt: 2);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front"), (detail.Id, "Detail")), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        h.PersonCheck.Shown.Should().HaveCount(2);
        h.PersonCheck.Shown[0].Should().Equal(h.Objects[front.StorageKey]);
        h.PersonCheck.Shown[1].Should().Equal(h.Objects[detail.StorageKey]);
        h.PersonCheck.Scopes.Should().OnlyContain(s => s.OrganizationId == h.OrganizationId && s.ProjectId == h.Project.Id);
        using InferenceApiDbContext db = h.NewDb();
        JsonDocument.Parse((await db.ProjectAvatarEvents.SingleAsync()).OutcomesJson!).RootElement
            .GetProperty("person_check").GetString().Should().Be("passed");
    }

    [Fact]
    public async Task A_picture_that_shows_a_person_is_refused_by_file_and_nothing_is_written()
    {
        H h = Objects();
        ProjectFile front = h.AddImage("front.png", salt: 1);
        ProjectFile selfie = h.AddImage("with-me.png", salt: 2);
        byte[] selfieBytes = h.Objects[selfie.StorageKey];
        h.PersonCheck = new H.FakePersonCheck
        {
            Answer = bytes => bytes.AsSpan().SequenceEqual(selfieBytes) ? ObjectPersonCheckVerdict.Person : ObjectPersonCheckVerdict.NoPerson
        };

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front"), (selfie.Id, "Angle")), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("object_reference_shows_person");
        body.GetProperty("fileId").GetGuid().Should().Be(selfie.Id);
        body.GetProperty("message").GetString().Should().Contain("with-me.png");
        await AssertNoAvatarAsync(h);
    }

    [Fact]
    public async Task A_vision_provider_that_cannot_answer_fails_closed_with_503()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck { Answer = _ => ObjectPersonCheckVerdict.Unavailable };
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front")), CancellationToken.None));

        status.Should().Be(503);
        body.GetProperty("code").GetString().Should().Be("OBJECT_PERSON_CHECK_UNAVAILABLE");
        await AssertNoAvatarAsync(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_a_vision_provider_an_object_is_refused_and_nothing_is_stored(bool noCheckRegistered)
    {
        H h = Objects();
        if (noCheckRegistered) h.PersonCheck = null;
        else h.PersonCheck = new H.FakePersonCheck { Active = false, Answer = _ => ObjectPersonCheckVerdict.Person };
        ProjectFile front = h.AddImage();
        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front")), CancellationToken.None));
        (status, body.GetProperty("code").GetString()).Should().Be((409, "VISION_PROVIDER_REQUIRED"));
        body.GetProperty("message").GetString().Should().StartWith("Checking that a picture shows no person needs a vision model");
        (body.GetProperty("reason").GetString(), body.GetProperty("capability").GetString()).Should().Be(("NO_PROVIDER", "Vision"));
        body.TryGetProperty("canConfigure", out _).Should().BeTrue();
        h.PersonCheck?.Shown.Should().BeEmpty();
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(0);
        (await db.ProjectAvatarEvents.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Replacing_the_references_of_an_object_without_a_vision_provider_is_refused()
    {
        H h = Objects();
        ProjectFile front = h.AddImage();
        (int created, JsonElement made) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front")), CancellationToken.None));
        created.Should().Be(201, made.ToString());
        Guid avatarId = made.GetProperty("id").GetGuid();
        h.PersonCheck!.Active = false;
        ProjectFile other = h.AddImage();
        (int status, JsonElement body) = H.Read(await h.Controller().ReplaceReferences(
            h.Project.Id, avatarId, ReattachObject(other), CancellationToken.None));
        (status, body.GetProperty("code").GetString()).Should().Be((409, "VISION_PROVIDER_REQUIRED"));
    }

    [Fact]
    public async Task The_person_check_runs_only_after_every_other_check_passed()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck();
        ProjectFile front = h.AddImage("front.png", salt: 1);
        ProjectFile tiny = h.AddImage("tiny.png", width: 64, height: 64, salt: 2);

        (await CreateCode(h, (front.Id, "Front"), (tiny.Id, "Angle"))).Should().Be((400, "reference_out_of_limits"));

        h.PersonCheck.Shown.Should().BeEmpty("no picture is sent to a model for a request that is refused anyway");
    }

    [Fact]
    public async Task A_person_avatar_is_never_shown_to_the_person_check()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck { Answer = _ => ObjectPersonCheckVerdict.Person };
        ProjectFile face = h.AddImage();

        (int status, _) = H.Read(await h.Controller().Create(h.Project.Id, H.Create((face.Id, "Face")), CancellationToken.None));

        status.Should().Be(201);
        h.PersonCheck.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task A_video_is_checked_from_a_frame_picture_that_already_exists_and_is_pending_until_one_does()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck();
        ProjectFile front = h.AddImage();
        ProjectFile spin = h.AddProbedMedia("spin.mp4", "video/mp4", 6, "h264");

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front"), (spin.Id, "Motion")), CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_REFERENCE_PENDING");
        body.GetProperty("reason").GetString().Should().Be("frames");
        body.GetProperty("fileId").GetGuid().Should().Be(spin.Id);
        await AssertNoAvatarAsync(h);

        byte[] sheet = h.AddFilmstrip(spin);
        h.PersonCheck.Shown.Clear();
        (status, body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((front.Id, "Front"), (spin.Id, "Motion")), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        h.PersonCheck.Shown.Should().HaveCount(2);
        h.PersonCheck.Shown[1].Should().Equal(sheet, "the video is judged from its filmstrip sheet, never from bytes the API would have to decode");
    }

    [Fact]
    public async Task Without_a_vision_provider_a_video_is_refused_too_not_waved_through()
    {
        H h = Objects();
        h.PersonCheck!.Active = false;
        ProjectFile front = h.AddImage();
        ProjectFile spin = h.AddProbedMedia("spin.mp4", "video/mp4", 6, "h264");

        (await CreateCode(h, (front.Id, "Front"), (spin.Id, "Motion"))).Should().Be((409, "VISION_PROVIDER_REQUIRED"));
    }

    // ------------------------------------------------------------------ plan and limits

    [Fact]
    public async Task A_plan_without_object_avatars_gets_402_on_create_and_reattach_but_may_list_and_delete()
    {
        H h = Objects();
        Guid objectId = await h.CreateObjectAsync();
        ProjectFile other = h.AddImage("other.png", salt: 7);
        h.Entitlements.Set = new OrganizationEntitlementSet { AvatarObjectsAllowed = false, AvatarsAllowed = true, Tier = "trial" };

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((other.Id, "Front")) with { Name = "Other" }, CancellationToken.None));
        status.Should().Be(402);
        body.GetProperty("code").GetString().Should().Be(PlanGate.NotEntitledCode);
        body.GetProperty("feature").GetString().Should().Be("avatar_objects");
        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, objectId, ReattachObject(other), CancellationToken.None)).Status.Should().Be(402);

        JsonElement requirements = H.Read(await h.Controller().Requirements(h.Project.Id, CancellationToken.None)).Body;
        requirements.GetProperty("kinds").GetProperty("object").GetProperty("entitled").GetBoolean().Should().BeFalse();
        requirements.GetProperty("kinds").GetProperty("person").GetProperty("entitled").GetBoolean().Should().BeTrue();
        requirements.GetProperty("entitled").GetBoolean().Should().BeTrue("entitled is the person's flag");
        H.Read(await h.Controller().List(h.Project.Id, CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller().Delete(h.Project.Id, objectId, CancellationToken.None)).Status.Should().Be(200);

        // The person feature is judged on its own: a plan with objects but without people still makes objects.
        h.Entitlements.Set = new OrganizationEntitlementSet { AvatarObjectsAllowed = true, AvatarsAllowed = false, Tier = "creator" };
        (await CreateCode(h, (other.Id, "Front"))).Should().Be((201, null));
        (status, body) = H.Read(await h.Controller().Create(h.Project.Id, H.Create((h.AddImage("f.png", salt: 8).Id, "Face")), CancellationToken.None));
        status.Should().Be(402);
        body.GetProperty("feature").GetString().Should().Be("avatars");
    }

    [Fact]
    public void Object_avatars_are_off_on_Trial_and_on_from_Creator_up_in_the_catalog()
    {
        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Billing", "plans.catalog.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        var allowed = doc.RootElement.GetProperty("plans").EnumerateArray()
            .Where(p => p.TryGetProperty("entitlements", out _))
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("entitlements").GetProperty("avatarObjectsAllowed").GetBoolean());

        allowed.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["trial"] = false,
            ["creator"] = true,
            ["pro"] = true,
            ["team"] = true,
            ["enterprise"] = true
        });
        PlanGate.AvatarObjectsFeature.Should().Be("avatar_objects");
        PlanGateViolation refused = PlanGate.CheckAvatarObjects(new OrganizationEntitlementSet { AvatarObjectsAllowed = false, Tier = "trial" })!;
        (refused.StatusCode, refused.Feature).Should().Be((402, "avatar_objects"));
        PlanGate.CheckAvatarObjects(new OrganizationEntitlementSet { AvatarsAllowed = false }).Should().BeNull("people and objects are separate features");
        new OrganizationEntitlementSet().AvatarObjectsAllowed.Should().BeTrue("an omitted key means allowed, like every entitlement");
    }

    [Fact]
    public async Task People_and_objects_are_counted_apart_twenty_each()
    {
        H h = Objects();
        using (InferenceApiDbContext seed = h.NewDb())
        {
            for (int i = 0; i < ProjectAvatarLimits.MaxObjectsPerProject; i++) seed.ProjectAvatars.Add(Row(h, $"Object {i}", AvatarSubjectKind.Object));
            for (int i = 0; i < ProjectAvatarLimits.MaxAvatarsPerProject - 1; i++) seed.ProjectAvatars.Add(Row(h, $"Person {i}", AvatarSubjectKind.Person));
            await seed.SaveChangesAsync();
        }

        ProjectFile picture = h.AddImage("p.png", salt: 1);
        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((picture.Id, "Front")) with { Name = "One more object" }, CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_LIMIT_REACHED");
        body.GetProperty("message").GetString().Should().Contain("20 object avatars");

        // Twenty objects do not use up the people's allowance: the twentieth person still fits, the twenty-first does not.
        ProjectFile face = h.AddImage("face.png", salt: 2);
        H.Read(await h.Controller().Create(h.Project.Id, H.Create((face.Id, "Face")) with { Name = "Twentieth person" }, CancellationToken.None))
            .Status.Should().Be(201);
        ProjectFile face2 = h.AddImage("face2.png", salt: 3);
        (status, body) = H.Read(await h.Controller().Create(h.Project.Id, H.Create((face2.Id, "Face")) with { Name = "One more person" }, CancellationToken.None));
        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_LIMIT_REACHED");
    }

    [Fact]
    public async Task A_name_is_unique_across_both_kinds()
    {
        H h = Objects();
        await h.CreateReadyAsync("Rocket");
        ProjectFile picture = h.AddImage("rocket.png", salt: 5);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject((picture.Id, "Front")) with { Name = "Rocket" }, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_NAME_TAKEN");
    }

    // ------------------------------------------------------------------ switches

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_kind_whose_switch_is_off_is_absent_while_the_other_works(bool people, bool objects)
    {
        H h = Objects();
        Guid personId = await h.CreateReadyAsync("Anna");
        Guid objectId = await h.CreateObjectAsync("Machine");
        h.Options.Enabled = people;
        h.Options.Objects.Enabled = objects;
        (Guid onId, Guid offId) = people ? (personId, objectId) : (objectId, personId);
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;

        JsonElement requirements = H.Read(await h.Controller().Requirements(p, ct)).Body;
        requirements.GetProperty("kinds").GetProperty("person").GetProperty("enabled").GetBoolean().Should().Be(people);
        requirements.GetProperty("kinds").GetProperty("object").GetProperty("enabled").GetBoolean().Should().Be(objects);

        JsonElement list = H.Read(await h.Controller().List(p, ct)).Body;
        list.EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).Should().Equal(onId);

        H.Read(await h.Controller().Get(p, onId, ct)).Status.Should().Be(200);
        H.Read(await h.Controller().Get(p, offId, ct)).Status.Should().Be(404);
        H.Read(await h.Controller().Update(p, offId, new UpdateProjectAvatarRequest(Name: "Renamed"), ct)).Status.Should().Be(404);
        H.Read(await h.Controller().Usage(p, offId, ct)).Status.Should().Be(404);
        H.Read(await h.Controller().RevokeConsent(p, offId, ct)).Status.Should().Be(404);
        ProjectFile extra = h.AddImage("extra.png", salt: 42);
        H.Read(await h.Controller().ReplaceReferences(p, offId, people ? ReattachObject(extra) : ProjectAvatarsControllerTests.Reattest(extra), ct))
            .Status.Should().Be(404);

        // Creating the kind that is off answers 404, and nothing is written.
        ProjectFile fresh = h.AddImage("fresh.png", salt: 43);
        CreateProjectAvatarRequest offCreate = people
            ? H.CreateObject((fresh.Id, "Front")) with { Name = "Off" }
            : H.Create((fresh.Id, "Face")) with { Name = "Off" };
        H.Read(await h.Controller().Create(p, offCreate, ct)).Status.Should().Be(404);
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.ProjectAvatars.CountAsync()).Should().Be(2);
            (await db.ProjectAvatars.SingleAsync(a => a.Id == offId)).Name.Should().NotBe("Renamed");
        }

        // Delete always works on a row that exists: removing a likeness is never blocked by a product switch.
        H.Read(await h.Controller().Delete(p, offId, ct)).Status.Should().Be(200);
        using InferenceApiDbContext after = h.NewDb();
        (await after.ProjectAvatars.Select(a => a.Id).ToListAsync()).Should().Equal(onId);
    }

    [Fact]
    public async Task With_only_objects_switched_on_an_object_is_created_and_every_route_answers()
    {
        H h = Objects(people: false, objects: true);
        ProjectFile front = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, H.CreateObject((front.Id, "Front")), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        H.Read(await h.Controller().Get(h.Project.Id, body.GetProperty("id").GetGuid(), CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller().GetMediaConsent(h.Project.Id, CancellationToken.None)).Status.Should().Be(200);
    }

    [Fact]
    public async Task With_both_switches_off_every_route_answers_404_media_consent_included()
    {
        H h = Objects();
        Guid objectId = await h.CreateObjectAsync();
        h.Options.Enabled = false;
        h.Options.Objects.Enabled = false;
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;

        int[] statuses =
        [
            H.Read(await h.Controller().Requirements(p, ct)).Status,
            H.Read(await h.Controller().List(p, ct)).Status,
            H.Read(await h.Controller().Get(p, objectId, ct)).Status,
            H.Read(await h.Controller().Create(p, H.CreateObject((h.AddImage("x.png", salt: 3).Id, "Front")) with { Name = "X" }, ct)).Status,
            H.Read(await h.Controller().Update(p, objectId, new UpdateProjectAvatarRequest(Name: "Y"), ct)).Status,
            H.Read(await h.Controller().RevokeConsent(p, objectId, ct)).Status,
            H.Read(await h.Controller().Usage(p, objectId, ct)).Status,
            H.Read(await h.Controller().Delete(p, objectId, ct)).Status,
            H.Read(await h.Controller().GetMediaConsent(p, ct)).Status,
            H.Read(await h.Controller().GrantMediaConsent(p, ct)).Status,
            H.Read(await h.Controller().RevokeMediaConsent(p, ct)).Status,
        ];

        statuses.Should().AllSatisfy(s => s.Should().Be(404));
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(1);
        (await db.Projects.SingleAsync()).SubjectMediaConsentAt.Should().BeNull();
    }

    [Fact]
    public void Both_switches_default_to_off_and_bind_from_their_keys()
    {
        var defaults = new AvatarOptions();
        (defaults.Enabled, defaults.Objects.Enabled, defaults.AnyEnabled).Should().Be((false, false, false));

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Avatars:Objects:Enabled"] = "true" })
            .Build();
        var bound = new AvatarOptions();
        configuration.GetSection(AvatarOptions.SectionName).Bind(bound);

        (bound.Enabled, bound.Objects.Enabled, bound.AnyEnabled).Should().Be((false, true, true));
        bound.IsEnabled(AvatarSubjectKind.Object).Should().BeTrue();
        bound.IsEnabled(AvatarSubjectKind.Person).Should().BeFalse();
        AvatarOptions.AnyEnabledIn(configuration).Should().BeTrue();
        AvatarOptions.AnyEnabledIn(new ConfigurationBuilder().Build()).Should().BeFalse();
    }

    // ------------------------------------------------------------------ edit, re-attach, revoke, delete

    [Fact]
    public async Task Patching_four_full_non_ascii_identity_facts_is_stored_within_the_column_and_reads_back()
    {
        H h = Objects();
        Guid objectId = await h.CreateObjectAsync();
        Guid p = h.Project.Id;
        string field = new('ж', AvatarIdentityFacts.MaxFieldChars);
        var facts = new AvatarIdentityFacts(field, field, field, field);
        (int status, JsonElement body) = H.Read(await h.Controller().Update(p, objectId, new UpdateProjectAvatarRequest(IdentityFacts: facts), CancellationToken.None));
        status.Should().Be(200, body.ToString());
        string? stored = (await h.NewDb().ProjectAvatars.AsNoTracking().SingleAsync(a => a.Id == objectId)).IdentityFacts;
        stored.Should().NotBeNull();
        stored!.Length.Should().BeLessThanOrEqualTo(AvatarIdentityFacts.MaxStoredChars);
        AvatarIdentityFacts.Parse(stored).Should().Be(facts);
    }

    [Fact]
    public async Task An_object_is_renamed_and_described_but_takes_no_voice_and_no_label()
    {
        H h = Objects();
        Guid objectId = await h.CreateObjectAsync();
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;

        (int status, JsonElement body) = H.Read(await h.Controller().Update(p, objectId, new UpdateProjectAvatarRequest(Name: " La Marzocco ", Description: "Chrome"), ct));
        status.Should().Be(200, body.ToString());
        body.GetProperty("name").GetString().Should().Be("La Marzocco");
        body.GetProperty("description").GetString().Should().Be("Chrome");

        foreach ((UpdateProjectAvatarRequest request, string field) in new[]
                 {
                     (new UpdateProjectAvatarRequest(ProjectVoiceId: Guid.NewGuid()), "projectVoiceId"),
                     (new UpdateProjectAvatarRequest(ClearProjectVoice: true), "clearProjectVoice"),
                     (new UpdateProjectAvatarRequest(DisclosureLabel: "Visible"), "disclosureLabel"),
                     (new UpdateProjectAvatarRequest(DisclosesOtherwise: true), "disclosesOtherwise"),
                 })
        {
            (status, body) = H.Read(await h.Controller().Update(p, objectId, request, ct));
            status.Should().Be(400);
            body.GetProperty("code").GetString().Should().Be("field_not_for_objects");
            body.GetProperty("field").GetString().Should().Be(field);
        }

        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar row = await db.ProjectAvatars.SingleAsync();
        (row.DisclosureLabel, row.ProjectVoiceId).Should().Be((AvatarDisclosure.MetadataOnly, (Guid?)null));
    }

    [Fact]
    public async Task Replacing_an_objects_references_needs_the_object_statement_again_and_never_sets_a_retention()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck();
        ProjectFile first = h.AddImage("first.png", salt: 1);
        Guid objectId = await h.CreateObjectAsync("Machine", first);
        ProjectFile second = h.AddImage("second.png", salt: 2);
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;

        (int status, JsonElement body) = H.Read(await h.Controller().ReplaceReferences(
            p, objectId, ReattachObject(second) with { ConsentStatement = AvatarConsentStatement.English }, ct));
        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("object_statement_required");

        (status, body) = H.Read(await h.Controller().ReplaceReferences(p, objectId, ReattachObject(second) with { RetentionDays = 30 }, ct));
        (status, body.GetProperty("code").GetString(), body.GetProperty("field").GetString()).Should().Be((400, "field_not_for_objects", "retentionDays"));
        (status, body) = H.Read(await h.Controller().ReplaceReferences(p, objectId, ReattachObject(second) with { SubjectIsAdult = true }, ct));
        (status, body.GetProperty("code").GetString(), body.GetProperty("field").GetString()).Should().Be((400, "field_not_for_objects", "subjectIsAdult"));

        h.PersonCheck.Shown.Clear();
        (status, body) = H.Read(await h.Controller().ReplaceReferences(p, objectId, ReattachObject(second), ct));
        status.Should().Be(200, body.ToString());
        body.GetProperty("references").EnumerateArray().Select(r => r.GetProperty("projectFileId").GetGuid()).Should().Equal(second.Id);
        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be("avatar-object-2026-11");
        h.PersonCheck.Shown.Should().ContainSingle("the new picture is checked for people too");

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.SingleAsync()).RetentionUntil.Should().Be(ProjectAvatar.NoRetention);
        (await db.ProjectFiles.Where(f => f.Id == first.Id || f.Id == second.Id).Select(f => f.SourceKind).ToListAsync())
            .Should().OnlyContain(kind => kind == null);
        ProjectAvatarEvent changed = await db.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.ReferencesChanged);
        JsonElement outcomes = JsonDocument.Parse(changed.OutcomesJson!).RootElement;
        outcomes.GetProperty("person_check").GetString().Should().Be("passed");
        outcomes.GetProperty("subject_kind").GetString().Should().Be("object");
        outcomes.TryGetProperty("retention_days", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Re_attesting_a_person_under_the_old_text_moves_them_to_the_version_that_covers_pictures()
    {
        H h = Objects();
        ProjectFile face = h.AddImage("face.png", salt: 1);
        Guid personId = await h.CreateReadyAsync("Anna", face);
        using (InferenceApiDbContext db = h.NewDb())
        {
            ProjectAvatar row = await db.ProjectAvatars.SingleAsync();
            row.ConsentStatement = AvatarConsentStatement.English2026_11;
            row.ConsentStatementVersion = AvatarConsentStatement.Version2026_11;
            await db.SaveChangesAsync();
        }

        JsonElement before = H.Read(await h.Controller().Get(h.Project.Id, personId, CancellationToken.None)).Body;
        before.GetProperty("coversPictures").GetBoolean().Should().BeFalse("the first text covers videos only");
        before.GetProperty("selectable").GetBoolean().Should().BeTrue("it keeps working for video");

        // The old wording is no longer accepted for a new attestation...
        (int status, JsonElement body) = H.Read(await h.Controller().ReplaceReferences(
            h.Project.Id, personId,
            new ReplaceAvatarReferencesRequest(AvatarConsentStatement.English2026_11, "en", true, [new AvatarReferenceRequest(face.Id, "Face")], 30),
            CancellationToken.None));
        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("consent_statement_required");
        body.GetProperty("statementVersion").GetString().Should().Be("avatar-consent-2026-12");

        // ...and the current one moves the avatar to the version that covers pictures.
        (status, body) = H.Read(await h.Controller().ReplaceReferences(
            h.Project.Id, personId, ProjectAvatarsControllerTests.Reattest(face), CancellationToken.None));
        status.Should().Be(200, body.ToString());
        body.GetProperty("coversPictures").GetBoolean().Should().BeTrue();
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be("avatar-consent-2026-12");
        body.GetProperty("consent").GetProperty("statement").GetString().Should().Be(AvatarConsentStatement.English);
    }

    [Fact]
    public async Task Revoking_an_object_is_refused_because_no_person_stands_behind_it()
    {
        H h = Objects();
        Guid objectId = await h.CreateObjectAsync();

        (int status, JsonElement body) = H.Read(await h.Controller().RevokeConsent(h.Project.Id, objectId, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_NOT_A_PERSON");
        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar row = await db.ProjectAvatars.SingleAsync();
        (row.ConsentRevokedAt, row.ConsentRevokedByUserId).Should().Be(((DateTime?)null, (Guid?)null));
        (await db.ProjectAvatarEvents.CountAsync(e => e.Kind == AvatarEventKind.ConsentRevoked)).Should().Be(0);
    }

    [Fact]
    public async Task Deleting_an_object_removes_the_row_and_its_links_and_keeps_every_file()
    {
        H h = Objects();
        ProjectFile front = h.AddImage("front.png", salt: 1);
        ProjectFile side = h.AddImage("side.png", salt: 2);
        Guid objectId = await h.CreateObjectAsync("Machine", front, side);

        (int status, JsonElement body) = H.Read(await h.Controller().Delete(h.Project.Id, objectId, CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("rowDeleted").GetBoolean().Should().BeTrue();
        body.GetProperty("references").GetProperty("deleted").GetInt32().Should().Be(0);
        body.GetProperty("references").GetProperty("failed").GetInt32().Should().Be(0);
        body.GetProperty("references").GetProperty("kept").GetInt32().Should().Be(2);
        body.GetProperty("consentDocumentDeleted").GetBoolean().Should().BeFalse();

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(0);
        (await db.ProjectAvatarReferences.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.CountAsync(f => f.Id == front.Id || f.Id == side.Id)).Should().Be(2, "an object's files are ordinary project files");
        (await db.StorageRetentionMarks.CountAsync()).Should().Be(0);
        h.DeletedKeys.Should().BeEmpty();
        h.Objects.Should().ContainKeys(front.StorageKey, side.StorageKey);
        ProjectAvatarEvent deleted = await db.ProjectAvatarEvents.SingleAsync(e => e.Kind == AvatarEventKind.Deleted);
        JsonElement outcomes = JsonDocument.Parse(deleted.OutcomesJson!).RootElement;
        outcomes.GetProperty("references_kept").GetInt32().Should().Be(2);
        outcomes.GetProperty("references_deleted").GetInt32().Should().Be(0);
        outcomes.GetProperty("subject_kind").GetString().Should().Be("object");
    }

    [Fact]
    public async Task A_file_an_object_holds_is_still_protected_by_the_file_in_use_guard_and_free_again_after_the_delete()
    {
        H h = Objects();
        ProjectFile front = h.AddImage("front.png", salt: 1);
        Guid objectId = await h.CreateObjectAsync("Machine", front);

        using (InferenceApiDbContext db = h.NewDb())
        {
            AvatarFileUseConflict? use = await AvatarFileUse.FirstUseAsync(db, h.Project.Id, [front.Id], CancellationToken.None);
            use.Should().NotBeNull();
            (use!.AvatarId, use.AvatarName, use.FileId).Should().Be((objectId, "Machine", front.Id));
            (await AvatarFileUse.ReferencedFileIdsAsync(db, [front.Id], CancellationToken.None)).Should().Equal(front.Id);
        }

        // The guard does not read the switches: it holds even while objects are switched off.
        h.Options.Objects.Enabled = false;
        using (InferenceApiDbContext db = h.NewDb())
            (await AvatarFileUse.FirstUseAsync(db, h.Project.Id, [front.Id], CancellationToken.None)).Should().NotBeNull();

        h.Options.Objects.Enabled = true;
        H.Read(await h.Controller().Delete(h.Project.Id, objectId, CancellationToken.None)).Status.Should().Be(200);
        using InferenceApiDbContext after = h.NewDb();
        (await AvatarFileUse.FirstUseAsync(after, h.Project.Id, [front.Id], CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_person_delete_keeps_a_file_an_object_also_lists_and_a_person_release_keeps_an_objects_file_unmarked()
    {
        H h = Objects();
        ProjectFile shared = h.AddImage("shared.png", salt: 1);
        ProjectFile own = h.AddImage("own.png", salt: 2);
        Guid personId = await h.CreateReadyAsync("Anna", shared, own);
        Guid objectId = await h.CreateObjectAsync("Machine", shared);

        // Re-attesting the person without the shared file: only an avatar of a person marks files, so the file the
        // object still lists becomes an ordinary file again.
        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, personId, ProjectAvatarsControllerTests.Reattest(own), CancellationToken.None))
            .Status.Should().Be(200);
        using (InferenceApiDbContext db = h.NewDb())
            (await db.ProjectFiles.FindAsync(shared.Id))!.SourceKind.Should().BeNull();

        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, personId, ProjectAvatarsControllerTests.Reattest(own, shared), CancellationToken.None))
            .Status.Should().Be(200);
        (int status, JsonElement body) = H.Read(await h.Controller().Delete(h.Project.Id, personId, CancellationToken.None));
        status.Should().Be(200);
        body.GetProperty("references").GetProperty("kept").GetInt32().Should().Be(1, "the object still lists the shared file");
        using InferenceApiDbContext after = h.NewDb();
        (await after.ProjectFiles.FindAsync(shared.Id)).Should().NotBeNull();
        (await after.ProjectFiles.FindAsync(own.Id)).Should().BeNull();
        (await after.ProjectAvatars.Select(a => a.Id).ToListAsync()).Should().Equal(objectId);
    }

    [Fact]
    public async Task Usage_answers_for_an_object_too()
    {
        H h = Objects();
        Guid objectId = await h.CreateObjectAsync();

        (int status, JsonElement body) = H.Read(await h.Controller().Usage(h.Project.Id, objectId, CancellationToken.None));

        status.Should().Be(200, body.ToString());
        body.GetProperty("takeCount").GetInt32().Should().Be(0);
        body.GetProperty("picturesUsingReferences").GetInt32().Should().Be(0);
    }

    // ------------------------------------------------------------------ requirements

    [Fact]
    public async Task Requirements_serve_the_object_half_of_the_contract()
    {
        H h = Objects();
        h.PersonCheck = new H.FakePersonCheck();

        (int status, JsonElement body) = H.Read(await h.Controller().Requirements(h.Project.Id, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("statementVersion").GetString().Should().Be("avatar-consent-2026-12");
        body.GetProperty("statements").GetProperty("en").GetString().Should().Be(AvatarConsentStatement.English);
        body.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).Should().Equal("Face", "HalfBody", "FullBody", "Motion", "Speech");
        body.GetProperty("entitled").GetBoolean().Should().BeTrue();

        JsonElement kinds = body.GetProperty("kinds");
        kinds.EnumerateObject().Select(k => k.Name).Should().Equal("person", "object");
        kinds.GetProperty("person").EnumerateObject().Select(k => k.Name).Should().Equal("enabled", "entitled");
        (kinds.GetProperty("person").GetProperty("enabled").GetBoolean(), kinds.GetProperty("person").GetProperty("entitled").GetBoolean()).Should().Be((true, true));
        (kinds.GetProperty("object").GetProperty("enabled").GetBoolean(), kinds.GetProperty("object").GetProperty("entitled").GetBoolean()).Should().Be((true, true));
        body.GetProperty("objectStatementVersion").GetString().Should().Be("avatar-object-2026-11");
        body.GetProperty("objectStatements").GetProperty("en").GetString().Should().Be(AvatarObjectStatement.English);
        body.GetProperty("objectStatements").GetProperty("it").GetString().Should().Be(AvatarObjectStatement.Italian);
        body.GetProperty("objectRoles").EnumerateArray().Select(e => e.GetString()).Should().Equal("Front", "Angle", "Detail", "Motion");
        body.GetProperty("objectLimits").EnumerateObject().Select(k => k.Name).Should().Equal("maxImages", "maxVideos");
        (body.GetProperty("objectLimits").GetProperty("maxImages").GetInt32(), body.GetProperty("objectLimits").GetProperty("maxVideos").GetInt32()).Should().Be((8, 3));
        body.GetProperty("personCheck").GetBoolean().Should().BeTrue();
        body.GetProperty("mediaConsent").EnumerateObject().Select(k => k.Name).Should().Equal("consented", "consentedAt");
        body.GetProperty("mediaConsent").GetProperty("consented").GetBoolean().Should().BeFalse();
        body.GetProperty("mediaConsent").GetProperty("consentedAt").ValueKind.Should().Be(JsonValueKind.Null);

        h.PersonCheck.Active = false;
        H.Read(await h.Controller().Requirements(h.Project.Id, CancellationToken.None)).Body
            .GetProperty("personCheck").GetBoolean().Should().BeFalse("no Vision provider, no check");
    }

    // ------------------------------------------------------------------ media consent

    [Fact]
    public async Task The_media_consent_is_recorded_once_with_who_gave_it_and_withdrawn_idempotently()
    {
        H h = Objects();
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;

        (int status, JsonElement body) = H.Read(await h.Controller().GetMediaConsent(p, ct));
        (status, body.GetProperty("consented").GetBoolean()).Should().Be((200, false));
        body.GetProperty("consentedAt").ValueKind.Should().Be(JsonValueKind.Null);

        (status, body) = H.Read(await h.Controller().GrantMediaConsent(p, ct));
        (status, body.GetProperty("consented").GetBoolean()).Should().Be((200, true));
        DateTime at = body.GetProperty("consentedAt").GetDateTime();
        using (InferenceApiDbContext db = h.NewDb())
        {
            Project project = await db.Projects.SingleAsync();
            project.SubjectMediaConsentByUserId.Should().Be(h.OwnerId);
            project.SubjectMediaConsentAt.Should().Be(at);
        }

        // A second grant by somebody else keeps the first record.
        H.User editor = h.InRole(OrganizationRole.Admin);
        (status, body) = H.Read(await h.Controller(user: editor).GrantMediaConsent(p, ct));
        (status, body.GetProperty("consentedAt").GetDateTime()).Should().Be((200, at));
        using (InferenceApiDbContext db = h.NewDb())
            (await db.Projects.SingleAsync()).SubjectMediaConsentByUserId.Should().Be(h.OwnerId);

        H.Read(await h.Controller().Requirements(p, ct)).Body.GetProperty("mediaConsent").GetProperty("consented").GetBoolean().Should().BeTrue();

        (status, body) = H.Read(await h.Controller().RevokeMediaConsent(p, ct));
        (status, body.GetProperty("consented").GetBoolean()).Should().Be((200, false));
        (status, body) = H.Read(await h.Controller().RevokeMediaConsent(p, ct));
        (status, body.GetProperty("consented").GetBoolean()).Should().Be((200, false));
        using InferenceApiDbContext after = h.NewDb();
        Project cleared = await after.Projects.SingleAsync();
        (cleared.SubjectMediaConsentAt, cleared.SubjectMediaConsentByUserId).Should().Be(((DateTime?)null, (Guid?)null));
    }

    [Fact]
    public async Task The_media_consent_needs_edit_rights_a_verified_email_and_the_callers_own_workspace()
    {
        H h = Objects();
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;

        H.Read(await h.Controller(user: H.Stranger()).GetMediaConsent(p, ct)).Status.Should().Be(404);
        H.Read(await h.Controller(user: H.Stranger()).GrantMediaConsent(p, ct)).Status.Should().Be(404);
        H.Read(await h.Controller(user: H.Stranger()).RevokeMediaConsent(p, ct)).Status.Should().Be(404);
        H.Read(await h.Controller(user: h.InRole(OrganizationRole.Billing)).GrantMediaConsent(p, ct)).Status.Should().Be(403);
        H.Read(await h.Controller(user: h.Owner(emailVerified: false)).GrantMediaConsent(p, ct)).Status.Should().Be(403);

        using InferenceApiDbContext db = h.NewDb();
        (await db.Projects.SingleAsync()).SubjectMediaConsentAt.Should().BeNull();
    }

    // ------------------------------------------------------------------ helpers

    private static ReplaceAvatarReferencesRequest ReattachObject(params ProjectFile[] files) => new(
        AvatarObjectStatement.English, "en", false, [.. files.Select(f => new AvatarReferenceRequest(f.Id, "Front"))]);

    private static async Task<(int, string?)> CreateCode(H h, params (Guid, string)[] references)
    {
        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.CreateObject(references) with { Name = "Object " + Guid.NewGuid().ToString("N")[..6] }, CancellationToken.None));
        return (status, status == 201 ? null : body.GetProperty("code").GetString());
    }

    private static async Task AssertNoAvatarAsync(H h)
    {
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(0);
        (await db.ProjectAvatarReferences.CountAsync()).Should().Be(0);
        (await db.ProjectAvatarEvents.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.Select(f => f.SourceKind).ToListAsync()).Should().NotContain(ProjectAvatarReference.ProjectFileSourceKind);
    }

    private static ProjectAvatar Row(H h, string name, AvatarSubjectKind kind) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = h.Project.Id,
        SubjectKind = kind,
        Name = name,
        ConsentAttestedByUserId = h.OwnerId,
        ConsentAttestedAt = DateTime.UtcNow,
        ConsentStatement = kind == AvatarSubjectKind.Object ? AvatarObjectStatement.English : AvatarConsentStatement.English,
        ConsentStatementVersion = kind == AvatarSubjectKind.Object ? AvatarObjectStatement.Version : AvatarConsentStatement.Version,
        ConsentLocale = "en",
        ConsentSubjectName = kind == AvatarSubjectKind.Object ? string.Empty : "Somebody",
        ConsentSubjectAdult = kind == AvatarSubjectKind.Person,
        RetentionUntil = kind == AvatarSubjectKind.Object ? ProjectAvatar.NoRetention : DateTime.UtcNow.AddDays(100),
        DisclosureLabel = kind == AvatarSubjectKind.Object ? AvatarDisclosure.MetadataOnly : AvatarDisclosure.Visible,
        CreatedByUserId = h.OwnerId
    };
}
