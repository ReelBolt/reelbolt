using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputsTestKit;
using Fixture = ReelBolt.WorkflowEngine.Tests.AppInputs.AppInputChangeRequestTests.Fixture;

namespace ReelBolt.WorkflowEngine.Tests.AppInputs;

/// <summary>
/// Follow-up F3 (docs/design/app-inputs.md 29) on the L9 fixture (1 a script agent, 2 "Lite mode?", 3 the full clip, 4 "Finish
/// here", 5 the lite clip; the source run took 1-4): app fields combined with step targets in one request, run settings
/// (brief fields) changed through the same replay, and the chosen models in the price of a replay.
/// </summary>
public class AppInputFollowUpF3Tests
{
    private static readonly StepChangeTargetRequest RetakeClip =
        new(3, null, null, null, new Dictionary<string, Dictionary<string, JsonElement>> { ["*"] = new() { ["retake"] = J("true") } });

    [Fact]
    public async Task Fields_and_a_step_target_make_one_plan_one_run_and_one_row_per_seed()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(new CreateStepChangeRequest(null, null, null, null, null,
            Targets: [RetakeClip], Inputs: Values(("audience", "\"Tourists\""))));

        r.StatusCode.Should().Be(201, r.Message);
        List<StepChangeRequest> rows = await f.Db.StepChangeRequests.OrderBy(x => x.TargetStepOrder).ToListAsync();
        rows.Select(x => x.TargetStepOrder).Should().Equal(1, 3);
        rows.Select(x => x.ExecutionId).Distinct().Should().ContainSingle("one request is one run");
        AppInputChangeRows.IsAppInputRow(rows[0].OverridesJson).Should().BeTrue();
        rows[0].Instruction.Should().Contain("“Who it is for”");
        AppInputChangeRows.IsAppInputRow(rows[1].OverridesJson).Should().BeFalse("the clip row carries its own overrides");
        rows[1].OverridesJson.Should().Contain("retake");

        CompositionReplayPlan plan = CompositionReplayPlan.TryParse(rows[0].ReplayPlanJson)!;
        plan.For(1)!.Disposition.Should().Be(ReplayStepDisposition.Revise);
        plan.For(3)!.Disposition.Should().Be(ReplayStepDisposition.Revise);
        plan.For(5)!.Disposition.Should().Be(ReplayStepDisposition.Skip);

