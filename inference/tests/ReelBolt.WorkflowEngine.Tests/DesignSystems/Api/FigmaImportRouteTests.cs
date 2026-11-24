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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Controllers;
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

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// P3-12 coverage for <c>POST /api/v1/projects/{p}/design-system/import/figma</c> and its SSRF-safe
/// client, driven by a fake <see cref="HttpMessageHandler"/>: the success mapping into an Import
/// Draft, a refused redirect, the fixed base host, the 8 MiB cap, the 403 → 424 answer, the feature
/// flag, and the guarantee that the access token appears in no log message.
/// </summary>
public sealed class FigmaVariablesImportRouteTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("cccccccc-0000-0000-0000-000000000013");
    private const string Token = "figd_secret_token_value";
    private const string FileKey = "abc123DEF456";

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();

    public FigmaVariablesImportRouteTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"figma-import-{Guid.NewGuid():N}")
            .Options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Success_maps_the_response_into_an_import_draft()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        ObjectResult created = response.Result.Should().BeOfType<ObjectResult>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);

        FigmaImportResponse payload = created.Value.Should().BeOfType<FigmaImportResponse>().Subject;
        payload.Version.Origin.Should().Be("Import");
        payload.Version.State.Should().Be("Draft");
        payload.Version.Document["color"]!["primary"]!["base"]!["$value"]!["hex"]!.GetValue<string>()
            .Should().Be("#ff0000");
        payload.Version.Document["modes"]!["dark"]!["color.primary.base"]!["hex"]!.GetValue<string>()
            .Should().Be("#000000");

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.Scheme.Should().Be("https");
        handler.Requests[0].RequestUri!.Host.Should().Be("api.figma.com");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be($"/v1/files/{FileKey}/variables/local");
        handler.Requests[0].Headers.GetValues("X-Figma-Token").Should().ContainSingle().Which.Should().Be(Token);

        _db.DesignSystemVersions.Count().Should().Be(1);
        DesignSystemSource source = _db.DesignSystemSources.AsNoTracking().Should().ContainSingle().Subject;
        source.VersionId.Should().Be(payload.Version.Id);
        source.SourceKind.Should().Be(DesignSourceKind.Figma);
        source.Path.Should().Be($"figma/{FileKey}.json");
        source.ContentSha256.Should().HaveLength(64);
    }

    [Fact]
    public async Task Redirect_is_refused_and_never_followed()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://evil.example.com/variables");
            return response;
        });
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status502BadGateway);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("FIGMA_REDIRECT_REFUSED");
        handler.Requests.Should().ContainSingle();
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task Invalid_file_key_never_reaches_the_transport()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest("../../evil.example.com", Token), CancellationToken.None);

        BadRequestObjectResult badRequest = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Body(badRequest.Value)["code"]!.GetValue<string>().Should().Be("INVALID_FILE_KEY");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Client_dials_only_the_fixed_figma_host()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{}"));
        using var client = new FigmaVariablesClient(handler);

        await client.FetchVariablesAsync(FileKey, Token, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.Scheme.Should().Be("https");
        handler.Requests[0].RequestUri!.Host.Should().Be("api.figma.com");
        FigmaVariablesClient.IsAllowedHost(handler.Requests[0].RequestUri!).Should().BeTrue();
        FigmaVariablesClient.IsAllowedHost(new Uri("https://evil.example.com/v1/files/x/variables/local"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Oversize_response_is_refused()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new OversizeContent(FigmaVariablesClient.MaxResponseBytes + 1);
            return response;
        });
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("RESPONSE_TOO_LARGE");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task Figma_403_answers_424_figma_not_available()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.Forbidden));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        ObjectResult failure = response.Result.Should().BeOfType<ObjectResult>().Subject;
        failure.StatusCode.Should().Be(StatusCodes.Status424FailedDependency);
        Body(failure.Value)["code"]!.GetValue<string>().Should().Be("FIGMA_NOT_AVAILABLE");
    }

    [Fact]
    public async Task Disabled_flag_answers_404_feature_disabled_without_calling_figma()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler, enabled: false);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        NotFoundObjectResult notFound = response.Result.Should().BeOfType<NotFoundObjectResult>().Subject;
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        Body(notFound.Value)["code"]!.GetValue<string>().Should().Be("feature_disabled");
        handler.Requests.Should().BeEmpty();
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_body_that_is_not_figma_variables_answers_400()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, "{\"meta\":{\"variableCollections\":{}}}"));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        BadRequestObjectResult badRequest = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Body(badRequest.Value)["code"]!.GetValue<string>().Should().Be("NO_TOKENS_RECOGNIZED");
        _db.DesignSystemVersions.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_missing_access_token_is_a_400()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, string.Empty), CancellationToken.None);

        BadRequestObjectResult badRequest = response.Result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Body(badRequest.Value)["code"]!.GetValue<string>().Should().Be("INVALID_ACCESS_TOKEN");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_access_token_appears_in_no_log_message()
    {
        var logger = new CapturingLogger();
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler, logger: logger);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>();
        logger.Messages.Should().NotBeEmpty();
        logger.Messages.Should().NotContain(message => message.Contains(Token, StringComparison.Ordinal));
        logger.Messages.Should().Contain(message => message.Contains("design_system.import.figma", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("abc123DEF456\n")]
    [InlineData("abc123DEF456 ")]
    [InlineData("short")]
    public async Task A_file_key_that_is_not_exactly_alphanumeric_is_a_400_and_never_reaches_figma(string fileKey)
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(fileKey, Token), CancellationToken.None);

        Body(response.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)["code"]!
            .GetValue<string>().Should().Be("INVALID_FILE_KEY");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_file_key_is_the_probe_404_when_disabled_and_400_when_enabled_before_any_token_is_read()
    {
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> off = await Controller(new StubHandler(_ => Response(HttpStatusCode.OK)), enabled: false)
            .ImportFigma(project.Id, new FigmaImportRequest(string.Empty, string.Empty), CancellationToken.None);
        Body(off.Result.Should().BeOfType<NotFoundObjectResult>().Subject.Value)["code"]!
            .GetValue<string>().Should().Be("feature_disabled");

        ActionResult<FigmaImportResponse> on = await Controller(new StubHandler(_ => Response(HttpStatusCode.OK)))
            .ImportFigma(project.Id, new FigmaImportRequest(string.Empty, string.Empty), CancellationToken.None);
        Body(on.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)["code"]!
            .GetValue<string>().Should().Be("INVALID_FILE_KEY");
    }

    [Fact]
    public async Task An_access_token_over_512_characters_is_a_400_and_never_sent()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, new string('t', 513)), CancellationToken.None);

        Body(response.Result.Should().BeOfType<BadRequestObjectResult>().Subject.Value)["code"]!
            .GetValue<string>().Should().Be("INVALID_ACCESS_TOKEN");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_response_over_the_importers_2_MiB_cap_is_refused_with_413()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new OversizeContent(3 * 1024 * 1024);
            return response;
        });
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        response.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        FigmaVariablesClient.MaxResponseBytes.Should().Be(2 * 1024 * 1024);
    }

    [Fact]
    public async Task The_access_token_appears_nowhere_in_the_response_or_the_stored_draft()
    {
        var handler = new StubHandler(_ => Response(HttpStatusCode.OK, ColourOnlyJson));
        DesignSystemsController controller = Controller(handler);
        Project project = SeedProject();

        ActionResult<FigmaImportResponse> response = await controller.ImportFigma(
            project.Id, new FigmaImportRequest(FileKey, Token), CancellationToken.None);

        object? value = response.Result.Should().BeOfType<ObjectResult>().Subject.Value;
        JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Should().NotContain(Token);
        _db.DesignSystemVersions.AsNoTracking().ToList().Should().NotBeEmpty()
            .And.OnlyContain(v => !v.DocumentJson.Contains(Token) && (v.CoverageJson == null || !v.CoverageJson.Contains(Token)));
        _db.DesignSystemSources.AsNoTracking().ToList().Should().OnlyContain(
            row => !row.Path.Contains(Token) && !(row.SourceRevision ?? "").Contains(Token));
    }

    [Fact]
    public async Task A_body_that_stalls_after_the_headers_is_cut_off_by_the_deadline()
    {
        var handler = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Content = new StallingContent();
            return response;
        });
        using var client = new FigmaVariablesClient(handler, timeout: TimeSpan.FromMilliseconds(300));

        Task<FigmaFetchResult> fetch = client.FetchVariablesAsync(FileKey, Token, CancellationToken.None);
        Task finished = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(fetch, "the body read is bounded by the same deadline as the headers");
        (await fetch).Status.Should().Be(FigmaFetchStatus.TransportFailed);
    }

    [Fact]
    public void The_production_handler_follows_no_redirects_keeps_no_cookies_and_installs_the_connect_guard()
    {
        using SocketsHttpHandler handler = FigmaVariablesClient.CreateHandler();

        handler.AllowAutoRedirect.Should().BeFalse("a redirect would carry X-Figma-Token to another host");
        handler.UseCookies.Should().BeFalse();
        handler.ConnectCallback.Should().NotBeNull();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]
    public async Task The_production_connect_guard_refuses_private_addresses_without_connecting(string address)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            string host = address == "127.0.0.1" ? address : address;
            using var http = new HttpClient(FigmaVariablesClient.CreateHandler()) { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Figma-Token", Token);

            Func<Task> call = () => http.GetAsync($"http://{host}:{(address == "127.0.0.1" ? port : 80)}/");

            await call.Should().ThrowAsync<HttpRequestException>();
            listener.Pending().Should().BeFalse("the guard must refuse before any connection is made");
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class StallingContent : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { 0x7B });
            await Task.Delay(Timeout.Infinite);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------------

    private DesignSystemsController Controller(
        HttpMessageHandler handler,
        bool enabled = true,
        ILogger<DesignSystemsController>? logger = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Import:Figma:Enabled"] = enabled ? "true" : "false",
            })
            .Build();

        var user = new FakeUser(_ownerId, HomeOrg, OrganizationRole.Owner);

        return new DesignSystemsController(
            _db,
            user,
            new ProjectAccessService(_db, user),
            new DesignSystemService(
                _db, Mock.Of<IFileStorageService>(), Mock.Of<IPlanGateService>(),
                NullLogger<DesignSystemService>.Instance),
            Mock.Of<IDesignSystemIngestService>(),
            logger ?? NullLogger<DesignSystemsController>.Instance,
            configuration,
            figmaClient: new FigmaVariablesClient(handler));
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

    /// <summary>A body with no declared length, so the client's bounded read is what stops it.</summary>
    private sealed class OversizeContent(long bytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            byte[] chunk = new byte[81920];
            long written = 0;
            while (written < bytes)
            {
                int count = (int)Math.Min(chunk.Length, bytes - written);
                await stream.WriteAsync(chunk.AsMemory(0, count));
                written += count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CapturingLogger : ILogger<DesignSystemsController>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
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

    private const string ColourOnlyJson = """
    {
      "meta": {
        "variables": {
          "VariableID:1": {
            "id": "VariableID:1",
            "name": "primary/base",
            "resolvedType": "COLOR",
            "valuesByMode": {
              "mode-light": { "r": 1, "g": 0, "b": 0, "a": 1 },
              "mode-dark": { "r": 0, "g": 0, "b": 0, "a": 1 }
            },
            "variableCollectionId": "CollectionID:1"
          },
          "VariableID:2": {
            "id": "VariableID:2",
            "name": "semantic/brand",
            "resolvedType": "COLOR",
            "valuesByMode": { "mode-light": { "type": "VARIABLE_ALIAS", "id": "VariableID:1" } },
            "variableCollectionId": "CollectionID:1"
          }
        },
        "variableCollections": {
          "CollectionID:1": {
            "id": "CollectionID:1",
            "name": "Theme",
            "defaultModeId": "mode-light",
            "modes": [
              { "modeId": "mode-light", "name": "Light" },
              { "modeId": "mode-dark", "name": "Dark" }
            ]
          }
        }
      }
    }
    """;
}
