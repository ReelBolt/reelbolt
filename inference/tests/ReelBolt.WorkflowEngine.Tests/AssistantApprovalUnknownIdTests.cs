using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using System;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>nginx tries the previous deploy slot on a 404 for an unknown approval id: pin that status.</summary>
public class AssistantApprovalUnknownIdTests
{
    [Fact]
    public void Unknown_approval_id_answers_404_and_a_known_one_204()
    {
        var user = new Mock<ICurrentUser>();
        user.SetupGet(u => u.UserId).Returns(Guid.NewGuid());
        var broker = new AssistantApprovalBroker();
        var controller = new AssistantApprovalsController(null!, user.Object, broker);

        controller.Answer(Guid.NewGuid(), new AssistantApprovalAnswerRequest("allow")).Should().BeOfType<NotFoundResult>();

        Guid id = Guid.NewGuid();
        using var ticket = broker.Open(id, user.Object.UserId, user.Object.OrganizationId);
        controller.Answer(id, new AssistantApprovalAnswerRequest("allow")).Should().BeOfType<NoContentResult>();
    }
}
