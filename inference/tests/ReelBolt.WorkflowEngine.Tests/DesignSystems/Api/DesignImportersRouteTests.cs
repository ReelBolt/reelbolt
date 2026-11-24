using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Shared.DesignSystems.Importers;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Api;

/// <summary>
/// T-008: <c>GET /api/v1/design-system/importers</c> publishes the capability matrix with enum
/// names as strings. The route is authorized and project-free: the table is static data.
/// </summary>
public sealed class DesignImportersRouteTests
{
    private static DesignImportersController Controller() => new();

    [Fact]
    public async Task The_matrix_route_answers_the_table()
    {
        ActionResult<ImportCapabilitiesResponse> response = await Controller().List(CancellationToken.None);

        OkObjectResult ok = response.Result.Should().BeOfType<OkObjectResult>().Subject;
        ImportCapabilitiesResponse payload = ok.Value.Should().BeOfType<ImportCapabilitiesResponse>().Subject;

        payload.Schema.Should().Be(DesignImportCapabilities.Schema);
        payload.Importers.Select(i => i.Id)
            .Should().BeEquivalentTo(DesignImportCapabilities.Matrix.Select(m => m.ImporterId));
        payload.Importers.Should().HaveCount(DesignImportCapabilities.Matrix.Count);
    }

    [Fact]
    public async Task The_payload_carries_the_enum_names_as_strings()
    {
        ActionResult<ImportCapabilitiesResponse> response = await Controller().List(CancellationToken.None);
        ImportCapabilitiesResponse payload = (ImportCapabilitiesResponse)((OkObjectResult)response.Result!).Value!;

        ImporterCapabilityResponse claude = payload.Importers.Single(i => i.Id == "claude-design");
        claude.Axes.Single(a => a.Axis == nameof(DesignImportAxis.Assets)).Support
            .Should().Be(nameof(DesignImportSupport.Full));
        claude.Axes.Single(a => a.Axis == nameof(DesignImportAxis.Fonts)).Support
            .Should().Be(nameof(DesignImportSupport.Partial));

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().Contain("\"Tokens\"");
        json.Should().Contain("\"Partial\"");
        json.Should().NotContain("\"axis\":0");
    }

    [Fact]
    public void The_route_is_authorized_and_does_not_take_a_project_id()
    {
        typeof(DesignImportersController).GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();

        RouteAttribute? route = typeof(DesignImportersController).GetCustomAttribute<RouteAttribute>();
        route.Should().NotBeNull();
        route!.Template.Should().Be("api/v1/design-system");

        MethodInfo method = typeof(DesignImportersController).GetMethod(nameof(DesignImportersController.List))!;
        method.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("importers");
        method.GetParameters().Should().OnlyContain(p => p.ParameterType == typeof(CancellationToken));
    }
}
