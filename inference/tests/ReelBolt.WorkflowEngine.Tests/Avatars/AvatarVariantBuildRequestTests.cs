#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Avatars.Builds;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Billing;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;
using Rig = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarBuildRig;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L12 of docs/design/avatar-objects.md, "Avatar variants" made BY THE PICTURE MODEL, the request half: the
/// estimate, the order of the checks of a start, the media permission (the project's consent, or one call allowed by
/// a person at the screen), the hold and the run-rate bucket, and the build routes.
/// </summary>
public class AvatarVariantBuildRequestTests
{
    private static async Task<(int Status, JsonElement Body)> StartAsync(
        Rig rig, Guid avatarId, CreateAvatarVariantRequest body, ICurrentUser? user = null, ClaimsPrincipal? principal = null) =>
        H.Read(await rig.Controller(user, principal, path: VariantRoute(rig, avatarId)).CreateVariant(rig.H.Project.Id, avatarId, body, CancellationToken.None));

    /// <summary>The variant route a person's browser posts to (the media rule counts only on the surface's own route).</summary>
    private static string VariantRoute(Rig rig, Guid avatarId) => $"/api/v1/projects/{rig.H.Project.Id}/avatars/{avatarId}/variants";

    private static async Task<(int Status, JsonElement Body)> EstimateAsync(Rig rig, Guid avatarId, CreateAvatarVariantRequest? body = null) =>
        H.Read(await rig.Controller().EstimateVariant(rig.H.Project.Id, avatarId, body, CancellationToken.None));

