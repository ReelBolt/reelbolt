using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using Xunit;
using AssistantToolTestFactory = ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory;

namespace ReelBolt.WorkflowEngine.Tests.Tenancy;

/// <summary>
/// WP A14, the assistant seams: <b>what "another organization" means to the platform assistant's
/// project context, where a conversation actually lives, and which overload of the confirmation-token
/// contract a workspace-scoped tool is required to use.</b>
///
/// <para>
/// The assistant's project axis is already enumerated exhaustively from the tool registration in
/// <c>Controllers/AssistantAuthorityTests.ProjectAxis_NonOwnerIsRefusedWithTheNoProjectError_...</c>.
/// What is asserted here are the three things that matrix cannot express: (a) that the boundary is the
/// ORGANIZATION and not the owner, so a teammate's project is genuinely in context rather than
/// "absent"; (b) that a conversation is scoped to the workspace it was created in, for the same user;
/// and (c) that <c>ApplyAssignAgentSkills</c> verifies a token minted WITH the workspace, which is
/// A11's organization binding observed from the tool rather than from the token service.
/// </para>
/// </summary>
public sealed class CrossTenantAssistantToolTests : IDisposable
{
    private static readonly Guid SharedOrg = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid OtherOrg = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _teammateId = Guid.NewGuid();
    private readonly Guid _strangerId = Guid.NewGuid();
    private readonly Project _shared;

    public CrossTenantAssistantToolTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"cross-tenant-assistant-{Guid.NewGuid():N}")
            .Options);

        _shared = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Team Project",
            OwnerId = _ownerId,
            OrganizationId = SharedOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Projects.Add(_shared);
        _db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = _shared.Id,
            OriginalFileName = "team-notes.md",
            DirectoryPath = "/",
            UploadedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeUser(Guid userId, Guid? organizationId, OrganizationRole? role, bool isPlatformAdmin = false) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "u@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = isPlatformAdmin;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole { get; } = role;
    }

    private ICurrentUser Owner() => new FakeUser(_ownerId, SharedOrg, OrganizationRole.Owner);
    private ICurrentUser Teammate() => new FakeUser(_teammateId, SharedOrg, OrganizationRole.Member);
    private ICurrentUser Stranger() => new FakeUser(_strangerId, OtherOrg, OrganizationRole.Owner);

    private static string AsText(object? raw) => raw switch
    {
        null => string.Empty,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => raw.ToString()!
    };

    private static string? ErrorOf(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.ValueKind == JsonValueKind.Object
               && doc.RootElement.TryGetProperty("error", out JsonElement error)
            ? error.GetString()
            : null;
    }

    // ---------------------------------------------------------------------------------------------------
    // The project context: the boundary is the organization, not the owner
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A project belongs to the ORGANIZATION, so a teammate's project is a project the caller may read —
    /// the assistant must hand it over rather than answering "no project in context". This is the half
    /// of the rule that an owner-only reading would get wrong, and nothing else asserts it.
    /// </summary>
    [Fact]
    public async Task A_teammates_project_in_the_same_organization_is_in_context_not_absent()
    {
        foreach ((string toolName, AIFunctionArguments arguments) in new (string, AIFunctionArguments)[]
                 {
                     ("ListProjectFiles", new AIFunctionArguments()),
                     ("GetProject", new AIFunctionArguments { ["projectId"] = _shared.Id })
                 })
        {
            AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, Teammate());
            AIFunction tool = provider.GetTools(new AssistantToolContext(_teammateId, _shared.Id))
                .Single(t => t.Name == toolName);

            string text = AsText(await tool.InvokeAsync(arguments));

            ErrorOf(text).Should().NotBe("no project in context",
                $"'{toolName}' must treat a project in the caller's own organization as in context, "
                + "whatever member of it created the project");
        }

        AssistantToolProvider files = AssistantToolTestFactory.CreateProvider(_db, Teammate());
        string listing = AsText(await files
            .GetTools(new AssistantToolContext(_teammateId, _shared.Id))
            .Single(t => t.Name == "ListProjectFiles")
            .InvokeAsync(new AIFunctionArguments()));

        // ...and the payload is real, not merely "not the refusal".
        listing.Should().Contain("team-notes.md");
    }

    /// <summary>
    /// The other half, on both of the assistant's "absent" paths: the page context literal
    /// (<c>no project in context</c>, the same answer a call with no project at all gets) and the
    /// explicit-id literal (<c>project not found</c>, the same answer a nonexistent id gets). Neither
    /// is a permission error, and neither leaks the project's name or a file name — which is the whole
    /// point of treating "not yours" as "not there".
    /// </summary>
    [Fact]
    public async Task Another_organizations_project_is_absent_never_refused_and_leaks_nothing()
    {
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, Stranger());

        // The page context names the stranger's project (nothing in the arguments), or the caller names it
        // themselves as projectId — and the second is what the model does now that every project-scoped
        // tool takes projectId. The explicit id must read exactly like an id that does not exist.
        (string tool, string literal, AIFunctionArguments arguments)[] cases =
        [
            ("ListProjectFiles", "no project in context", new AIFunctionArguments()),
            ("ListProjectFiles", "project not found",
                new AIFunctionArguments { ["projectId"] = _shared.Id }),
            ("GetProject", "project not found",
                new AIFunctionArguments { ["projectId"] = _shared.Id })
        ];

        foreach ((string toolName, string literal, AIFunctionArguments arguments) in cases)
        {
            AIFunction tool = provider.GetTools(new AssistantToolContext(_strangerId, _shared.Id))
                .Single(t => t.Name == toolName);

            string text = AsText(await tool.InvokeAsync(arguments));

            ErrorOf(text).Should().Be(literal,
                $"'{toolName}' must answer a project outside the caller's organization exactly as it "
                + "answers one that does not exist, never with a permission-shaped error");
            text.Should().NotContain("Team Project");
            text.Should().NotContain("team-notes.md");

            if (arguments.ContainsKey("projectId"))
            {
                string missing = AsText(await tool.InvokeAsync(new AIFunctionArguments { ["projectId"] = Guid.NewGuid() }));
                text.Should().Be(missing,
                    $"'{toolName}': an id from another organization and an id that does not exist must be byte-identical");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // Conversations are per workspace
    // ---------------------------------------------------------------------------------------------------

    private AssistantThreadsController Threads(ICurrentUser user) =>
        new(_db, user, new ProjectAccessService(_db, user));

    [Fact]
    public async Task A_conversation_lives_in_the_workspace_it_was_created_in()
    {
        AssistantThread owner = await SeedThreadAsync(Owner());
        ICurrentUser samePersonInAnotherWorkspace =
            new FakeUser(_ownerId, OtherOrg, OrganizationRole.Owner);

        // Listing: the thread is simply not there.
        OkObjectResult listed = (await Threads(samePersonInAnotherWorkspace).ListThreads(CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Subject;
        ((IReadOnlyList<AssistantThreadSummaryDto>)listed.Value!).Should().BeEmpty();

        // Reading it by id: 404, never 403 — and never the other workspace's content.
        (await Threads(samePersonInAnotherWorkspace).GetThread(owner.Id, CancellationToken.None))
            .Result.Should().BeOfType<NotFoundResult>();
        (await Threads(samePersonInAnotherWorkspace).DeleteThread(owner.Id, CancellationToken.None))
            .Should().BeOfType<NotFoundResult>();

        _db.ChangeTracker.Clear();
        (await _db.AssistantThreads.CountAsync(t => t.Id == owner.Id))
            .Should().Be(1, "a refused cross-workspace delete must remove nothing");
    }

    [Fact]
    public async Task A_conversation_is_readable_in_the_workspace_it_was_created_in()
    {
        AssistantThread owner = await SeedThreadAsync(Owner());

        // The positive control: same controller, same id, the workspace it belongs to.
        (await Threads(Owner()).GetThread(owner.Id, CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>();

        (await Threads(Owner()).ListThreads(CancellationToken.None))
            .Result.Should().BeOfType<OkObjectResult>().Which.Value
            .Should().BeAssignableTo<IReadOnlyList<AssistantThreadSummaryDto>>()
            .Which.Should().ContainSingle(t => t.Id == owner.Id);
    }

    private async Task<AssistantThread> SeedThreadAsync(ICurrentUser user)
    {
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            OrganizationId = user.OrganizationId ?? throw new InvalidOperationException("thread fixture needs an organization"),
            Title = "t",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.AssistantThreads.Add(thread);
        _db.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Role = "user",
            Content = "hello",
            Sequence = 1,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return thread;
    }

    // ---------------------------------------------------------------------------------------------------
    // A11: the workspace-scoped tool must verify a workspace-BOUND token
    // ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The organization binding observed from the tool. <c>BuildAssignAgentSkillsPayload</c> is public
    /// and the operation name is internal, so this test can mint a token for exactly the payload the
    /// tool will recompute — but through the THREE-argument overload, i.e. without the workspace. A tool
    /// that verified the same way would accept it, which is the regression this pins: the binding is
    /// only real if the apply half asks for it.
    /// </summary>
    [Fact]
    public async Task The_workspace_skill_tool_refuses_a_token_minted_without_the_workspace()
    {
        AgentDefinition custom = CustomAgent(SharedOrg, _ownerId);
        _db.AgentDefinitions.Add(custom);
        await _db.SaveChangesAsync();

        string skill = ReelBolt.Shared.Skills.SkillCatalog.All[0].Name;
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();

        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(_db, Owner(), confirmationTokens: tokens);
        var context = new AssistantToolContext(_ownerId);

        object? proposed = await provider.GetTools(context).Single(t => t.Name == "ProposeAssignAgentSkills")
            .InvokeAsync(new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { skill } });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string workspaceBoundToken = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        // The same payload the tool computes, but with NO workspace in the MAC.
        string payload = AssistantAdminTools.BuildAssignAgentSkillsPayload(custom.Id, custom.Name, [skill]);
        string workspaceFreeToken = tokens.Compute(AssistantAdminTools.OpAssignAgentSkills, payload, _ownerId);

        workspaceFreeToken.Should().NotBe(workspaceBoundToken,
            "the workspace is part of the MAC, so the two tokens must differ");

        object? refused = await provider.GetTools(context).Single(t => t.Name == "ApplyAssignAgentSkills")
            .InvokeAsync(new AIFunctionArguments
            {
                ["agentId"] = custom.Id,
                ["skills"] = new[] { skill },
                ["confirmationToken"] = workspaceFreeToken
            });

        ErrorOf(AsText(refused)).Should().Be("stale confirmationToken, re-propose and try again");
        _db.ChangeTracker.Clear();
        (await _db.AgentDefinitions.SingleAsync(a => a.Id == custom.Id)).AssignedSkillsJson
            .Should().BeNull("a refused apply must write nothing");

        // ...and the token the tool actually minted is accepted, so the refusal above is the binding
        // and not a tool that refuses everything.
        object? applied = await provider.GetTools(context).Single(t => t.Name == "ApplyAssignAgentSkills")
            .InvokeAsync(new AIFunctionArguments
            {
                ["agentId"] = custom.Id,
                ["skills"] = new[] { skill },
                ["confirmationToken"] = workspaceBoundToken
            });

        AsText(applied).Should().Contain("\"assigned\":true");
    }

    private static AgentDefinition CustomAgent(Guid organizationId, Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        Name = "CustomAgent",
        Description = "d",
        SystemPrompt = "p",
        AgentType = AgentType.Custom,
        IsBuiltIn = false,
        OwnerId = ownerId,
        OrganizationId = organizationId,
        CreatedAt = DateTime.UtcNow
    };
}
