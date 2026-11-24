#nullable enable
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane A2 of docs/design/avatars.md: the avatar REST surface. Consent is verbatim (en and it) or nothing is written;
/// tenancy is 404 across organisations and 403 for a role too low; every reference check fails closed; revoke is
/// immediate and wins races; delete reports what it removed; the kill switch hides everything.
/// </summary>
public class ProjectAvatarsControllerTests
{
    // ------------------------------------------------------------------ consent statement

    [Fact]
    public void The_consent_statements_are_the_counsel_drafts_byte_for_byte()
    {
        // SHA-256 of the one-line constants in docs/legal/drafts/avatars-privacy-terms.md section 3 (UTF-8, no newline).
        // The first version (videos only) is never edited: rows attested under it store exactly these bytes.
        Hex(AvatarConsentStatement.English2026_11).Should().Be("c315fd47c51371c816f27b06603ecdf961dc1f64f4a2c0c89bf24eb3237ee3c1");
        Hex(AvatarConsentStatement.Italian2026_11).Should().Be("77838de775350c15560b69b236f698b85e337f94f4fd97dfcd697e8cf68e0eb5");
        Encoding.UTF8.GetByteCount(AvatarConsentStatement.English2026_11).Should().Be(456);
        Encoding.UTF8.GetByteCount(AvatarConsentStatement.Italian2026_11).Should().Be(553);
        AvatarConsentStatement.Version2026_11.Should().Be("avatar-consent-2026-11");
        AvatarConsentStatement.Locales.Should().Equal("en", "it");

        static string Hex(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    }

    [Theory]
    [InlineData("en", "en", true)]
    [InlineData("it", "it", true)]
    [InlineData("it", "en", false)]
    [InlineData("en", "it", false)]
    [InlineData("fr", "en", false)]
    [InlineData(null, "en", false)]
    public void A_statement_matches_only_its_own_locale(string? locale, string statementOf, bool expected)
    {
        AvatarConsentStatement.Matches(locale, AvatarConsentStatement.For(statementOf)).Should().Be(expected);
        AvatarConsentStatement.Matches(locale, AvatarConsentStatement.For(statementOf) + " ").Should().BeFalse("ordinal, untrimmed");
    }

    [Fact]
    public async Task Create_records_the_English_attestation_verbatim_links_the_files_and_is_selectable()
    {
        var h = new H();
        byte[] png = H.Png(1024, 1280);
        ProjectFile face = h.AddFile("face.png", "image/png", png);
        ProjectFile speech = h.AddProbedMedia("hello.wav", "audio/wav", 6.5);

        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, H.Create((face.Id, "Face"), (speech.Id, "Speech")), CancellationToken.None));

        status.Should().Be(201, body.ToString());
        body.GetProperty("selectable").GetBoolean().Should().BeTrue();
        body.GetProperty("consent").GetProperty("statement").GetString().Should().Be(AvatarConsentStatement.English);
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be("avatar-consent-2026-12", "a new attestation is always under the current text");
        body.GetProperty("consent").GetProperty("locale").GetString().Should().Be("en");
        body.GetProperty("consent").GetProperty("attestedByUserId").GetGuid().Should().Be(h.OwnerId);
        body.GetProperty("references").GetArrayLength().Should().Be(2);
        body.GetProperty("primaryReferenceId").ValueKind.Should().Be(JsonValueKind.String);

        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatarReference image = await db.ProjectAvatarReferences.SingleAsync(r => r.ProjectFileId == face.Id);
        image.ContentSha256.Should().Be(H.Sha256(png));
        (image.Width, image.Height).Should().Be((1024, 1280));
        (await db.ProjectAvatarReferences.SingleAsync(r => r.ProjectFileId == speech.Id)).DurationMs.Should().Be(6500);
        (await db.ProjectFiles.FindAsync(face.Id))!.SourceKind.Should().Be(ProjectAvatarReference.ProjectFileSourceKind);
        (await db.ProjectAvatars.SingleAsync()).RetentionUntil.Should().BeCloseTo(DateTime.UtcNow.AddDays(90), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Create_in_Italian_stores_the_Italian_statement_and_its_locale()
    {
        var h = new H();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id,
            H.Create((face.Id, "Face")) with { ConsentStatement = AvatarConsentStatement.Italian, ConsentLocale = "it" },
            CancellationToken.None));

