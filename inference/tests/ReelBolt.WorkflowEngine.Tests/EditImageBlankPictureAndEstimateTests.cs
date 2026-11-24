using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>R13.1 (blank picture), R14.1 (estimate + chargedCredits on paid picture tools).</summary>
public partial class EditImageDocumentControllersTests
{
    [Fact]
    public async Task R13_1_Create_without_a_source_but_with_a_size_makes_a_blank_transparent_picture()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(Width: 1080, Height: 1920), CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("name").GetString().Should().Be("New picture");
        body.GetProperty("canvasWidth").GetInt32().Should().Be(1080);
        body.GetProperty("canvasHeight").GetInt32().Should().Be(1920);
        body.GetProperty("layers").GetArrayLength().Should().Be(0);
        body.GetProperty("backgroundColor").ValueKind.Should().Be(JsonValueKind.Null, "blank means transparent");

        Guid id = body.GetProperty("id").GetGuid();
        (int get, JsonElement stored) = Harness.Read(await h.Documents().Get(projectId, id, CancellationToken.None));
        get.Should().Be(200);
        stored.GetProperty("layers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task R13_1_A_blank_picture_takes_a_name_and_a_background_colour()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();

        (int status, JsonElement body) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(Name: "Poster", Width: 16, Height: 4096, BackgroundColor: "#112233"),
            CancellationToken.None));

        status.Should().Be(200);
        body.GetProperty("name").GetString().Should().Be("Poster");
        body.GetProperty("backgroundColor").GetString().Should().Be("#112233");
    }

    [Theory]
    [InlineData(15, 100)]
    [InlineData(100, 4097)]
    [InlineData(0, 0)]
    public async Task R13_1_A_blank_picture_outside_16_to_4096_is_400(int width, int height)
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();

        (int status, _) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(Width: width, Height: height), CancellationToken.None));

        status.Should().Be(400);
    }

    [Fact]
    public async Task R13_1_No_source_and_no_size_is_still_404_and_a_foreign_project_is_404()
    {
        Harness h = new();
        Guid projectId = await h.SeedProjectAsync();

        (int none, _) = Harness.Read(await h.Documents().Create(
            projectId, new CreateEditImageDocumentRequest(), CancellationToken.None));
        (int foreign, _) = Harness.Read(await h.Documents().Create(
            Guid.NewGuid(), new CreateEditImageDocumentRequest(Width: 100, Height: 100), CancellationToken.None));

        none.Should().Be(404);
        foreign.Should().Be(404);
    }

    [Fact]
    public async Task R14_1_Capabilities_carry_an_estimate_with_billing_on()
    {
        Billing.FakeNonWorkflowCharges charges = new() { EstimateMillis = 4_500, AvailableMillis = 10_000 };
        Harness h = new() { Charges = charges, EditProvider = Harness.FalEdit() };
        h.RealDescribe = true;
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "expand", null, doc.Id, null, null, CancellationToken.None));

        status.Should().Be(200);
        JsonElement estimate = body.GetProperty("estimate");
        estimate.GetProperty("billingEnabled").GetBoolean().Should().BeTrue();
        estimate.GetProperty("credits").GetDouble().Should().Be(4.5);
        estimate.GetProperty("balanceCredits").GetDouble().Should().Be(10);
        estimate.GetProperty("affordable").GetBoolean().Should().BeTrue();
        body.GetProperty("canConfigure").ValueKind.Should().Be(JsonValueKind.False);
    }

    [Fact]
    public async Task R14_1_An_estimate_above_the_balance_is_not_affordable()
    {
        Billing.FakeNonWorkflowCharges charges = new() { EstimateMillis = 4_500, AvailableMillis = 1_000 };
        Harness h = new() { Charges = charges };
        (Guid projectId, _) = await h.SeedDocumentAsync();

        (_, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "removeBackground", null, null, null, null, CancellationToken.None));

        body.GetProperty("estimate").GetProperty("affordable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task R14_1_Capabilities_with_billing_off_say_billing_is_off()
    {
        Harness h = new() { EditProvider = Harness.FalEdit() };
        h.RealDescribe = true;
        (Guid projectId, _) = await h.SeedDocumentAsync();

        (_, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "expand", null, null, null, null, CancellationToken.None));

        JsonElement estimate = body.GetProperty("estimate");
        estimate.GetProperty("billingEnabled").GetBoolean().Should().BeFalse();
        estimate.GetProperty("affordable").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task R14_1_A_missing_provider_answers_a_reason_code_and_canConfigure()
    {
        Harness h = new();
        h.Segmentation = null;
        (Guid projectId, _) = await h.SeedDocumentAsync();

        (int status, JsonElement body) = Harness.Read(await h.Operations().GetOperationCapabilities(
            projectId, "removeBackground", null, null, null, null, CancellationToken.None));

        status.Should().Be(409);
        body.GetProperty("code").GetString().Should().Be("SEGMENTATION_PROVIDER_UNAVAILABLE");
        body.GetProperty("reason").GetString().Should().Be("NO_PROVIDER");
        body.GetProperty("canConfigure").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task R14_1_A_settled_cut_out_reports_the_credits_it_used()
    {
        Billing.FakeNonWorkflowCharges charges = new();
        Harness h = new() { Charges = charges };
        (Guid projectId, EditImageDocumentDto doc) = await h.SeedDocumentAsync();
        var controller = h.Documents();

        (int status, _) = Harness.Read(await controller.SegmentSubject(
            projectId, doc.Id, new SegmentSubjectRequest(Prompt: "the person"), CancellationToken.None));

        status.Should().Be(200);
        charges.Settled.Should().ContainSingle();
        controller.Response.Headers.ContainsKey("X-Charged-Credits").Should().BeTrue();
    }
}
