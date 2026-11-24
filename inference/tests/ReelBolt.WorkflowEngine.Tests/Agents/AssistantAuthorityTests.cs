using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// Product decision 1, in executable form: the assistant's authority is exactly the live caller's
/// authority, re-derived per tool call. These tests fail if anyone reintroduces a captured user id,
/// a constructor-time admin boolean, or a memoized gate — each of which is a privilege-escalation
/// bug, not an optimisation.
/// </summary>
public class AssistantAuthorityTests
{
    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    /// <summary>
    /// Counts <c>SaveChanges</c>/<c>SaveChangesAsync</c> calls so a test can assert a refused tool
    /// wrote NOTHING, rather than only inspecting the response string it handed back.
    /// </summary>
    private sealed class WriteCountingDbContext(DbContextOptions<InferenceApiDbContext> options)
        : InferenceApiDbContext(options)
    {
        public int SaveCount { get; private set; }

        public void ResetSaveCount() => SaveCount = 0;

        public override int SaveChanges()
        {
            SaveCount++;
            return base.SaveChanges();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static WriteCountingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WriteCountingDbContext(options);
    }

    /// <summary>
    /// An <see cref="ICurrentUser"/> whose admin claim is decided by a delegate, so a test can
    /// control — and count — exactly when the claim is read.
    /// </summary>
    private sealed class StubCurrentUser(Guid userId, Func<bool> isAdmin) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;

        public bool IsPlatformAdmin => isAdmin();

        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);

        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static AssistantToolProvider CreateProvider(InferenceApiDbContext db, ICurrentUser currentUser) =>
        AssistantToolTestFactory.CreateProvider(db, currentUser);

