using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Services.Telemetry;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The welcome answers (docs/design/new-user-funnel.md sections 4 to 6): <c>GET /api/v1/onboarding</c>'s
/// <c>profile</c>, <c>PUT /api/v1/onboarding/profile</c>, and the two server events it writes. Follows
/// <see cref="OnboardingControllerTests"/>.
/// </summary>
public class OnboardingProfileTests
{
    /// <summary>Loses a first-insert race on the profile row: a competing writer (the signup's suggested persona) creates it first.</summary>
    private sealed class RacingDb(DbContextOptions<InferenceApiDbContext> options, ITenantContext tenant, Guid userId) : InferenceApiDbContext(options, tenant)
    {
        private bool _raced;

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!_raced && ChangeTracker.Entries<UserOnboardingProfile>().Any(e => e.State == EntityState.Added))
            {
                _raced = true;
                await using var other = new InferenceApiDbContext(options, tenant);
                other.UserOnboardingProfiles.Add(new UserOnboardingProfile { UserId = userId, SuggestedPersona = "educator" });
                await other.SaveChangesAsync(cancellationToken);
                throw new DbUpdateException("dup", new Npgsql.PostgresException("dup", "ERROR", "ERROR", Npgsql.PostgresErrorCodes.UniqueViolation));
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class Rig
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public TestTenancy.MutableTenant Tenant { get; } = new();
        public InferenceApiDbContext Db { get; }
        public RecordingProductEventLog Log { get; } = new();
        public bool Ingest { get; set; } = true;

        public Rig(bool racing = false)
        {
            var options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Db = racing ? new RacingDb(options, Tenant, UserId) : new InferenceApiDbContext(options, Tenant);
        }

        public OnboardingController Controller(Guid? as_ = null)
        {
            Guid uid = as_ ?? UserId;
            Tenant.ActAs(uid);
            var u = new Mock<ICurrentUser>();
            u.SetupGet(x => x.UserId).Returns(uid);
            u.SetupGet(x => x.IsAuthenticated).Returns(true);
            u.SetupGet(x => x.OrganizationId).Returns(TestTenancy.OrgOf(uid));
            return new OnboardingController(
                Db,
                u.Object,
                Options.Create(new AlphaTelemetryOptions { ClientIngestEnabled = Ingest }),
                Log);
        }

        /// <summary>What the Go API writes at account creation: the landing page's persona and nothing else.</summary>
        public async Task SuggestAsync(string persona, Guid? user = null)
        {
            Db.UserOnboardingProfiles.Add(new UserOnboardingProfile { UserId = user ?? UserId, SuggestedPersona = persona });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public UserOnboardingProfile? Stored(Guid? user = null)
        {
            Guid uid = user ?? UserId;
            return Db.UserOnboardingProfiles.AsNoTracking().SingleOrDefault(p => p.UserId == uid);
        }
    }

    private static OnboardingResponse Body(ActionResult<OnboardingResponse> r) => (OnboardingResponse)((OkObjectResult)r.Result!).Value!;

    private static OnboardingProfileResponse Saved(ActionResult<OnboardingProfileResponse> r) => (OnboardingProfileResponse)((OkObjectResult)r.Result!).Value!;

    private static SaveOnboardingProfileRequest Answer(string? persona, string? material) => new() { Persona = persona, Material = material };

    private static SaveOnboardingProfileRequest Skip() => new() { Skipped = true };

    [Fact]
    public async Task Get_returns_a_profile_of_nulls_for_a_person_nothing_is_known_about()
    {
        var rig = new Rig();
        OnboardingResponse r = Body(await rig.Controller().Get(default));
        r.Profile.Should().Be(new OnboardingProfileDto(null, null, null, null, null));
        r.StarterTemplateKey.Should().BeNull();
        rig.Stored().Should().BeNull("reading the profile never creates it");
    }

    [Fact]
    public async Task Get_reads_back_the_persona_the_signup_suggested()
    {
        var rig = new Rig();
        await rig.SuggestAsync("nonprofit");
        OnboardingResponse r = Body(await rig.Controller().Get(default));
        r.Profile.SuggestedPersona.Should().Be("nonprofit");
        r.Profile.Persona.Should().BeNull("a suggestion is a preselection, never an answer");
        r.Profile.Material.Should().BeNull();
        r.Profile.AnsweredAt.Should().BeNull();
        r.Profile.SkippedAt.Should().BeNull();
        r.StarterTemplateKey.Should().BeNull();
    }

    public static TheoryData<string, string, string> EveryAnswer()
    {
        TheoryData<string, string, string> data = new();
        foreach (string persona in AcquisitionVocabulary.Personas)
        {
            foreach (string material in AcquisitionVocabulary.Materials)
                data.Add(persona, material, material switch { "code" => "quick-win-promo", "idea" => "animation-from-idea", _ => "edit-my-video" });
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryAnswer))]
    public async Task An_answer_is_stored_and_returns_the_profile_with_the_starter_recipe(string persona, string material, string starter)
    {
        var rig = new Rig();
        DateTime before = DateTime.UtcNow;
        OnboardingProfileResponse r = Saved(await rig.Controller().PutProfile(Answer(persona, material), default));

        r.Profile.Persona.Should().Be(persona);
        r.Profile.Material.Should().Be(material);
        r.Profile.SuggestedPersona.Should().BeNull();
        r.Profile.AnsweredAt.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        r.Profile.SkippedAt.Should().BeNull();
        r.StarterTemplateKey.Should().Be(starter);

        UserOnboardingProfile row = rig.Stored()!;
        (row.Persona, row.Material, row.AnsweredAt, row.SkippedAt).Should().Be((persona, material, r.Profile.AnsweredAt, null));

        // The same thing reads back from GET.
        OnboardingResponse get = Body(await rig.Controller().Get(default));
        get.Profile.Should().Be(r.Profile);
        get.StarterTemplateKey.Should().Be(starter);
    }

