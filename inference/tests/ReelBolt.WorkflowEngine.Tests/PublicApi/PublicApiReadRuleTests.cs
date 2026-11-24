using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>Which public API calls a read-only (Trial) plan may still make.</summary>
public class PublicApiReadRuleTests
{
    [Theory]
    [InlineData("GET", "api/public/v1/apps", true)]
    [InlineData("HEAD", "api/public/v1/apps", true)]
    [InlineData("GET", "api/public/v1/projects/{projectId:guid}/workflows/{workflowId:guid}", true)]
    [InlineData("POST", "api/public/v1/projects/{projectId:guid}/workflows/{workflowId:guid}/estimate", true)]
    [InlineData("POST", "api/public/v1/projects/{projectId:guid}/outputs/{stepResultId:guid}/link", false)]
    [InlineData("POST", "api/public/v1/projects/{projectId:guid}/workflows/{workflowId:guid}/runs", false)]
    [InlineData("POST", "api/public/v1/runs/{id:guid}/stop", false)]
    [InlineData("POST", "api/public/v1/runs/{id:guid}/resume", false)]
    [InlineData("POST", "api/public/v1/runs/{id:guid}/retry", false)]
    [InlineData("POST", "api/public/v1/uploads", false)]
    [InlineData("POST", "api/public/v1/projects/{projectId:guid}/import", false)]
    [InlineData("DELETE", "api/public/v1/projects/{projectId:guid}/files/{id:guid}", false)]
    [InlineData("PUT", "api/public/v1/workflows/{id:guid}", false)]
    [InlineData("PATCH", "api/public/v1/webhooks/{id:guid}", false)]
    [InlineData("POST", "api/public/v1/webhooks", false)]
    [InlineData("POST", "api/public/v1/webhooks/{id:guid}/test", false)]
    [InlineData("POST", "api/public/v1/webhooks/{id:guid}/rotate", false)]
    [InlineData("POST", "api/public/v1/run-requests/{id:guid}/cancel", false)]
    [InlineData("POST", "", false)]
    [InlineData("POST", null, false)]
    public void Only_gets_heads_and_the_estimate_post_are_reads(string method, string? route, bool expected) =>
        PublicApiReadRule.IsRead(method, route).Should().Be(expected);

    [Fact]
    public void Of_the_real_public_api_actions_only_the_estimate_post_is_a_write_that_passes_as_a_read()
    {
        string[] writeVerbs = ["POST", "PUT", "PATCH", "DELETE"];
        List<string> passing = [];
        int writes = 0;
        foreach (Type controller in typeof(PublicApiControllerBase).Assembly.GetTypes()
                     .Where(t => !t.IsAbstract && typeof(PublicApiControllerBase).IsAssignableFrom(t)))
        {
            // A derived [Route] replaces the base one, so take the most derived declaration.
            string prefix = string.Empty;
            for (Type? t = controller; t is not null; t = t.BaseType)
            {
                if (t.GetCustomAttributes<RouteAttribute>(inherit: false).FirstOrDefault() is { } r)
                {
                    prefix = r.Template;
                    break;
                }
            }

            foreach (MethodInfo action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (IRouteTemplateProvider http in action.GetCustomAttributes().OfType<IRouteTemplateProvider>()
                             .Where(a => a is HttpMethodAttribute))
                {
                    string verb = ((HttpMethodAttribute)http).HttpMethods.Single();
                    if (!writeVerbs.Contains(verb)) continue;
                    writes++;
                    string route = (http.Template ?? string.Empty).StartsWith("~/", StringComparison.Ordinal)
                        ? http.Template![2..]
                        : $"{prefix}/{http.Template}".TrimEnd('/');
                    if (PublicApiReadRule.IsRead(verb, route)) passing.Add($"{verb} {route}");
                }
            }
        }

        writes.Should().BeGreaterThan(15, "the reflection must really find the public write actions");
        passing.Should().ContainSingle().Which.Should().EndWith("/estimate");
    }

    [Fact]
    public void CheckApiWrite_is_a_402_with_the_read_only_code_on_trial_and_nothing_otherwise()
    {
        PlanGateViolation v = PlanGate.CheckApiWrite(new OrganizationEntitlementSet { ApiAccess = false, Tier = "trial" })!;
        v.StatusCode.Should().Be(402);
        v.Code.Should().Be("API_PLAN_READ_ONLY");
        v.Feature.Should().Be("api_write");
        v.Message.Should().Contain("read through the API");
        PlanGate.CheckApiWrite(new OrganizationEntitlementSet { ApiAccess = true }).Should().BeNull();
    }
}