    private static AgentDefinition SeedAgent(InferenceApiDbContext db, Guid? ownerId, string name)
    {
        var agent = new AgentDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            SystemPrompt = "You are a test agent",
            AgentType = AgentType.AuthorAgent,
            IsBuiltIn = ownerId is null,
            OwnerId = ownerId,
            CreatedAt = DateTime.UtcNow
        };
        db.AgentDefinitions.Add(agent);
        db.SaveChanges();
        return agent;
    }

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId, string name = "Project")
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    /// <summary>
    /// The text an <see cref="AIFunction"/> invocation produced, whether the runtime handed back a
    /// JSON string, a JsonElement or a materialized object.
    /// </summary>
    private static string AsText(object? raw) => raw switch
    {
        null => string.Empty,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => raw.ToString()!
    };

    private static AIFunction AdminTool(AssistantToolProvider provider, AssistantToolContext context) =>
        provider.GetTools(context).Single(t => t.Name == "ListAgentsAdmin");

    // ---------------------------------------------------------------------
    // Review Focus #1 — the admin gate, refused
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AdminTool_IsRefused_WhenCallerIsNotAdmin()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        SeedAgent(db, ownerId: null, "AuthorAgent");

        var provider = CreateProvider(db, new StubCurrentUser(callerId, () => false));
        AIFunction tool = AdminTool(provider, new AssistantToolContext(callerId));

        int agentsBefore = await db.AgentDefinitions.CountAsync();
        db.ResetSaveCount();

        object? raw = await tool.InvokeAsync(new AIFunctionArguments());

        // 1. The response is the refusal the model can act on, not a payload.
        using JsonDocument doc = JsonDocument.Parse(AsText(raw));
        doc.RootElement.GetProperty("error").GetString().Should().Be("admin privileges required");

        // 2. ...and the caller's session did not merely get a nicer message: nothing was written.
        //    Asserting on the DbContext (writes + change tracker), not only on the string.
        db.SaveCount.Should().Be(0, "a refused admin tool must not persist anything");
        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("a refused admin tool must leave the change tracker clean");
        (await db.AgentDefinitions.CountAsync()).Should().Be(agentsBefore);
    }

    [Fact]
    public async Task AdminTool_SucceedsForAdmin_AndIsNotOverBlocked()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        SeedAgent(db, ownerId: null, "AuthorAgent");

        var provider = CreateProvider(db, new StubCurrentUser(adminId, () => true));
        AIFunction tool = AdminTool(provider, new AssistantToolContext(adminId));

        object? raw = await tool.InvokeAsync(new AIFunctionArguments());

        // The gate must not degrade into "always refuse": an admin really does get the payload.
        string text = AsText(raw);
        text.Should().Contain("AuthorAgent");
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().BeGreaterThan(0);
    }

    // ---------------------------------------------------------------------
    // Review Focus #1 — the gate is read per call, never cached
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AdminTool_ActuallyReadsIsAdminOnEachCall_NotOnceAtConstruction()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        SeedAgent(db, ownerId: null, "AuthorAgent");

        // The caller is not an admin on the first read and is one on every read after it.
        int adminReads = 0;
        var currentUser = new StubCurrentUser(callerId, () => Interlocked.Increment(ref adminReads) > 1);

        // ONE provider instance serves BOTH calls. If any implementation memoized IsAdmin in a
        // field, captured it in the constructor, or wrapped it in a Lazy, the second call would
        // still be refused — this test exists to fail in exactly that case.
        var provider = CreateProvider(db, currentUser);
        AIFunction tool = AdminTool(provider, new AssistantToolContext(callerId));

        object? firstRaw = await tool.InvokeAsync(new AIFunctionArguments());
        using (JsonDocument first = JsonDocument.Parse(AsText(firstRaw)))
        {
            first.RootElement.GetProperty("error").GetString().Should().Be("admin privileges required");
        }

        object? secondRaw = await tool.InvokeAsync(new AIFunctionArguments());
        string secondText = AsText(secondRaw);
        secondText.Should().Contain("AuthorAgent",
            "the live claim must be re-read on the second call, so the now-admin caller succeeds");

        // Exactly one read per invocation — the claim is consulted, never remembered.
        adminReads.Should().Be(2, "IsAdmin must be read once per tool call and never cached");
    }

    [Fact]
    public void AssistantToolProvider_CapturesNoIdentityAndMemoizesNoAuthority()
    {
        // The structural half of the test above: whatever the ordering test proves behaviourally,
        // the provider must also have nowhere to STORE a user id, an authority bit or a lazily
        // computed decision about either. This is what catches a re-introduced `_userId` field even
        // if the tool bodies happen to read the live claim too.
        FieldInfo[] instanceFields = typeof(AssistantToolProvider)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        instanceFields
            .Where(f => f.FieldType == typeof(Guid) || f.FieldType == typeof(Guid?) || f.FieldType == typeof(bool))
            .Select(f => f.Name)
            .Should().BeEmpty("the provider must hold no captured identity or authority");

        instanceFields
            .Where(f => f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>))
            .Select(f => f.Name)
            .Should().BeEmpty("no authority may be memoized behind a Lazy");
    }

    // ---------------------------------------------------------------------
    // Review Focus #5 — no project in context is a normal, actionable state
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ProjectTool_WithNoProjectInContext_ReturnsAnActionableError_NotAThrow()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        var provider = CreateProvider(db, new StubCurrentUser(callerId, () => false));

        // A page with no project still gets a working assistant: every project-scoped tool answers
        // with the same JSON error instead of throwing.
        var context = new AssistantToolContext(callerId);

        foreach (string toolName in new[] { "ListAgents", "ListProjectFiles" })
        {
            AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
            object? raw = await tool.InvokeAsync(new AIFunctionArguments());

            using JsonDocument doc = JsonDocument.Parse(AsText(raw));
            doc.RootElement.GetProperty("error").GetString().Should().Be("no project in context");
        }
    }

    [Fact]
    public async Task ProjectTool_ForAProjectTheCallerDoesNotOwn_IsRefusedWithTheSameError()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid someoneElse = Guid.NewGuid();
        Project theirs = SeedProject(db, someoneElse, "Theirs");
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = theirs.Id,
            OriginalFileName = "secret.mp4",
            DirectoryPath = "/",
            UploadedAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var provider = CreateProvider(db, new StubCurrentUser(callerId, () => true));

        // Ownership is re-checked against the live caller on every call, so pointing the context at
        // a stranger's project id is indistinguishable from pointing it at nothing — and never
        // confirms that the project exists.
        var context = new AssistantToolContext(callerId, theirs.Id);
        AIFunction tool = provider.GetTools(context).Single(t => t.Name == "ListProjectFiles");

        object? raw = await tool.InvokeAsync(new AIFunctionArguments());

        string text = AsText(raw);
        using JsonDocument doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("error").GetString().Should().Be("no project in context");
        text.Should().NotContain("secret.mp4");
    }

    [Fact]
    public async Task ProjectTool_ForAnOwnedProject_StillSucceeds()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project mine = SeedProject(db, callerId, "Mine");
        var provider = CreateProvider(db, new StubCurrentUser(callerId, () => false));
        AIFunction tool = provider.GetTools(new AssistantToolContext(callerId, mine.Id))
            .Single(t => t.Name == "ListProjectFiles");

        object? raw = await tool.InvokeAsync(new AIFunctionArguments());

        using JsonDocument doc = JsonDocument.Parse(AsText(raw));
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("files").ValueKind.Should().Be(JsonValueKind.Array);
    }
}
