using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Brand;

/// <summary>
/// P4-01 coverage for SPEC 16.3's editor defaults: the heading font's mapping onto the editor's
/// thirteen faces (exact, keyword, null), the inferred 204 when the project has no usable design
/// member, the approved-version read, and the "brand fills only what the caller omitted" precedence
/// used by the assistant and the <c>layers/text</c> route. Direct controller construction on an
/// InMemory context, the same harness the other design-system controller tests use.
/// </summary>
public sealed class EditorDefaultsTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("cccccccc-0000-0000-0000-0000000000a1");
    private static readonly Guid ForeignOrg = Guid.Parse("dddddddd-0000-0000-0000-0000000000a2");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();

    public EditorDefaultsTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"editor-defaults-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------------------------------------------
    // 1. The font mapping (exact, keyword, null)
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    // Exact names from the editor's own list, any casing.
    [InlineData("Inter", "Inter")]
    [InlineData("montserrat", "Montserrat")]
    [InlineData("Chakra Petch", "Chakra Petch")]
    [InlineData("JetBrains Mono", "JetBrains Mono")]
    [InlineData("Roboto Condensed", "Roboto Condensed")]
    [InlineData("Playfair Display", "Playfair Display")]
    // Keyword groups.
    [InlineData("Helvetica Neue", "Inter")]
    [InlineData("Arial", "Inter")]
    [InlineData("system-ui", "Inter")]
    [InlineData("Roboto", "Roboto Condensed")]
    [InlineData("Gotham", "Montserrat")]
    [InlineData("Proxima Nova", "Montserrat")]
    [InlineData("Raleway", "Montserrat")]
    [InlineData("Nunito", "Poppins")]
    [InlineData("Quicksand", "Poppins")]
    [InlineData("DM Sans", "Poppins")]
    [InlineData("Oswald", "Oswald")]
    [InlineData("Impact", "Bebas Neue")]
    [InlineData("Anton SC", "Anton")]
    [InlineData("Courier New", "JetBrains Mono")]
    [InlineData("Comic Sans MS", "Bangers")]
    [InlineData("Georgia", "Playfair Display")]
    [InlineData("Brush Script MT", null)]
    public void NearestEditorFont_maps_onto_the_editors_own_list(string family, string? expected)
    {
        string? actual = BrandFontMatcher.NearestEditorFont([family], BrandFontMatcher.EditorFontFamilies);

        actual.Should().Be(expected);
    }

    [Fact]
    public void NearestEditorFont_prefers_an_exact_match_anywhere_in_the_stack()
    {
        BrandFontMatcher.NearestEditorFont(["Satoshi", "Inter"], BrandFontMatcher.EditorFontFamilies)
            .Should().Be("Inter");
    }

    [Fact]
    public void NearestEditorFont_is_null_without_families_or_an_editor_list()
    {
        BrandFontMatcher.NearestEditorFont([], BrandFontMatcher.EditorFontFamilies).Should().BeNull();
        BrandFontMatcher.NearestEditorFont(null, BrandFontMatcher.EditorFontFamilies).Should().BeNull();
        BrandFontMatcher.NearestEditorFont(["Inter"], []).Should().BeNull();
    }

    [Fact]
    public void NearestEditorFont_only_returns_a_face_the_editor_list_offers()
    {
        // "Helvetica" would map to Inter, but this caller offers no Inter.
        BrandFontMatcher.NearestEditorFont(["Helvetica Neue"], ["Anton", "Bangers"]).Should().BeNull();
    }

    // ---------------------------------------------------------------------------------------------------
    // 2. The route: 204 without a usable member, 200 with one
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Editor_defaults_is_204_without_a_design_system()
    {
        Project project = SeedProject(HomeOrg, _ownerId);

        ActionResult<EditorDefaultsResponse> response = await Controller(Owner())
            .GetEditorDefaults(project.Id, CancellationToken.None);

        response.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Editor_defaults_is_204_when_the_current_version_is_not_approved()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedDesignSystem(project, DesignVersionState.Draft, Document("Inter", "#123456"));

        ActionResult<EditorDefaultsResponse> response = await Controller(Owner())
            .GetEditorDefaults(project.Id, CancellationToken.None);

        response.Result.Should().BeOfType<NoContentResult>();
    }

    [Fact]
    public async Task Editor_defaults_returns_the_mapped_font_and_the_text_colour()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedDesignSystem(project, DesignVersionState.Approved, Document("Inter", "#123456"));

        ActionResult<EditorDefaultsResponse> response = await Controller(Owner())
            .GetEditorDefaults(project.Id, CancellationToken.None);

        EditorDefaultsResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<EditorDefaultsResponse>().Subject;
        body.FontFamily.Should().Be("Inter");
        body.FontWeight.Should().Be(700);
        body.Color.Should().Be("#123456");
        body.StrokeColor.Should().Be("#000000");
        body.Warnings.Should().BeEmpty();
    }

    [Fact]
    public async Task Editor_defaults_returns_the_text_colour_of_a_canonical_document()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        JsonObject document = DesignDocument.New();
        DesignDocument.SetToken(
            document,
            "typography.family.heading",
            "fontFamily",
            new JsonArray("Inter"),
            new TokenProvenance("imported", "test", null, null),
            1.0);
        DesignDocument.SetToken(
            document,
            "color.text",
            "color",
            new JsonObject { ["hex"] = "#123456", ["colorSpace"] = "srgb" },
            new TokenProvenance("imported", "test", null, null),
            1.0);
        SeedDesignSystem(project, DesignVersionState.Approved, document.ToJsonString());

        ActionResult<EditorDefaultsResponse> response = await Controller(Owner())
            .GetEditorDefaults(project.Id, CancellationToken.None);

        EditorDefaultsResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<EditorDefaultsResponse>().Subject;
        body.Color.Should().Be("#123456");
    }

    [Fact]
    public async Task Editor_defaults_warns_when_the_brand_face_is_substituted()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedDesignSystem(project, DesignVersionState.Approved, Document("Helvetica Neue", "#123456"));

        ActionResult<EditorDefaultsResponse> response = await Controller(Owner())
            .GetEditorDefaults(project.Id, CancellationToken.None);

        EditorDefaultsResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<EditorDefaultsResponse>().Subject;
        body.FontFamily.Should().Be("Inter");
        body.Warnings.Should().Equal("font_substituted:Helvetica Neue->Inter");
    }

    [Fact]
    public async Task Editor_defaults_returns_a_null_font_when_the_brand_face_is_not_offered()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedDesignSystem(project, DesignVersionState.Approved, Document("Wingdings", "#123456"));

        ActionResult<EditorDefaultsResponse> response = await Controller(Owner())
            .GetEditorDefaults(project.Id, CancellationToken.None);

        EditorDefaultsResponse body = response.Result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeOfType<EditorDefaultsResponse>().Subject;
        body.FontFamily.Should().BeNull("the editor keeps its own default rather than a face the brand did not ask for");
        body.Color.Should().Be("#123456");
    }

    [Fact]
    public async Task Editor_defaults_for_a_foreign_project_is_404()
    {
        Project project = SeedProject(HomeOrg, _ownerId);
        SeedDesignSystem(project, DesignVersionState.Approved, Document("Inter", "#123456"));

        ActionResult<EditorDefaultsResponse> response = await Controller(
                new FakeUser(_strangerId, ForeignOrg, OrganizationRole.Owner))
            .GetEditorDefaults(project.Id, CancellationToken.None);

        response.Result.Should().BeOfType<NotFoundResult>();
    }

    // ---------------------------------------------------------------------------------------------------
    // 3. Omitted-vs-explicit precedence (the assistant and the layers/text route share this)
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void With_editor_defaults_fills_a_field_left_at_the_legacy_default()
    {
        var defaults = new EditorDefaultsResponse("Inter", 700, "#123456", "#654321", []);
        var content = new TextContentDto("Title", Stroke: new TextStrokeDto("#000000", 6));

        TextContentDto merged = EditImageDocumentSupport.WithEditorDefaults(content, defaults);

        merged.FontFamily.Should().Be("Inter");
        merged.Color.Should().Be("#123456");
        merged.Stroke!.Color.Should().Be("#654321");
    }

    [Fact]
    public void With_editor_defaults_keeps_every_explicit_non_default_value()
    {
        var defaults = new EditorDefaultsResponse("Inter", 700, "#123456", "#654321", []);
        var content = new TextContentDto(
            "Title", "Anton", 800, 96, 1.05, 0, "left", "#000000", new TextStrokeDto("#111111", 6));

        TextContentDto merged = EditImageDocumentSupport.WithEditorDefaults(content, defaults);

        merged.FontFamily.Should().Be("Anton");
        merged.FontWeight.Should().Be(800);
        merged.Color.Should().Be("#000000");
        merged.Stroke!.Color.Should().Be("#111111");
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private ICurrentUser Owner() => new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

    private DesignSystemsController Controller(ICurrentUser user) => new(
        _db,
        user,
        new ProjectAccessService(_db, user),
        new DesignSystemService(
            _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
            NullLogger<DesignSystemService>.Instance),
        Mock.Of<IDesignSystemIngestService>(),
        NullLogger<DesignSystemsController>.Instance);

    private Project SeedProject(Guid organizationId, Guid ownerId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = ownerId,
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private void SeedDesignSystem(Project project, DesignVersionState state, string documentJson)
    {
        Guid versionId = Guid.NewGuid();
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Kind = DesignSystemKinds.Design,
            Name = "Brand",
            CurrentVersionId = versionId,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.DesignSystems.Add(system);
        _db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = versionId,
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = state,
            DocumentJson = documentJson,
            DocumentDigest = new string('a', 64),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = _ownerId,
            CreatedAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
    }

    /// <summary>A minimal design document: a heading font-family token and a <c>color.text</c> colour token.</summary>
    private static string Document(string headingFamily, string textColor) => new JsonObject
    {
        ["typography"] = new JsonObject
        {
            ["family"] = new JsonObject
            {
                ["heading"] = new JsonObject
                {
                    ["$type"] = "fontFamily",
                    ["$value"] = new JsonArray(headingFamily),
                },
            },
        },
        ["color"] = new JsonObject
        {
            ["text"] = new JsonObject
            {
                ["$type"] = "color",
                ["$value"] = new JsonObject { ["hex"] = textColor, ["colorSpace"] = "srgb" },
            },
        },
    }.ToJsonString();
}
