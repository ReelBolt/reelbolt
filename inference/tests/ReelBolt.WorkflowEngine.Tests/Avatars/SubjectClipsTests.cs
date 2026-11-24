#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Avatars;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using Xunit;
using H = ReelBolt.WorkflowEngine.Tests.Avatars.AvatarTestHarness;

namespace ReelBolt.WorkflowEngine.Tests.Avatars;

/// <summary>
/// Lane L5 of docs/design/avatar-objects.md, "Generating a clip with avatars from the editor (subject clips)": the
/// <c>subject-clips</c> routes and <see cref="ISubjectClipService"/>. Every refusal in the design's order (a refusal writes
/// nothing), the hidden <see cref="WorkflowDefinitionKind.SubjectClip"/> run and its one inline <c>VideoGenerate</c> step
/// (subjects, the media permission set by the server, the output registered, the spend cap = the estimate), the estimate
/// (equal to the engine's own hold), the status, the stop, the hidden kind, and the wire shapes the web client reads. No
/// vendor is reachable: the video model is a fake whose declarations each test chooses.
/// </summary>
public class SubjectClipsTests
{
    // ------------------------------------------------------------------ the happy path

    [Fact]
    public async Task A_clip_of_a_person_and_an_object_queues_one_hidden_kind_9_run_with_one_inline_step()
    {
        Rig rig = await Rig.CreateAsync();

        (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));

        status.Should().Be(202, body.ToString());
        Guid executionId = body.GetProperty("executionId").GetGuid();
        body.GetProperty("state").GetString().Should().Be("queued");
        body.GetProperty("credits").GetDouble().Should().Be(140, "the hold the engine reserves (120 for the six seconds, 20 for the input material)");
        body.ToString().Should().NotContain("projects/").And.NotContain("storageKey");

        using InferenceApiDbContext db = rig.Db();
        WorkflowDefinition workflow = await db.WorkflowDefinitions.Include(w => w.Steps).SingleAsync();
        workflow.Kind.Should().Be(WorkflowDefinitionKind.SubjectClip);
        ((int)workflow.Kind).Should().Be(9);
        workflow.Name.Should().Be("Avatar clip: Anna, Espresso machine");
        WorkflowStep step = workflow.Steps.Should().ContainSingle().Subject;
        step.StepType.Should().Be(StepType.VideoGenerate);
        step.RegisterOutputAsProjectFile.Should().BeTrue("the takes are ordinary project video files the media pool shows");
        step.CacheMode.Should().Be(StepCacheMode.Never, "a new clip is asked for every time");
        step.Label.Should().Be("She opens the box");

        VideoGenerateStepConfig config = Config(step);
        config.PlanSource.Should().Be(VideoGeneratePlanSource.Inline);
        config.InlinePrompt.Should().Be("She opens the box");
        config.Subjects!.Select(s => s.AvatarId).Should().Equal(rig.Anna, rig.Machine);
        config.AvatarId.Should().BeNull();
        config.AllowSourceMediaEgress.Should().BeTrue("set by the server because the media consent is recorded");
        (config.Model, config.Resolution, config.Ratio, config.DurationSeconds, config.Takes, config.MaxClips)
            .Should().Be(("MiniMax-H3", "768P", "16:9", 6, 1, 1));
        config.ProviderId.Should().Be(Rig.CapabilityId, "the model the clip was priced on is pinned");
        config.SubjectMode.Should().Be(VideoSubjectMode.Auto);
        config.AvatarAudioProjectFileId.Should().BeNull();
        config.MaxSpendCredits.Should().Be(140m, "the step's own spend cap is the estimate's high figure");
        config.Validate().Should().BeEmpty();

