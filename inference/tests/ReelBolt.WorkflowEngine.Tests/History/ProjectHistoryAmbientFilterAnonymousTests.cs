#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Auth;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// The filter is registered globally, so anonymous routes (the container healthcheck
/// <c>/api/v1/health</c>) pass through it. Release b5a39e91e threw on <c>ICurrentUser.UserId</c>
/// there, the healthcheck answered 500 and the stack never reached the engine.
/// </summary>
public sealed class ProjectHistoryAmbientFilterAnonymousTests
{
    [Fact]
    public async Task Anonymous_request_passes_through_without_reading_the_user_id()
    {
        var user = new Mock<ICurrentUser>(MockBehavior.Strict);
        user.SetupGet(u => u.IsAuthenticated).Returns(false);
        var filter = new ProjectHistoryAmbientFilter(
            Mock.Of<IProjectHistoryService>(), Mock.Of<IProjectAccessService>(), user.Object);

        var actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor());
        var executing = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), new object());
        bool nextCalled = false;

        await filter.OnActionExecutionAsync(executing, () =>
        {
            nextCalled = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), new object()));
        });

        nextCalled.Should().BeTrue();
        user.VerifyGet(u => u.UserId, Times.Never);
    }
}
