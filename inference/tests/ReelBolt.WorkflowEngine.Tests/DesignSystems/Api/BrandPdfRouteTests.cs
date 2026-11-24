using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P3-13 coverage for <c>POST /api/v1/projects/{p}/design-system/import/pdf</c>, driven by a fake
/// <see cref="HttpMessageHandler"/> in place of the import-worker: the success path into a
/// low-confidence Import Draft, the <c>%PDF-</c> sniff (415), an unreachable worker (503) and the
/// 20 MiB cap (413). The worker bearer is asserted present on the one worker call.
/// </summary>
public sealed class BrandPdfRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("cccccccc-0000-0000-0000-000000000013");
    private const string WorkerToken = "worker_secret_token_value";
    private const int PdfMaxBytes = 20 * 1024 * 1024;

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public BrandPdfRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"brand-pdf-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Success_maps_the_worker_text_into_an_import_draft()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, """{"text":"Primary #112233","truncated":false}"""));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<PdfImportResponse> response = await controller.ImportPdf(
            project.Id, File(PdfText("Primary #112233")), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        PdfImportResponse payload = created.Value.Should().BeOfType<PdfImportResponse>().Subject;
        payload.Version.Origin.Should().Be("Import");
        payload.Version.State.Should().Be("Draft");

        JsonObject token = payload.Version.Document["color"]!["primary"]!.AsObject();
        token["$value"]!["hex"]!.GetValue<string>().Should().Be("#112233");
        token["$extensions"]!["com.reelbolt"]!["conf"]!.GetValue<double>().Should().Be(0.4);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/v1/pdf-text");
        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization!.Parameter.Should().Be(WorkerToken);
        _db.DesignSystemVersions.Count().Should().Be(1);
        DesignSystemSource source = _db.DesignSystemSources.AsNoTracking().Should().ContainSingle().Subject;
        source.VersionId.Should().Be(payload.Version.Id);
        source.SourceKind.Should().Be(DesignSourceKind.Upload);
        source.ContentSha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task A_body_without_pdf_magic_is_415_and_never_reaches_the_worker()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, """{"text":"Primary #112233"}"""));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<PdfImportResponse> response = await controller.ImportPdf(
            project.Id, File(Encoding.UTF8.GetBytes("this is not a pdf")), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status415UnsupportedMediaType);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("NOT_A_PDF");
        handler.Requests.Should().BeEmpty();
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task An_unreachable_worker_is_503_import_worker_unavailable()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<PdfImportResponse> response = await controller.ImportPdf(
            project.Id, File(PdfText("Primary #112233")), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("import_worker_unavailable");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task An_over_cap_upload_is_413_and_never_reaches_the_worker()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{}"));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        byte[] big = new byte[PdfMaxBytes + 1];
        "%PDF-1.4"u8.CopyTo(big);

        ActionResult<PdfImportResponse> response = await controller.ImportPdf(
            project.Id, File(big), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("PDF_TOO_LARGE");
        handler.Requests.Should().BeEmpty();
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public async Task Import_Pdf_Enabled_false_answers_404_feature_disabled_and_never_reaches_the_worker(string flag)
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{}"));
        DesignSystemsController controller = Controller(handler, pdfEnabled: flag);
        Project project = SeedProject();

        ActionResult<PdfImportResponse> response = await controller.ImportPdf(
            project.Id, File(PdfText("Primary #112233")), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("feature_disabled");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unset_worker_token_is_503_import_worker_unavailable_without_calling_the_worker()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{}"));
        DesignSystemsController controller = Controller(handler, workerToken: " ");
        Project project = SeedProject();

        ActionResult<PdfImportResponse> response = await controller.ImportPdf(
            project.Id, File(PdfText("Primary #112233")), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeAssignableTo<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("import_worker_unavailable");
        handler.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private DesignSystemsController Controller(
        HttpMessageHandler handler, IPlanGateService? planGate = null, string? pdfEnabled = null, string? workerToken = WorkerToken)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Import:Worker:Token"] = workerToken,
                ["Import:Pdf:Enabled"] = pdfEnabled,
            })
            .Build();

        IPlanGateService gate = planGate ?? PassingGate();
        var user = new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

        var controller = new DesignSystemsController(
            _db,
            user,
            new ProjectAccessService(_db, user),
            new DesignSystemService(
                _db, Mock.Of<IFileStorageService>(), gate,
                NullLogger<DesignSystemService>.Instance),
            Mock.Of<IDesignSystemIngestService>(),
            NullLogger<DesignSystemsController>.Instance,
            configuration,
            planGate: gate,
            importWorker: new ImportWorkerClient(
                new HttpClient(handler) { BaseAddress = new Uri("http://import-worker") }, configuration));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        return controller;
    }

    private static IPlanGateService PassingGate()
    {
        var mock = new Mock<IPlanGateService>();
        mock.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return mock.Object;
    }

    private Project SeedProject()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = _ownerId,
            OrganizationId = HomeOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private static byte[] PdfText(string body) => Encoding.UTF8.GetBytes("%PDF-1.4\n" + body);

    private static FormFile File(byte[] bytes, string name = "brand.pdf")
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name);
    }

    private static HttpResponseMessage Response(HttpStatusCode code, string? body = null)
    {
        var response = new HttpResponseMessage(code);
        if (body is not null)
            response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return response;
    }

    private static JsonObject Body(object? value) => JsonNode
        .Parse(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!
        .AsObject();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }
}