    private static int JobCount(Rig rig)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return db.AvatarBuildJobs.Count();
    }

    // ------------------------------------------------------------------ estimate

    [Fact]
    public async Task The_estimate_prices_every_picture_and_writes_nothing()
    {
        Rig rig = new();
        (Guid car, _) = await rig.CarAsync(pictures: 3);

        // No statement, no label, no media consent: the estimate is shown before any of them is given.
        (int status, JsonElement body) = await EstimateAsync(rig, car);

        status.Should().Be(200, body.ToString());
        body.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        body.GetProperty("pictures").GetInt32().Should().Be(3);
        body.GetProperty("videos").GetInt32().Should().Be(0);
        body.GetProperty("credits").GetDouble().Should().Be(12, "three pictures at 4 credits each");
        body.GetProperty("creditsHigh").GetDouble().Should().Be(12);
        body.GetProperty("balanceCredits").GetDouble().Should().Be(1000);
        body.GetProperty("affordable").GetBoolean().Should().BeTrue();
        body.GetProperty("unavailableReason").ValueKind.Should().Be(JsonValueKind.Null);

        rig.Charges!.Holds.Should().BeEmpty("an estimate holds nothing");
        rig.Charges.Estimates.Should().HaveCount(3).And.OnlyContain(e =>
            e.Kind == InferenceProviderKind.FalAi && e.Capability == InferenceProviderCapability.ImageEdit && e.ModelCode == Rig.Model
            && e.Unit == UsageKind.ImageEditImages && e.BillingMode == UsageBillingMode.Platform);
        rig.Rate.Hits.Should().BeEmpty();
        JobCount(rig).Should().Be(0);
        rig.Images.Edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_vision_provider_a_variant_start_is_refused_before_anything_is_held_and_the_estimate_says_why()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync(pictures: 2);
        rig.H.PersonCheck!.Active = false;

        (int status, JsonElement body) = await StartAsync(rig, car, Rig.Body());
        (status, body.GetProperty("code").GetString()).Should().Be((409, "VISION_PROVIDER_REQUIRED"));
        body.GetProperty("message").GetString().Should().StartWith("Checking that a picture shows no person needs a vision model");
        rig.Charges!.Holds.Should().BeEmpty();
        rig.Rate.Hits.Should().BeEmpty();
        JobCount(rig).Should().Be(0);
        rig.Images.Edits.Should().BeEmpty();

        (int estimateStatus, JsonElement estimate) = await EstimateAsync(rig, car);
        estimateStatus.Should().Be(200);
        (estimate.GetProperty("unavailableReason").GetString(), estimate.GetProperty("affordable").GetBoolean()).Should().Be(("VISION_PROVIDER_REQUIRED", false));
    }

    [Fact]
    public async Task The_estimate_follows_the_chosen_pictures_and_says_when_the_workspace_cannot_pay()
    {
        Rig rig = new() { Charges = new FakeNonWorkflowCharges { AvailableMillis = 5_000, EstimateMillis = 4_000 } };
        (Guid car, _) = await rig.CarAsync(pictures: 3);
        Guid[] referenceIds;
        using (InferenceApiDbContext db = rig.H.NewDb())
            referenceIds = [.. db.ProjectAvatarReferences.Where(r => r.AvatarId == car).OrderBy(r => r.Ordinal).Select(r => r.Id)];

        (_, JsonElement one) = await EstimateAsync(rig, car, new CreateAvatarVariantRequest(ReferenceIds: [referenceIds[1]]));
        (_, JsonElement all) = await EstimateAsync(rig, car, new CreateAvatarVariantRequest());
        (int foreign, JsonElement refused) = await EstimateAsync(rig, car, new CreateAvatarVariantRequest(ReferenceIds: [Guid.NewGuid()]));

        (one.GetProperty("pictures").GetInt32(), one.GetProperty("credits").GetDouble(), one.GetProperty("affordable").GetBoolean()).Should().Be((1, 4d, true));
        (all.GetProperty("pictures").GetInt32(), all.GetProperty("credits").GetDouble(), all.GetProperty("affordable").GetBoolean()).Should().Be((3, 12d, false));
        (foreign, refused.GetProperty("code").GetString()).Should().Be((400, "variant_reference_invalid"));
    }

    [Fact]
    public async Task An_estimate_with_no_usable_picture_model_answers_200_and_names_the_reason()
    {
        Rig rig = new();
        (Guid car, _) = await rig.CarAsync(pictures: 2);
        ResolvedImageProvider fal = rig.Provider!;

        rig.Provider = null;
        (int missing, JsonElement a) = await EstimateAsync(rig, car, new CreateAvatarVariantRequest(ReferenceIds: [], IncludeVideos: false));
        rig.Provider = fal with { ModelName = "bria/fibo-edit/edit" };
        (int unsupported, JsonElement b) = await EstimateAsync(rig, car);

        (missing, a.GetProperty("unavailableReason").GetString()).Should().Be((200, "VARIANT_PROVIDER_MISSING"));
        (unsupported, b.GetProperty("unavailableReason").GetString()).Should().Be((200, "VARIANT_MODEL_UNSUPPORTED"));
        foreach (JsonElement body in new[] { a, b })
        {
            body.GetProperty("affordable").GetBoolean().Should().BeFalse();
            body.GetProperty("credits").ValueKind.Should().Be(JsonValueKind.Null);
            body.GetProperty("pictures").GetInt32().Should().Be(2, "an empty list of references means every picture");
            body.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        }

        rig.Charges!.Estimates.Should().BeEmpty();
    }

    [Fact]
    public async Task With_billing_off_the_estimate_says_so_and_a_start_holds_nothing()
    {
        Rig rig = new() { Charges = null };
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();

        (int status, JsonElement body) = await EstimateAsync(rig, car);
        status.Should().Be(200);
        body.GetProperty("billingEnabled").GetBoolean().Should().BeFalse();
        body.GetProperty("affordable").GetBoolean().Should().BeTrue();
        body.GetProperty("credits").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("pictures").GetInt32().Should().Be(2);

        Guid jobId = await rig.StartAsync(car);
        AvatarBuildJob job = rig.Job(jobId);
        job.HoldId.Should().BeNull();
        AvatarBuildParams.Read(job.ParamsJson).HoldMillis.Should().Be(0);
    }

    // ------------------------------------------------------------------ start: the order of the checks

    [Fact]
    public async Task A_start_refuses_in_the_order_of_the_design_and_writes_nothing_until_everything_passed()
    {
        Rig rig = new() { Moderation = new Rig.Screen(text => text.Contains("forbidden", StringComparison.Ordinal)) };
        (Guid car, List<ProjectFile> files) = await rig.CarAsync(pictures: 2);
        Guid anna = await rig.H.CreateReadyAsync("Anna");
        byte[] original = rig.H.Objects[files[0].StorageKey];
        Guid projectId = rig.H.Project.Id;

        // Everything is wrong at once; each fix reveals the next refusal.
        rig.H.Options.Objects.Enabled = false;
        H.User unverified = rig.H.Owner(emailVerified: false);
        rig.H.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = false };
        rig.H.Objects[files[0].StorageKey] = H.Png(1024, 1024, 255);
        ResolvedImageProvider fal = rig.Provider!;
        rig.Provider = null;
        rig.Charges = new FakeNonWorkflowCharges { RefuseHolds = true, AvailableMillis = 1_000, EstimateMillis = 4_000 };
        rig.Rate.Refuse = true;
        var bad = new CreateAvatarVariantRequest(Instruction: "", VariantLabel: "", ConsentStatement: "ok", ConsentLocale: "en");

        (await StartAsync(rig, car, bad, unverified)).Status.Should().Be(404, "the objects switch comes first");
        rig.H.Options.Objects.Enabled = true;

        (int s1, JsonElement b1) = await StartAsync(rig, car, bad, unverified);
        (s1, b1.GetProperty("code").GetString()).Should().Be((403, "EMAIL_NOT_VERIFIED"));

        (int s2, JsonElement b2) = await StartAsync(rig, car, bad);
        (s2, b2.GetProperty("code").GetString(), b2.GetProperty("feature").GetString()).Should().Be((402, "PLAN_NOT_ENTITLED", "avatar_objects"));
        rig.H.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = true };

        (await StartAsync(rig, Guid.NewGuid(), bad)).Status.Should().Be(404, "an avatar the project does not have");
        (int s3, JsonElement b3) = await StartAsync(rig, anna, bad);
        (s3, b3.GetProperty("code").GetString()).Should().Be((409, "VARIANT_NOT_FOR_PEOPLE"));

        (int s4, JsonElement b4) = await StartAsync(rig, car, bad);
        (s4, b4.GetProperty("code").GetString(), b4.GetProperty("reason").GetString()).Should().Be((409, "AVATAR_NOT_USABLE", "reference_missing"));
        rig.H.Objects[files[0].StorageKey] = original;

        (int s5, JsonElement b5) = await StartAsync(rig, car, bad);
        (s5, b5.GetProperty("code").GetString()).Should().Be((400, "object_statement_required"));
        b5.GetProperty("statements").GetProperty("it").GetString().Should().Be(AvatarObjectStatement.Italian);
        CreateAvatarVariantRequest stated = bad with { ConsentStatement = AvatarObjectStatement.English };

        (int s6, JsonElement b6) = await StartAsync(rig, car, stated);
        (s6, b6.GetProperty("code").GetString()).Should().Be((409, "CONSENT_REQUIRED"));
        rig.GrantMediaConsent();

        (int s7, JsonElement b7) = await StartAsync(rig, car, stated);
        (s7, b7.GetProperty("code").GetString()).Should().Be((409, "VARIANT_PROVIDER_MISSING"));
        rig.Provider = fal with { ModelName = "bria/fibo-edit/edit" };
        (int s8, JsonElement b8) = await StartAsync(rig, car, stated);
        (s8, b8.GetProperty("code").GetString()).Should().Be((409, "VARIANT_MODEL_UNSUPPORTED"), "an edit model that takes one picture only");
        rig.Provider = fal with { Kind = InferenceProviderKind.OpenAICompatible };
        (await StartAsync(rig, car, stated)).Body.GetProperty("code").GetString().Should().Be("VARIANT_PROVIDER_MISSING", "fal.ai only");
        rig.Provider = fal;

        (int s9, JsonElement b9) = await StartAsync(rig, car, stated);
        (s9, b9.GetProperty("code").GetString()).Should().Be((400, "variant_instruction_invalid"));
        (int s10, JsonElement b10) = await StartAsync(rig, car, stated with { Instruction = "make it forbidden" });
        (s10, b10.GetProperty("code").GetString()).Should().Be((400, "variant_label_invalid"));
        CreateAvatarVariantRequest labelled = stated with { Instruction = "make it forbidden", VariantLabel = "Red" };

        (int s11, JsonElement b11) = await StartAsync(rig, car, labelled);
        (s11, b11.GetProperty("code").GetString()).Should().Be((400, "CONTENT_BLOCKED"), "the member's words are screened before anything is held");
        rig.Charges.Holds.Should().BeEmpty();
        CreateAvatarVariantRequest good = labelled with { Instruction = "paint it matte red" };

        (int s12, JsonElement b12) = await StartAsync(rig, car, good);
        (s12, b12.GetProperty("code").GetString()).Should().Be((402, "INSUFFICIENT_CREDITS"));
        b12.GetProperty("required").GetDouble().Should().Be(8);
        rig.Rate.Hits.Should().BeEmpty("credits come before the run-rate bucket");
        rig.Charges = new FakeNonWorkflowCharges { AvailableMillis = 1_000_000, EstimateMillis = 4_000 };

        (int s13, JsonElement b13) = await StartAsync(rig, car, good);
        (s13, b13.GetProperty("code").GetString()).Should().Be((429, "RUN_RATE_LIMITED"));
        b13.GetProperty("retryAfterSeconds").GetInt32().Should().Be(780);
        rig.Charges.Holds.Should().ContainSingle();
        rig.Charges.Released.Should().ContainSingle("a start the run limit refused gives its hold back");

        JobCount(rig).Should().Be(0, "no refusal wrote a job");
        rig.Images.Edits.Should().BeEmpty();
        rig.Queue.Reader.TryRead(out _).Should().BeFalse();
        using InferenceApiDbContext db = rig.H.NewDb();
        (await db.Projects.SingleAsync(p => p.Id == projectId)).SubjectMediaConsentAt.Should().NotBeNull();

        rig.Rate.Refuse = false;
        (int ok, JsonElement started) = await StartAsync(rig, car, good);
        ok.Should().Be(202, started.ToString());
        started.GetProperty("state").GetString().Should().Be("queued");
    }

    [Fact]
    public async Task The_limits_count_what_exists_and_what_is_still_being_made()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();
        await rig.CarAsync(pictures: 1, name: "Van");

        (await StartAsync(rig, car, Rig.Body(label: "Red"))).Status.Should().Be(202);

        // A build that is still running holds its label, its name and its place among the ten.
        (int sameLabel, JsonElement a) = await StartAsync(rig, car, Rig.Body(label: "Red") with { Name = "Other name" });
        (int sameName, JsonElement b) = await StartAsync(rig, car, Rig.Body(label: "Blue") with { Name = "Car · Red" });
        (int existingName, JsonElement c) = await StartAsync(rig, car, Rig.Body(label: "Green") with { Name = "Van" });
        (sameLabel, a.GetProperty("code").GetString()).Should().Be((409, "VARIANT_LABEL_TAKEN"));
        (sameName, b.GetProperty("code").GetString()).Should().Be((409, "AVATAR_NAME_TAKEN"));
        (existingName, c.GetProperty("code").GetString()).Should().Be((409, "AVATAR_NAME_TAKEN"));

        for (int i = 1; i < ProjectAvatarLimits.MaxVariantsPerAvatar; i++)
            (await StartAsync(rig, car, Rig.Body(label: "V" + i))).Status.Should().Be(202);
        (int eleventh, JsonElement d) = await StartAsync(rig, car, Rig.Body(label: "V10"));
        (eleventh, d.GetProperty("code").GetString()).Should().Be((409, "VARIANT_LIMIT_REACHED"));

        (int tooLong, JsonElement e) = await StartAsync(rig, car, Rig.Body(instruction: new string('x', 1001), label: "Long"));
        (tooLong, e.GetProperty("code").GetString()).Should().Be((400, "variant_instruction_invalid"));
    }

    [Fact]
    public async Task Videos_are_refused_when_no_video_model_is_set_up()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        ProjectFile front = rig.H.AddImage("kettle.png", salt: 3);
        ProjectFile clip = rig.H.AddProbedMedia("kettle.mp4", "video/mp4", 5, "h264");
        rig.H.AddFilmstrip(clip);
        Guid kettle = await rig.H.CreateObjectAsync("Kettle", front, clip);

        (int status, JsonElement body) = await StartAsync(rig, kettle, Rig.Body() with { IncludeVideos = true });

        status.Should().Be(409, body.ToString());
        body.GetProperty("code").GetString().Should().Be("VARIANT_VIDEO_MODEL_MISSING");
        JobCount(rig).Should().Be(0);
        rig.Charges!.Holds.Should().BeEmpty();

        // Without videos the same avatar gets its variant from its picture alone.
        (await StartAsync(rig, kettle, Rig.Body())).Status.Should().Be(202);
        AvatarBuildParams.Read(rig.Job(SingleJobId(rig)).ParamsJson).VideoReferenceIds.Should().BeEmpty();
    }

    private static Guid SingleJobId(Rig rig)
    {
        using InferenceApiDbContext db = rig.H.NewDb();
        return db.AvatarBuildJobs.Single().Id;
    }

    // ------------------------------------------------------------------ the media permission

    [Fact]
    public async Task The_projects_recorded_consent_allows_sending_the_pictures()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();

        Guid jobId = await rig.StartAsync(car);

        rig.Job(jobId).AllowedBy.Should().Be("project_consent");
    }

    [Fact]
    public async Task A_person_at_the_screen_may_allow_one_call_and_that_never_records_the_projects_consent()
    {
        Rig rig = new();
        (Guid car, _) = await rig.CarAsync();

        (int without, JsonElement refused) = await StartAsync(rig, car, Rig.Body(label: "A"));
        (int saidNo, _) = await StartAsync(rig, car, Rig.Body(label: "B") with { AllowMediaEgress = false });
        (int allowed, JsonElement started) = await StartAsync(rig, car, Rig.Body(label: "C") with { AllowMediaEgress = true });

        (without, refused.GetProperty("code").GetString()).Should().Be((409, "CONSENT_REQUIRED"));
        saidNo.Should().Be(409);
        allowed.Should().Be(202, started.ToString());
        rig.Job(started.GetProperty("jobId").GetGuid()).AllowedBy.Should().Be("call");
        rig.Project().SubjectMediaConsentAt.Should().BeNull("allowing one call never records the project consent");
        rig.Project().SubjectMediaConsentByUserId.Should().BeNull();
    }

    [Theory]
    [InlineData("apiKeyId")]
    [InlineData("scope")]
    [InlineData("act")]
    public async Task Another_kind_of_caller_cannot_allow_it_by_saying_so(string claim)
    {
        Rig rig = new();
        (Guid car, _) = await rig.CarAsync();
        string type = claim switch { "apiKeyId" => AuthClaims.ApiKeyId, "scope" => AuthClaims.Scope, _ => OnBehalfOfTokenIssuer.ActorClaim };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(type, "x")], "test"));

        (int status, JsonElement body) = await StartAsync(rig, car, Rig.Body() with { AllowMediaEgress = true }, principal: principal);

        (status, body.GetProperty("code").GetString()).Should().Be((409, "CONSENT_REQUIRED"));
        JobCount(rig).Should().Be(0);

        // With the project's own consent on record the same caller is served: the per-call rule is not asked at all.
        rig.GrantMediaConsent();
        (await StartAsync(rig, car, Rig.Body(), principal: principal)).Status.Should().Be(202);
    }

    [Fact]
    public async Task The_route_asks_the_one_media_rule_once_and_only_for_a_call_that_says_so()
    {
        // A signed-in person on another route (the assistant runs services inside its chat request): the word counts for nothing.
        Rig rig = new();
        (Guid car, _) = await rig.CarAsync();
        H.Read(await rig.Controller(path: "/api/v1/assistant/chat").CreateVariant(
            rig.H.Project.Id, car, Rig.Body() with { AllowMediaEgress = true }, CancellationToken.None)).Status.Should().Be(409);

        // A rule that says no refuses even a signed-in person on the route; one that says yes is asked only when it is needed.
        var never = new Recording(null);
        H.Read(await rig.Controller(permission: never, path: VariantRoute(rig, car)).CreateVariant(
            rig.H.Project.Id, car, Rig.Body() with { AllowMediaEgress = true }, CancellationToken.None)).Status.Should().Be(409);
        never.Asked.Should().ContainSingle().Which.Should().Match<MediaEgressCall>(c =>
            c.ProjectId == rig.H.Project.Id && c.Surface == MediaEgressSurface.AvatarVariant && c.Via == MediaEgressVia.Request);

        var always = new Recording(ReelBolt.Shared.Workflows.Apps.MediaEgressPermission.ByApp);
        H.Read(await rig.Controller(permission: always, path: VariantRoute(rig, car)).CreateVariant(rig.H.Project.Id, car, Rig.Body(), CancellationToken.None))
            .Status.Should().Be(409, "the call did not say allowMediaEgress");
        always.Asked.Should().BeEmpty();
    }

    private sealed class Recording(string? grantor) : IMediaEgressCallPermission
    {
        public List<MediaEgressCall> Asked { get; } = [];

        public ValueTask<string?> GrantorAsync(MediaEgressCall call, CancellationToken ct)
        {
            Asked.Add(call);
            return ValueTask.FromResult(grantor);
        }
    }

    // ------------------------------------------------------------------ what a start writes

    [Fact]
    public async Task A_start_holds_all_pictures_hits_the_paid_bucket_once_and_queues_the_job()
    {
        Rig rig = new() { Moderation = new Rig.Screen(_ => false) };
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync(pictures: 3);

        (int status, JsonElement body) = await StartAsync(rig, car, Rig.Body() with { Description = "The red one" });

        status.Should().Be(202, body.ToString());
        Guid jobId = body.GetProperty("jobId").GetGuid();
        AvatarBuildJob job = rig.Job(jobId);
        (job.Kind, job.State, job.Stage, job.Done, job.Total).Should().Be(("variant", "queued", "queued", 0, 3));
        (job.ProjectId, job.OrganizationId, job.RequestedByUserId, job.SourceAvatarId).Should().Be((rig.H.Project.Id, rig.H.OrganizationId, rig.H.OwnerId, (Guid?)car));
        (job.Instruction, job.Name, job.VariantLabel, job.StatementLocale).Should().Be(("paint it matte red", "Car · Red", "Red", "en"));
        job.AvatarId.Should().BeNull();
        job.FailureCode.Should().BeNull();

        AvatarBuildParams parameters = AvatarBuildParams.Read(job.ParamsJson);
        parameters.PictureReferenceIds.Should().HaveCount(3);
        parameters.Description.Should().Be("The red one");
        parameters.VariantDescription.Should().Be("paint it matte red", "what sets it apart defaults to the instruction");
        parameters.HoldMillis.Should().Be(12_000);
        parameters.InstructionScreened.Should().BeTrue();

        rig.Charges!.Holds.Should().ContainSingle().Which.Should().Be((rig.H.OrganizationId, UsageSource.Editor, 12_000L));
        job.HoldId.Should().NotBeNull();
        rig.Moderation!.Texts.Should().Equal("paint it matte red");
        rig.Rate.Hits.Should().ContainSingle().Which.Should().Be((rig.H.OwnerId, rig.H.Project.Id, RunRateBucket.Paid, "avatar_build"));
        rig.Queue.Holds(jobId).Should().BeTrue();
        rig.Images.Edits.Should().BeEmpty("the request only queues; the worker does the work");
    }

    [Fact]
    public async Task A_variant_of_a_variant_changes_that_variants_pictures_and_attaches_to_the_original()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync(pictures: 1);
        Guid red = await rig.StartAsync(car);
        await rig.RunAsync(red);
        Guid redAvatar = rig.Job(red).AvatarId!.Value;

        Guid matte = await rig.StartAsync(redAvatar, Rig.Body("make the paint matte", "Matte red"));
        rig.Job(matte).Name.Should().Be("Car · Matte red", "named after the original, not after the variant it was made from");
        await rig.RunAsync(matte);

        using InferenceApiDbContext db = rig.H.NewDb();
        ProjectAvatar made = await db.ProjectAvatars.SingleAsync(a => a.Id == rig.Job(matte).AvatarId);
        made.VariantOfAvatarId.Should().Be(car, "one level only");
        rig.Job(matte).SourceAvatarId.Should().Be(redAvatar, "the pictures changed are the red variant's");
    }

    // ------------------------------------------------------------------ the build routes

    [Fact]
    public async Task The_build_routes_list_read_and_cancel_a_projects_jobs_only()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();
        Guid jobId = await rig.StartAsync(car);
        Guid projectId = rig.H.Project.Id;

        (int listed, JsonElement list) = H.Read(await rig.Controller().ListBuilds(projectId, CancellationToken.None));
        (int got, JsonElement one) = H.Read(await rig.Controller().GetBuild(projectId, jobId, CancellationToken.None));

        listed.Should().Be(200);
        list.GetArrayLength().Should().Be(1);
        got.Should().Be(200);
        one.GetProperty("jobId").GetGuid().Should().Be(jobId);
        one.GetProperty("kind").GetString().Should().Be("variant");
        one.GetProperty("state").GetString().Should().Be("queued");
        one.GetProperty("stage").GetString().Should().Be("queued");
        one.GetProperty("progress").GetProperty("done").GetInt32().Should().Be(0);
        one.GetProperty("progress").GetProperty("total").GetInt32().Should().Be(2);
        one.GetProperty("name").GetString().Should().Be("Car · Red");
        one.GetProperty("avatarId").ValueKind.Should().Be(JsonValueKind.Null);
        one.GetProperty("failureCode").ValueKind.Should().Be(JsonValueKind.Null);
        one.GetProperty("credits").GetDouble().Should().Be(8);
        one.ToString().Should().NotContain("userFiles").And.NotContain("paramsJson").And.NotContain("holdId");

        // Another organization's caller, and another project's id, see nothing.
        H.Read(await rig.Controller(H.Stranger()).GetBuild(projectId, jobId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await rig.Controller(H.Stranger()).ListBuilds(projectId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await rig.Controller(H.Stranger()).CancelBuild(projectId, jobId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await rig.Controller().GetBuild(projectId, Guid.NewGuid(), CancellationToken.None)).Status.Should().Be(404);
        Guid otherProject = Guid.NewGuid();
        using (InferenceApiDbContext db = rig.H.NewDb())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", OwnerId = rig.H.OwnerId, OrganizationId = rig.H.OrganizationId });
            db.SaveChanges();
        }

        H.Read(await rig.Controller().GetBuild(otherProject, jobId, CancellationToken.None)).Status.Should().Be(404, "a job is read through its own project");
        H.Read(await rig.Controller().CancelBuild(otherProject, jobId, CancellationToken.None)).Status.Should().Be(404);

        (int cancelled, JsonElement after) = H.Read(await rig.Controller().CancelBuild(projectId, jobId, CancellationToken.None));
        (int again, JsonElement same) = H.Read(await rig.Controller().CancelBuild(projectId, jobId, CancellationToken.None));
        (cancelled, after.GetProperty("state").GetString()).Should().Be((200, "cancelled"));
        (again, same.GetProperty("state").GetString()).Should().Be((200, "cancelled"), "cancelling twice is the same answer");

        // The objects switch off: the routes do not exist.
        rig.H.Options.Objects.Enabled = false;
        H.Read(await rig.Controller().ListBuilds(projectId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await rig.Controller().GetBuild(projectId, jobId, CancellationToken.None)).Status.Should().Be(404);
        H.Read(await rig.Controller().EstimateVariant(projectId, car, null, CancellationToken.None)).Status.Should().Be(404);
    }

    [Fact]
    public async Task A_role_with_no_project_access_is_refused_every_build_route()
    {
        Rig rig = new();
        rig.GrantMediaConsent();
        (Guid car, _) = await rig.CarAsync();
        Guid jobId = await rig.StartAsync(car);
        H.User billing = rig.H.InRole(OrganizationRole.Billing);
        Guid projectId = rig.H.Project.Id;

        H.Read(await rig.Controller(billing).ListBuilds(projectId, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await rig.Controller(billing).GetBuild(projectId, jobId, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await rig.Controller(billing).EstimateVariant(projectId, car, null, CancellationToken.None)).Status.Should().Be(403);
        H.Read(await rig.Controller(billing).CreateVariant(projectId, car, Rig.Body(label: "Blue"), CancellationToken.None)).Status.Should().Be(403);
        H.Read(await rig.Controller(billing).CancelBuild(projectId, jobId, CancellationToken.None)).Status.Should().Be(403);
        rig.Job(jobId).State.Should().Be("queued");
        rig.Charges!.Holds.Should().ContainSingle("only the owner's own start held anything");
    }
}
