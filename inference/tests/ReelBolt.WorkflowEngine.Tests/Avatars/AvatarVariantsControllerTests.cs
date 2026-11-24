#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L12 of docs/design/avatar-objects.md, "Avatar variants" made FROM OTHER FILES on the avatar REST surface: a
/// variant is a complete avatar of its own that names its original, with a label and a description. One level only,
/// the kind of its original, the same project, at most ten per original, a label used once per original; variants are
/// not counted toward the project's limit; deleting the original leaves them, ungrouped; a variant of a person is the
/// full consent form again for the same person.
/// </summary>
public class AvatarVariantsControllerTests
{
    private static H Both()
    {
        var h = new H();
        h.Options.Enabled = true;
        h.Options.Objects.Enabled = true;
        return h;
    }

    private static CreateProjectAvatarRequest ObjectVariant(H h, Guid originalId, string label, string? description = "matte red paint, black rims", byte salt = 50) =>
        H.CreateObject((h.AddImage($"variant-{label}-{salt}.png", salt: salt).Id, "Front")) with
        {
            Name = null!,
            SubjectKind = null,
            VariantOfAvatarId = originalId,
            VariantLabel = label,
            VariantDescription = description
        };

    private static async Task<(int Status, JsonElement Body)> CreateAsync(H h, CreateProjectAvatarRequest request) =>
        H.Read(await h.Controller().Create(h.Project.Id, request, CancellationToken.None));

    // ------------------------------------------------------------------ shape

    [Fact]
    public async Task A_variant_from_other_files_is_a_complete_avatar_that_names_its_original()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");

        (int status, JsonElement body) = await CreateAsync(h, ObjectVariant(h, car, " Red "));

        status.Should().Be(201, body.ToString());
        body.GetProperty("subjectKind").GetString().Should().Be("Object", "a variant has the kind of its original when none is sent");
        body.GetProperty("variantOfAvatarId").GetGuid().Should().Be(car);
        body.GetProperty("variantLabel").GetString().Should().Be("Red");
        body.GetProperty("variantDescription").GetString().Should().Be("matte red paint, black rims");
        body.GetProperty("variantCount").GetInt32().Should().Be(0);
        body.GetProperty("name").GetString().Should().Be("Car · Red", "a variant with no name of its own is named after its original and label");
        body.GetProperty("selectable").GetBoolean().Should().BeTrue("a variant has its own files and its own attestation");
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be(AvatarObjectStatement.Version);

        (int _, JsonElement original) = H.Read(await h.Controller().Get(h.Project.Id, car, CancellationToken.None));
        original.GetProperty("variantCount").GetInt32().Should().Be(1);
        original.GetProperty("variantOfAvatarId").ValueKind.Should().Be(JsonValueKind.Null);
        original.GetProperty("variantLabel").ValueKind.Should().Be(JsonValueKind.Null);