        rig.Queued.Should().ContainSingle();
        rig.Queued[0].Kind.Should().Be(WorkflowDefinitionKind.SubjectClip);
        rig.Queued[0].ExecutionId.Should().Be(executionId);
        rig.Queued[0].Initiator.Should().Be(ExecutionInitiator.Human);
        rig.Preflights.Last().VideoGenerateConfigJson.Should().Be(step.VideoGenerateConfigJson, "the balance check prices the very step that is queued");
        rig.RunRate.Calls.Should().ContainSingle().Which.Should().Be((RunRateBucket.Paid, SubjectClipCodes.RunRateSource));
    }

    [Fact]
    public async Task The_estimate_writes_nothing_and_equals_the_engines_hold_and_the_spend_cap()
    {
        Rig rig = await Rig.CreateAsync();

        (int status, JsonElement estimate) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(), CancellationToken.None));

        status.Should().Be(200, estimate.ToString());
        estimate.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        estimate.GetProperty("credits").GetDouble().Should().Be(140);
        estimate.GetProperty("creditsHigh").GetDouble().Should().Be(140);
        estimate.GetProperty("balanceCredits").GetDouble().Should().Be(100_000);
        estimate.GetProperty("affordable").GetBoolean().Should().BeTrue();
        estimate.GetProperty("path").GetString().Should().Be("References");
        estimate.GetProperty("stillIncluded").GetBoolean().Should().BeFalse();
        estimate.GetProperty("unavailableReason").ValueKind.Should().Be(JsonValueKind.Null);
        await rig.AssertNothingWrittenAsync();

        H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None)).Status.Should().Be(202);
        using InferenceApiDbContext db = rig.Db();
        string json = (await db.WorkflowSteps.SingleAsync()).VideoGenerateConfigJson!;
        long? engineHold = await VideoGenerateCreditPricing.TryHoldMillisAsync(json, rig.Providers.Object, rig.Clients, rig.Prices.Object);
        engineHold.Should().Be(140_000, "the engine reserves exactly what the estimate showed");
        Config(json).MaxSpendCredits.Should().Be((decimal)estimate.GetProperty("creditsHigh").GetDouble());
    }

    [Fact]
    public async Task Two_takes_hold_twice_and_a_bring_your_own_model_still_gets_a_positive_cap()
    {
        Rig rig = await Rig.CreateAsync();
        H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body() with { Takes = 2 }, CancellationToken.None)).Status.Should().Be(202);
        VideoGenerateStepConfig two = Config(await rig.SingleStepAsync());
        (two.Takes, two.MaxClips, two.MaxSpendCredits).Should().Be((2, 2, 280m));

        Rig byo = await Rig.CreateAsync(arrange: r => r.HoldPerSecondMillis = 0);
        H.Read(await byo.Controller().Start(byo.ProjectId, byo.Body(), CancellationToken.None)).Status.Should().Be(202);
        Config(await byo.SingleStepAsync()).MaxSpendCredits.Should().Be(Math.Ceiling(CreditCurrency.CreditsFromUsd(0.70m) * 100m) / 100m,
            "held at nothing, the step is still capped at the vendor's own price of its take and input material");
    }

    // ------------------------------------------------------------------ who may generate with a person (Avatars:Generation)

    [Fact]
    public async Task With_person_generation_off_a_clip_of_a_person_is_refused_before_anything_is_queued_and_objects_still_work()
    {
        Rig rig = await Rig.CreateAsync();
        rig.H.Options.Generation = AvatarPersonGeneration.Off;

        (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
        (status, Code(body)).Should().Be((409, SubjectClipCodes.GenerationDisabled),
            "the engine would refuse the run with AVATAR_GENERATION_DISABLED, so it is never queued");
        body.GetProperty("message").GetString().Should().Contain("objects only");
        rig.Queued.Should().BeEmpty();
        rig.RunRate.Calls.Should().BeEmpty("a refused clip uses none of the run limit");

        (int estimated, JsonElement estimate) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(), CancellationToken.None));
        estimated.Should().Be(200, estimate.ToString());
        estimate.GetProperty("unavailableCode").GetString().Should().Be(SubjectClipCodes.GenerationDisabled);
        estimate.GetProperty("affordable").GetBoolean().Should().BeFalse();
        estimate.GetProperty("credits").ValueKind.Should().Be(JsonValueKind.Null, "no price is shown for a clip that cannot run");
        await rig.AssertNothingWrittenAsync();

        // A clip of objects only needs only the objects switch.
        H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Machine), CancellationToken.None)).Status.Should().Be(202);
        rig.Queued.Should().ContainSingle();
    }

    [Fact]
    public async Task In_the_platform_admin_smoke_mode_only_a_platform_admin_may_make_a_clip_of_a_person()
    {
        Rig rig = await Rig.CreateAsync();
        rig.H.Options.Generation = AvatarPersonGeneration.PlatformAdminOnly;

        // No account row (or an ordinary one): refused, as the engine refuses a run whose initiator is not an admin.
        (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
        (status, Code(body)).Should().Be((409, SubjectClipCodes.GenerationDisabled));
        body.GetProperty("message").GetString().Should().Contain("platform administrators");
        rig.Queued.Should().BeEmpty();

        using (InferenceApiDbContext db = rig.Db())
        {
            db.ApplicationUsers.Add(new ApplicationUser { Id = rig.H.OwnerId, Email = "owner@example.test", DisplayName = "owner", IsAdmin = true });
            await db.SaveChangesAsync();
        }

        H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None)).Status.Should().Be(202);
        rig.Queued.Should().ContainSingle();
    }

    [Fact]
    public void The_person_generation_switch_defaults_to_off_and_binds_from_the_engines_key()
    {
        new AvatarOptions().Generation.Should().Be(AvatarPersonGeneration.Off);

        foreach ((string value, AvatarPersonGeneration expected) in new[]
                 {
                     ("Off", AvatarPersonGeneration.Off), ("PlatformAdminOnly", AvatarPersonGeneration.PlatformAdminOnly), ("On", AvatarPersonGeneration.On)
                 })
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [AvatarOptions.GenerationKey] = value })
                .Build();
            var bound = new AvatarOptions();
            configuration.GetSection(AvatarOptions.SectionName).Bind(bound);
            bound.Generation.Should().Be(expected);
        }

        // The API's words are the engine's, so one AVATARS_GENERATION means the same on both sides.
        Enum.GetNames<AvatarPersonGeneration>().Should().Equal(Enum.GetNames<ReelBolt.WorkflowEngine.Services.Avatars.AvatarGenerationMode>());
    }

    // ------------------------------------------------------------------ the order of the checks

    [Fact]
    public async Task Every_refusal_comes_in_the_designs_order_and_writes_nothing()
    {
        // 1. neither switch: a bare 404 before the project is even looked at.
        {
            Rig rig = await Rig.CreateAsync();
            rig.H.Options.Enabled = false;
            rig.H.Options.Objects.Enabled = false;
            (await rig.Controller(H.Stranger()).Start(rig.ProjectId, rig.Body(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
            (await rig.Controller().Estimate(rig.ProjectId, rig.Body(), CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
            await rig.AssertNothingWrittenAsync();
        }

        // 2. project access: another workspace's project is 404; a viewer may not start one.
        {
            Rig rig = await Rig.CreateAsync();
            H.Read(await rig.Controller(H.Stranger()).Start(rig.ProjectId, rig.Body(), CancellationToken.None)).Status.Should().Be(404);
            H.Read(await rig.Controller(rig.H.InRole(OrganizationRole.Billing)).Start(rig.ProjectId, rig.Body(), CancellationToken.None))
                .Status.Should().BeOneOf(403, 404);
            await rig.AssertNothingWrittenAsync();
        }

        // 3. an unverified email.
        {
            Rig rig = await Rig.CreateAsync();
            (int status, JsonElement body) = H.Read(await rig.Controller(rig.H.Owner(emailVerified: false)).Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((403, "EMAIL_NOT_VERIFIED"));
            await rig.AssertNothingWrittenAsync();
        }

        // 4. the subjects the ids name: unknown (404 with its code), a kind switched off, too many, two people.
        {
            Rig rig = await Rig.CreateAsync();
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(Guid.NewGuid()), CancellationToken.None));
            (status, Code(body)).Should().Be((404, "AVATAR_NOT_FOUND"));

            rig.H.Options.Objects.Enabled = false;
            (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((404, "AVATAR_NOT_FOUND"), "a row of a kind whose switch is off is treated as absent");
            rig.H.Options.Objects.Enabled = true;

            Guid cup = await rig.H.CreateObjectAsync("Cup");
            Guid saucer = await rig.H.CreateObjectAsync("Saucer");
            (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Anna, rig.Machine, cup, saucer), CancellationToken.None));
            (status, Code(body)).Should().Be((400, "too_many_subjects"));

            Guid bruno = await rig.H.CreateReadyAsync("Bruno");
            (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Anna, bruno), CancellationToken.None));
            (status, Code(body)).Should().Be((400, "too_many_people"));
            await rig.AssertNothingWrittenAsync();
        }

        // 5. the plan: video generation first, then each kind named.
        foreach ((Func<OrganizationEntitlementSet, OrganizationEntitlementSet> off, string feature) in new (Func<OrganizationEntitlementSet, OrganizationEntitlementSet>, string)[]
                 {
                     (s => s with { VideoGenAllowed = false }, "video_generation"),
                     (s => s with { AvatarsAllowed = false }, "avatars"),
                     (s => s with { AvatarObjectsAllowed = false }, "avatar_objects")
                 })
        {
            Rig rig = await Rig.CreateAsync(consented: false);
            rig.H.Entitlements.Set = off(rig.H.Entitlements.Set);
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body), body.GetProperty("feature").GetString()).Should().Be((402, "PLAN_NOT_ENTITLED", feature), "the plan comes before the consent");
            await rig.AssertNothingWrittenAsync();
        }

        // 6. the media consent, before the one rule, the model and the credits.
        {
            Rig rig = await Rig.CreateAsync(consented: false, arrange: r => r.Balance = 1);
            await rig.RevokeAsync(rig.Anna);
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((409, "CONSENT_REQUIRED"));
            await rig.AssertNothingWrittenAsync();
        }

        // 7. the one rule.
        {
            Rig rig = await Rig.CreateAsync(arrange: r => r.Balance = 1);
            await rig.RevokeAsync(rig.Anna);
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body), body.GetProperty("reason").GetString()).Should().Be((409, "AVATAR_NOT_USABLE", "consent_revoked"));
            await rig.AssertNothingWrittenAsync();
        }

        // 8. a model that takes no pictures, naming the one of its family that does.
        {
            Rig rig = await Rig.CreateAsync(arrange: r =>
            {
                r.Kind = InferenceProviderKind.FalAi;
                r.Model = "fal-ai/veo3.1";
                r.Client.References = VideoReferenceInputSupport.None;
                r.Client.Images = VideoImageInputSupport.None;
                r.Balance = 1;
            });
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((409, "AVATAR_NOT_SUPPORTED_BY_MODEL"));
            body.GetProperty("sibling").GetString().Should().Be("fal-ai/veo3.1/reference-to-video");
            await rig.AssertNothingWrittenAsync();
        }

        // 9. the settings against what the model offers.
        {
            Rig rig = await Rig.CreateAsync(arrange: r => r.Balance = 1);
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId,
                rig.Body() with { DurationSeconds = 30, Ratio = "4:5", Prompt = "  " }, CancellationToken.None));
            (status, Code(body)).Should().Be((400, SubjectClipCodes.RequestInvalid));
            body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            string[] errors = [.. body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()!)];
            errors.Should().Contain("Say what happens in the clip.")
                .And.Contain("This model makes clips in these shapes: 16:9, 9:16, 1:1.")
                .And.Contain("The length must be 4 to 15 seconds for this model.");
            await rig.AssertNothingWrittenAsync();
        }

        // 10. credits.
        {
            Rig rig = await Rig.CreateAsync(arrange: r => r.Balance = 100_000);
            rig.RunRate.Refuse = true;
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((402, "INSUFFICIENT_CREDITS"), "credits come before the run rate");
            body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
            body.GetProperty("missing").GetDouble().Should().Be(40);
            rig.RunRate.Calls.Should().BeEmpty();
            await rig.AssertNothingWrittenAsync();
        }

        // 11. the paid run rate, with the wait in Retry-After.
        {
            Rig rig = await Rig.CreateAsync();
            rig.RunRate.Refuse = true;
            SubjectClipsController controller = rig.Controller();
            (int status, JsonElement body) = H.Read(await controller.Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((429, "RUN_RATE_LIMITED"));
            body.GetProperty("retryAfterSeconds").GetInt32().Should().Be(120);
            controller.Response.Headers.RetryAfter.ToString().Should().Be("120");
            await rig.AssertNothingWrittenAsync();
        }

        // 12. the plan's concurrency cap, raised by the queue.
        {
            Rig rig = await Rig.CreateAsync();
            rig.QueueThrows = new PlanLimitException(new PlanGateViolation("CONCURRENCY_LIMIT_REACHED", "concurrency", "Too many runs.", 429));
            (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(), CancellationToken.None));
            (status, Code(body)).Should().Be((429, "CONCURRENCY_LIMIT_REACHED"));
            await rig.AssertNothingWrittenAsync();
        }
    }

    [Fact]
    public async Task A_web_session_may_allow_sending_media_for_one_call_on_the_route_and_nothing_else_may()
    {
        Rig rig = await Rig.CreateAsync(consented: false);

        // Another route (the assistant's chat request runs the service in process): the word counts for nothing.
        (int status, JsonElement body) = H.Read(await rig.Controller(path: "/api/v1/assistant/chat")
            .Start(rig.ProjectId, rig.Body() with { AllowMediaEgress = true }, CancellationToken.None));
        (status, Code(body)).Should().Be((409, "CONSENT_REQUIRED"));

        // An API key on the route itself: no.
        (status, body) = H.Read(await rig.Controller(scoped: true).Start(rig.ProjectId, rig.Body() with { AllowMediaEgress = true }, CancellationToken.None));
        (status, Code(body)).Should().Be((409, "CONSENT_REQUIRED"));
        await rig.AssertNothingWrittenAsync();

        // The person at the screen, on this route: yes, for this one call; the project consent is not recorded.
        H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body() with { AllowMediaEgress = true }, CancellationToken.None)).Status.Should().Be(202);
        Config(await rig.SingleStepAsync()).AllowSourceMediaEgress.Should().BeTrue();
        using InferenceApiDbContext db = rig.Db();
        (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == rig.ProjectId)).SubjectMediaConsentAt.Should().BeNull("a per-call word never records the project consent");
    }

    [Fact]
    public async Task The_estimate_says_what_is_missing_beside_the_price_instead_of_refusing()
    {
        // No consent: the price, and CONSENT_REQUIRED (a start asks for it).
        Rig rig = await Rig.CreateAsync(consented: false, arrange: r => r.Balance = 10_000);
        (int status, JsonElement e) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(), CancellationToken.None));
        status.Should().Be(200, e.ToString());
        e.GetProperty("unavailableCode").GetString().Should().Be("CONSENT_REQUIRED");
        e.GetProperty("credits").GetDouble().Should().Be(140);
        e.GetProperty("affordable").GetBoolean().Should().BeFalse("10 credits do not pay a 140-credit hold");
        await rig.AssertNothingWrittenAsync();

        // No video model at all.
        Rig none = await Rig.CreateAsync(arrange: r => r.NoProvider = true);
        (status, e) = H.Read(await none.Controller().Estimate(none.ProjectId, none.Body(), CancellationToken.None));
        status.Should().Be(200);
        e.GetProperty("unavailableCode").GetString().Should().Be(SubjectClipCodes.ModelMissing);
        e.GetProperty("unavailableReason").GetString().Should().Be("No video model is set up for this workspace. Ask an administrator to add one.");
        e.GetProperty("affordable").GetBoolean().Should().BeFalse();
        (int startStatus, JsonElement started) = H.Read(await none.Controller().Start(none.ProjectId, none.Body(), CancellationToken.None));
        (startStatus, Code(started)).Should().Be((409, SubjectClipCodes.ModelMissing));

        // Billing off: no figure, affordable.
        Rig free = await Rig.CreateAsync(arrange: r => r.BillingOn = false);
        (status, e) = H.Read(await free.Controller().Estimate(free.ProjectId, free.Body(), CancellationToken.None));
        (status, e.GetProperty("billingEnabled").GetBoolean(), e.GetProperty("affordable").GetBoolean()).Should().Be((200, false, true));
        e.GetProperty("credits").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_model_with_only_an_image_input_shows_the_subjects_through_one_still_that_is_part_of_the_price()
    {
        Rig rig = await Rig.CreateAsync(arrange: r =>
        {
            r.Kind = InferenceProviderKind.FalAi;
            r.Model = "fal-ai/kling-video/v3/turbo/pro/image-to-video";
            r.Client.References = VideoReferenceInputSupport.None;
            r.Client.Images = VideoImageInputSupport.FirstFrame;
        });

        (int status, JsonElement e) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(), CancellationToken.None));
        status.Should().Be(200, e.ToString());
        e.GetProperty("path").GetString().Should().Be("FirstFrame");
        e.GetProperty("stillIncluded").GetBoolean().Should().BeTrue();
        e.GetProperty("credits").GetDouble().Should().Be(120 + 12.6, "the take, and the one still at its fixed hold ($0.063 at 1.2x)");

        // No picture model to make the still: unavailable in the estimate, refused at start, nothing written.
        Rig noStill = await Rig.CreateAsync(arrange: r =>
        {
            r.Kind = InferenceProviderKind.FalAi;
            r.Model = "fal-ai/kling-video/v3/turbo/pro/image-to-video";
            r.Client.References = VideoReferenceInputSupport.None;
            r.Client.Images = VideoImageInputSupport.FirstFrame;
            r.StillKind = null;
        });
        (status, e) = H.Read(await noStill.Controller().Estimate(noStill.ProjectId, noStill.Body(), CancellationToken.None));
        e.GetProperty("unavailableCode").GetString().Should().Be(SubjectClipCodes.StillProviderMissing);
        (int startStatus, JsonElement started) = H.Read(await noStill.Controller().Start(noStill.ProjectId, noStill.Body(), CancellationToken.None));
        (startStatus, Code(started)).Should().Be((409, SubjectClipCodes.StillProviderMissing));
        await noStill.AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task A_clip_with_speech_resolves_and_prices_through_LipSync_and_without_speech_through_VideoGeneration()
    {
        void TalkingHead(Rig r)
        {
            r.Kind = InferenceProviderKind.FalAi;
            r.Model = "minimax/h3-max/lip-sync/image-to-video";
            r.Client.References = new VideoReferenceInputSupport(0, 0, 1, 1, 0, 15_000, true, true) { AnimatesStill = true, MinAudioMs = 5_000 };
            r.Client.Images = VideoImageInputSupport.FirstFrame;
        }

        Rig rig = await Rig.CreateAsync(arrange: TalkingHead);
        ProjectFile speech = rig.H.AddProbedMedia("hello.wav", "audio/wav", 8.4);

        (int status, JsonElement body) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(rig.Anna) with { SpeechFileId = speech.Id }, CancellationToken.None));
        status.Should().Be(200, body.ToString());
        rig.Providers.Verify(p => p.ResolveLipSyncAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        rig.Providers.Verify(p => p.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never,
            "a talking clip never asks for a VideoGeneration model");
        rig.Priced.Should().NotBeEmpty().And.OnlyContain(r => r.Capability == InferenceProviderCapability.LipSync,
            "the hold is priced against the LipSync price row");

        // Without speech the same project and model stay on VideoGeneration (a model that takes pictures).
        Rig plain = await Rig.CreateAsync();
        (status, body) = H.Read(await plain.Controller().Estimate(plain.ProjectId, plain.Body(), CancellationToken.None));
        status.Should().Be(200, body.ToString());
        plain.Providers.Verify(p => p.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        plain.Providers.Verify(p => p.ResolveLipSyncAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        plain.Priced.Should().OnlyContain(r => r.Capability == InferenceProviderCapability.VideoGeneration);
    }

    [Fact]
    public async Task A_clip_with_speech_and_no_lip_sync_model_answers_the_lip_sync_refusal_before_anything_is_held()
    {
        Rig rig = await Rig.CreateAsync(arrange: r => { r.NoLipSync = true; });
        ProjectFile speech = rig.H.AddProbedMedia("hello.wav", "audio/wav", 8.4);

        (int status, JsonElement estimate) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(rig.Anna) with { SpeechFileId = speech.Id }, CancellationToken.None));
        status.Should().Be(200, estimate.ToString());
        estimate.GetProperty("unavailableCode").GetString().Should().Be("LIP_SYNC_MODEL_NOT_SET");
        estimate.GetProperty("unavailableReason").GetString().Should().Be(LipSyncStepResolution.NoModelMessage);

        (status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Anna) with { SpeechFileId = speech.Id }, CancellationToken.None));
        (status, Code(body)).Should().Be((409, "LIP_SYNC_MODEL_NOT_SET"));
        rig.Priced.Should().BeEmpty("nothing is priced or held without a model");
        await rig.AssertNothingWrittenAsync();
    }
    [Fact]
    public async Task A_talking_clip_is_one_person_and_a_wav_or_mp3_of_the_project_and_lasts_as_long_as_its_speech()
    {
        void TalkingHead(Rig r)
        {
            r.Kind = InferenceProviderKind.FalAi;
            r.Model = "minimax/h3-max/lip-sync/image-to-video";
            r.Client.References = new VideoReferenceInputSupport(0, 0, 1, 1, 0, 15_000, true, true) { AnimatesStill = true, MinAudioMs = 5_000 };
            r.Client.Images = VideoImageInputSupport.FirstFrame;
        }

        Rig rig = await Rig.CreateAsync(arrange: TalkingHead);
        ProjectFile speech = rig.H.AddProbedMedia("hello.wav", "audio/wav", 8.4);
        ProjectFile picture = rig.H.AddImage("not-speech.png", salt: 77);

        // With an object as well: one person and nothing else.
        (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body() with { SpeechFileId = speech.Id }, CancellationToken.None));
        (status, Code(body)).Should().Be((400, SubjectClipCodes.SpeechNeedsOnePerson));

        // Not a recording.
        (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Anna) with { SpeechFileId = picture.Id }, CancellationToken.None));
        (status, Code(body)).Should().Be((400, SubjectClipCodes.SpeechFileInvalid));

        // Without speech, a model that animates a still cannot show anyone.
        (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Anna), CancellationToken.None));
        (status, Code(body)).Should().Be((409, "AVATAR_NOT_SUPPORTED_BY_MODEL"));
        await rig.AssertNothingWrittenAsync();

        (int estimateStatus, JsonElement estimate) = H.Read(await rig.Controller().Estimate(rig.ProjectId, rig.Body(rig.Anna) with { SpeechFileId = speech.Id }, CancellationToken.None));
        estimateStatus.Should().Be(200, estimate.ToString());
        estimate.GetProperty("path").GetString().Should().Be("TalkingHead");
        estimate.GetProperty("stillIncluded").GetBoolean().Should().BeTrue();

        (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body(rig.Anna) with { SpeechFileId = speech.Id, DurationSeconds = 4 }, CancellationToken.None));
        status.Should().Be(202, body.ToString());
        VideoGenerateStepConfig config = Config(await rig.SingleStepAsync());
        config.AvatarAudioProjectFileId.Should().Be(speech.Id);
        config.DurationSeconds.Should().Be(9, "8.4 s of speech, rounded up: the take is as long as what is said");
        config.Subjects!.Select(s => s.AvatarId).Should().Equal(rig.Anna);
    }

    [Fact]
    public async Task A_model_named_by_the_caller_must_be_a_video_model_of_this_workspace_or_the_platform()
    {
        Rig rig = await Rig.CreateAsync();
        Guid foreign = Guid.NewGuid();
        using (InferenceApiDbContext db = rig.Db())
        {
            db.ModelCapabilities.Add(new ModelCapability { Id = Rig.CapabilityId, ModelId = Guid.NewGuid(), OrganizationId = null, Capability = InferenceProviderCapability.VideoGeneration });
            db.ModelCapabilities.Add(new ModelCapability { Id = foreign, ModelId = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), Capability = InferenceProviderCapability.VideoGeneration });
            await db.SaveChangesAsync();
        }

        (int status, JsonElement body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body() with { CapabilityId = foreign }, CancellationToken.None));
        (status, Code(body)).Should().Be((400, SubjectClipCodes.ModelNotFound), "another workspace's model is not found, whatever it is");
        (status, body) = H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body() with { CapabilityId = Guid.NewGuid() }, CancellationToken.None));
        (status, Code(body)).Should().Be((400, SubjectClipCodes.ModelNotFound));
        await rig.AssertNothingWrittenAsync();

        H.Read(await rig.Controller().Start(rig.ProjectId, rig.Body() with { CapabilityId = Rig.CapabilityId }, CancellationToken.None)).Status.Should().Be(202);
        Config(await rig.SingleStepAsync()).ProviderId.Should().Be(Rig.CapabilityId);
    }

    // ------------------------------------------------------------------ status, stop

    [Fact]
    public async Task The_status_maps_the_hidden_run_and_names_the_subjects()
    {
        Rig rig = await Rig.CreateAsync();
        Guid queued = await rig.SeedRunAsync(ExecutionStatus.Queued);
        Guid running = await rig.SeedRunAsync(ExecutionStatus.Running);
        Guid ready = await rig.SeedRunAsync(ExecutionStatus.Passed, StepStatus.Completed, takes: 2);
        Guid failed = await rig.SeedRunAsync(ExecutionStatus.Failed, StepStatus.Failed, failureCode: "BUDGET_EXCEEDED", error: "The step's spend cap was reached.");
        Guid cancelled = await rig.SeedRunAsync(ExecutionStatus.Cancelled);

        async Task<JsonElement> Get(Guid id)
        {
            (int status, JsonElement body) = H.Read(await rig.Controller().Get(rig.ProjectId, id, CancellationToken.None));
            status.Should().Be(200);
            return body;
        }

        (await Get(queued)).GetProperty("state").GetString().Should().Be("queued");
        (await Get(running)).GetProperty("state").GetString().Should().Be("running");
        JsonElement done = await Get(ready);
        done.GetProperty("state").GetString().Should().Be("ready");
        done.GetProperty("outputFileIds").EnumerateArray().Select(e => e.GetGuid()).Should().Equal(rig.SeededFiles[ready]);
        done.GetProperty("subjects").EnumerateArray().Select(s => (s.GetProperty("name").GetString(), s.GetProperty("kind").GetString()))
            .Should().Equal(("Anna", "Person"), ("Espresso machine", "Object"));
        JsonElement bad = await Get(failed);
        (bad.GetProperty("state").GetString(), bad.GetProperty("failureCode").GetString(), bad.GetProperty("message").GetString())
            .Should().Be(("failed", "BUDGET_EXCEEDED", "The step's spend cap was reached."));
        bad.GetProperty("outputFileIds").GetArrayLength().Should().Be(0);
        (await Get(cancelled)).GetProperty("state").GetString().Should().Be("cancelled");
        done.ToString().Should().NotContain("projects/").And.NotContain("storageKey");

        // A held result is not served.
        using (InferenceApiDbContext db = rig.Db())
        {
            (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == rig.ProjectId)).CsamOutputHoldAt = DateTime.UtcNow.AddMinutes(30);
            await db.SaveChangesAsync();
        }

        JsonElement held = await Get(ready);
        (held.GetProperty("state").GetString(), held.GetProperty("failureCode").GetString()).Should().Be(("failed", "OUTPUT_UNAVAILABLE"));
        held.GetProperty("outputFileIds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Another_projects_run_another_kinds_run_and_a_missing_one_are_the_same_404()
    {
        Rig rig = await Rig.CreateAsync();
        Guid otherKind = await rig.SeedRunAsync(ExecutionStatus.Running, kind: WorkflowDefinitionKind.VideoEnhance);
        Guid otherProject = await rig.SeedRunAsync(ExecutionStatus.Running, projectId: Guid.NewGuid());

        foreach (Guid id in new[] { otherKind, otherProject, Guid.NewGuid() })
        {
            (await rig.Controller().Get(rig.ProjectId, id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
            (await rig.Controller().Cancel(rig.ProjectId, id, CancellationToken.None)).Result.Should().BeOfType<NotFoundResult>();
        }

        H.Read(await rig.Controller(H.Stranger()).Get(rig.ProjectId, otherKind, CancellationToken.None)).Status.Should().Be(404);
        rig.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task A_running_clip_is_stopped_through_the_workflow_stop_event_and_a_finished_one_is_409()
    {
        Rig rig = await Rig.CreateAsync();
        Guid running = await rig.SeedRunAsync(ExecutionStatus.Running);
        Guid done = await rig.SeedRunAsync(ExecutionStatus.Passed, StepStatus.Completed);

        (int status, JsonElement body) = H.Read(await rig.Controller().Cancel(rig.ProjectId, running, CancellationToken.None));
        (status, body.GetProperty("state").GetString()).Should().Be((202, "stopping"));
        rig.Published.Should().ContainSingle().Which.ExecutionId.Should().Be(running);
        rig.Published[0].RequestedByUserId.Should().Be(rig.H.OwnerId);

        (status, body) = H.Read(await rig.Controller().Cancel(rig.ProjectId, done, CancellationToken.None));
        (status, Code(body)).Should().Be((409, SubjectClipCodes.AlreadyFinished), "the web reads a 409 as \"it ended first\"");
        rig.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task The_service_lists_the_projects_clips_for_the_assistant()
    {
        Rig rig = await Rig.CreateAsync();
        Guid running = await rig.SeedRunAsync(ExecutionStatus.Running, queuedAt: DateTime.UtcNow.AddMinutes(-1));
        Guid done = await rig.SeedRunAsync(ExecutionStatus.Passed, StepStatus.Completed, queuedAt: DateTime.UtcNow.AddMinutes(-5));
        await rig.SeedRunAsync(ExecutionStatus.Running, kind: WorkflowDefinitionKind.VideoEnhance);

        using InferenceApiDbContext db = rig.Db();
        ISubjectClipService service = rig.Service(db);
        (await service.ListAsync(rig.ProjectId, activeOnly: false, CancellationToken.None)).Select(s => s.ExecutionId).Should().Equal(running, done);
        (await service.ListAsync(rig.ProjectId, activeOnly: true, CancellationToken.None)).Select(s => s.ExecutionId).Should().Equal(running);
    }

    // ------------------------------------------------------------------ the hidden kind

    [Fact]
    public async Task The_hidden_kind_is_not_listed_deleted_retried_or_resumed_over_REST_and_the_orphan_reaper_takes_it()
    {
        Rig rig = await Rig.CreateAsync();
        Guid executionId = await rig.SeedRunAsync(ExecutionStatus.Failed, StepStatus.Failed);
        Guid workflowId;
        using (InferenceApiDbContext db = rig.Db())
            workflowId = (await db.WorkflowExecutions.SingleAsync(e => e.Id == executionId)).WorkflowDefinitionId;

        await using InferenceApiDbContext context = rig.H.NewDb();
        ICurrentUser user = rig.H.Owner();
        var workflows = new WorkflowsController(
            context, user, TestTenancy.Access(context, user), Mock.Of<IPublishEndpoint>(),
            new WorkflowTemplateProvisioningService(context, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
            Mock.Of<IWorkflowExecutionService>(), NullLogger<WorkflowsController>.Instance);

        ActionResult<List<WorkflowDefinitionResponse>> list = await workflows.List(rig.ProjectId, default);
        list.Result.Should().BeOfType<OkObjectResult>().Which.Value.Should().BeAssignableTo<List<WorkflowDefinitionResponse>>().Which.Should().BeEmpty();
        (await workflows.Delete(rig.ProjectId, workflowId, default)).Should().BeOfType<NotFoundResult>();
        JsonSerializer.Serialize((await workflows.ResumeExecution(rig.ProjectId, workflowId, executionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Contain("SUBJECT_CLIP_RESUME_NOT_SUPPORTED");
        JsonSerializer.Serialize((await workflows.RetryExecution(rig.ProjectId, workflowId, executionId, null, default)).Result
            .Should().BeOfType<ConflictObjectResult>().Subject.Value).Should().Contain("SUBJECT_CLIP_RETRY_NOT_SUPPORTED");

        using (InferenceApiDbContext check = rig.Db())
        {
            (await check.WorkflowExecutions.SingleAsync()).Status.Should().Be(ExecutionStatus.Failed, "a refusal touches nothing");
            (await check.WorkflowDefinitions.CountAsync(w => w.Kind == WorkflowDefinitionKind.Standard)).Should().Be(0,
                "every assistant, MCP, dashboard and apps path reads Kind == Standard");
        }

        DateTime now = DateTime.UtcNow;
        await using InferenceApiDbContext reaper = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid project = Guid.NewGuid();
        WorkflowDefinition W(DateTime at) => new() { Id = Guid.NewGuid(), ProjectId = project, Name = "Avatar clip: Anna", Kind = WorkflowDefinitionKind.SubjectClip, CreatedAt = at, UpdatedAt = at };
        WorkflowDefinition orphan = W(now.AddHours(-2)), young = W(now.AddMinutes(-30)), ran = W(now.AddDays(-2));
        reaper.WorkflowDefinitions.AddRange(orphan, young, ran);
        reaper.WorkflowExecutions.Add(new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = ran.Id, ProjectId = project, Status = ExecutionStatus.Passed, CorrelationId = "c" });
        await reaper.SaveChangesAsync();
        (await TimelineRenderOrphanReaper.SelectOrphans(reaper.WorkflowDefinitions, now - TimeSpan.FromHours(1)).Select(w => w.Id).ToListAsync())
            .Should().Equal(orphan.Id);
    }

    [Fact]
    public void A_per_call_media_word_counts_only_on_the_subject_clip_routes()
    {
        HttpRequest Request(string method, string path)
        {
            DefaultHttpContext http = new();
            http.Request.Method = method;
            http.Request.Path = path;
            return http.Request;
        }

        string p = Guid.NewGuid().ToString();
        MediaEgressCallPermission.IsOwnRoute(MediaEgressSurface.SubjectClip, Request("POST", $"/api/v1/projects/{p}/subject-clips")).Should().BeTrue();
        MediaEgressCallPermission.IsOwnRoute(MediaEgressSurface.SubjectClip, Request("POST", $"/api/v1/projects/{p}/subject-clips/estimate")).Should().BeTrue();
        MediaEgressCallPermission.IsOwnRoute(MediaEgressSurface.SubjectClip, Request("GET", $"/api/v1/projects/{p}/subject-clips")).Should().BeFalse();
        MediaEgressCallPermission.IsOwnRoute(MediaEgressSurface.SubjectClip, Request("POST", $"/api/v1/projects/{p}/subject-clips/{Guid.NewGuid()}/cancel")).Should().BeFalse();
        MediaEgressCallPermission.IsOwnRoute(MediaEgressSurface.SubjectClip, Request("POST", "/api/v1/assistant/chat")).Should().BeFalse();
        MediaEgressCallPermission.IsOwnRoute(MediaEgressSurface.PictureOperation, Request("POST", $"/api/v1/projects/{p}/subject-clips")).Should().BeFalse();
        ((int)MediaEgressSurface.PictureOperation).Should().Be(0, "the surfaces are append-only");
        ((int)MediaEgressSurface.SubjectClip).Should().Be(1);
    }

    // ------------------------------------------------------------------ the web contract

    /// <summary>
    /// The wire shapes against the web client that reads them (<c>web/lib/editing/subjects/subject-clip-api.ts</c>): every
    /// field of each response interface is a property the API writes, every field of the request is one the API reads, and
    /// the codes and plan features the dialog words are the ones the API answers.
    /// </summary>
    [Fact]
    public void The_json_shapes_are_the_ones_the_web_client_reads()
    {
        string source = File.ReadAllText(WebClientPath());
        JsonSerializerOptions web = new(JsonSerializerDefaults.Web);

        static HashSet<string> Props(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name)];

        HashSet<string> estimate = Props(JsonSerializer.SerializeToElement(new SubjectClipEstimate(true, 1, 1, 1, true, "References", false, null, null), web));
        HashSet<string> started = Props(JsonSerializer.SerializeToElement(new SubjectClipStarted(Guid.NewGuid(), "queued", 1), web));
        HashSet<string> status = Props(JsonSerializer.SerializeToElement(
            new SubjectClipStatus(Guid.NewGuid(), "ready", [Guid.NewGuid()], null, null, [new SubjectClipSubjectName("Anna", "Person")]), web));
        HashSet<string> subject = Props(JsonSerializer.SerializeToElement(new SubjectClipSubjectName("Anna", "Person"), web));
        HashSet<string> request = [.. typeof(SubjectClipRequestBody).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))];

        estimate.Should().BeEquivalentTo(Fields(source, "SubjectClipEstimate"));
        started.Should().BeEquivalentTo(Fields(source, "SubjectClipStart"));
        status.Should().BeEquivalentTo(Fields(source, "SubjectClipStatus"));
        subject.Should().BeEquivalentTo(["name", "kind"]);
        request.Should().Contain(Fields(source, "SubjectClipRequest"), "every field the web sends is read");
        request.Except(Fields(source, "SubjectClipRequest")).Should().BeEquivalentTo(["allowMediaEgress"], "the only field the editor does not send");
        typeof(SubjectClipSubjectRequest).GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Should().Equal("avatarId");

        foreach (string state in new[] { "queued", "running", "ready", "failed", "cancelled" })
            source.Should().Contain($"'{state}'");
        foreach (string code in new[]
                 {
                     SubjectClipCodes.ConsentRequired, SubjectClipCodes.NotSupportedByModel, ProjectSubjectReader.NotUsableCode,
                     ProjectSubjectReader.NotFoundCode, PlanGate.NotEntitledCode, NonWorkflowChargeCodes.InsufficientCredits,
                     EmailVerificationGate.ErrorCode, RunRateRefusal.Code
                 })
            source.Should().Contain($"'{code}'", $"the dialog words {code}");
        foreach (string feature in new[] { PlanGate.VideoGenerationFeature, PlanGate.AvatarsFeature, PlanGate.AvatarObjectsFeature })
            source.Should().Contain($"'{feature}'");
        foreach (string path in Enum.GetNames<VideoSubjectPath>())
            source.Should().Contain($"`{path}`", "the estimate's path is one the web names");
        foreach (string mode in Enum.GetNames<VideoSubjectMode>())
            source.Should().Contain($"'{mode}'");
        SubjectClipService.MaxPromptLength.Should().Be(int.Parse(Constant(source, "SUBJECT_CLIP_PROMPT_MAX")));
        SubjectClipService.MaxTakes.Should().Be(int.Parse(Constant(source, "SUBJECT_CLIP_MAX_TAKES")));
        SubjectClipService.DefaultSeconds.Should().Be(int.Parse(Constant(source, "SUBJECT_CLIP_DEFAULT_SECONDS")));
    }

    private static string WebClientPath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "web", "lib", "editing", "subjects", "subject-clip-api.ts")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("the test must find the web client beside the inference tree");
        return Path.Combine(dir!, "web", "lib", "editing", "subjects", "subject-clip-api.ts");
    }

    /// <summary>The field names of one exported TypeScript interface (comment lines left out).</summary>
    private static HashSet<string> Fields(string source, string name)
    {
        System.Text.RegularExpressions.Match m = Regex.Match(source, @"export interface " + name + @" \{(?<body>.*?)\n\}", RegexOptions.Singleline);
        m.Success.Should().BeTrue($"the web client declares {name}");
        return [.. m.Groups["body"].Value.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("/", StringComparison.Ordinal) && !l.StartsWith("*", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @"^(?<f>[A-Za-z]+)\??:"))
            .Where(f => f.Success)
            .Select(f => f.Groups["f"].Value)];
    }

    private static string Constant(string source, string name) =>
        Regex.Match(source, @"export const " + name + @" = (?<v>\d+);").Groups["v"].Value;

    // ------------------------------------------------------------------ helpers

    private static string Code(JsonElement body) => body.GetProperty("code").GetString()!;

    private static VideoGenerateStepConfig Config(WorkflowStep step) => Config(step.VideoGenerateConfigJson!);

    private static VideoGenerateStepConfig Config(string json) =>
        JsonSerializer.Deserialize<VideoGenerateStepConfig>(json, VideoGenerateStepConfig.JsonOptions)!;

    /// <summary>
    /// A project with a person "Anna" and an object "Espresso machine", the media consent recorded, a MiniMax-like video
    /// model (reference mode, 4..15 s, $0.10/s at 768P), prices at 1.2x the vendor's figure, a fal.ai picture-edit model for
    /// stills, a rich workspace and an open run limit. Each test changes what it needs.
    /// </summary>
    private sealed class Rig
    {
        public static readonly Guid CapabilityId = Guid.Parse("c0ffee00-0000-4000-8000-000000000001");

        public H H { get; } = new();
        public Guid ProjectId => H.Project.Id;
        public Guid Anna { get; private set; }
        public Guid Machine { get; private set; }
        public InferenceProviderKind Kind { get; set; } = InferenceProviderKind.MiniMax;
        public string Model { get; set; } = "MiniMax-H3";
        public bool NoProvider { get; set; }
        public bool NoLipSync { get; set; }
        public List<PriceRequest> Priced { get; } = [];
        public InferenceProviderKind? StillKind { get; set; } = InferenceProviderKind.FalAi;
        public bool BillingOn { get; set; } = true;
        public long Balance { get; set; } = 100_000_000;
        public long HoldPerSecondMillis { get; set; } = -1;
        public FakeVideoModel Client { get; } = new();
        public IVideoGenerationClientFactory Clients => Client;
        public Mock<IInferenceProviderResolver> Providers { get; } = new();
        public Mock<IModelPriceService> Prices { get; } = new();
        public FakeRunRate RunRate { get; } = new();
        public Exception? QueueThrows { get; set; }
        public List<(WorkflowDefinitionKind Kind, Guid? ExecutionId, ExecutionInitiator Initiator)> Queued { get; } = [];
        public List<WorkflowStep> Preflights { get; } = [];
        public List<WorkflowExecutionStopRequested> Published { get; } = [];
        public Dictionary<Guid, List<Guid>> SeededFiles { get; } = [];

        public static async Task<Rig> CreateAsync(bool consented = true, Action<Rig>? arrange = null)
        {
            Rig rig = new();
            rig.H.Options.Enabled = true;
            rig.H.Options.Objects.Enabled = true;
            rig.H.Options.Generation = AvatarPersonGeneration.On;
            rig.H.Entitlements.Set = new OrganizationEntitlementSet { AvatarsAllowed = true, AvatarObjectsAllowed = true, VideoGenAllowed = true };
            rig.Anna = await rig.H.CreateReadyAsync("Anna");
            rig.Machine = await rig.H.CreateObjectAsync("Espresso machine");
            using (InferenceApiDbContext db = rig.Db())
            {
                db.AgentDefinitions.Add(new AgentDefinition { Id = Guid.NewGuid(), Name = "VideoTransform", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
                if (consented) (await db.Projects.IgnoreQueryFilters().SingleAsync(p => p.Id == rig.ProjectId)).SubjectMediaConsentAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }

            arrange?.Invoke(rig);
            rig.Wire();
            return rig;
        }

        /// <summary>A fresh context on the harness's shared store.</summary>
        public InferenceApiDbContext Db() => H.NewDb();

        private void Wire()
        {
            Providers.Setup(p => p.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid? _, CancellationToken _) => NoProvider
                    ? null
                    : new ResolvedVideoGenerationProvider(CapabilityId, "video", Kind, "https://vendor.example", Model, "key", 60,
                        OrganizationId: null, IsPlatformManaged: true, AccountId: Guid.NewGuid()));
            // A talking clip (a speech file) resolves through the LipSync capability, never VideoGeneration.
            Providers.Setup(p => p.ResolveLipSyncAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid? _, CancellationToken _) => NoProvider || NoLipSync
                    ? null
                    : new ResolvedVideoGenerationProvider(CapabilityId, "lipsync", Kind, "https://vendor.example", Model, "key", 60,
                        OrganizationId: null, IsPlatformManaged: true, AccountId: Guid.NewGuid()));
            Providers.Setup(p => p.ResolveImageJobAsync(InferenceProviderCapability.ImageEdit, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((InferenceProviderCapability _, Guid? _, CancellationToken _) => StillKind is { } kind
                    ? new ResolvedImageProvider(Guid.NewGuid(), "pictures", kind, InferenceProviderCapability.ImageEdit, "https://queue.fal.run", "", "key", 60)
                    : null);

            // 1.2x the vendor's own figure, in millicredits (1 credit = $0.006).
            Prices.Setup(p => p.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
                .Returns((PriceRequest r, CancellationToken _) =>
                {
                    Priced.Add(r);
                    decimal perSecond = HoldPerSecondMillis >= 0
                        ? HoldPerSecondMillis
                        : Client.Capabilities.PricePerSecondByResolution[r.Variant ?? "768P"] * 1.2m / 0.006m * 1000m;
                    return ValueTask.FromResult(new PriceQuote((long)(perSecond * r.Quantity), 0m, null));
                });
            Prices.Setup(p => p.PriceQuotedUsdAsync(It.IsAny<decimal>(), It.IsAny<UsageKind>(), It.IsAny<UsageBillingMode>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                .Returns((decimal usd, UsageKind _, UsageBillingMode _, DateTime? _, CancellationToken _) =>
                    ValueTask.FromResult(new PriceQuote(HoldPerSecondMillis == 0 ? 0 : (long)(usd * 1.2m / 0.006m * 1000m), usd, null)));
        }

        public SubjectClipService Service(InferenceApiDbContext db)
        {
            Mock<IWorkflowExecutionService> executions = new();
            executions.Setup(e => e.QueueAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(),
                    It.IsAny<ExecutionInitiator>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<RunChoices?>(),
                    It.IsAny<WorkflowDefinitionKind>(), It.IsAny<Guid?>(), It.IsAny<Func<CancellationToken, Task>?>()))
                .ReturnsAsync((Guid _, Guid workflowId, Guid _, string? _, ExecutionInitiator initiator, CancellationToken _, bool _, RunChoices? _,
                    WorkflowDefinitionKind kind, Guid? executionId, Func<CancellationToken, Task>? _) =>
                {
                    if (QueueThrows is not null) throw QueueThrows;
                    Queued.Add((kind, executionId, initiator));
                    return new WorkflowExecutionResponse(executionId ?? Guid.NewGuid(), workflowId, "Queued", null, null, 0, null, null, null, [], []);
                });

            Mock<IWorkflowCostEstimator> costs = new();
            costs.SetupGet(c => c.BillingEnabled).Returns(() => BillingOn);
            costs.Setup(c => c.PreflightAsync(It.IsAny<Guid?>(), It.IsAny<IReadOnlyList<WorkflowStep>>(), It.IsAny<CancellationToken>()))
                .Returns(async (Guid? _, IReadOnlyList<WorkflowStep> steps, CancellationToken ct) =>
                {
                    // The real estimator prices a VideoGenerate step through the engine's own call, so does this one.
                    Preflights.Add(steps[0]);
                    long hold = await VideoGenerateCreditPricing.TryHoldMillisAsync(steps[0].VideoGenerateConfigJson, Providers.Object, Clients, Prices.Object, ct) ?? 0;
                    WorkflowCostEstimate estimate = new(true, false, 0, 0, hold, [], 0, hold, CreditRangeBasis.FirstRun, 0, []);
                    return new WorkflowPreflightResult(Balance < hold ? WorkflowPreflightOutcome.InsufficientCredits : WorkflowPreflightOutcome.Allowed, estimate, Balance);
                });

            Mock<IPublishEndpoint> publish = new();
            publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionStopRequested>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecutionStopRequested, CancellationToken>((m, _) => Published.Add(m))
                .Returns(Task.CompletedTask);

            IOptions<AvatarOptions> options = Microsoft.Extensions.Options.Options.Create(H.Options);
            return new SubjectClipService(
                db, options, new ProjectSubjectReader(db, H.Storage.Object, options, NullLogger<ProjectSubjectReader>.Instance),
                NullLogger<SubjectClipService>.Instance, executions.Object, Providers.Object, Clients, Prices.Object, costs.Object,
                H.Entitlements, RunRate, publish.Object);
        }

        public SubjectClipsController Controller(ICurrentUser? user = null, string? path = null, bool scoped = false)
        {
            InferenceApiDbContext db = Db();
            user ??= H.Owner();
            DefaultHttpContext http = new();
            http.Request.Method = "POST";
            http.Request.Path = path ?? $"/api/v1/projects/{ProjectId}/subject-clips";
            List<Claim> claims = [new("sub", user.UserId.ToString())];
            if (scoped) claims.Add(new Claim(ReelBolt.Shared.Auth.AuthClaims.Scope, "runs.write"));
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
            return new SubjectClipsController(user, TestTenancy.Access(db, user), Service(db), Microsoft.Extensions.Options.Options.Create(H.Options))
            {
                ControllerContext = new ControllerContext { HttpContext = http }
            };
        }

        /// <summary>The editor's body: the subjects in order (Anna then the machine by default), and the plain settings.</summary>
        public SubjectClipRequestBody Body(params Guid[] subjects) => new(
            Subjects: [.. (subjects.Length == 0 ? [Anna, Machine] : subjects).Select(id => new SubjectClipSubjectRequest(id))],
            Prompt: "  She opens the box  ",
            DurationSeconds: 6,
            Ratio: "16:9",
            Resolution: "768P",
            Takes: 1);

        public async Task RevokeAsync(Guid avatarId)
        {
            using InferenceApiDbContext db = Db();
            ProjectAvatar avatar = await db.ProjectAvatars.SingleAsync(a => a.Id == avatarId);
            avatar.ConsentRevokedAt = DateTime.UtcNow;
            avatar.ConsentRevokedByUserId = H.OwnerId;
            await db.SaveChangesAsync();
        }

        public async Task<WorkflowStep> SingleStepAsync()
        {
            using InferenceApiDbContext db = Db();
            return await db.WorkflowSteps.SingleAsync();
        }

        public async Task AssertNothingWrittenAsync()
        {
            using InferenceApiDbContext db = Db();
            (await db.WorkflowDefinitions.CountAsync()).Should().Be(0, "a refused request writes no workflow");
            (await db.WorkflowExecutions.CountAsync()).Should().Be(0);
            Queued.Should().BeEmpty();
        }

        /// <summary>A hidden run of this project (or another one) as the engine leaves it, with its takes registered as files.</summary>
        public async Task<Guid> SeedRunAsync(
            ExecutionStatus status, StepStatus? step = null, int takes = 1, string? failureCode = null, string? error = null,
            WorkflowDefinitionKind kind = WorkflowDefinitionKind.SubjectClip, Guid? projectId = null, DateTime? queuedAt = null)
        {
            Guid project = projectId ?? ProjectId;
            Guid workflowId = Guid.NewGuid(), stepId = Guid.NewGuid(), executionId = Guid.NewGuid();
            VideoGenerateStepConfig config = new()
            {
                InlinePrompt = "She opens the box",
                MaxSpendCredits = 140,
                AllowSourceMediaEgress = true,
                Subjects = [new VideoGenerateSubject(Anna), new VideoGenerateSubject(Machine)],
                Takes = takes,
                MaxClips = takes
            };
            using InferenceApiDbContext db = Db();
            db.WorkflowDefinitions.Add(new WorkflowDefinition
            {
                Id = workflowId,
                ProjectId = project,
                Name = "Avatar clip: Anna, Espresso machine",
                Kind = kind,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                Steps =
                [
                    new WorkflowStep
                    {
                        Id = stepId, WorkflowDefinitionId = workflowId, AgentDefinitionId = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoGenerate,
                        VideoGenerateConfigJson = JsonSerializer.Serialize(config, VideoGenerateStepConfig.JsonOptions), RegisterOutputAsProjectFile = true
                    }
                ]
            });
            DateTime started = DateTime.UtcNow.AddMinutes(-2);
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = workflowId,
                ProjectId = project,
                OrganizationId = H.OrganizationId,
                Status = status,
                CorrelationId = "c",
                QueuedAt = queuedAt ?? DateTime.UtcNow,
                StartedAt = started,
                FirstStartedAt = started,
                InitiatedByUserId = H.OwnerId
            });

            if (step is { } stepStatus)
            {
                string prefix = $"projects/{project}/agentFiles/generated/{executionId:D}/";
                string? output = stepStatus == StepStatus.Completed
                    ? JsonSerializer.Serialize(new { clips = Array.Empty<object>() })
                    : JsonSerializer.Serialize(new { clips = Array.Empty<object>(), meta = new { failed = failureCode, error } });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = stepId,
                    Status = stepStatus,
                    OutputJson = output,
                    FailureCode = failureCode,
                    ErrorDetails = error,
                    OutputStorageKey = stepStatus == StepStatus.Completed ? prefix + "step-1-v1.mp4" : null,
                    ExecutedAt = DateTime.UtcNow.AddMinutes(-1)
                });

                if (stepStatus == StepStatus.Completed)
                {
                    List<Guid> files = [];
                    // Registered in a different order than the takes, to pin the take order of the answer.
                    for (int take = takes; take >= 1; take--)
                    {
                        ProjectFile file = new()
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = project,
                            OriginalFileName = $"Avatar clip- Anna - She opens the box {take}.mp4",
                            MimeType = "video/mp4",
                            StorageKey = prefix + $"step-1-v{take}.mp4",
                            Category = "agentFiles",
                            OutputProducedAt = started,
                            CsamStatus = CsamScanStatus.Clean,
                            ModerationStatus = FileModerationStatus.Clean
                        };
                        db.ProjectFiles.Add(file);
                        files.Insert(0, file.Id);
                    }

                    SeededFiles[executionId] = files;
                }
            }

            await db.SaveChangesAsync();
            return executionId;
        }
    }

    /// <summary>A run limit that lets every run through, or refuses with a two-minute wait; it records the bucket and source.</summary>
    private sealed class FakeRunRate : IAssistantExecutionGuard
    {
        public bool Refuse { get; set; }
        public List<(RunRateBucket Bucket, string Source)> Calls { get; } = [];

        public async Task<RunRateDecision> TryAcquireAsync(
            Guid userId, Guid projectId, RunRateBucket bucket, string source, Func<Task<Guid?>> insertCallback, CancellationToken ct)
        {
            Calls.Add((bucket, source));
            if (Refuse) return new RunRateDecision(false, bucket, 10, 10, 3600, 120);
            Guid? started = await insertCallback();
            return new RunRateDecision(started is not null, bucket, 1, 10, 3600, 0);
        }
    }

    /// <summary>A video model whose declarations each test chooses: 4..15 s, 768P and 2K, three shapes, $0.01 per reference item.</summary>
    private sealed class FakeVideoModel : IVideoGenerationClientFactory, IVideoGenerationClient, IVideoImageInputSupport, IVideoReferenceInputSupport
    {
        public VideoReferenceInputSupport References { get; set; } = new(9, 3, 3, 12, 15_000, 15_000, false, true);
        public VideoImageInputSupport Images { get; set; } = VideoImageInputSupport.FirstAndLastFrame;

        public VideoGenerationCapabilities Capabilities { get; } = new(
            new Dictionary<string, decimal> { ["768P"] = 0.10m, ["2K"] = 0.20m }, 4, 15, ["16:9", "9:16", "1:1"]);

        public IVideoGenerationClient Get(ResolvedVideoGenerationProvider provider) => this;

        public VideoGenerationCapabilities Describe(string model) => Capabilities;

        public VideoImageInputSupport ImageInputSupport(string model) => Images;

        public VideoReferenceInputSupport ReferenceInputSupport(string model) => References;

        public decimal InputMaterialCostUsd(string model, string resolution, IReadOnlyList<VideoReferenceMaterial> references) => 0.01m * references.Count;

        public Task<ExternalJobHandle> SubmitAsync(VideoGenerationRequest request, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExternalJobStatus> PollAsync(ExternalJobHandle handle, CancellationToken ct) => throw new NotSupportedException();

        public Task<VideoGenerationProbeResult> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<Stream> DownloadAsync(string downloadUrl, CancellationToken ct) => throw new NotSupportedException();
    }
}
