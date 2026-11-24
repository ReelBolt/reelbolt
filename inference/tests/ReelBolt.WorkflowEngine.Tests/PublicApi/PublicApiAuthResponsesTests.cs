using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Services.PublicApi;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>The challenge and model-binding answers of the public surface are the documented envelope.</summary>
public class PublicApiAuthResponsesTests
{
    private static async Task<(DefaultHttpContext Http, JsonNode? Body)> ChallengeAsync(string path)
    {
        DefaultHttpContext http = new();
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        JwtBearerChallengeContext context = new(http,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)), new JwtBearerOptions(), new AuthenticationProperties());
        await PublicApiAuthResponses.WriteChallengeAsync(context);
        http.Response.Body.Position = 0;
        string text = await new StreamReader(http.Response.Body).ReadToEndAsync();
        return (http, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Theory]
    [InlineData("/api/public/v1/me")]
    [InlineData("/api/public/v1/projects/abc/runs")]
    public async Task A_challenge_on_a_public_route_is_401_api_key_required_in_the_envelope(string path)
    {
        (DefaultHttpContext http, JsonNode? body) = await ChallengeAsync(path);

        http.Response.StatusCode.Should().Be(401);
        http.Response.ContentType.Should().StartWith("application/json");
        http.Response.Headers["X-Request-Id"].ToString().Should().NotBeNullOrEmpty();
        http.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
        body!["error"]!["code"]!.GetValue<string>().Should().Be("api_key_required");
        body["error"]!["message"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_challenge_on_a_dashboard_route_is_left_to_the_default()
    {
        DefaultHttpContext http = new();
        http.Request.Path = "/api/projects";
        JwtBearerChallengeContext context = new(http,
            new AuthenticationScheme("Bearer", null, typeof(JwtBearerHandler)), new JwtBearerOptions(), new AuthenticationProperties());

        await PublicApiAuthResponses.WriteChallengeAsync(context);

        context.Handled.Should().BeFalse();
        http.Response.StatusCode.Should().Be(200, "the framework's own challenge sets the status later");
    }

    private static ActionContext ActionFor(System.Type controller, ModelStateDictionary modelState)
    {
        ServiceCollection services = new();
        services.AddSingleton<ProblemDetailsFactory, StubFactory>();
        DefaultHttpContext http = new() { RequestServices = services.BuildServiceProvider() };
        return new ActionContext(http, new RouteData(), new ControllerActionDescriptor { ControllerTypeInfo = System.Reflection.IntrospectionExtensions.GetTypeInfo(controller) }, modelState);
    }

    private sealed class StubFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(HttpContext httpContext, int? statusCode = null, string? title = null,
            string? type = null, string? detail = null, string? instance = null) => new() { Status = statusCode };

        public override ValidationProblemDetails CreateValidationProblemDetails(HttpContext httpContext, ModelStateDictionary modelStateDictionary,
            int? statusCode = null, string? title = null, string? type = null, string? detail = null, string? instance = null) =>
            new(modelStateDictionary) { Status = statusCode };
    }

    [Fact]
    public void A_model_state_failure_on_a_public_controller_is_the_invalid_request_envelope_with_details()
    {
        ModelStateDictionary state = new();
        state.AddModelError("limit", "The value 'abc' is not valid for limit.");
        state.AddModelError("", "A non-empty request body is required.");

        IActionResult result = PublicApiAuthResponses.InvalidModelState(ActionFor(typeof(RunsController), state));

        ObjectResult obj = result.Should().BeOfType<ObjectResult>().Subject;
        obj.StatusCode.Should().Be(400);
        JsonNode error = ((JsonObject)obj.Value!)["error"]!;
        error["code"]!.GetValue<string>().Should().Be("invalid_request");
        error["details"]!["limit"]![0]!.GetValue<string>().Should().Contain("abc");
        error["details"]![""]![0]!.GetValue<string>().Should().Contain("body");
    }

    [Fact]
    public void A_model_state_failure_on_a_dashboard_controller_keeps_the_validation_problem_details()
    {
        ModelStateDictionary state = new();
        state.AddModelError("name", "required");

        IActionResult result = PublicApiAuthResponses.InvalidModelState(ActionFor(typeof(ProjectsController), state));

        BadRequestObjectResult bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        bad.Value.Should().BeOfType<ValidationProblemDetails>().Which.Errors.Should().ContainKey("name");
    }

    [Fact]
    public void The_error_body_schema_declares_the_optional_members_real_errors_carry()
    {
        string[] declared = typeof(PublicApiErrorBody).GetProperties().Select(p => p.Name).ToArray();
        declared.Should().Contain(["ResetsAt", "RetryAfterSeconds", "Window", "CapCredits", "UsedCredits", "RequiredScopes", "Details"]);
    }

    private static JsonNode CommittedDoc()
    {
        string? dir = System.AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "docs"))) dir = Path.GetDirectoryName(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "docs", "public-api", "openapi-v1.json")))!;
    }

    [Fact]
    public void The_committed_error_body_schema_permits_extra_properties()
    {
        JsonNode schema = CommittedDoc()["components"]!["schemas"]!["PublicApiErrorBody"]!;
        schema["additionalProperties"].Should().NotBeNull("402/403/429 bodies carry gate-specific extras")
            .And.Subject.Should().BeOfType<JsonObject>("an explicit free-form schema, never false");
        ((JsonObject)schema["properties"]!).ContainsKey("resetsAt").Should().BeTrue();
    }

    [Fact]
    public void The_committed_public_document_carries_only_the_api_key_scheme()
    {
        JsonNode doc = CommittedDoc();
        ((JsonObject)doc["components"]!["securitySchemes"]!).Select(p => p.Key).Should().Equal("ApiKey");
        doc["security"].Should().BeNull();
    }
}