        using InferenceApiDbContext db = h.NewDb();
        ProjectAvatarEvent created = await db.ProjectAvatarEvents.SingleAsync(e => e.AvatarId == body.GetProperty("id").GetGuid());
        JsonDocument.Parse(created.OutcomesJson!).RootElement.GetProperty("variant").GetBoolean().Should().BeTrue();
        created.OutcomesJson.Should().NotContain("Red").And.NotContain("matte", "the audit trail holds counts and outcome words only");
    }

    [Fact]
    public async Task A_variant_keeps_a_name_of_its_own_when_one_is_sent()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");

        (int status, JsonElement body) = await CreateAsync(h, ObjectVariant(h, car, "Red") with { Name = "The red one", SubjectKind = "Object" });

        status.Should().Be(201, body.ToString());
        body.GetProperty("name").GetString().Should().Be("The red one");
    }

    [Fact]
    public async Task A_variant_of_a_variant_attaches_to_the_original()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");
        (_, JsonElement red) = await CreateAsync(h, ObjectVariant(h, car, "Red", salt: 51));

        (int status, JsonElement darker) = await CreateAsync(h, ObjectVariant(h, red.GetProperty("id").GetGuid(), "Dark red", salt: 52));

        status.Should().Be(201, darker.ToString());
        darker.GetProperty("variantOfAvatarId").GetGuid().Should().Be(car, "one level only");
        darker.GetProperty("name").GetString().Should().Be("Car · Dark red");
        (_, JsonElement original) = H.Read(await h.Controller().Get(h.Project.Id, car, CancellationToken.None));
        original.GetProperty("variantCount").GetInt32().Should().Be(2);
        (_, JsonElement first) = H.Read(await h.Controller().Get(h.Project.Id, red.GetProperty("id").GetGuid(), CancellationToken.None));
        first.GetProperty("variantCount").GetInt32().Should().Be(0, "a variant never has variants of its own");
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task A_variant_has_the_kind_of_its_original()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        Guid car = await h.CreateObjectAsync("Car");

        (int asObject, JsonElement objectBody) = await CreateAsync(h, ObjectVariant(h, anna, "Red") with { SubjectKind = "Object" });
        asObject.Should().Be(400);
        objectBody.GetProperty("code").GetString().Should().Be("variant_kind_mismatch");

        (int asPerson, JsonElement personBody) = await CreateAsync(h,
            H.Create((h.AddImage("p.png", salt: 77).Id, "Face")) with { SubjectKind = "Person", VariantOfAvatarId = car, VariantLabel = "Red", VariantDescription = "red" });
        asPerson.Should().Be(400);
        personBody.GetProperty("code").GetString().Should().Be("variant_kind_mismatch");

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(2, "a refusal writes nothing");
    }

    [Fact]
    public async Task The_original_must_be_an_avatar_of_this_project()
    {
        H h = Both();
        // Another project's avatar, written straight into the store.
        Guid otherProject = Guid.NewGuid();
        Guid foreign;
        using (InferenceApiDbContext db = h.NewDb())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", OwnerId = h.OwnerId, OrganizationId = h.OrganizationId });
            foreign = AvatarSeed.Attach(db, otherProject, "Elsewhere").Id;
        }

        foreach (Guid id in new[] { foreign, Guid.NewGuid() })
        {
            (int status, JsonElement body) = await CreateAsync(h, ObjectVariant(h, id, "Red", salt: (byte)(60 + (id == foreign ? 0 : 1))));
            status.Should().Be(400);
            body.GetProperty("code").GetString().Should().Be("variant_original_not_found");
        }
    }

    [Fact]
    public async Task A_variant_needs_a_label_and_a_description_and_they_need_an_original()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");

        (int noLabel, JsonElement a) = await CreateAsync(h, ObjectVariant(h, car, " ", salt: 61));
        (int longLabel, JsonElement b) = await CreateAsync(h, ObjectVariant(h, car, new string('r', 61), salt: 62));
        (int noDescription, JsonElement c) = await CreateAsync(h, ObjectVariant(h, car, "Red", description: null, salt: 63));
        (int longDescription, JsonElement d) = await CreateAsync(h, ObjectVariant(h, car, "Red", description: new string('d', 501), salt: 64));
        (int orphan, JsonElement e) = await CreateAsync(h,
            H.CreateObject((h.AddImage("o.png", salt: 65).Id, "Front")) with { Name = "Loose", VariantLabel = "Red", VariantDescription = "red" });

        (noLabel, a.GetProperty("code").GetString()).Should().Be((400, "variant_label_invalid"));
        (longLabel, b.GetProperty("code").GetString()).Should().Be((400, "variant_label_invalid"));
        (noDescription, c.GetProperty("code").GetString()).Should().Be((400, "variant_description_invalid"));
        (longDescription, d.GetProperty("code").GetString()).Should().Be((400, "variant_description_invalid"));
        (orphan, e.GetProperty("code").GetString()).Should().Be((400, "variant_fields_without_original"));

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_label_is_used_once_per_original()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");
        Guid van = await h.CreateObjectAsync("Van");
        (await CreateAsync(h, ObjectVariant(h, car, "Red", salt: 70))).Status.Should().Be(201);

        (int again, JsonElement body) = await CreateAsync(h, ObjectVariant(h, car, "Red", salt: 71) with { Name = "Another red" });
        (int otherOriginal, JsonElement vanRed) = await CreateAsync(h, ObjectVariant(h, van, "Red", salt: 72));

        again.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("VARIANT_LABEL_TAKEN");
        otherOriginal.Should().Be(201, vanRed.ToString());
    }

    [Fact]
    public async Task An_avatar_has_at_most_ten_variants_and_they_do_not_count_toward_the_projects_limit()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");
        for (int i = 0; i < ProjectAvatarLimits.MaxVariantsPerAvatar; i++)
            (await CreateAsync(h, ObjectVariant(h, car, "V" + i, salt: (byte)(100 + i)))).Status.Should().Be(201);

        (int eleventh, JsonElement body) = await CreateAsync(h, ObjectVariant(h, car, "V10", salt: 120));
        eleventh.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("VARIANT_LIMIT_REACHED");

        // 1 original and 10 variants: the project still takes 19 more originals, and then no more.
        for (int i = 0; i < ProjectAvatarLimits.MaxObjectsPerProject - 1; i++)
            await h.CreateObjectAsync("Thing " + i, h.AddImage($"thing-{i}.png", salt: (byte)(130 + i)));
        (int over, JsonElement limit) = await CreateAsync(h, H.CreateObject((h.AddImage("last.png", salt: 250).Id, "Front")) with { Name = "One too many" });
        over.Should().Be(409);
        limit.GetProperty("code").GetString().Should().Be("AVATAR_LIMIT_REACHED");

        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.CountAsync(a => a.VariantOfAvatarId == null)).Should().Be(ProjectAvatarLimits.MaxObjectsPerProject);
        (await db.ProjectAvatars.CountAsync(a => a.VariantOfAvatarId == car)).Should().Be(ProjectAvatarLimits.MaxVariantsPerAvatar);
    }

    // ------------------------------------------------------------------ delete

    [Fact]
    public async Task Deleting_the_original_leaves_its_variants_ungrouped()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");
        (_, JsonElement red) = await CreateAsync(h, ObjectVariant(h, car, "Red", salt: 80));
        Guid redId = red.GetProperty("id").GetGuid();

        (int deleted, _) = H.Read(await h.Controller().Delete(h.Project.Id, car, CancellationToken.None));
        deleted.Should().Be(200);

        (int status, JsonElement after) = H.Read(await h.Controller().Get(h.Project.Id, redId, CancellationToken.None));
        status.Should().Be(200);
        after.GetProperty("variantOfAvatarId").ValueKind.Should().Be(JsonValueKind.Null, "the variant stays, ungrouped");
        after.GetProperty("variantLabel").GetString().Should().Be("Red");
        after.GetProperty("selectable").GetBoolean().Should().BeTrue();
        using InferenceApiDbContext db = h.NewDb();
        (await db.ProjectAvatars.AnyAsync(a => a.Id == car)).Should().BeFalse();
    }

    // ------------------------------------------------------------------ people

    [Fact]
    public async Task A_variant_of_a_person_is_the_full_consent_form_again_for_the_same_person()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        ProjectFile photo = h.AddImage("anna-suit.png", salt: 90);

        // Who the person is and the basis are taken from the original when left out.
        (int status, JsonElement body) = await CreateAsync(h, H.Create((photo.Id, "Face")) with
        {
            Name = null!,
            ConsentSubjectName = null,
            ConsentBasis = null,
            VariantOfAvatarId = anna,
            VariantLabel = "Suit",
            VariantDescription = "in a dark suit"
        });

        status.Should().Be(201, body.ToString());
        body.GetProperty("subjectKind").GetString().Should().Be("Person");
        body.GetProperty("name").GetString().Should().Be("Anna · Suit");
        body.GetProperty("variantOfAvatarId").GetGuid().Should().Be(anna);
        body.GetProperty("consent").GetProperty("subjectName").GetString().Should().Be("Anna Rossi");
        body.GetProperty("consent").GetProperty("basis").GetString().Should().Be("Self");
        body.GetProperty("consent").GetProperty("statementVersion").GetString().Should().Be(AvatarConsentStatement.Version);
        body.GetProperty("retentionUntil").ValueKind.Should().Be(JsonValueKind.String, "a variant of a person has its own keep-until date");
    }

    [Fact]
    public async Task A_variant_of_a_person_cannot_name_another_person_or_basis_and_still_needs_the_statement()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        CreateProjectAvatarRequest Variant(byte salt) => H.Create((h.AddImage($"v{salt}.png", salt: salt).Id, "Face")) with
        {
            Name = "Anna in a suit",
            VariantOfAvatarId = anna,
            VariantLabel = "Suit" + salt,
            VariantDescription = "in a dark suit"
        };

        (int otherName, JsonElement a) = await CreateAsync(h, Variant(91) with { ConsentSubjectName = "Bea Bianchi" });
        (int otherBasis, JsonElement b) = await CreateAsync(h, Variant(92) with { ConsentBasis = "WrittenPermission" });
        (int noStatement, JsonElement c) = await CreateAsync(h, Variant(93) with { ConsentStatement = "yes" });
        (int notAdult, JsonElement d) = await CreateAsync(h, Variant(94) with { SubjectIsAdult = false });
        (int same, JsonElement e) = await CreateAsync(h, Variant(95) with { ConsentSubjectName = "Anna Rossi", ConsentBasis = "Self" });

        (otherName, a.GetProperty("code").GetString(), a.GetProperty("field").GetString()).Should().Be((400, "variant_subject_mismatch", "consentSubjectName"));
        (otherBasis, b.GetProperty("code").GetString(), b.GetProperty("field").GetString()).Should().Be((400, "variant_subject_mismatch", "consentBasis"));
        (noStatement, c.GetProperty("code").GetString()).Should().Be((400, "consent_statement_required"));
        (notAdult, d.GetProperty("code").GetString()).Should().Be((400, "subject_must_be_adult"));
        same.Should().Be(201, e.ToString());
    }

    [Fact]
    public async Task A_person_whose_consent_was_withdrawn_gets_no_variant()
    {
        H h = Both();
        Guid anna = await h.CreateReadyAsync("Anna");
        H.Read(await h.Controller().RevokeConsent(h.Project.Id, anna, CancellationToken.None)).Status.Should().Be(200);

        (int status, JsonElement body) = await CreateAsync(h, H.Create((h.AddImage("late.png", salt: 96).Id, "Face")) with
        {
            Name = "Anna later",
            VariantOfAvatarId = anna,
            VariantLabel = "Later",
            VariantDescription = "a later photo"
        });

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("AVATAR_CONSENT_REVOKED");
    }

    // ------------------------------------------------------------------ edit

    [Fact]
    public async Task Patch_edits_a_variants_label_and_description_and_refuses_them_on_an_original()
    {
        H h = Both();
        Guid car = await h.CreateObjectAsync("Car");
        (_, JsonElement red) = await CreateAsync(h, ObjectVariant(h, car, "Red", salt: 97));
        (_, JsonElement blue) = await CreateAsync(h, ObjectVariant(h, car, "Blue", salt: 98));
        Guid redId = red.GetProperty("id").GetGuid();

        (int edited, JsonElement body) = H.Read(await h.Controller().Update(
            h.Project.Id, redId, new UpdateProjectAvatarRequest(VariantLabel: " Crimson ", VariantDescription: "deep crimson, gloss"), CancellationToken.None));
        (int taken, JsonElement clash) = H.Read(await h.Controller().Update(
            h.Project.Id, blue.GetProperty("id").GetGuid(), new UpdateProjectAvatarRequest(VariantLabel: "Crimson"), CancellationToken.None));
        (int cleared, JsonElement empty) = H.Read(await h.Controller().Update(
            h.Project.Id, redId, new UpdateProjectAvatarRequest(VariantDescription: " "), CancellationToken.None));
        (int original, JsonElement notVariant) = H.Read(await h.Controller().Update(
            h.Project.Id, car, new UpdateProjectAvatarRequest(VariantLabel: "Base"), CancellationToken.None));

        edited.Should().Be(200, body.ToString());
        body.GetProperty("variantLabel").GetString().Should().Be("Crimson");
        body.GetProperty("variantDescription").GetString().Should().Be("deep crimson, gloss");
        body.GetProperty("name").GetString().Should().Be("Car · Red", "the name is the avatar's own and is edited on its own");
        (taken, clash.GetProperty("code").GetString()).Should().Be((409, "VARIANT_LABEL_TAKEN"));
        (cleared, empty.GetProperty("code").GetString()).Should().Be((400, "variant_description_invalid"));
        (original, notVariant.GetProperty("code").GetString()).Should().Be((400, "not_a_variant"));
    }

    [Fact]
    public void The_limits_are_the_design_figures()
    {
        ProjectAvatarLimits.MaxVariantsPerAvatar.Should().Be(10);
        ProjectAvatarLimits.MaxVariantLabelLength.Should().Be(60);
        ProjectAvatarLimits.MaxVariantDescriptionLength.Should().Be(500);
        ProjectAvatarLimits.MaxVariantInstructionLength.Should().Be(1000);
        ProjectAvatar.VariantDisplayName(" Car ", " Red ").Should().Be("Car · Red");
        ProjectAvatar.VariantDisplayName(new string('n', 199), "Red").Should().HaveLength(ProjectAvatarLimits.MaxNameLength);
    }
}