        status.Should().Be(201, body.ToString());
        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar avatar = await db.ProjectAvatars.SingleAsync();
        (avatar.ConsentStatement, avatar.ConsentLocale, avatar.ConsentStatementVersion)
            .Should().Be((AvatarConsentStatement.Italian, "it", AvatarConsentStatement.Version));
    }

    [Theory]
    [InlineData("it", "en")]
    [InlineData("en", "it")]
    [InlineData("de", "en")]
    [InlineData("en", "weaker")]
    [InlineData("en", "padded")]
    public async Task Anything_but_the_exact_statement_of_the_locale_is_refused_with_the_required_statement(string locale, string statement)
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        string sent = statement switch
        {
            "weaker" => "they said it was fine",
            "padded" => AvatarConsentStatement.English + "\n",
            _ => AvatarConsentStatement.For(statement)!
        };

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id,
            H.Create((face.Id, "Face")) with { ConsentStatement = sent, ConsentLocale = locale }, CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be("consent_statement_required");
        body.GetProperty("requiredStatement").GetString().Should().Be(AvatarConsentStatement.For(locale) ?? AvatarConsentStatement.English);
        body.GetProperty("statements").GetProperty("it").GetString().Should().Be(AvatarConsentStatement.Italian);
        await AssertNothingWrittenAsync(h, face);
    }

    public static TheoryData<string, Func<CreateProjectAvatarRequest, CreateProjectAvatarRequest>> ConsentFieldCases() => new()
    {
        { "consent_subject_required", r => r with { ConsentSubjectName = "  " } },
        { "subject_must_be_adult", r => r with { SubjectIsAdult = false } },
        { "consent_basis_invalid", r => r with { ConsentBasis = "Parent" } },
        { "consent_basis_invalid", r => r with { ConsentBasis = "self" } },
        { "public_figure_requires_self", r => r with { ConsentBasis = "WrittenPermission", SubjectIsPublicFigure = true } },
        { "retention_out_of_range", r => r with { RetentionDays = 0 } },
        { "retention_out_of_range", r => r with { RetentionDays = 731 } },
        { "name_invalid", r => r with { Name = " " } },
        { "disclosure_attestation_required", r => r with { DisclosureLabel = "MetadataOnly" } },
        { "disclosure_label_invalid", r => r with { DisclosureLabel = "None" } },
    };

    [Theory]
    [MemberData(nameof(ConsentFieldCases))]
    public async Task Each_missing_or_wrong_consent_field_is_refused_and_nothing_is_written(
        string code, Func<CreateProjectAvatarRequest, CreateProjectAvatarRequest> change)
    {
        var h = new H();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, change(H.Create((face.Id, "Face"))), CancellationToken.None));

        status.Should().Be(400);
        body.GetProperty("code").GetString().Should().Be(code);
        await AssertNothingWrittenAsync(h, face);
    }

    [Fact]
    public async Task A_public_figure_is_allowed_only_as_the_account_holder_themselves()
    {
        var h = new H();
        ProjectFile face = h.AddImage();

        (int status, _) = H.Read(await h.Controller().Create(h.Project.Id,
            H.Create((face.Id, "Face")) with { SubjectIsPublicFigure = true, ConsentBasis = "Self" }, CancellationToken.None));

        status.Should().Be(201);
    }

    [Fact]
    public async Task MetadataOnly_disclosure_is_accepted_only_with_the_other_disclosure_ticked()
    {
        var h = new H();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id,
            H.Create((face.Id, "Face")) with { DisclosureLabel = "MetadataOnly", DisclosesOtherwise = true }, CancellationToken.None));

        status.Should().Be(201);
        body.GetProperty("disclosureLabel").GetString().Should().Be("MetadataOnly");
    }

    // ------------------------------------------------------------------ tenancy, role, email, plan

    [Fact]
    public async Task Another_organisation_gets_404_on_every_route_and_writes_nothing()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        ProjectFile face = h.AddImage("other.png", salt: 9);
        H.User stranger = H.Stranger();

        Statuses(await Every(h, avatarId, face, stranger)).Should().AllSatisfy(s => s.Should().Be(404));
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(1);
        (await db.ProjectAvatars.SingleAsync()).ConsentRevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Another_projects_avatar_is_404_through_this_projects_route()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        Project sibling = new() { Id = Guid.NewGuid(), Name = "Q", OwnerId = h.OwnerId, OrganizationId = h.OrganizationId };
        using (InferenceApiDbContext seed = h.NewDb())
        {
            seed.Projects.Add(sibling);
            await seed.SaveChangesAsync();
        }

        H.Read(await h.Controller().Get(sibling.Id, avatarId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await h.Controller().RevokeConsent(sibling.Id, avatarId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await h.Controller().Delete(sibling.Id, avatarId, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task A_billing_role_gets_403_on_every_route()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        ProjectFile face = h.AddImage("other.png", salt: 9);

        Statuses(await Every(h, avatarId, face, h.InRole(OrganizationRole.Billing))).Should().AllSatisfy(s => s.Should().Be(403));
    }

    [Fact]
    public async Task A_member_who_did_not_create_the_project_may_edit_but_not_delete()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        H.User member = h.Member();

        H.Read(await h.Controller(user: member).Get(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller(user: member).Delete(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await h.Controller(user: member).RevokeConsent(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);
    }

    [Fact]
    public async Task An_unverified_email_cannot_create_or_reattest()
    {
        var h = new H();
        ProjectFile face = h.AddImage();

        (int status, JsonElement body) = H.Read(await h.Controller(user: h.Owner(emailVerified: false)).Create(
            h.Project.Id, H.Create((face.Id, "Face")), CancellationToken.None));

        status.Should().Be(403);
        body.GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        await AssertNothingWrittenAsync(h, face);
    }

    [Fact]
    public async Task A_plan_without_avatars_gets_402_on_create_and_reattest_but_may_still_list_revoke_and_delete()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        ProjectFile face = h.AddImage("second.png", salt: 3);
        h.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = false, Tier = "trial" };

        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, H.Create((face.Id, "Face")) with { Name = "Other" }, CancellationToken.None));
        status.Should().Be(402);
        body.GetProperty("code").GetString().Should().Be(PlanGate.NotEntitledCode);
        body.GetProperty("feature").GetString().Should().Be("avatars");
        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, avatarId, Reattest(face), CancellationToken.None)).Status.Should().Be(402);

        H.Read(await h.Controller().Requirements(h.Project.Id, CancellationToken.None)).Body.GetProperty("entitled").GetBoolean().Should().BeFalse();
        H.Read(await h.Controller().List(h.Project.Id, CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller().RevokeConsent(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);
        H.Read(await h.Controller().Delete(h.Project.Id, avatarId, CancellationToken.None)).Status.Should().Be(200);
    }

    [Fact]
    public void The_avatars_feature_is_off_on_Trial_and_Creator_and_on_from_Pro_up_in_the_catalog()
    {
        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Billing", "plans.catalog.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        var allowed = doc.RootElement.GetProperty("plans").EnumerateArray()
            .Where(p => p.TryGetProperty("entitlements", out _))
            .ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("entitlements").GetProperty("avatarsAllowed").GetBoolean());

        allowed.Should().BeEquivalentTo(new System.Collections.Generic.Dictionary<string, bool>
        {
            ["trial"] = false,
            ["creator"] = false,
            ["pro"] = true,
            ["team"] = true,
            ["enterprise"] = true
        });
        PlanGate.CheckAvatars(new OrganizationEntitlementSet { AvatarsAllowed = false })!.StatusCode.Should().Be(402);
        new OrganizationEntitlementSet().AvatarsAllowed.Should().BeTrue("an omitted key means allowed, like every entitlement");
    }

    // ------------------------------------------------------------------ kill switch

    [Fact]
    public async Task With_the_kill_switch_off_every_route_answers_404_and_writes_nothing()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        ProjectFile face = h.AddImage("other.png", salt: 9);
        h.Options.Enabled = false;

        Statuses(await Every(h, avatarId, face, h.Owner())).Should().AllSatisfy(s => s.Should().Be(404));

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(1, "neither create nor delete ran");
        (await db.ProjectAvatars.SingleAsync()).ConsentRevokedAt.Should().BeNull("revoke did not run");
        (await db.ProjectFiles.FindAsync(face.Id))!.SourceKind.Should().BeNull();
        h.DeletedKeys.Should().BeEmpty();
    }

    [Fact]
    public void The_kill_switch_defaults_to_off()
    {
        new ReelBolt.Inference.Api.Services.Avatars.AvatarOptions().Enabled.Should().BeFalse();
    }

    // ------------------------------------------------------------------ fail-closed reference checks

    [Fact]
    public async Task A_file_of_another_project_or_a_quarantined_file_is_reference_file_not_found()
    {
        var h = new H();
        Guid otherProject = Guid.NewGuid();
        using (InferenceApiDbContext seed = h.NewDb())
        {
            seed.Projects.Add(new Project { Id = otherProject, Name = "Q", OwnerId = h.OwnerId, OrganizationId = h.OrganizationId });
            await seed.SaveChangesAsync();
        }

        ProjectFile foreign = h.AddFile("x.png", "image/png", H.Png(512, 512), projectId: otherProject);
        ProjectFile moderated = h.AddImage("m.png", salt: 2, tweak: f => f.ModerationStatus = FileModerationStatus.Quarantined);

        foreach (ProjectFile file in new[] { foreign, moderated })
            (await CreateCode(h, (file.Id, "Face"))).Should().Be((400, "reference_file_not_found"));
        (await CreateCode(h, (Guid.NewGuid(), "Face"))).Should().Be((400, "reference_file_not_found"));
    }

    [Fact]
    public async Task A_CSAM_quarantine_anywhere_in_the_project_stops_avatars_there_altogether()
    {
        var h = new H();
        ProjectFile clean = h.AddImage("ok.png", salt: 3);
        h.AddImage("c.png", salt: 1, tweak: f => f.CsamStatus = CsamScanStatus.Quarantined);

        (await CreateCode(h, (clean.Id, "Face"))).Should().Be((409, "PROJECT_CONTENT_QUARANTINED"));
    }

    [Fact]
    public async Task A_file_another_writer_owns_is_refused_and_its_source_kind_is_never_overwritten()
    {
        var h = new H();
        foreach (string kind in new[] { "github", "local_folder", "google_drive", "output_copy" })
        {
            ProjectFile synced = h.AddImage($"{kind}.png", salt: (byte)kind.Length, tweak: f => f.SourceKind = kind);

            (await CreateCode(h, (synced.Id, "Face"))).Should().Be((400, "reference_file_source_not_allowed"));
            using InferenceApiDbContext db = h.NewDb();
            (await db.ProjectFiles.FindAsync(synced.Id))!.SourceKind.Should().Be(kind);
        }
    }

    [Fact]
    public async Task Kinds_roles_counts_and_sizes_follow_the_limits()
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        ProjectFile heic = h.AddFile("x.heic", "image/heic", [1, 2, 3]);
        ProjectFile pdf = h.AddFile("x.pdf", "application/pdf", [1, 2, 3]);
        ProjectFile wav = h.AddProbedMedia("a.wav", "audio/wav", 5);
        ProjectFile huge = h.AddImage("huge.png", salt: 7, tweak: f => f.SizeBytes = ProjectAvatarLimits.MaxImageBytes + 1);

        (await CreateCode(h, (face.Id, "Face"), (heic.Id, "Face"))).Should().Be((400, "reference_kind_not_allowed"));
        (await CreateCode(h, (face.Id, "Face"), (pdf.Id, "Face"))).Should().Be((400, "reference_kind_not_allowed"));
        (await CreateCode(h, (face.Id, "Speech"))).Should().Be((400, "reference_role_not_allowed"));
        (await CreateCode(h, (face.Id, "Face"), (wav.Id, "Motion"))).Should().Be((400, "reference_role_not_allowed"));
        (await CreateCode(h, (face.Id, "Smile"))).Should().Be((400, "reference_role_invalid"));
        (await CreateCode(h, (wav.Id, "Speech"))).Should().Be((400, "no_image_reference"));
        (await CreateCode(h)).Should().Be((400, "no_image_reference"));
        (await CreateCode(h, (huge.Id, "Face"))).Should().Be((400, "references_too_large"));
        (await CreateCode(h, (face.Id, "Face"), (face.Id, "HalfBody"))).Should().Be((400, "duplicate_reference"));

        ProjectFile[] nine = [.. Enumerable.Range(10, 9).Select(i => h.AddImage($"f{i}.png", salt: (byte)i))];
        (await CreateCode(h, [.. nine.Select(f => (f.Id, "Face"))])).Should().Be((400, "too_many_references"));
    }

    [Theory]
    [InlineData(255, 1024)]
    [InlineData(5761, 3000)]
    [InlineData(400, 1100)]
    public async Task A_picture_outside_the_pixel_limits_is_refused(int width, int height)
    {
        var h = new H();
        ProjectFile face = h.AddImage(width: width, height: height);

        (await CreateCode(h, (face.Id, "Face"))).Should().Be((400, "reference_out_of_limits"));
    }

    [Fact]
    public async Task A_video_or_recording_is_checked_against_its_ingest_probe_and_waits_without_one()
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        ProjectFile unprobed = h.AddFile("raw.mp4", "video/mp4", [9, 9, 9]);
        ProjectFile tooLong = h.AddProbedMedia("long.mp4", "video/mp4", 16, "h264");
        ProjectFile vp9 = h.AddProbedMedia("vp9.mp4", "video/mp4", 5, "vp9");
        ProjectFile slow = h.AddProbedMedia("slow.mp4", "video/mp4", 5, "h264", fpsNum: 15);
        ProjectFile ntsc = h.AddProbedMedia("ntsc.mov", "video/quicktime", 5, "hevc", fpsNum: 24000, fpsDen: 1001);
        ProjectFile shortAudio = h.AddProbedMedia("short.mp3", "audio/mpeg", 1.5);

        (await CreateCode(h, (face.Id, "Face"), (unprobed.Id, "Motion"))).Should().Be((409, "AVATAR_REFERENCE_PENDING"));
        (await CreateCode(h, (face.Id, "Face"), (tooLong.Id, "Motion"))).Should().Be((400, "reference_out_of_limits"));
        (await CreateCode(h, (face.Id, "Face"), (vp9.Id, "Motion"))).Should().Be((400, "reference_out_of_limits"));
        (await CreateCode(h, (face.Id, "Face"), (slow.Id, "Motion"))).Should().Be((400, "reference_out_of_limits"));
        (await CreateCode(h, (face.Id, "Face"), (shortAudio.Id, "Speech"))).Should().Be((400, "reference_out_of_limits"));
        (await CreateCode(h, (face.Id, "Face"), (ntsc.Id, "Motion"))).Should().Be((201, null), "23.976 fps H.265 MOV is inside the envelope");
    }

    [Theory]
    [InlineData("voice.m4a", "audio/mp4")]            // a type the avatar does not take
    [InlineData("voice.mp3", "audio/mpeg")]           // labelled MP3, but the bytes are an M4A container
    [InlineData("voice.wav", "audio/wav")]            // labelled WAV, but the bytes are Ogg
    public async Task A_recording_that_is_not_wav_or_mp3_by_its_bytes_is_refused_at_the_reference(string name, string mime)
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        byte[] container = mime == "audio/wav" ? [.. "OggS"u8, 0, 2, 0, 0, 0, 0, 0, 0] : [0, 0, 0, 0x20, .. "ftypM4A "u8];
        ProjectFile voice = h.AddProbedMedia(name, mime, 6, bytes: container);

        (await CreateCode(h, (face.Id, "Face"), (voice.Id, "Speech"))).Should().Be(
            (400, mime == "audio/mp4" ? "reference_kind_not_allowed" : "reference_out_of_limits"));
    }

    [Theory]
    [InlineData(FileModerationStatus.Unmoderated, "AVATAR_REFERENCE_PENDING")]
    [InlineData(FileModerationStatus.Scanning, "AVATAR_REFERENCE_PENDING")]
    [InlineData(FileModerationStatus.Error, "AVATAR_REFERENCE_MODERATION_ERROR")]
    public async Task With_moderation_on_a_reference_without_a_clean_verdict_is_refused(FileModerationStatus status, string code)
    {
        var h = new H();
        h.Moderation.Enabled = true;
        ProjectFile face = h.AddImage(tweak: f => f.ModerationStatus = status);

        (await CreateCode(h, (face.Id, "Face"))).Should().Be((409, code));
        await AssertNothingWrittenAsync(h, face);
    }

    [Fact]
    public async Task With_moderation_off_an_unmoderated_reference_is_accepted()
    {
        var h = new H();
        ProjectFile face = h.AddImage(tweak: f => f.ModerationStatus = FileModerationStatus.Unmoderated);

        (await CreateCode(h, (face.Id, "Face"))).Should().Be((201, null));
    }

    [Fact]
    public async Task With_CSAM_matching_on_an_unscanned_reference_is_refused_and_queued_for_a_scan()
    {
        var h = new H();
        h.Csam.Arachnid.Enabled = true;
        ProjectFile notScanned = h.AddImage(tweak: f => f.CsamStatus = CsamScanStatus.NotScanned);
        ProjectFile pending = h.AddImage("p.png", salt: 4, tweak: f => f.CsamStatus = CsamScanStatus.Pending);

        (await CreateCode(h, (notScanned.Id, "Face"))).Should().Be((409, "AVATAR_REFERENCE_UNSCANNED"));
        (await CreateCode(h, (pending.Id, "Face"))).Should().Be((409, "AVATAR_REFERENCE_UNSCANNED"));

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.FindAsync(notScanned.Id))!.CsamStatus.Should().Be(CsamScanStatus.Pending, "a re-scan is queued");
        (await db.ProjectAvatars.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task With_CSAM_matching_on_a_recording_is_not_waited_on_because_hash_matching_never_covers_audio()
    {
        var h = new H();
        h.Csam.Arachnid.Enabled = true;
        ProjectFile face = h.AddImage();
        ProjectFile speech = h.AddProbedMedia("voice.mp3", "audio/mpeg", 6);
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.ProjectFiles.FindAsync(speech.Id))!.CsamStatus = CsamScanStatus.NotScanned;
            await db.SaveChangesAsync();
        }

        (await CreateCode(h, (face.Id, "Face"), (speech.Id, "Speech"))).Should().Be((201, null));

        using InferenceApiDbContext check = h.NewDb();
        (await check.ProjectFiles.FindAsync(speech.Id))!.CsamStatus.Should().Be(CsamScanStatus.NotScanned, "nothing is re-queued");
    }

    [Fact]
    public async Task A_file_declared_as_audio_that_holds_a_video_stream_waits_for_a_clean_scan()
    {
        var h = new H();
        h.Csam.Arachnid.Enabled = true;
        ProjectFile face = h.AddImage();
        ProjectFile coverArt = h.AddProbedMedia("voice.mp3", "audio/mpeg", 6, videoCodec: "mjpeg");
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.ProjectFiles.FindAsync(coverArt.Id))!.CsamStatus = CsamScanStatus.NotScanned;
            await db.SaveChangesAsync();
        }

        (await CreateCode(h, (face.Id, "Face"), (coverArt.Id, "Speech"))).Should().Be((409, "AVATAR_REFERENCE_UNSCANNED"));

        using InferenceApiDbContext check = h.NewDb();
        (await check.ProjectFiles.FindAsync(coverArt.Id))!.CsamStatus.Should().Be(CsamScanStatus.Pending, "a scan is queued");
        (await check.ProjectAvatars.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_project_with_quarantined_content_takes_no_new_avatar()
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        using (InferenceApiDbContext db = h.NewDb())
        {
            (await db.Projects.FindAsync(h.Project.Id))!.ContentQuarantinedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        (await CreateCode(h, (face.Id, "Face"))).Should().Be((409, "PROJECT_CONTENT_QUARANTINED"));
    }

    [Fact]
    public async Task The_avatar_limit_and_live_name_uniqueness_are_409()
    {
        var h = new H();
        await h.CreateReadyAsync("Anna");
        ProjectFile face = h.AddImage("again.png", salt: 77);
        H.Read(await h.Controller().Create(h.Project.Id, H.Create((face.Id, "Face")) with { Name = " Anna " }, CancellationToken.None))
            .Body.GetProperty("code").GetString().Should().Be("AVATAR_NAME_TAKEN");

        using (InferenceApiDbContext db = h.NewDb())
        {
            for (int i = 0; i < ProjectAvatarLimits.MaxAvatarsPerProject - 1; i++)
            {
                db.ProjectAvatars.Add(new ProjectAvatar
                {
                    Id = Guid.NewGuid(),
                    ProjectId = h.Project.Id,
                    Name = $"Filler {i}",
                    ConsentStatement = "x",
                    ConsentAttestedAt = DateTime.UtcNow,
                    ConsentAttestedByUserId = h.OwnerId,
                    RetentionUntil = DateTime.UtcNow.AddDays(1)
                });
            }

            await db.SaveChangesAsync();
        }

        ProjectFile another = h.AddImage("another.png", salt: 78);
        H.Read(await h.Controller().Create(h.Project.Id, H.Create((another.Id, "Face")) with { Name = "Zoe" }, CancellationToken.None))
            .Should().Match<(int Status, JsonElement Body)>(r => r.Status == 409 && r.Body.GetProperty("code").GetString() == "AVATAR_LIMIT_REACHED");
    }

    [Fact]
    public async Task A_linked_voice_must_be_a_selectable_voice_of_the_same_project()
    {
        var h = new H();
        ProjectFile face = h.AddImage();
        Guid revokedVoice = Guid.NewGuid();
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.ProjectVoices.Add(new ProjectVoice
            {
                Id = revokedVoice,
                ProjectId = h.Project.Id,
                Name = "V",
                Source = ProjectVoiceSource.Cloned,
                RemoteVoiceId = "r",
                ConsentAttestedAt = DateTime.UtcNow,
                ConsentAttestedByUserId = h.OwnerId,
                ConsentStatement = "s",
                ConsentSubjectName = "n",
                ConsentRevokedAt = DateTime.UtcNow,
                ConsentRevokedByUserId = h.OwnerId,
                RetentionUntil = DateTime.UtcNow.AddDays(3)
            });
            await db.SaveChangesAsync();
        }

        (await CreateCode(h, req => req with { ProjectVoiceId = Guid.NewGuid() }, (face.Id, "Face"))).Should().Be((400, "project_voice_not_found"));
        (await CreateCode(h, req => req with { ProjectVoiceId = revokedVoice }, (face.Id, "Face"))).Should().Be((400, "project_voice_not_selectable"));
    }

    // ------------------------------------------------------------------ revoke and races

    [Fact]
    public async Task Revoke_makes_the_avatar_unselectable_at_once_and_is_idempotent()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();

        (int status, JsonElement first) = H.Read(await h.Controller().RevokeConsent(h.Project.Id, avatarId, CancellationToken.None));
        status.Should().Be(200);
        first.GetProperty("selectable").GetBoolean().Should().BeFalse();
        first.GetProperty("unselectableReason").GetString().Should().Be(ProjectAvatar.ReasonConsentRevoked);
        DateTime revokedAt = first.GetProperty("consent").GetProperty("revokedAt").GetDateTime();

        (_, JsonElement second) = H.Read(await h.Controller(user: h.Member()).RevokeConsent(h.Project.Id, avatarId, CancellationToken.None));
        second.GetProperty("consent").GetProperty("revokedAt").GetDateTime().Should().Be(revokedAt);
        second.GetProperty("consent").GetProperty("revokedByUserId").GetGuid().Should().Be(h.OwnerId, "the first revoke's actor stays");

        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, avatarId, Reattest(h.AddImage("n.png", salt: 5)), CancellationToken.None))
            .Body.GetProperty("code").GetString().Should().Be("AVATAR_CONSENT_REVOKED");
    }

    [Fact]
    public async Task A_revoke_that_races_a_reattestation_retries_on_a_fresh_read_and_lands()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        // Between the revoke's read and its write, another request re-attests (which moves xmin).
        h.Interceptor.Action = async () =>
        {
            using InferenceApiDbContext other = h.NewDb();
            ProjectAvatar row = await other.ProjectAvatars.SingleAsync(a => a.Id == avatarId);
            row.ConsentAttestedAt = DateTime.UtcNow;
            await other.SaveChangesAsync();
        };

        (int status, JsonElement body) = H.Read(await h.Controller(h.NewDb(intercepted: true)).RevokeConsent(h.Project.Id, avatarId, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("unselectableReason").GetString().Should().Be(ProjectAvatar.ReasonConsentRevoked);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.SingleAsync()).ConsentRevokedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_reattestation_that_loses_to_a_revoke_writes_nothing_and_says_consent_was_withdrawn()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();
        ProjectFile replacement = h.AddImage("new.png", salt: 42);
        h.Interceptor.Action = async () =>
        {
            using InferenceApiDbContext other = h.NewDb();
            ProjectAvatar row = await other.ProjectAvatars.SingleAsync(a => a.Id == avatarId);
            row.ConsentRevokedAt = DateTime.UtcNow;
            row.ConsentRevokedByUserId = h.OwnerId;
            await other.SaveChangesAsync();
        };

        (int status, JsonElement body) = H.Read(await h.Controller(h.NewDb(intercepted: true))
            .ReplaceReferences(h.Project.Id, avatarId, Reattest(replacement), CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_CONSENT_REVOKED");
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatarReferences.AnyAsync(r => r.ProjectFileId == replacement.Id)).Should().BeFalse();
        (await db.ProjectFiles.FindAsync(replacement.Id))!.SourceKind.Should().BeNull();
    }

    [Fact]
    public async Task Changing_the_references_is_a_full_reattestation_that_restarts_retention_and_frees_dropped_files()
    {
        var h = new H();
        ProjectFile first = h.AddImage("a.png", salt: 1);
        Guid avatarId = await h.CreateReadyAsync("Anna", first);
        ProjectFile second = h.AddImage("b.png", salt: 2);

        (int status, JsonElement body) = H.Read(await h.Controller().ReplaceReferences(h.Project.Id, avatarId,
            Reattest(second) with { ConsentStatement = AvatarConsentStatement.Italian, ConsentLocale = "it", RetentionDays = 10 },
            CancellationToken.None));

        status.Should().Be(200, body.ToString());
        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatar avatar = await db.ProjectAvatars.SingleAsync();
        avatar.ConsentLocale.Should().Be("it");
        avatar.RetentionUntil.Should().BeCloseTo(DateTime.UtcNow.AddDays(10), TimeSpan.FromMinutes(1));
        (await db.ProjectAvatarReferences.Select(r => r.ProjectFileId).ToListAsync()).Should().Equal(second.Id);
        (await db.ProjectFiles.FindAsync(first.Id))!.SourceKind.Should().BeNull("no avatar lists it any more");
        (await db.ProjectFiles.FindAsync(second.Id))!.SourceKind.Should().Be(ProjectAvatarReference.ProjectFileSourceKind);

        H.Read(await h.Controller().ReplaceReferences(h.Project.Id, avatarId, Reattest(second) with { ConsentStatement = "ok" }, CancellationToken.None))
            .Body.GetProperty("code").GetString().Should().Be("consent_statement_required");
    }

    [Fact]
    public async Task Patch_edits_name_description_voice_and_label_but_never_consent()
    {
        var h = new H();
        Guid avatarId = await h.CreateReadyAsync();

        (int status, JsonElement body) = H.Read(await h.Controller().Update(h.Project.Id, avatarId,
            new UpdateProjectAvatarRequest(Name: "Anna R.", Description: "Our founder"), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("name").GetString().Should().Be("Anna R.");
        body.GetProperty("description").GetString().Should().Be("Our founder");
        body.GetProperty("consent").GetProperty("statement").GetString().Should().Be(AvatarConsentStatement.English);
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Delete_reports_what_it_removed_including_each_vendor_copy_and_writes_down_the_row()
    {
        var h = new H();
        ProjectFile face = h.AddImage("a.png", salt: 1);
        ProjectFile speech = h.AddProbedMedia("s.wav", "audio/wav", 4);
        ProjectFile release = h.AddFile("release.pdf", "application/pdf", [5, 5, 5]);
        Guid avatarId = await CreateWithAsync(h, req => req with { ConsentDocumentFileId = release.Id, ConsentBasis = "WrittenPermission" },
            (face.Id, "Face"), (speech.Id, "Speech"));
        Guid capability = await SeedCapabilityAsync(h);
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.ProjectAvatarVendorAssets.Add(new ProjectAvatarVendorAsset { Id = Guid.NewGuid(), AvatarId = avatarId, RemoteAssetId = "mm_file://1", ModelCapabilityId = capability });
            db.ProjectAvatarVendorAssets.Add(new ProjectAvatarVendorAsset { Id = Guid.NewGuid(), AvatarId = avatarId, RemoteAssetId = "mm_file://2" });
            await db.SaveChangesAsync();
        }

        h.Vendor.Outcome = AvatarVendorAssetDeleteOutcome.VendorRefused;

        (int status, JsonElement body) = H.Read(await h.Controller().Delete(h.Project.Id, avatarId, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("rowDeleted").GetBoolean().Should().BeTrue();
        body.GetProperty("references").GetProperty("deleted").GetInt32().Should().Be(2);
        body.GetProperty("references").GetProperty("failed").GetInt32().Should().Be(0);
        body.GetProperty("consentDocumentDeleted").GetBoolean().Should().BeTrue();
        body.GetProperty("vendorAssets").EnumerateArray().Select(v => (v.GetProperty("provider").GetString(), v.GetProperty("outcome").GetString()))
            .Should().BeEquivalentTo([("MiniMax", "vendor_refused"), ((string?)null, "vendor_refused")]);
        h.Vendor.Deleted.Should().BeEquivalentTo(["mm_file://1", "mm_file://2"]);
        h.DeletedKeys.Should().BeEquivalentTo([face.StorageKey, speech.StorageKey, release.StorageKey]);

        using InferenceApiDbContext after = h.NewDb();
        (await after.ProjectAvatars.AnyAsync()).Should().BeFalse("the row goes even when a vendor refuses");
        (await after.ProjectAvatarReferences.AnyAsync()).Should().BeFalse();
        (await after.ProjectAvatarVendorAssets.AnyAsync()).Should().BeFalse();
        (await after.ProjectFiles.AnyAsync(f => f.Id == face.Id || f.Id == speech.Id || f.Id == release.Id)).Should().BeFalse();
        (await after.StorageRetentionMarks.AnyAsync()).Should().BeFalse("every object went, so every mark was dropped");
    }

    [Fact]
    public async Task Delete_keeps_a_file_another_avatar_lists_and_reports_a_failed_object_delete()
    {
        var h = new H();
        ProjectFile shared = h.AddImage("shared.png", salt: 1);
        ProjectFile own = h.AddImage("own.png", salt: 2);
        Guid first = await h.CreateReadyAsync("Anna", shared, own);
        await h.CreateReadyAsync("Bea", shared);
        h.FailingDeletes.Add(own.StorageKey);
        h.Vendor.Throws = true;

        (int status, JsonElement body) = H.Read(await h.Controller().Delete(h.Project.Id, first, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("references").GetProperty("deleted").GetInt32().Should().Be(0);
        body.GetProperty("references").GetProperty("failed").GetInt32().Should().Be(1);
        body.GetProperty("references").GetProperty("kept").GetInt32().Should().Be(1);
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectFiles.FindAsync(shared.Id)).Should().NotBeNull("the other avatar still lists it");
        (await db.ProjectFiles.FindAsync(own.Id)).Should().BeNull("the row went with the avatar");
        (await db.StorageRetentionMarks.SingleAsync()).StorageKey.Should().Be(own.StorageKey, "the storage sweep finishes the object");
        (await db.ProjectAvatars.Select(a => a.Name).ToListAsync()).Should().Equal("Bea");
    }

    // ------------------------------------------------------------------ requirements

    [Fact]
    public async Task Requirements_serve_both_statements_the_version_and_every_limit()
    {
        var h = new H();
        h.Moderation.Enabled = true;

        (int status, JsonElement body) = H.Read(await h.Controller().Requirements(h.Project.Id, CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("statementVersion").GetString().Should().Be(AvatarConsentStatement.Version);
        body.GetProperty("statements").GetProperty("en").GetString().Should().Be(AvatarConsentStatement.English);
        body.GetProperty("statements").GetProperty("it").GetString().Should().Be(AvatarConsentStatement.Italian);
        body.GetProperty("limits").GetProperty("maxImages").GetInt32().Should().Be(ProjectAvatarLimits.MaxImages);
        body.GetProperty("limits").GetProperty("maxVideoMs").GetInt32().Should().Be(ProjectAvatarLimits.MaxVideoMs);
        body.GetProperty("mimeTypes").GetProperty("image").EnumerateArray().Select(e => e.GetString()).Should().NotContain("image/heic");
        body.GetProperty("roles").EnumerateArray().Select(e => e.GetString()).Should().Equal("Face", "HalfBody", "FullBody", "Motion", "Speech");
        body.GetProperty("entitled").GetBoolean().Should().BeTrue();
        body.GetProperty("moderationEnabled").GetBoolean().Should().BeTrue();
        body.GetProperty("csamScanningEnabled").GetBoolean().Should().BeFalse();
    }

    // ------------------------------------------------------------------ helpers

    internal static ReplaceAvatarReferencesRequest Reattest(params ProjectFile[] files) => new(
        AvatarConsentStatement.English, "en", true, [.. files.Select(f => new AvatarReferenceRequest(f.Id, "Face"))], 30);

    private static async Task<(int, string?)> CreateCode(H h, params (Guid, string)[] references) =>
        await CreateCode(h, r => r, references);

    private static async Task<(int, string?)> CreateCode(H h, Func<CreateProjectAvatarRequest, CreateProjectAvatarRequest> change, params (Guid, string)[] references)
    {
        (int status, JsonElement body) = H.Read(await h.Controller().Create(
            h.Project.Id, change(H.Create(references) with { Name = "Avatar " + Guid.NewGuid().ToString("N")[..6] }), CancellationToken.None));
        return (status, status == 201 ? null : body.GetProperty("code").GetString());
    }

    private static async Task<Guid> CreateWithAsync(H h, Func<CreateProjectAvatarRequest, CreateProjectAvatarRequest> change, params (Guid, string)[] references)
    {
        (int status, JsonElement body) = H.Read(await h.Controller().Create(h.Project.Id, change(H.Create(references)), CancellationToken.None));
        status.Should().Be(201, body.ToString());
        return body.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SeedCapabilityAsync(H h)
    {
        var view = new ReelBolt.Shared.Inference.ModelCapabilityView
        {
            Id = Guid.NewGuid(),
            Name = "minimax",
            Kind = InferenceProviderKind.MiniMax,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://api.minimax.io",
            ModelName = "MiniMax-H3"
        };
        using InferenceApiDbContext db = h.NewDb();
        db.AddFlat(view);
        await db.SaveChangesAsync();
        return view.Id;
    }

    private static async Task AssertNothingWrittenAsync(H h, ProjectFile file)
    {
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(0);
        (await db.ProjectAvatarReferences.CountAsync()).Should().Be(0);
        (await db.ProjectFiles.FindAsync(file.Id))!.SourceKind.Should().BeNull();
    }

    private static int[] Statuses(IActionResult?[] results) => [.. results.Select(r => H.Read(r).Status)];

    /// <summary>One call of each of the nine routes, as <paramref name="user"/>.</summary>
    private static async Task<IActionResult?[]> Every(H h, Guid avatarId, ProjectFile face, ReelBolt.Shared.Auth.ICurrentUser user)
    {
        Guid p = h.Project.Id;
        CancellationToken ct = CancellationToken.None;
        return
        [
            (await h.Controller(user: user).Requirements(p, ct)).Result,
            (await h.Controller(user: user).List(p, ct)).Result,
            (await h.Controller(user: user).Get(p, avatarId, ct)).Result,
            (await h.Controller(user: user).Create(p, H.Create((face.Id, "Face")) with { Name = "New" }, ct)).Result,
            (await h.Controller(user: user).Update(p, avatarId, new UpdateProjectAvatarRequest(Name: "Renamed"), ct)).Result,
            (await h.Controller(user: user).ReplaceReferences(p, avatarId, Reattest(face), ct)).Result,
            (await h.Controller(user: user).RevokeConsent(p, avatarId, ct)).Result,
            (await h.Controller(user: user).Usage(p, avatarId, ct)).Result,
            (await h.Controller(user: user).Delete(p, avatarId, ct)).Result,
        ];
    }
}