    [Fact]
    public void Every_starter_recipe_an_answer_can_return_is_a_listed_template()
    {
        // PUT /onboarding/profile hands this key to POST /projects, which accepts listed recipes only.
        HashSet<string> listed = WorkflowTemplateCatalog.GetListed().Select(t => t.Key).ToHashSet();
        foreach (string material in AcquisitionVocabulary.Materials)
            listed.Should().Contain(AcquisitionVocabulary.StarterTemplateKeyFor(material)!, "the starter of '{0}' must be creatable", material);
    }

    [Theory]
    [InlineData(null, "footage", "invalid_persona")]
    [InlineData("", "footage", "invalid_persona")]
    [InlineData("Creator", "footage", "invalid_persona")]
    [InlineData("creator ", "footage", "invalid_persona")]
    [InlineData("influencer", "footage", "invalid_persona")]
    [InlineData("creator", null, "invalid_material")]
    [InlineData("creator", "", "invalid_material")]
    [InlineData("creator", "Footage", "invalid_material")]
    [InlineData("creator", "photos", "invalid_material")]
    [InlineData(null, null, "invalid_persona")]
    public async Task A_value_outside_the_closed_sets_is_400_and_writes_nothing(string? persona, string? material, string error)
    {
        var rig = new Rig();
        await rig.SuggestAsync("business");

        ActionResult<OnboardingProfileResponse> result = await rig.Controller().PutProfile(Answer(persona, material), default);

        result.Result.Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { error });
        UserOnboardingProfile row = rig.Stored()!;
        (row.Persona, row.Material, row.AnsweredAt, row.SkippedAt, row.SuggestedPersona).Should().Be((null, null, null, null, "business"));
        rig.Log.Rows.Should().BeEmpty();
    }

    [Theory]
    [InlineData("creator", null)]
    [InlineData(null, "footage")]
    [InlineData("creator", "footage")]
    public async Task A_skip_that_also_carries_an_answer_is_400_and_writes_nothing(string? persona, string? material)
    {
        var rig = new Rig();
        var request = new SaveOnboardingProfileRequest { Skipped = true, Persona = persona, Material = material };
        (await rig.Controller().PutProfile(request, default)).Result
            .Should().BeOfType<BadRequestObjectResult>().Which.Value.Should().BeEquivalentTo(new { error = "invalid_profile" });
        rig.Stored().Should().BeNull();
        rig.Log.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_explicit_skipped_false_with_both_answers_is_an_answer()
    {
        var rig = new Rig();
        var request = new SaveOnboardingProfileRequest { Skipped = false, Persona = "agency", Material = "footage" };
        Saved(await rig.Controller().PutProfile(request, default)).Profile.Persona.Should().Be("agency");
        (await rig.Controller().PutProfile(new SaveOnboardingProfileRequest { Skipped = false }, default)).Result
            .Should().BeOfType<BadRequestObjectResult>("skipped:false with nothing else answers nothing");
    }

    [Fact]
    public async Task A_skip_is_stored_without_an_answer_and_has_no_starter_recipe()
    {
        var rig = new Rig();
        await rig.SuggestAsync("creator");
        OnboardingProfileResponse r = Saved(await rig.Controller().PutProfile(Skip(), default));

        r.Profile.SkippedAt.Should().NotBeNull();
        r.Profile.AnsweredAt.Should().BeNull("a skip is not an answer");
        r.Profile.Persona.Should().BeNull();
        r.Profile.Material.Should().BeNull();
        r.Profile.SuggestedPersona.Should().Be("creator", "the suggestion is not turned into an answer by a skip");
        r.StarterTemplateKey.Should().BeNull("with no material there is nothing to choose a recipe from");

        // Skipping again keeps the first time.
        await Task.Delay(20);
        Saved(await rig.Controller().PutProfile(Skip(), default)).Profile.SkippedAt.Should().Be(r.Profile.SkippedAt);
    }

    [Fact]
    public async Task Answering_again_overwrites_persona_and_material_and_keeps_the_first_answered_at()
    {
        var rig = new Rig();
        await rig.SuggestAsync("software");
        OnboardingProfileResponse first = Saved(await rig.Controller().PutProfile(Answer("software", "code"), default));
        first.StarterTemplateKey.Should().Be("quick-win-promo");
        DateTime firstUpdate = rig.Stored()!.UpdatedAt;

        await Task.Delay(20);
        OnboardingProfileResponse second = Saved(await rig.Controller().PutProfile(Answer("creator", "footage"), default));

        second.Profile.Persona.Should().Be("creator");
        second.Profile.Material.Should().Be("footage");
        second.Profile.AnsweredAt.Should().Be(first.Profile.AnsweredAt, "the first answer is when the person was onboarded");
        second.Profile.SuggestedPersona.Should().Be("software", "an answer never rewrites what the landing page suggested");
        second.StarterTemplateKey.Should().Be("edit-my-video");
        rig.Db.UserOnboardingProfiles.Count().Should().Be(1);
        rig.Stored()!.UpdatedAt.Should().BeAfter(firstUpdate);
    }

    [Fact]
    public async Task A_skip_then_an_answer_keeps_both_times_and_a_later_skip_does_not_undo_the_answer()
    {
        var rig = new Rig();
        OnboardingProfileResponse skipped = Saved(await rig.Controller().PutProfile(Skip(), default));
        OnboardingProfileResponse answered = Saved(await rig.Controller().PutProfile(Answer("educator", "idea"), default));

        answered.Profile.SkippedAt.Should().Be(skipped.Profile.SkippedAt);
        answered.Profile.AnsweredAt.Should().NotBeNull();
        answered.StarterTemplateKey.Should().Be("animation-from-idea");

        OnboardingProfileResponse again = Saved(await rig.Controller().PutProfile(Skip(), default));
        again.Profile.Persona.Should().Be("educator");
        again.Profile.Material.Should().Be("idea");
        again.Profile.AnsweredAt.Should().Be(answered.Profile.AnsweredAt);
        again.StarterTemplateKey.Should().Be("animation-from-idea", "the recipe follows the stored material");
    }

    [Fact]
    public async Task The_profile_is_per_user()
    {
        var rig = new Rig();
        Guid other = Guid.NewGuid();
        await rig.SuggestAsync("agency", other);
        Saved(await rig.Controller().PutProfile(Answer("creator", "footage"), default));

        OnboardingResponse theirs = Body(await rig.Controller(other).Get(default));
        theirs.Profile.Should().Be(new OnboardingProfileDto(null, null, "agency", null, null));
        rig.Stored(other)!.Persona.Should().BeNull();
        rig.Stored()!.SuggestedPersona.Should().BeNull();
    }

    [Fact]
    public async Task The_events_are_server_rows_written_once_per_user_with_the_persona_as_the_only_detail()
    {
        var rig = new Rig();
        Guid other = Guid.NewGuid();

        await rig.Controller().PutProfile(Answer("software", "code"), default);
        await rig.Controller().PutProfile(Answer("creator", "footage"), default);
        await rig.Controller().PutProfile(Skip(), default);
        await rig.Controller().PutProfile(Skip(), default);
        await rig.Controller(other).PutProfile(Skip(), default);
        await rig.Controller(other).PutProfile(Answer("nonprofit", "idea"), default);

        rig.Log.Rows.Select(r => (r.UserId, r.Event, r.Detail)).Should().Equal(
            (rig.UserId, "onboarding.profile_saved", "software"),
            (rig.UserId, "onboarding.profile_skipped", null),
            (other, "onboarding.profile_skipped", null),
            (other, "onboarding.profile_saved", "nonprofit"));
        rig.Log.Rows.Should().OnlyContain(r => r.Source == ProductEventSources.Server && r.VisitorId == null);
        rig.Log.Rows.Select(r => r.OrganizationId).Should().Equal(
            TestTenancy.OrgOf(rig.UserId), TestTenancy.OrgOf(rig.UserId), TestTenancy.OrgOf(other), TestTenancy.OrgOf(other));
    }

    [Fact]
    public async Task The_events_need_no_telemetry_switch_because_the_server_observed_them()
    {
        // The tour and checklist events are browser reports and 404 with ingest off; these are not.
        var rig = new Rig { Ingest = false };
        (await rig.Controller().Events(new OnboardingEventRequest { Event = "tour_started" }, default)).Should().BeOfType<NotFoundResult>();

        Saved(await rig.Controller().PutProfile(Answer("business", "footage"), default)).Profile.Persona.Should().Be("business");
        rig.Log.Rows.Select(r => (r.Event, r.Detail, r.Source)).Should().Equal(("onboarding.profile_saved", "business", "server"));
    }

    [Fact]
    public async Task A_failing_event_log_never_fails_the_save()
    {
        var rig = new Rig();
        rig.Log.Throw = true;

        OnboardingProfileResponse r = Saved(await rig.Controller().PutProfile(Answer("creator", "idea"), default));

        r.Profile.Persona.Should().Be("creator");
        rig.Stored()!.Material.Should().Be("idea");
        Saved(await rig.Controller().PutProfile(Skip(), default)).Profile.SkippedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_answer_that_loses_the_first_insert_race_is_re_applied_to_the_winning_row()
    {
        var rig = new Rig(racing: true);
        OnboardingProfileResponse r = Saved(await rig.Controller().PutProfile(Answer("creator", "footage"), default));

        r.Profile.Persona.Should().Be("creator");
        r.Profile.SuggestedPersona.Should().Be("educator", "the row the signup wrote first is kept, and answered on");
        r.Profile.AnsweredAt.Should().NotBeNull();
        UserOnboardingProfile row = rig.Stored()!;
        (row.Persona, row.Material, row.SuggestedPersona).Should().Be(("creator", "footage", "educator"));
    }

    [Fact]
    public async Task A_picked_plan_is_shown_by_get_and_kept_by_put_profile()
    {
        var rig = new Rig();
        rig.Db.UserOnboardingProfiles.Add(new UserOnboardingProfile { UserId = rig.UserId, SuggestedPersona = "creator", PickedPlan = "team" });
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();

        Body(await rig.Controller().Get(default)).Profile.PickedPlan.Should().Be("team");
        Saved(await rig.Controller().PutProfile(Answer("creator", "idea"), default)).Profile.PickedPlan.Should().Be("team");
        rig.Stored()!.PickedPlan.Should().Be("team");
    }

    [Fact]
    public async Task Deleting_the_picked_plan_clears_only_the_callers_and_is_idempotent()
    {
        var rig = new Rig();
        Guid other = Guid.NewGuid();
        rig.Db.UserOnboardingProfiles.AddRange(
            new UserOnboardingProfile { UserId = rig.UserId, PickedPlan = "team" },
            new UserOnboardingProfile { UserId = other, PickedPlan = "pro" });
        await rig.Db.SaveChangesAsync();
        rig.Db.ChangeTracker.Clear();

        (await rig.Controller().ClearPickedPlan(default)).Should().BeOfType<NoContentResult>();
        (await rig.Controller().ClearPickedPlan(default)).Should().BeOfType<NoContentResult>();

        rig.Stored()!.PickedPlan.Should().BeNull();
        rig.Stored(other)!.PickedPlan.Should().Be("pro");
        Body(await rig.Controller().Get(default)).Profile.PickedPlan.Should().BeNull();
    }

    [Fact]
    public async Task Deleting_the_picked_plan_without_a_row_is_a_no_op()
    {
        var rig = new Rig();
        (await rig.Controller().ClearPickedPlan(default)).Should().BeOfType<NoContentResult>();
        rig.Stored().Should().BeNull();
    }

    [Fact]
    public void The_wire_shape_is_the_contracts_camel_case()
    {
        JsonSerializerOptions web = new(JsonSerializerDefaults.Web);
        DateTime at = new(2026, 11, 11, 9, 30, 0, DateTimeKind.Utc);
        var profile = new OnboardingProfileDto("creator", "footage", "creator", at, null);

        using JsonDocument put = JsonDocument.Parse(JsonSerializer.Serialize(new OnboardingProfileResponse(profile, "edit-my-video"), web));
        put.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("profile", "starterTemplateKey");
        put.RootElement.GetProperty("profile").EnumerateObject().Select(p => p.Name)
            .Should().Equal("persona", "material", "suggestedPersona", "answeredAt", "skippedAt", "pickedPlan");
        put.RootElement.GetProperty("profile").GetProperty("skippedAt").ValueKind.Should().Be(JsonValueKind.Null);
        put.RootElement.GetProperty("starterTemplateKey").GetString().Should().Be("edit-my-video");

        var get = new OnboardingResponse(
            new OnboardingTourDto("pending", null, null), new OnboardingChecklistDto(false, []), profile, null);
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(get, web));
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().Equal("tour", "checklist", "profile", "starterTemplateKey");

        SaveOnboardingProfileRequest skip = JsonSerializer.Deserialize<SaveOnboardingProfileRequest>("{\"skipped\":true}", web)!;
        (skip.Skipped, skip.Persona, skip.Material).Should().Be((true, null, null));
        SaveOnboardingProfileRequest answer = JsonSerializer.Deserialize<SaveOnboardingProfileRequest>(
            "{\"persona\":\"creator\",\"material\":\"footage\"}", web)!;
        (answer.Skipped, answer.Persona, answer.Material).Should().Be((null, "creator", "footage"));
    }

    [Fact]
    public void The_profile_route_is_put_onboarding_profile_behind_the_controllers_authorization()
    {
        var method = typeof(OnboardingController).GetMethod(nameof(OnboardingController.PutProfile))!;
        method.GetCustomAttributes(typeof(HttpPutAttribute), true).Cast<HttpPutAttribute>().Single().Template.Should().Be("profile");
        typeof(OnboardingController).GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Single().Template
            .Should().Be("api/v1/onboarding");
        typeof(OnboardingController).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true).Should().NotBeEmpty();
        method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), true).Should().BeEmpty();
    }
}