        r.Created!.Targets!.Select(t => t.Kind).Should().Equal("inputs", "step");
        r.Created.InputChanges.Should().ContainSingle(c => c.InputId == "audience");
        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == rows[0].ExecutionId);
        RunBriefJson.TryParse(queued.RunBriefJson)!.Inputs!["audience"].GetString().Should().Be("Tourists");
    }

    [Fact]
    public async Task A_step_target_a_changed_field_already_redoes_is_refused_by_its_path()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null,
            Targets: [RetakeClip], Inputs: Values(("videoPrompt", "\"A barista draws a heart\""))));

        r.StatusCode.Should().Be(400);
        JsonSerializer.Serialize(r.Extra).Should().Contain("targets[0]");
    }

    [Fact]
    public async Task Unchanged_fields_next_to_a_step_target_are_not_a_refusal()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null,
            Targets: [RetakeClip], Inputs: Values(("audience", "\"Baristas\""))));

        r.StatusCode.Should().Be(200, r.Message);
        r.Preview!.Targets!.Select(t => t.Kind).Should().Equal("step");
    }

    [Fact]
    public async Task A_new_language_re_makes_the_steps_that_read_it_and_the_run_carries_it()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(new CreateStepChangeRequest(null, null, null, null, null,
            Brief: new RunBrief(Language: "it")));

        r.StatusCode.Should().Be(201, r.Message);
        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        row.TargetStepOrder.Should().Be(1, "the script agent reads the language; the clips do not");
        AppInputChangeRows.BriefLines(row.OverridesJson).Should().ContainSingle().Which.Should().BeEquivalentTo(
            new BriefChangeLine("brief:language", "Language", "(automatic)", "it"));
        r.Created!.InputChanges.Should().ContainSingle().Which.Type.Should().Be("brief");

        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
        StoredRunBrief brief = RunBriefJson.TryParse(queued.RunBriefJson)!;
        brief.Language.Should().Be("it");
        brief.Inputs!["audience"].GetString().Should().Be("Baristas", "the app's values ride along unchanged");
        queued.UserRequest.Should().Be(f.Run.UserRequest);

        StepChangeRequestListResponse list = (await f.Service.ListAsync(f.Project, f.Run.Id, CancellationToken.None))!;
        list.Requests.Single().InputChanges.Should().ContainSingle(c => c.InputId == "brief:language" && c.After == "it");
    }

    [Fact]
    public async Task New_notes_recompose_the_request_with_the_app_s_own_text()
    {
        using Fixture f = new();

        StepChangeRequestResult r = await f.Request(new CreateStepChangeRequest(null, null, null, null, null,
            Brief: new RunBrief(Notes: "Make it cosy.")));

        r.StatusCode.Should().Be(201, r.Message);
        StepChangeRequest row = await f.Db.StepChangeRequests.SingleAsync();
        row.TargetStepOrder.Should().Be(1);
        WorkflowExecution queued = await f.Db.WorkflowExecutions.SingleAsync(e => e.Id == row.ExecutionId);
        queued.UserRequest.Should().Be("Made for \"Baristas\".\n\nNotes for this run:\nMake it cosy.");
    }

    [Fact]
    public async Task Settings_that_change_the_whole_film_or_that_nothing_reads_are_refused()
    {
        using Fixture f = new();

        StepChangeRequestResult shape = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null, Brief: new RunBrief(Format: "9:16")));
        shape.StatusCode.Should().Be(400);
        shape.Code.Should().Be(AppInputChangeCodes.BriefNotChangeable);
        shape.Extra!["field"].Should().Be("format");

        StepChangeRequestResult unused = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null, Brief: new RunBrief(OverlayCardAudio: "mute")));
        unused.StatusCode.Should().Be(409);
        unused.Code.Should().Be(AppInputChangeCodes.BriefNotUsed);

        StepChangeRequestResult same = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null, Brief: new RunBrief(Language: "auto")));
        same.StatusCode.Should().Be(400);
        same.Code.Should().Be(AppInputChangeCodes.Unchanged);

        StepChangeRequestResult bad = await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null, Brief: new RunBrief(LengthSec: 99999)));
        bad.StatusCode.Should().Be(400);
        (await f.Db.StepChangeRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Capabilities_list_the_page_s_run_settings_with_what_they_re_make()
    {
        AppManifest page = AppInputChangeRequestTests.App with
        {
            Inputs =
            [
                .. AppInputChangeRequestTests.App.Inputs!,
                new AppManifestInput(AppManifestVocabulary.KindBrief, Field: "language", Label: "Language of the video"),
                new AppManifestInput(AppManifestVocabulary.KindBrief, Field: "format"),
                new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "Anything else"),
            ]
        };
        using Fixture f = new(page: page);

        StepChangeAppInputsResponse caps = (await f.Service.GetCapabilitiesAsync(f.Project, f.Run.Id, CancellationToken.None))!.AppInputs!;

        caps.BriefFields!.Select(b => b.Key).Should().Equal("brief:language", "brief:format", "brief:notes");
        StepChangeBriefFieldResponse language = caps.BriefFields!.Single(b => b.Key == "brief:language");
        language.Supported.Should().BeTrue();
        language.Label.Should().Be("Language of the video");
        language.UsedBy.Should().Equal("Write the script");
        StepChangeBriefFieldResponse format = caps.BriefFields!.Single(b => b.Key == "brief:format");
        format.Supported.Should().BeFalse();
        format.ReasonCode.Should().Be(AppInputChangeCodes.BriefNotChangeable);
        caps.BriefFields!.Single(b => b.Key == "brief:notes").Value!.Value.GetString().Should().Be("Keep it warm.");
    }

    [Fact]
    public async Task The_replay_price_of_a_run_without_model_choices_enters_no_model_scope()
    {
        using Fixture f = new();

        (await f.Estimate(new CreateStepChangeRequest(null, null, null, null, null, Inputs: Values(("audience", "\"Tourists\""))))).StatusCode.Should().Be(200);

        f.Costs.LastAgentModels.Should().BeNull();
    }

    private sealed class Models(params AppModelCandidate[] candidates) : IAppInputModelResolverSource
    {
        public Task<IReadOnlyList<AppModelCandidate>> CandidatesAsync(Guid? organizationId, Guid projectId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AppModelCandidate>>(candidates);
    }

    [Fact]
    public async Task A_replay_carrying_the_page_prices_the_chosen_agent_and_step_models()
    {
        Guid agent = Guid.NewGuid();
        Guid chat = Guid.NewGuid();
        Guid speech = Guid.NewGuid();
        AppManifest page = Manifest(
            new AppManifestInput(AppManifestVocabulary.KindInput, Id: "writerModel", Type: "model", Capability: "Chat", Agent: agent),
            new AppManifestInput(AppManifestVocabulary.KindInput, Id: "voiceModel", Type: "model", Capability: "SpeechSynthesis"));
        Dictionary<string, JsonElement> values = Values(("writerModel", $"\"{chat}\""), ("voiceModel", $"\"{speech}\""));
        Models models = new(new AppModelCandidate(chat, "Chat", "OpenAICompatible", "gpt-x", "GPT X", IsPlatform: true));

        using ((await ReplayModelPricing.ResolveAsync(models, Guid.NewGuid(), Guid.NewGuid(), AppManifestJson.Serialize(page), values, CancellationToken.None)).Enter())
        {
            AppAgentModelAmbient.For(agent).Should().Be(chat);
            AppAgentModelAmbient.IsChosenStepModel(speech).Should().BeTrue();
        }

        AppAgentModelAmbient.Current.Should().BeNull("the scope is restored");

        using ((await ReplayModelPricing.ResolveAsync(models, Guid.NewGuid(), Guid.NewGuid(), null, values, CancellationToken.None)).Enter())
        {
            AppAgentModelAmbient.Current.Should().BeNull("a replay with no page holds at the ordinary models, as the engine does");
            AppAgentModelAmbient.StepModels.Should().BeNull();
        }
    }
}
