using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Skills;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Inference.Api.Services.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// T-006: the assistant's admin surface. Three properties, each pinned by a test that would fail if
/// it were violated rather than merely described in a comment:
///
/// <list type="number">
/// <item><b>Every admin tool refuses a non-admin, and refuses inertly.</b> The theory below runs
/// over the admin-gated names <em>read from the provider</em>, not from a list written here, so a
/// newly registered admin tool is covered the moment it exists and one that forgets its gate fails
/// instead of escaping.</item>
/// <item><b>The gate is re-read per call</b>, never captured in a constructor, a field or a
/// <c>Lazy</c> — asserted behaviourally for every admin tool, plus structurally (each tool's first
/// executed call must be <c>ICurrentUser.get_IsPlatformAdmin</c>).</item>
/// <item><b>A provider's API key never comes back out</b> — no tool result contains the plaintext
/// key, the stored ciphertext, or anything outside the <c>hasApiKey</c>/<c>apiKeyLastFour</c>
/// redaction vocabulary.</item>
/// </list>
/// </summary>
public class AssistantAdminToolTests
{
    /// <summary>A real, public, DNS-free endpoint: <c>IsDisallowedEndpoint</c> rejects loopback and
    /// private ranges, and resolving a hostname here would make the suite depend on the network.</summary>
    private const string PublicEndpoint = "https://8.8.8.8/v1";

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
    /// An <see cref="ICurrentUser"/> whose PLATFORM claim is decided by a delegate, so a test can
    /// control — and count — exactly when the claim is read, with the organization axis (A11) set
    /// separately: the two authorities are independent, and most of the tests below need to say
    /// exactly which one the caller holds.
    /// </summary>
    private sealed class StubCurrentUser(
        Guid userId,
        Func<bool> isAdmin,
        Guid? organizationId = null,
        OrganizationRole? orgRole = OrganizationRole.Member) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;

        public bool IsPlatformAdmin => isAdmin();

        public Guid? OrganizationId { get; } = organizationId;

        public OrganizationRole? OrgRole { get; } = orgRole;
    }

    /// <summary>
    /// An <see cref="ICurrentUser"/> whose ORGANIZATION ROLE is a mutable property, so the
    /// per-call-read property can be exercised on the organization axis exactly as the
    /// counter-backed stub exercises it on the platform axis.
    /// </summary>
    private sealed class MutableRoleCurrentUser(Guid userId, Guid organizationId) : ICurrentUser
    {
        public OrganizationRole? Role { get; set; } = OrganizationRole.Member;

        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId { get; } = organizationId;
        public OrganizationRole? OrgRole => Role;
    }

    private static AssistantToolProvider CreateProvider(
        InferenceApiDbContext db, ICurrentUser currentUser, InferenceProviderTester? providerTester = null) =>
        AssistantToolTestFactory.CreateProvider(db, currentUser, providerTester: providerTester);

    /// <summary>A caller with neither authority — the shape every "refused" assertion starts from.</summary>
    private static AssistantToolProvider NonAdminProvider(InferenceApiDbContext db, Guid userId) =>
        CreateProvider(db, new StubCurrentUser(userId, () => false, organizationId: null, orgRole: null));

    /// <summary>
    /// A caller who holds BOTH authorities: a platform admin who is also the Owner of the workspace
    /// they are sitting in. Most tests need this because a single provider instance carries both
    /// kinds of tool, and each kind reads its own authority.
    /// </summary>
    private static AssistantToolProvider AdminProvider(InferenceApiDbContext db, Guid userId) =>
        CreateProvider(db, new StubCurrentUser(userId, () => true, TestTenancy.OrgOf(userId), OrganizationRole.Owner));

    /// <summary>
    /// A11's star witness: a PLATFORM admin whose role in the workspace they are sitting in is only
    /// Member. The platform claim must open the platform tools and nothing else.
    /// </summary>
    private static AssistantToolProvider PlatformAdminWhoIsOnlyAnOrgMemberProvider(InferenceApiDbContext db, Guid userId) =>
        CreateProvider(db, new StubCurrentUser(userId, () => true, TestTenancy.OrgOf(userId), OrganizationRole.Member));

    /// <summary>The mirror image: no platform claim at all, but Owner of the active workspace.</summary>
    private static AssistantToolProvider OrgOwnerProvider(InferenceApiDbContext db, Guid userId) =>
        CreateProvider(db, new StubCurrentUser(userId, () => false, TestTenancy.OrgOf(userId), OrganizationRole.Owner));

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Project",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static AgentDefinition SeedAgent(InferenceApiDbContext db, Guid? ownerId, string name, Guid? organizationId = null)
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
            OrganizationId = organizationId,
            CreatedAt = DateTime.UtcNow
        };
        db.AgentDefinitions.Add(agent);
        db.SaveChanges();
        return agent;
    }

    private static ModelCapabilityView SeedProvider(
        InferenceApiDbContext db, string name, bool isDefault = false,
        InferenceProviderCapability capability = InferenceProviderCapability.Chat,
        string? plaintextKey = null,
        InferenceProviderKind kind = InferenceProviderKind.OpenAICompatible,
        string? endpoint = null,
        Guid? organizationId = null)
    {
        var provider = new ModelCapabilityView
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Name = name,
            Kind = kind,
            Capability = capability,
            Endpoint = endpoint ?? PublicEndpoint,
            ModelName = "some-model",
            ApiKeyEncrypted = plaintextKey is null ? null : "ciphertext-of-" + plaintextKey,
            ApiKeyLastFour = plaintextKey is null ? null : plaintextKey[^4..],
            IsDefault = isDefault,
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.AddFlat(provider);
        db.SaveChanges();
        return provider;
    }

    /// <summary>
    /// A complete <c>ProposeUpdateInferenceProvider</c>/<c>ApplyUpdateInferenceProvider</c> argument
    /// set, every field defaulting to "unchanged". Spelled out in full rather than partially, since
    /// a partially-populated <see cref="AIFunctionArguments"/> would make a test's meaning depend on
    /// which parameters the runtime treats as optional.
    /// </summary>
    private static AIFunctionArguments UpdateArguments(
        Guid providerId,
        string? name = null,
        string? kind = null,
        string? endpoint = null,
        string? modelName = null,
        string? apiKey = null,
        bool? isDefault = null,
        bool? isEnabled = null,
        int? timeoutSeconds = null,
        string? capability = null,
        bool? personalOAuthMode = null,
        string? confirmationToken = null) => new()
        {
            ["providerId"] = providerId,
            ["name"] = name,
            ["kind"] = kind,
            ["endpoint"] = endpoint,
            ["modelName"] = modelName,
            ["apiKey"] = apiKey,
            ["isDefault"] = isDefault,
            ["isEnabled"] = isEnabled,
            ["timeoutSeconds"] = timeoutSeconds,
            ["capability"] = capability,
            ["personalOAuthMode"] = personalOAuthMode,
            ["confirmationToken"] = confirmationToken
        };

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

    private static string ErrorOf(string text)
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    private static AIFunction Tool(AssistantToolProvider provider, AssistantToolContext context, string name) =>
        provider.GetTools(context).Single(t => t.Name == name);

    /// <summary>
    /// One callable argument set for an admin tool, keyed by its registered name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is NOT the theory's input list — that comes from the provider (see
    /// <see cref="AdminToolNames"/>). It exists only because <c>AIFunctionFactory</c> refuses to
    /// invoke a tool whose required parameters are absent, so the theories below need <em>some</em>
    /// well-typed invocation for each tool.
    /// </para>
    /// <para>
    /// It cannot silently omit a tool the way a hand-written theory input list can: the
    /// <c>_ =&gt; throw</c> arm makes an unknown name fail loudly, and
    /// <see cref="AdminToolNames_EachHaveAnInvocationRecipe"/> turns that into a named, readable
    /// failure. The values are deliberately innocuous (<c>Guid.NewGuid()</c> targets that do not
    /// exist) — the refusal theory never reaches them, and the per-call theory only needs the body
    /// to run far enough to prove the gate re-opened.
    /// </para>
    /// </remarks>
    private static AIFunctionArguments ArgumentsFor(string toolName) => toolName switch
    {
        "ListInferenceProviders" => new AIFunctionArguments(),
        "ListSkills" => new AIFunctionArguments(),
        "ListAgentsAdmin" => new AIFunctionArguments(),
        "GetDecisionCalibration" => new AIFunctionArguments(),

        "ProposeCreateInferenceProvider" => new AIFunctionArguments
        {
            ["name"] = "Probe",
            ["kind"] = "OpenAICompatible",
            ["endpoint"] = PublicEndpoint,
            ["modelName"] = "probe-model",
            ["apiKey"] = (string?)null,
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = 30,
            ["capability"] = "Chat",
            ["personalOAuthMode"] = false
        },

        "ApplyCreateInferenceProvider" => WithToken(ArgumentsFor("ProposeCreateInferenceProvider"), "probe-token"),

        "ProposeUpdateInferenceProvider" => new AIFunctionArguments
        {
            ["providerId"] = Guid.NewGuid(),
            ["name"] = (string?)null,
            ["kind"] = (string?)null,
            ["endpoint"] = (string?)null,
            ["modelName"] = (string?)null,
            ["apiKey"] = (string?)null,
            ["isDefault"] = (bool?)null,
            ["isEnabled"] = (bool?)null,
            ["timeoutSeconds"] = (int?)null,
            ["capability"] = (string?)null,
            ["personalOAuthMode"] = (bool?)null
        },

        "ApplyUpdateInferenceProvider" => WithToken(ArgumentsFor("ProposeUpdateInferenceProvider"), "probe-token"),

        "ProposeDeleteInferenceProvider" => new AIFunctionArguments { ["providerId"] = Guid.NewGuid() },

        "TestInferenceProvider" => new AIFunctionArguments { ["providerId"] = Guid.NewGuid() },

        "ApplyDeleteInferenceProvider" => WithToken(ArgumentsFor("ProposeDeleteInferenceProvider"), "probe-token"),

        "ProposeAssignAgentSkills" => new AIFunctionArguments
        {
            ["agentId"] = Guid.NewGuid(),
            ["skills"] = new[] { SkillCatalog.All[0].Name }
        },

        "ApplyAssignAgentSkills" => WithToken(ArgumentsFor("ProposeAssignAgentSkills"), "probe-token"),

        _ => throw new InvalidOperationException(
            $"No invocation recipe for the admin tool '{toolName}'. Add one to {nameof(ArgumentsFor)} — "
            + "this is not the theory's input list (that comes from the provider), but every admin tool "
            + "needs SOME well-typed invocation for the gate theories to run against.")
    };

    private static AIFunctionArguments WithToken(AIFunctionArguments arguments, string token)
    {
        arguments["confirmationToken"] = token;
        return arguments;
    }

    // ---------------------------------------------------------------------
    // The admin set is read FROM THE PROVIDER, not from a list written here
    // ---------------------------------------------------------------------

    /// <summary>
    /// The theory's input, and the whole point of it: the names come from the provider's own
    /// registration, so an admin tool added tomorrow is tested tomorrow, and an admin tool that
    /// skips its gate FAILS here rather than being quietly absent from a hand-written list.
    /// </summary>
    public static TheoryData<string> AdminToolNames()
    {
        var data = new TheoryData<string>();

        using WriteCountingDbContext db = CreateContext();
        var provider = NonAdminProvider(db, Guid.NewGuid());

        foreach (string name in provider.GetAdminToolNames(new AssistantToolContext(Guid.NewGuid())))
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>Which of the two independent authorities governs a gated tool (A11).</summary>
    private enum Authority { Platform, Organization }

    /// <summary>
    /// Which authority governs a tool, read MECHANICALLY from the first method the tool body calls:
    /// the live platform claim, or the live organization read. A tool that checks neither — or does
    /// work before checking — cannot be classified and throws, naming the offending first call. This
    /// is deliberately not a second hand-maintained list beside the registration: the split the
    /// behaviour theories below run on is derived from the code under test.
    /// </summary>
    private static Authority AuthorityOf(string toolName)
    {
        MethodInfo? method = typeof(AssistantAdminTools)
            .GetMethod(toolName, BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull(
            $"{nameof(AssistantAdminTools)} must expose a private tool method named '{toolName}'");

        string? firstCall = FirstCalledMethodName(method!);
        return firstCall switch
        {
            "get_IsPlatformAdmin" => Authority.Platform,
            "get_OrganizationId" => Authority.Organization,
            _ => throw new InvalidOperationException(
                $"'{toolName}' must read its authority as the FIRST thing it does, off ICurrentUser "
                + $"(get_IsPlatformAdmin for a platform-scoped tool, get_OrganizationId for an "
                + $"organization-scoped one); its first executed call was '{firstCall ?? "<none>"}'.")
        };
    }

    /// <summary>The gated names, straight from the provider (never from a list written here).</summary>
    private static IReadOnlyList<string> GatedToolNames()
    {
        using WriteCountingDbContext db = CreateContext();
        var provider = NonAdminProvider(db, Guid.NewGuid());
        return provider.GetAdminToolNames(new AssistantToolContext(Guid.NewGuid())).ToList();
    }

    private static TheoryData<string> Names(IEnumerable<string> names)
    {
        var data = new TheoryData<string>();
        foreach (string name in names)
        {
            data.Add(name);
        }

        return data;
    }

    private static TheoryData<string> NamesWhere(Func<string, bool> predicate)
    {
        var data = new TheoryData<string>();
        foreach (string name in GatedToolNames().Where(predicate))
        {
            data.Add(name);
        }

        return data;
    }

    /// <summary>The platform-scoped half of the gated set — the tools that read <c>IsPlatformAdmin</c>.</summary>
    public static TheoryData<string> PlatformScopedToolNames() =>
        NamesWhere(name => AuthorityOf(name) == Authority.Platform);

    /// <summary>The organization-scoped half — the tools that read the live organization and role.</summary>
    public static TheoryData<string> OrganizationScopedToolNames() =>
        NamesWhere(name => AuthorityOf(name) == Authority.Organization);

    /// <summary>
    /// The organization-scoped tools that take a TARGET AGENT, derived the same way — from the
    /// invocation recipe a tool-shaped argument set already needs — rather than from a second list.
    /// The D5 theories run over these: "an agent from another organization" is meaningless for a tool
    /// with no agent argument, such as the catalog listing.
    /// </summary>
    public static TheoryData<string> OrganizationScopedAgentToolNames() =>
        NamesWhere(name => AuthorityOf(name) == Authority.Organization && ArgumentsFor(name).ContainsKey("agentId"));

    /// <summary>
    /// The classification is EXHAUSTIVE and DISJOINT over the gated set, so no tool can sit outside
    /// both behaviour theories and thereby escape the tests that exist to cover it. Both sides must
    /// be non-empty, or "exhaustive" would be satisfiable by an empty partition.
    /// </summary>
    [Fact]
    public void EveryGatedTool_IsClassifiedIntoExactlyOneAuthority()
    {
        List<string> platform = GatedToolNames().Where(n => AuthorityOf(n) == Authority.Platform).ToList();
        List<string> organization = GatedToolNames().Where(n => AuthorityOf(n) == Authority.Organization).ToList();

        platform.Should().NotBeEmpty("the platform-scoped half must exist and be tested");
        organization.Should().NotBeEmpty("the organization-scoped half must exist and be tested");
        platform.Should().NotIntersectWith(organization);
        platform.Concat(organization).Should().BeEquivalentTo(GatedToolNames());
    }

    [Fact]
    public void AdminToolNames_AreNonEmpty_Registered_AndOnlyPartOfTheSet()
    {
        using WriteCountingDbContext db = CreateContext();
        var provider = NonAdminProvider(db, Guid.NewGuid());
        var context = new AssistantToolContext(Guid.NewGuid());

        IReadOnlyList<string> adminNames = provider.GetAdminToolNames(context);

        // A guard on the guard: if the provider ever reported no admin tools, the theory below
        // would silently run zero cases and "pass". This fails loudly instead.
        adminNames.Should().NotBeEmpty("the admin surface must be enumerable from the provider");
        adminNames.Should().OnlyHaveUniqueItems();

        IReadOnlyList<string> registered = provider.GetTools(context).Select(t => t.Name).ToList();
        IReadOnlyList<string> declaredByTheSurface = new AssistantAdminTools(
                db, ConfirmationTokenService.CreateEphemeral(), new StubCurrentUser(Guid.NewGuid(), () => false), new FakeSecretProtector())
            .GetTools(context)
            .Select(t => t.Name)
            .ToList();

        // The provider's gated subset must be exactly what the admin surface class declares — i.e.
        // moving a tool into (or out of) AssistantAdminTools changes the set the theories below run
        // over, with no second list to update.
        adminNames.Should().BeEquivalentTo(declaredByTheSurface);

        // ...and it is a part of the tool set, not all of it: a provider where everything is
        // "admin" would pass a naive check while telling us nothing.
        adminNames.Count.Should().BeLessThan(registered.Count, "the admin surface is a part of the tool set, not all of it");

        // ListAgentsAdmin is the admin-gated canary T-004 registered; T-006 must not have added a
        // second tool of that name, nor dropped it while moving it into this surface.
        adminNames.Should().ContainSingle(n => n == "ListAgentsAdmin");
        registered.Count(n => n == "ListAgentsAdmin").Should().Be(1, "the registered set may contain the name exactly once");
    }

    [Fact]
    public void TheFirstCallDetector_ActuallyDetects()
    {
        // A self-test of the IL walker, so the gate assertion above cannot be vacuous: if the walker
        // ever silently returned null for everything, "the first call is get_IsPlatformAdmin" would be an
        // assertion nobody could trust. This payload builder's body is a single Serialize call, so
        // the detector has a known, non-gate answer to find.
        MethodInfo payloadBuilder = typeof(AssistantAdminTools)
            .GetMethod(nameof(AssistantAdminTools.BuildDeleteInferenceProviderPayload))!;

        FirstCalledMethodName(payloadBuilder).Should().Be("Serialize");
    }

    /// <summary>
    /// The names of the two live authority reads a gate may start with. BOTH are reads of the LIVE
    /// caller, which is the property; which one it is distinguishes the platform-scoped tools from the
    /// organization-scoped ones and is asserted exhaustively by
    /// <see cref="EveryGatedTool_IsClassifiedIntoExactlyOneAuthority"/>.
    /// </summary>
    private static readonly string[] LiveAuthorityReads = ["get_IsPlatformAdmin", "get_OrganizationId"];

    [Theory]
    [MemberData(nameof(AdminToolNames))]
    public void AdminTool_FirstExecutedCallIsALiveAuthorityRead(string toolName)
    {
        // The literal "first statement" requirement, checked mechanically rather than trusted: the
        // first method each tool body CALLS must be one of the live authority reads on ICurrentUser.
        // Work done before the gate — a read, a look-up, an allocation — would show up here as a
        // different first call.
        MethodInfo? method = typeof(AssistantAdminTools)
            .GetMethod(toolName, BindingFlags.Instance | BindingFlags.NonPublic);

        method.Should().NotBeNull(
            $"{nameof(AssistantAdminTools)} must expose a private tool method named '{toolName}' "
            + "(a new admin tool is expected to follow the same naming as its registered name, so this check can find it)");

        FirstCalledMethodName(method!).Should().BeOneOf(
            LiveAuthorityReads,
            $"the gate must be the FIRST thing '{toolName}' does — not merely somewhere in the body");
    }

    [Theory]
    [MemberData(nameof(AdminToolNames))]
    public void AdminTool_EachHaveAnInvocationRecipe(string toolName)
    {
        // Guards the theories below: a newly registered admin tool with no recipe would otherwise
        // fail them with an opaque marshalling error. Here it fails by name, telling the author
        // exactly what to add.
        Action act = () => ArgumentsFor(toolName);
        act.Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(AdminToolNames))]
    public async Task AdminTool_RefusesANonAdmin_AndWritesNothing(string toolName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project project = SeedProject(db, callerId);
        SeedAgent(db, ownerId: null, "AuthorAgent");
        SeedProvider(db, "Existing", plaintextKey: "sk-plaintext-key-1234");

        var provider = NonAdminProvider(db, callerId);
        var context = new AssistantToolContext(callerId, project.Id);
        AIFunction tool = Tool(provider, context, toolName);

        int providersBefore = await db.FlatProviders().CountAsync();
        int agentsBefore = await db.AgentDefinitions.CountAsync();
        db.ResetSaveCount();

        object? raw = await tool.InvokeAsync(ArgumentsFor(toolName));

        // 1. The refusal the model can act on — identical for every gated tool, so a caller cannot
        //    tell "you are not an admin" from "that provider does not exist".
        ErrorOf(AsText(raw)).Should().Be("admin privileges required");

        // 2. ...and it was inert, not merely reported as such. Asserting the DbContext — the write
        //    count, the change tracker and the row counts — is the difference between "the response
        //    looked right" and "nothing happened".
        db.SaveCount.Should().Be(0, $"a refused '{toolName}' must not persist anything");
        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty($"a refused '{toolName}' must leave the change tracker clean");
        (await db.FlatProviders().CountAsync()).Should().Be(providersBefore);
        (await db.AgentDefinitions.CountAsync()).Should().Be(agentsBefore);
    }

    [Theory]
    [MemberData(nameof(PlatformScopedToolNames))]
    public async Task PlatformTool_ReadsThePlatformClaimOnEveryCall_NotOnceAtConstruction(string toolName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project project = SeedProject(db, callerId);

        // The caller is not a platform admin on the first read and is one on every read after it.
        int adminReads = 0;
        var currentUser = new StubCurrentUser(
            callerId,
            () => Interlocked.Increment(ref adminReads) > 1,
            TestTenancy.OrgOf(callerId),
            OrganizationRole.Owner);

        // ONE provider instance serves BOTH calls. A memoized/captured/Lazy authority would keep
        // refusing the second call, which is exactly the case this test exists to fail.
        var provider = CreateProvider(db, currentUser);
        AIFunction tool = Tool(provider, new AssistantToolContext(callerId, project.Id), toolName);

        object? firstRaw = await tool.InvokeAsync(ArgumentsFor(toolName));
        ErrorOf(AsText(firstRaw)).Should().Be("admin privileges required");

        object? secondRaw = await tool.InvokeAsync(ArgumentsFor(toolName));
        AsText(secondRaw).Should().NotContain(
            "admin privileges required",
            $"'{toolName}' must re-read the live platform claim, so the now-admin caller gets past the gate");

        adminReads.Should().Be(2, $"IsPlatformAdmin must be read once per '{toolName}' call and never cached");
    }

    [Theory]
    [MemberData(nameof(OrganizationScopedToolNames))]
    public async Task OrganizationTool_ReadsTheOrganizationRoleOnEveryCall_NotOnceAtConstruction(string toolName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project project = SeedProject(db, callerId);

        // An Owner of the active workspace, and NOT a platform admin: the organization axis alone.
        var currentUser = new MutableRoleCurrentUser(callerId, TestTenancy.OrgOf(callerId));

        var provider = CreateProvider(db, currentUser);
        AIFunction tool = Tool(provider, new AssistantToolContext(callerId, project.Id), toolName);

        // A Member is refused, an Owner is not — on ONE provider instance, so a role memoized at
        // construction (or caught by a Lazy) would keep refusing the second call.
        object? firstRaw = await tool.InvokeAsync(ArgumentsFor(toolName));
        ErrorOf(AsText(firstRaw)).Should().Be("admin privileges required");

        currentUser.Role = OrganizationRole.Owner;
        object? secondRaw = await tool.InvokeAsync(ArgumentsFor(toolName));
        AsText(secondRaw).Should().NotContain(
            "admin privileges required",
            $"'{toolName}' must re-read the live organization role, so the now-Owner caller gets past the gate");

        currentUser.Role = OrganizationRole.Member;
        object? thirdRaw = await tool.InvokeAsync(ArgumentsFor(toolName));
        ErrorOf(AsText(thirdRaw)).Should().Be(
            "admin privileges required",
            $"'{toolName}' must re-read the role in BOTH directions — a memoized grant is a "
            + "privilege-escalation bug");
    }

    // ---------------------------------------------------------------------
    // The refusal must hold with a request that WOULD otherwise succeed
    // ---------------------------------------------------------------------

    [Fact]
    public async Task NonAdmin_WithAFullyValidCreateRequest_IsRefused_AndCreatesNothing()
    {
        // The theory above calls every tool with no arguments, which proves the gate fires but not
        // that a *valid* request is inert. This one hands a non-admin a create request that would
        // otherwise succeed outright, which is the shape a prompt-injected "create me a provider"
        // would actually take.
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        var provider = NonAdminProvider(db, callerId);
        var context = new AssistantToolContext(callerId);

        AIFunction propose = Tool(provider, context, "ProposeCreateInferenceProvider");
        object? proposed = await propose.InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "Injected Provider",
            ["kind"] = "OpenAICompatible",
            ["endpoint"] = PublicEndpoint,
            ["modelName"] = "some-model",
            ["apiKey"] = "sk-attacker-key-9999",
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = 30,
            ["capability"] = "Chat",
            ["personalOAuthMode"] = false
        });

        ErrorOf(AsText(proposed)).Should().Be("admin privileges required");

        // Even holding a token, the non-admin cannot apply: Apply re-derives the same refusal.
        AIFunction apply = Tool(provider, context, "ApplyCreateInferenceProvider");
        object? applied = await apply.InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "Injected Provider",
            ["kind"] = "OpenAICompatible",
            ["endpoint"] = PublicEndpoint,
            ["modelName"] = "some-model",
            ["apiKey"] = "sk-attacker-key-9999",
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = 30,
            ["capability"] = "Chat",
            ["personalOAuthMode"] = false,
            ["confirmationToken"] = "any-token-at-all"
        });

        ErrorOf(AsText(applied)).Should().Be("admin privileges required");
        db.SaveCount.Should().Be(0);
        (await db.FlatProviders().CountAsync(p => p.Name == "Injected Provider")).Should().Be(0);
    }

    [Fact]
    public async Task NonAdmin_WithAFullyValidDeleteRequest_IsRefused_AndDeletesNothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        ModelCapabilityView provider = SeedProvider(db, "Production", plaintextKey: "sk-plaintext-key-1234");

        var tools = NonAdminProvider(db, callerId);
        var context = new AssistantToolContext(callerId);
        db.ResetSaveCount();

        object? applied = await Tool(tools, context, "ApplyDeleteInferenceProvider").InvokeAsync(
            new AIFunctionArguments { ["providerId"] = provider.Id, ["confirmationToken"] = "guessed" });

        ErrorOf(AsText(applied)).Should().Be("admin privileges required");
        db.SaveCount.Should().Be(0);
        (await db.FlatProviders().CountAsync(p => p.Id == provider.Id)).Should().Be(1);
    }

    // ---------------------------------------------------------------------
    // Not over-blocked: an admin really can do the work
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Admin_CreatesAProvider_AndListInferenceProvidersReflectsIt()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        object? proposed = await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(
            new AIFunctionArguments
            {
                ["name"] = "Primary",
                ["kind"] = "OpenAICompatible",
                ["endpoint"] = PublicEndpoint,
                ["modelName"] = "some-model",
                ["apiKey"] = "sk-live-key-4321",
                ["isDefault"] = true,
                ["isEnabled"] = true,
                ["timeoutSeconds"] = 30,
                ["capability"] = "Chat",
                ["personalOAuthMode"] = false
            });

        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;
        proposal.RootElement.GetProperty("summary").GetString().Should().Contain("CREATE inference provider \"Primary\"");

        object? applied = await Tool(provider, context, "ApplyCreateInferenceProvider").InvokeAsync(
            new AIFunctionArguments
            {
                ["name"] = "Primary",
                ["kind"] = "OpenAICompatible",
                ["endpoint"] = PublicEndpoint,
                ["modelName"] = "some-model",
                ["apiKey"] = "sk-live-key-4321",
                ["isDefault"] = true,
                ["isEnabled"] = true,
                ["timeoutSeconds"] = 30,
                ["capability"] = "Chat",
                ["personalOAuthMode"] = false,
                ["confirmationToken"] = token
            });

        AsText(applied).Should().Contain("\"created\":true");

        ModelCapabilityView stored = await db.FlatProviders().SingleAsync();
        stored.Name.Should().Be("Primary");
        stored.IsDefault.Should().BeTrue();
        stored.ApiKeyLastFour.Should().Be("4321");
        stored.ApiKeyEncrypted.Should().NotBe("sk-live-key-4321", "the key is stored encrypted, never verbatim");

        object? listed = await Tool(provider, context, "ListInferenceProviders").InvokeAsync(new AIFunctionArguments());
        using JsonDocument list = JsonDocument.Parse(AsText(listed));
        list.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        list.RootElement.GetArrayLength().Should().Be(1);
        list.RootElement[0].GetProperty("name").GetString().Should().Be("Primary");
        list.RootElement[0].GetProperty("hasApiKey").GetBoolean().Should().BeTrue();
        list.RootElement[0].GetProperty("apiKeyLastFour").GetString().Should().Be("4321");

        // The tree: account -> models -> capabilities, and the capability id is the one every other tool takes.
        JsonElement model = list.RootElement[0].GetProperty("models").EnumerateArray().Single();
        model.GetProperty("modelCode").GetString().Should().Be("some-model");
        model.GetProperty("displayName").GetString().Should().Be("Primary");
        JsonElement capability = model.GetProperty("capabilities").EnumerateArray().Single();
        capability.GetProperty("id").GetGuid().Should().Be(stored.Id);
        capability.GetProperty("capability").GetString().Should().Be("Chat");
        capability.GetProperty("isDefault").GetBoolean().Should().BeTrue();
        list.RootElement[0].GetProperty("providerId").GetGuid().Should().Be(stored.ProviderId);
    }

    [Fact]
    public async Task Admin_CanUpdateAProvider_AnEmptyApiKeyClearsIt_AnOmittedOneKeepsIt()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Primary", plaintextKey: "sk-live-key-4321");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        // An omitted (null) apiKey must leave the stored key alone.
        await ApplyUpdateAsync(provider, context, db, seeded.Id, name: "Renamed", apiKey: null);
        ModelCapabilityView afterRename = await db.FlatProviders().SingleAsync(p => p.Id == seeded.Id);
        afterRename.Name.Should().Be("Renamed");
        afterRename.ApiKeyEncrypted.Should().NotBeNull("a null apiKey means 'leave the stored key unchanged'");

        // An explicit empty string must clear it — and clear the last-four with it, so the admin is
        // never shown a confident hasApiKey with a stale hint behind it.
        await ApplyUpdateAsync(provider, context, db, seeded.Id, name: null, apiKey: "");
        ModelCapabilityView afterClear = await db.FlatProviders().SingleAsync(p => p.Id == seeded.Id);
        afterClear.ApiKeyEncrypted.Should().BeNull();
        afterClear.ApiKeyLastFour.Should().BeNull();
    }

    [Fact]
    public async Task Admin_PromotingADefault_ClearsOnlyTheOtherDefaultOfTheSameCapability()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView chatDefault = SeedProvider(db, "OldChat", isDefault: true);
        ModelCapabilityView chatOther = SeedProvider(db, "NewChat");
        ModelCapabilityView transcriptionDefault = SeedProvider(
            db, "Transcription", isDefault: true, capability: InferenceProviderCapability.Transcription);

        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        await ApplyUpdateAsync(provider, context, db, chatOther.Id, name: null, apiKey: null, isDefault: true);

        // The composite (capability, is_default) rule: promoting a Chat row must never clear the
        // Transcription default, and vice versa.
        (await db.FlatProviders().SingleAsync(p => p.Id == chatOther.Id)).IsDefault.Should().BeTrue();
        (await db.FlatProviders().SingleAsync(p => p.Id == chatDefault.Id)).IsDefault.Should().BeFalse();
        (await db.FlatProviders().SingleAsync(p => p.Id == transcriptionDefault.Id)).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_CanDeleteAProvider_ButTheDefaultOneIsRefused()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView theDefault = SeedProvider(db, "TheDefault", isDefault: true);
        ModelCapabilityView ordinary = SeedProvider(db, "Ordinary");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        // Mirrors InferenceProvidersController.Delete's 409: deleting the default would leave its
        // capability with no default at all.
        object? refused = await Tool(provider, context, "ProposeDeleteInferenceProvider")
            .InvokeAsync(new AIFunctionArguments { ["providerId"] = theDefault.Id });
        ErrorOf(AsText(refused)).Should().Contain("Cannot delete the default inference provider");
        (await db.FlatProviders().CountAsync(p => p.Id == theDefault.Id)).Should().Be(1);

        // ...and the ordinary one really does go, via the propose→apply pair.
        object? proposed = await Tool(provider, context, "ProposeDeleteInferenceProvider")
            .InvokeAsync(new AIFunctionArguments { ["providerId"] = ordinary.Id });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;
        proposal.RootElement.GetProperty("name").GetString().Should().Be("Ordinary",
            "the token is bound to the name as well as the id");

        object? applied = await Tool(provider, context, "ApplyDeleteInferenceProvider")
            .InvokeAsync(new AIFunctionArguments { ["providerId"] = ordinary.Id, ["confirmationToken"] = token });
        AsText(applied).Should().Contain("\"deleted\":true");
        (await db.FlatProviders().CountAsync(p => p.Id == ordinary.Id)).Should().Be(0);
    }

    [Fact]
    public async Task ApplyDeleteInferenceProvider_IsBoundToTheName_ARenameInvalidatesTheToken()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Before");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        object? proposed = await Tool(provider, context, "ProposeDeleteInferenceProvider")
            .InvokeAsync(new AIFunctionArguments { ["providerId"] = seeded.Id });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        // The row is renamed between propose and apply — a confirmation the user saw for "Before"
        // must not destroy a provider that is now called something else.
        // The flat row's name is its model's display name.
        db.TrackedCapability(seeded.Id).Model.DisplayName = "After";
        await db.SaveChangesAsync();

        db.ResetSaveCount();
        object? applied = await Tool(provider, context, "ApplyDeleteInferenceProvider")
            .InvokeAsync(new AIFunctionArguments { ["providerId"] = seeded.Id, ["confirmationToken"] = token });

        ErrorOf(AsText(applied)).Should().Be("stale confirmationToken, re-propose and try again");
        db.SaveCount.Should().Be(0, "a stale token must write nothing at all");
        (await db.FlatProviders().CountAsync(p => p.Id == seeded.Id)).Should().Be(1);
    }

    [Fact]
    public async Task ApplyCreateInferenceProvider_WithAFabricatedToken_WritesNothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        object? applied = await Tool(provider, context, "ApplyCreateInferenceProvider").InvokeAsync(
            new AIFunctionArguments
            {
                ["name"] = "Forged",
                ["kind"] = "OpenAICompatible",
                ["endpoint"] = PublicEndpoint,
                ["modelName"] = "some-model",
                ["apiKey"] = (string?)null,
                ["isDefault"] = false,
                ["isEnabled"] = true,
                ["timeoutSeconds"] = 30,
                ["capability"] = "Chat",
                ["personalOAuthMode"] = false,
                ["confirmationToken"] = "0000000000000000000000000000000000000000000000000000000000000000"
            });

        ErrorOf(AsText(applied)).Should().Be("stale confirmationToken, re-propose and try again");
        db.SaveCount.Should().Be(0);
        (await db.FlatProviders().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ApplyUpdateInferenceProvider_WithAStaleToken_WritesNothing()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Primary");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        // A token proposed for a DIFFERENT new name is not a token for this one.
        object? proposed = await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(
            new AIFunctionArguments
            {
                ["providerId"] = seeded.Id,
                ["name"] = "ProposedName",
                ["kind"] = (string?)null,
                ["endpoint"] = (string?)null,
                ["modelName"] = (string?)null,
                ["apiKey"] = (string?)null,
                ["isDefault"] = (bool?)null,
                ["isEnabled"] = (bool?)null,
                ["timeoutSeconds"] = (int?)null,
                ["capability"] = (string?)null,
                ["personalOAuthMode"] = (bool?)null
            });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        db.ResetSaveCount();
        object? applied = await Tool(provider, context, "ApplyUpdateInferenceProvider").InvokeAsync(
            new AIFunctionArguments
            {
                ["providerId"] = seeded.Id,
                ["name"] = "SomethingElse",
                ["kind"] = (string?)null,
                ["endpoint"] = (string?)null,
                ["modelName"] = (string?)null,
                ["apiKey"] = (string?)null,
                ["isDefault"] = (bool?)null,
                ["isEnabled"] = (bool?)null,
                ["timeoutSeconds"] = (int?)null,
                ["capability"] = (string?)null,
                ["personalOAuthMode"] = (bool?)null,
                ["confirmationToken"] = token
            });

        ErrorOf(AsText(applied)).Should().Be("stale confirmationToken, re-propose and try again");
        db.SaveCount.Should().Be(0);
        (await db.FlatProviders().SingleAsync(p => p.Id == seeded.Id)).Name.Should().Be("Primary");
    }

    // ---------------------------------------------------------------------
    // The endpoint rule holds on UPDATE, against the EFFECTIVE pair
    // ---------------------------------------------------------------------

    /// <summary>The compose service's own loopback bind — the address the OpenJev exception exists for.</summary>
    private const string LoopbackEndpoint = "http://127.0.0.1:8791";

    private const string EndpointRejectedMessage =
        "Endpoint must be a public https/http URL; internal/private/loopback addresses are not allowed.";

    /// <summary>
    /// Flipping an <c>OpenJev</c> row to a kind with no loopback exception — with the endpoint
    /// <b>omitted</b> — must be refused.
    /// </summary>
    /// <remarks>
    /// The rule is kind-dependent, so it is a property of the <b>pair</b>: the effective endpoint
    /// (<c>requestEndpoint ?? provider.Endpoint</c>) judged against the effective kind
    /// (<c>parsedKind ?? provider.Kind</c>). Checking it only when the request carried an endpoint
    /// left the stored one unexamined the moment a kind change re-framed it, so this exact request
    /// used to walk past a rule the create path enforces on the same row.
    /// </remarks>
    [Fact]
    public async Task ProposeUpdateInferenceProvider_RefusesAKindChangeThatWouldStrandALoopbackEndpoint()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView openJev = SeedProvider(
            db, "OpenJev", capability: InferenceProviderCapability.Decision,
            kind: InferenceProviderKind.OpenJev, endpoint: LoopbackEndpoint);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        db.ResetSaveCount();
        object? proposed = await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(openJev.Id, kind: "DeepSeek"));

        ErrorOf(AsText(proposed)).Should().Be(EndpointRejectedMessage);
        db.SaveCount.Should().Be(0, "a refused proposal must write nothing");

        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == openJev.Id);
        stored.Kind.Should().Be(InferenceProviderKind.OpenJev);
        stored.Endpoint.Should().Be(LoopbackEndpoint);
    }

    /// <summary>
    /// The apply half re-runs the same rule before it verifies the token, so a request that got past
    /// an older propose cannot be applied through an already-issued token either.
    /// </summary>
    [Fact]
    public async Task ApplyUpdateInferenceProvider_AlsoRefusesTheStrandedLoopbackEndpoint()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView openJev = SeedProvider(
            db, "OpenJev", capability: InferenceProviderCapability.Decision,
            kind: InferenceProviderKind.OpenJev, endpoint: LoopbackEndpoint);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        db.ResetSaveCount();
        object? applied = await Tool(provider, context, "ApplyUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(
                openJev.Id,
                kind: "DeepSeek",
                confirmationToken: new string('0', 64)));

        // The endpoint rule is reported rather than the stale-token rule: validation precedes token
        // verification, so the reason the caller sees is the real one.
        ErrorOf(AsText(applied)).Should().Be(EndpointRejectedMessage);
        db.SaveCount.Should().Be(0);

        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == openJev.Id);
        stored.Kind.Should().Be(InferenceProviderKind.OpenJev);
        stored.Endpoint.Should().Be(LoopbackEndpoint);
    }

    /// <summary>
    /// The OpenJev exception survives: re-stating <c>OpenJev</c> on a row already at a loopback
    /// address is still the legal pair, and still updatable end to end.
    /// </summary>
    [Fact]
    public async Task UpdateInferenceProvider_StillAcceptsAnOpenJevRowAtALoopbackEndpoint()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView openJev = SeedProvider(
            db, "OpenJev", capability: InferenceProviderCapability.Decision,
            kind: InferenceProviderKind.OpenJev, endpoint: LoopbackEndpoint);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        object? proposed = await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(openJev.Id, kind: "OpenJev", modelName: "jev-model"));

        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        proposal.RootElement.TryGetProperty("confirmationToken", out JsonElement token)
            .Should().BeTrue($"an OpenJev row must stay updatable at a loopback address, but got: {AsText(proposed)}");

        object? applied = await Tool(provider, context, "ApplyUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(openJev.Id, kind: "OpenJev", modelName: "jev-model", confirmationToken: token.GetString()!));

        AsText(applied).Should().NotContain("\"error\"");

        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == openJev.Id);
        stored.Kind.Should().Be(InferenceProviderKind.OpenJev);
        stored.Endpoint.Should().Be(LoopbackEndpoint);
        stored.ModelName.Should().Be("jev-model");
    }

    /// <summary>
    /// Positive control in the other direction: a kind change on a row whose stored endpoint is
    /// public is still allowed, so the check rejects a disallowed PAIR rather than kind changes.
    /// </summary>
    [Fact]
    public async Task UpdateInferenceProvider_AcceptsAKindChangeOverAPublicStoredEndpoint()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Public", capability: InferenceProviderCapability.Decision);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        object? proposed = await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, kind: "OpenAICompatible"));

        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        proposal.RootElement.TryGetProperty("confirmationToken", out JsonElement token)
            .Should().BeTrue($"a public stored endpoint stays legal, but got: {AsText(proposed)}");

        object? applied = await Tool(provider, context, "ApplyUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, kind: "OpenAICompatible", confirmationToken: token.GetString()!));

        AsText(applied).Should().NotContain("\"error\"");
        (await db.FlatProviders().SingleAsync(p => p.Id == seeded.Id)).Endpoint.Should().Be(PublicEndpoint);
    }

    // ---------------------------------------------------------------------
    // A stored key never follows the account to another host or kind (T-005)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("https://api.openai.com/v1", null)]   // another host
    [InlineData(null, "Anthropic")]                   // another kind
    public async Task UpdateInferenceProvider_MovingAKeyedAccountWithoutAKey_IsRefusedOnProposeAndApply(string? endpoint, string? kind)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Keyed", plaintextKey: "sk-stored-1234");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string proposed = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, endpoint: endpoint, kind: kind)));
        string applied = AsText(await Tool(provider, context, "ApplyUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, endpoint: endpoint, kind: kind, confirmationToken: "probe-token")));

        proposed.Should().Contain("API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE");
        applied.Should().Contain("API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE");
        ModelCapabilityView after = await db.FlatProviders().AsNoTracking().SingleAsync(p => p.Id == seeded.Id);
        after.Endpoint.Should().Be(PublicEndpoint);
        after.Kind.Should().Be(InferenceProviderKind.OpenAICompatible);
    }

    [Fact]
    public async Task UpdateInferenceProvider_AStoredEnvKeyDoesNotFollowTheAccountToAnotherHost()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Ambient", plaintextKey: "env:", kind: InferenceProviderKind.Anthropic,
            endpoint: "https://api.anthropic.com");
        var provider = AdminProvider(db, adminId);

        string proposed = AsText(await Tool(provider, new AssistantToolContext(adminId), "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, endpoint: "https://api.openai.com")));

        proposed.Should().Contain("API_KEY_REQUIRED_FOR_ENDPOINT_CHANGE");
    }

    [Fact]
    public async Task UpdateInferenceProvider_AnEnvKeyAtANonVendorHostIsRefused()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Anth", plaintextKey: "sk-stored-1234", kind: InferenceProviderKind.Anthropic,
            endpoint: "https://api.anthropic.com");
        var provider = AdminProvider(db, adminId);

        string proposed = AsText(await Tool(provider, new AssistantToolContext(adminId), "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, endpoint: "https://api.openai.com", apiKey: "env:")));

        proposed.Should().Contain("AMBIENT_KEY_ENDPOINT_NOT_ALLOWED");
    }

    [Fact]
    public async Task UpdateInferenceProvider_SameOriginPathChangeOrANewKeyIsStillProposed()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Keyed", plaintextKey: "sk-stored-1234");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string samePath = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, endpoint: PublicEndpoint.TrimEnd('/') + "/other")));
        string newKey = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(seeded.Id, endpoint: "https://api.openai.com/v1", apiKey: "sk-new-5678")));

        samePath.Should().Contain("confirmationToken");
        newKey.Should().Contain("confirmationToken");
    }

    [Fact]
    public async Task CreateInferenceProvider_AnEnvKeyAtANonVendorHostIsRefused()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        var provider = AdminProvider(db, adminId);

        string proposed = AsText(await Tool(provider, new AssistantToolContext(adminId), "ProposeCreateInferenceProvider")
            .InvokeAsync(CreateArguments("Ambient", "claude-x", null, kind: "Anthropic", endpoint: "https://api.openai.com", apiKey: "env:")));

        proposed.Should().Contain("AMBIENT_KEY_ENDPOINT_NOT_ALLOWED");
    }

    // ---------------------------------------------------------------------
    // Skills
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Admin_AssignsSkillsToACustomAgent_AndABuiltInAgentIsRefused()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        AgentDefinition custom = SeedAgent(db, adminId, "CustomAgent", TestTenancy.OrgOf(adminId));
        AgentDefinition builtIn = SeedAgent(db, ownerId: null, "BuiltInAgent");
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string skill = SkillCatalog.All[0].Name;

        object? proposed = await Tool(provider, context, "ProposeAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { skill } });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        object? applied = await Tool(provider, context, "ApplyAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { skill }, ["confirmationToken"] = token });
        AsText(applied).Should().Contain("\"assigned\":true");

        AgentDefinition stored = await db.AgentDefinitions.SingleAsync(a => a.Id == custom.Id);
        stored.AssignedSkillsJson.Should().Be(JsonSerializer.Serialize(new[] { skill }));

        // Mirrors SkillsController.SetSkills: a built-in's skills come from code, never a runtime
        // override — and the refusal must be inert.
        db.ResetSaveCount();
        object? refused = await Tool(provider, context, "ApplyAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = builtIn.Id, ["skills"] = new[] { skill }, ["confirmationToken"] = token });
        ErrorOf(AsText(refused)).Should().Contain("only editable for custom agents");
        db.SaveCount.Should().Be(0);
        (await db.AgentDefinitions.SingleAsync(a => a.Id == builtIn.Id)).AssignedSkillsJson.Should().BeNull();
    }

    [Fact]
    public async Task AssignAgentSkills_StoresCanonicalNames_AndRejectsUnknownOnes()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        AgentDefinition custom = SeedAgent(db, adminId, "CustomAgent", TestTenancy.OrgOf(adminId));
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string canonical = SkillCatalog.All[0].Name;

        object? refused = await Tool(provider, context, "ProposeAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { "not-a-real-skill" } });
        ErrorOf(AsText(refused)).Should().Contain("Unknown skill name");
        (await db.AgentDefinitions.SingleAsync(a => a.Id == custom.Id)).AssignedSkillsJson.Should().BeNull();

        // A differently-cased spelling is stored as the catalog's canonical name, so every later
        // consumer can match by simple equality.
        object? proposed = await Tool(provider, context, "ProposeAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { canonical.ToUpperInvariant() } });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        await Tool(provider, context, "ApplyAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments
            {
                ["agentId"] = custom.Id,
                ["skills"] = new[] { canonical.ToUpperInvariant() },
                ["confirmationToken"] = token
            });

        (await db.AgentDefinitions.SingleAsync(a => a.Id == custom.Id)).AssignedSkillsJson
            .Should().Be(JsonSerializer.Serialize(new[] { canonical }));
    }

    [Fact]
    public async Task Admin_ListSkills_ReturnsTheCatalog()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        var provider = OrgOwnerProvider(db, adminId);

        object? raw = await Tool(provider, new AssistantToolContext(adminId), "ListSkills")
            .InvokeAsync(new AIFunctionArguments());

        using JsonDocument doc = JsonDocument.Parse(AsText(raw));
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(SkillCatalog.All.Count);
        doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString())
            .Should().BeEquivalentTo(SkillCatalog.All.Select(s => s.Name));
    }

    // ---------------------------------------------------------------------
    // Review Focus: the key never comes back out
    // ---------------------------------------------------------------------

    [Fact]
    public async Task AdminTool_NeverEchoesApiKeyMaterial()
    {
        const string plaintextKey = "sk-" + "super-secret-value-9876";
        const string ciphertext = "ciphertext-of-" + plaintextKey;

        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        SeedProvider(db, "Existing", plaintextKey: plaintextKey);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        // Every result the model can see, for every read/propose/apply path that touches a provider.
        var results = new List<(string Tool, string Text)>
        {
            ("ListInferenceProviders",
                AsText(await Tool(provider, context, "ListInferenceProviders").InvokeAsync(new AIFunctionArguments()))),

            ("ProposeCreateInferenceProvider",
                AsText(await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(new AIFunctionArguments
                {
                    ["name"] = "New",
                    ["kind"] = "OpenAICompatible",
                    ["endpoint"] = PublicEndpoint,
                    ["modelName"] = "some-model",
                    ["apiKey"] = plaintextKey,
                    ["isDefault"] = false,
                    ["isEnabled"] = true,
                    ["timeoutSeconds"] = 30,
                    ["capability"] = "Chat",
                    ["personalOAuthMode"] = false
                }))),

            ("ProposeUpdateInferenceProvider",
                AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(new AIFunctionArguments
                {
                    ["providerId"] = (await db.FlatProviders().SingleAsync(p => p.Name == "Existing")).Id,
                    ["name"] = (string?)null,
                    ["kind"] = (string?)null,
                    ["endpoint"] = (string?)null,
                    ["modelName"] = (string?)null,
                    ["apiKey"] = plaintextKey,
                    ["isDefault"] = (bool?)null,
                    ["isEnabled"] = (bool?)null,
                    ["timeoutSeconds"] = (int?)null,
                    ["capability"] = (string?)null,
                    ["personalOAuthMode"] = (bool?)null
                })))
        };

        foreach ((string tool, string text) in results)
        {
            text.Should().NotContain(plaintextKey, $"'{tool}' must never echo a plaintext API key");
            text.Should().NotContain(ciphertext, $"'{tool}' must never echo the stored ciphertext");
            text.Should().NotContain("ApiKeyEncrypted", $"'{tool}' must not even name the ciphertext column");
            text.Should().NotContain("\"apiKey\"", $"'{tool}' must expose only the hasApiKey/apiKeyLastFour vocabulary");
        }

        // The redaction vocabulary IS present, so the test cannot pass by returning nothing at all.
        string list = results.Single(r => r.Tool == "ListInferenceProviders").Text;
        list.Should().Contain("\"hasApiKey\":true");
        list.Should().Contain("\"apiKeyLastFour\":\"9876\"");
    }

    [Fact]
    public async Task AdminTool_ApplyCreate_DoesNotEchoTheSubmittedKey()
    {
        const string plaintextKey = "sk-another-secret-1357";

        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        var arguments = new AIFunctionArguments
        {
            ["name"] = "New",
            ["kind"] = "OpenAICompatible",
            ["endpoint"] = PublicEndpoint,
            ["modelName"] = "some-model",
            ["apiKey"] = plaintextKey,
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = 30,
            ["capability"] = "Chat",
            ["personalOAuthMode"] = false
        };

        object? proposed = await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(arguments);
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        arguments["confirmationToken"] = token;
        string applied = AsText(await Tool(provider, context, "ApplyCreateInferenceProvider").InvokeAsync(arguments));

        applied.Should().NotContain(plaintextKey);
        applied.Should().Contain("\"hasApiKey\":true");
        applied.Should().Contain("\"apiKeyLastFour\":\"1357\"");
    }


    // ---------------------------------------------------------------------
    // A11 — platform authority and organization authority are INDEPENDENT
    // ---------------------------------------------------------------------

    /// <summary>
    /// A11's central requirement, in one test: a caller who IS a platform admin but is only a MEMBER
    /// of the workspace they are sitting in gets the platform tools and none of the
    /// organization-scoped ones, and the refusal writes nothing.
    /// </summary>
    /// <remarks>
    /// Expressed over BOTH halves of the split, read from the classification rather than from a list
    /// written here: every organization-scoped tool must refuse this caller, and every platform-scoped
    /// tool must serve it. A future tool classified the wrong way fails one half of the assertion.
    /// </remarks>
    [Fact]
    public async Task PlatformAdminWhoIsOnlyAnOrgMember_IsRefusedTheOrganizationScopedTools()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(callerId);
        AgentDefinition custom = SeedAgent(db, callerId, "CustomAgent", org);

        var provider = PlatformAdminWhoIsOnlyAnOrgMemberProvider(db, callerId);
        var context = new AssistantToolContext(callerId);

        // The organization half: refused, whatever the platform claim says.
        foreach (string toolName in GatedToolNames().Where(n => AuthorityOf(n) == Authority.Organization))
        {
            db.ResetSaveCount();
            object? raw = await Tool(provider, context, toolName).InvokeAsync(ArgumentsFor(toolName));

            ErrorOf(AsText(raw)).Should().Be(
                "admin privileges required",
                $"'{{toolName}}' is organization-scoped, so a platform admin who is only a Member of "
                + "the active workspace must be refused by it");

            db.SaveCount.Should().Be(0, $"a refused '{toolName}' must not persist anything");
        }

        // ...and the platform half still works, so the refusal above is the ORG gate and not a
        // caller who happens to be refused by everything.
        string listed = AsText(await Tool(provider, context, "ListAgentsAdmin").InvokeAsync(new AIFunctionArguments()));
        listed.Should().NotContain("admin privileges required");

        // The custom agent the caller could not touch is untouched.
        (await db.AgentDefinitions.SingleAsync(a => a.Id == custom.Id)).AssignedSkillsJson.Should().BeNull();
    }

    /// <summary>
    /// The mirror image: an Owner of the active workspace needs no platform claim. Without this, the
    /// re-gate could be satisfied by demanding BOTH authorities, which would lock every real
    /// organization out of its own agents.
    /// </summary>
    [Fact]
    public async Task OrgOwnerWithoutAnyPlatformClaim_CanAssignTheirOwnCustomAgentsSkills()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        AgentDefinition custom = SeedAgent(db, callerId, "CustomAgent", TestTenancy.OrgOf(callerId));

        var provider = OrgOwnerProvider(db, callerId);
        var context = new AssistantToolContext(callerId);
        string skill = SkillCatalog.All[0].Name;

        object? proposed = await Tool(provider, context, "ProposeAssignAgentSkills")
            .InvokeAsync(new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { skill } });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        object? applied = await Tool(provider, context, "ApplyAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = custom.Id, ["skills"] = new[] { skill }, ["confirmationToken"] = token });

        AsText(applied).Should().Contain("\"assigned\":true");
        (await db.AgentDefinitions.SingleAsync(a => a.Id == custom.Id)).AssignedSkillsJson
            .Should().Be(JsonSerializer.Serialize(new[] { skill }));
    }

    /// <summary>
    /// Decision D5 on the assistant surface: another organization's custom agent answers exactly what
    /// a nonexistent id answers — <c>agent not found</c> — and never the role refusal, so a caller
    /// cannot use the difference to learn that a guessed id is real.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrganizationScopedAgentToolNames))]
    public async Task OrganizationTool_OnAnAgentFromAnotherOrganization_IsNotFound_NotRefused(string toolName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid strangerId = Guid.NewGuid();
        AgentDefinition theirs = SeedAgent(db, strangerId, "Theirs", TestTenancy.OrgOf(strangerId));

        // An Owner of their OWN workspace, so the role check is satisfied: what refuses this call is
        // the target's organization, not the caller's authority.
        var provider = OrgOwnerProvider(db, callerId);
        var context = new AssistantToolContext(callerId);

        AIFunctionArguments arguments = ArgumentsFor(toolName);
        arguments["agentId"] = theirs.Id;

        db.ResetSaveCount();
        object? raw = await Tool(provider, context, toolName).InvokeAsync(arguments);

        string text = AsText(raw);
        ErrorOf(text).Should().Be(
            "agent not found",
            $"'{toolName}' must answer a cross-organization agent the way it answers a nonexistent "
            + "one (decision D5), never with a role-shaped refusal");
        ErrorOf(text).Should().NotBe("admin privileges required");
        db.SaveCount.Should().Be(0, "a cross-organization target must write nothing");

        // The stranger's agent is untouched and its name never leaked into the reply.
        AgentDefinition stored = await db.AgentDefinitions.SingleAsync(a => a.Id == theirs.Id);
        stored.AssignedSkillsJson.Should().BeNull();
        text.Should().NotContain("Theirs");
    }

    /// <summary>
    /// The same property for every organization-scoped tool, with an id that matches nothing at all:
    /// indistinguishable from the cross-organization answer above.
    /// </summary>
    [Theory]
    [MemberData(nameof(OrganizationScopedAgentToolNames))]
    public async Task OrganizationTool_OnANonexistentAgent_IsNotFound(string toolName)
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        var provider = OrgOwnerProvider(db, callerId);

        AIFunctionArguments arguments = ArgumentsFor(toolName);
        arguments["agentId"] = Guid.NewGuid();

        db.ResetSaveCount();
        string text = AsText(await Tool(provider, new AssistantToolContext(callerId), toolName).InvokeAsync(arguments));

        ErrorOf(text).Should().Be("agent not found");
        db.SaveCount.Should().Be(0);
    }

    /// <summary>
    /// Switching workspace between the two halves of a propose/apply pair never writes. The token is
    /// MAC'd over the proposing organization, so a proposal made in one workspace does not survive
    /// into another — and the agent's own organization decides the answer before the token is even
    /// consulted.
    /// </summary>
    [Fact]
    public async Task AssignAgentSkills_ProposedInOneWorkspace_CannotBeAppliedInAnother()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid orgA = TestTenancy.OrgOf(callerId);
        Guid orgB = Guid.NewGuid();
        AgentDefinition agentA = SeedAgent(db, callerId, "AgentA", orgA);
        string skill = SkillCatalog.All[0].Name;

        // The caller is an Owner of org A, proposes there...
        var providerA = CreateProvider(db, new StubCurrentUser(callerId, () => false, orgA, OrganizationRole.Owner));
        var context = new AssistantToolContext(callerId);

        object? proposed = await Tool(providerA, context, "ProposeAssignAgentSkills")
            .InvokeAsync(new AIFunctionArguments { ["agentId"] = agentA.Id, ["skills"] = new[] { skill } });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;

        // ...then the SAME user, now working in org B, presents that token.
        var providerB = CreateProvider(db, new StubCurrentUser(callerId, () => false, orgB, OrganizationRole.Owner));

        db.ResetSaveCount();
        object? applied = await Tool(providerB, context, "ApplyAssignAgentSkills").InvokeAsync(
            new AIFunctionArguments { ["agentId"] = agentA.Id, ["skills"] = new[] { skill }, ["confirmationToken"] = token });

        ErrorOf(AsText(applied)).Should().Be("agent not found");
        db.SaveCount.Should().Be(0, "a token from another workspace must never become a write");
        (await db.AgentDefinitions.SingleAsync(a => a.Id == agentA.Id)).AssignedSkillsJson.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // A11 — the provider tools act on PLATFORM rows and nothing else
    // ---------------------------------------------------------------------

    /// <summary>
    /// The platform half of the re-gate: an organization's bring-your-own provider row is not a
    /// platform resource, so the platform-scoped provider tools treat it exactly as they treat a
    /// nonexistent one — the same answer <c>/api/v1/inference-providers</c> gives for it (A9b).
    /// </summary>
    /// <remarks>
    /// Before A11 these tools read <c>_db.FlatProviders()</c> through the tenant filter, which
    /// admits the caller's OWN organization's rows: a platform admin could list, retest, edit or even
    /// DELETE a workspace's bring-your-own provider through a surface that describes itself as
    /// platform-admin CRUD, and promoting a platform row to default cleared the caller's organization's
    /// default for that capability as a side effect of <c>ClearOtherDefaults</c>.
    /// </remarks>
    [Fact]
    public async Task PlatformProviderTools_DoNotReachAnOrganizationsBringYourOwnRow()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(callerId);
        ModelCapabilityView platformRow = SeedProvider(db, "PlatformChat");
        ModelCapabilityView byoRow = SeedProvider(db, "OrgChat", organizationId: org);

        // A real tester, so TestInferenceProvider reaches its provider lookup instead of its earlier
        // "testing is not available in this host" branch — otherwise that entry would be asserting
        // the wrong refusal.
        var provider = CreateProvider(
            db,
            new StubCurrentUser(callerId, () => true, org, OrganizationRole.Owner),
            providerTester: TestBedlessTester(db));
        var context = new AssistantToolContext(callerId);

        // Listing shows the platform row and not the workspace's own.
        string listed = AsText(await Tool(provider, context, "ListInferenceProviders").InvokeAsync(new AIFunctionArguments()));
        listed.Should().Contain("PlatformChat");
        listed.Should().NotContain("OrgChat");

        // Every by-id tool answers for a BYO row exactly as it answers for a nonexistent id.
        var arguments = new AIFunctionArguments
        {
            ["providerId"] = byoRow.Id,
            ["name"] = (string?)null,
            ["kind"] = (string?)null,
            ["endpoint"] = (string?)null,
            ["modelName"] = (string?)null,
            ["apiKey"] = (string?)null,
            ["isDefault"] = (bool?)null,
            ["isEnabled"] = (bool?)null,
            ["timeoutSeconds"] = (int?)null,
            ["capability"] = (string?)null,
            ["personalOAuthMode"] = (bool?)null,
            ["confirmationToken"] = "0000000000000000000000000000000000000000000000000000000000000000"
        };

        foreach (string toolName in new[]
        {
            "ProposeUpdateInferenceProvider", "ApplyUpdateInferenceProvider",
            "ProposeDeleteInferenceProvider", "ApplyDeleteInferenceProvider",
            "TestInferenceProvider"
        })
        {
            db.ResetSaveCount();
            string text = AsText(await Tool(provider, context, toolName).InvokeAsync(arguments));

            ErrorOf(text).Should().Be(
                "inference provider not found",
                $"'{toolName}' is platform-scoped, so another scope's provider is not found there");
            db.SaveCount.Should().Be(0, $"a refused '{toolName}' must not persist anything");
        }

        // The workspace's row is intact and still its own.
        ModelCapabilityView stored = await db.FlatProviders().SingleAsync(p => p.Id == byoRow.Id);
        stored.Name.Should().Be("OrgChat");
        stored.OrganizationId.Should().Be(org);
    }

    /// <summary>
    /// The same rule on the way IN: a platform-managed create must not collide with, or clear the
    /// default of, a workspace's bring-your-own row. Names are unique per owning organization, so the
    /// duplicate check has to be scoped the same way the write is.
    /// </summary>
    [Fact]
    public async Task PlatformProviderTools_LeaveTheOrganizationsOwnProvidersAlone()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid org = TestTenancy.OrgOf(callerId);
        ModelCapabilityView byoDefault = SeedProvider(db, "OrgChat", isDefault: true, organizationId: org);
        ModelCapabilityView platformRow = SeedProvider(db, "PlatformChat");

        var provider = AdminProvider(db, callerId);
        var context = new AssistantToolContext(callerId);

        // A platform row may carry the same NAME as a workspace's row: they are different scopes.
        object? proposed = await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(new AIFunctionArguments
        {
            ["name"] = "OrgChat",
            ["kind"] = "OpenAICompatible",
            ["endpoint"] = PublicEndpoint,
            ["modelName"] = "some-model",
            ["apiKey"] = (string?)null,
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = 30,
            ["capability"] = "Chat",
            ["personalOAuthMode"] = false
        });
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        proposal.RootElement.TryGetProperty("confirmationToken", out JsonElement token)
            .Should().BeTrue($"a name clash with another scope must not block a platform create, but got: {AsText(proposed)}");

        // Promoting the PLATFORM row to default must not clear the workspace's own default.
        await ApplyUpdateAsync(provider, context, db, platformRow.Id, name: null, apiKey: null, isDefault: true);

        (await db.FlatProviders().SingleAsync(p => p.Id == platformRow.Id)).IsDefault.Should().BeTrue();
        (await db.FlatProviders().SingleAsync(p => p.Id == byoDefault.Id)).IsDefault.Should().BeTrue(
            "a platform default is scoped to the platform; the workspace's own default is a different row");

        _ = token;
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    /// <summary>
    /// A real <see cref="InferenceProviderTester"/> over mocked client factories. Only its NOT-FOUND
    /// path is exercised below — the scoped lookup decides before any factory is touched — so the
    /// mocks are never called; what matters is that the tool's "testing is not available in this
    /// host" branch is gone and the by-id assertion is about the SCOPE.
    /// </summary>
    private static InferenceProviderTester TestBedlessTester(InferenceApiDbContext db) =>
        new(db, new FakeSecretProtector(),
            Mock.Of<IChatClientFactory>(),
            Mock.Of<ITranscriptionClientFactory>(),
            Mock.Of<IEmbeddingClientFactory>(),
            Mock.Of<IDecisionClientFactory>(),
            Mock.Of<IVideoGenerationClientFactory>(),
            Mock.Of<ISpeechSynthesisClientFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<InferenceProviderTester>.Instance);

    /// <summary>Runs the propose→apply pair for a single-field provider update.</summary>
    // ---------------------------------------------------------------------
    // The model catalog: providerId on create, the account-level fields on update
    // ---------------------------------------------------------------------

    private static AIFunctionArguments CreateArguments(
        string name, string modelName, Guid? providerId, string? kind = null, string? endpoint = null, string? apiKey = null,
        string capability = "Chat", string? confirmationToken = null)
    {
        AIFunctionArguments args = new()
        {
            ["name"] = name,
            ["kind"] = kind,
            ["endpoint"] = endpoint,
            ["modelName"] = modelName,
            ["apiKey"] = apiKey,
            ["isDefault"] = false,
            ["isEnabled"] = true,
            ["timeoutSeconds"] = (int?)null,
            ["capability"] = capability,
            ["personalOAuthMode"] = false,
            ["providerId"] = providerId
        };
        if (confirmationToken is not null) args["confirmationToken"] = confirmationToken;
        return args;
    }

    private static async Task<string> ProposeTokenAsync(AssistantToolProvider provider, AssistantToolContext context, AIFunctionArguments args)
    {
        string text = AsText(await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(args));
        using JsonDocument proposal = JsonDocument.Parse(text);
        proposal.RootElement.TryGetProperty("confirmationToken", out JsonElement token).Should().BeTrue(text);
        return token.GetString()!;
    }

    [Fact]
    public async Task Create_WithProviderId_AddsTheModelUnderTheExistingAccount_UsingItsEndpointAndKey()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Primary", plaintextKey: "sk-live-key-4321");
        (Guid accountId, _) = db.ParentsOf(seeded.Id);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string token = await ProposeTokenAsync(provider, context, CreateArguments("Primary vision", "vision-model", accountId, capability: "Vision"));
        string applied = AsText(await Tool(provider, context, "ApplyCreateInferenceProvider")
            .InvokeAsync(CreateArguments("Primary vision", "vision-model", accountId, capability: "Vision", confirmationToken: token)));

        applied.Should().Contain("\"created\":true");
        applied.Should().NotContain("sk-live-key-4321");
        (await db.InferenceProviders.CountAsync()).Should().Be(1, "the model joins the existing account");
        ModelCapabilityView added = await db.FlatProviders().SingleAsync(p => p.Name == "Primary vision");
        added.ProviderId.Should().Be(accountId);
        added.Capability.Should().Be(InferenceProviderCapability.Vision);
        added.Endpoint.Should().Be(PublicEndpoint);
        added.ApiKeyEncrypted.Should().Be("ciphertext-of-sk-live-key-4321");
    }

    [Fact]
    public async Task Create_TheConfirmationTokenIsBoundToProviderId()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Primary", plaintextKey: "sk-live-key-4321");
        (Guid accountId, _) = db.ParentsOf(seeded.Id);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        // Every other argument is identical (kind and endpoint may repeat the account's), so only providerId differs.
        // A token minted for a NEW account cannot be spent attaching the model to an existing one...
        string newAccountToken = await ProposeTokenAsync(provider, context,
            CreateArguments("Second", "m2", null, kind: "OpenAICompatible", endpoint: PublicEndpoint));
        db.ResetSaveCount();
        string refused = AsText(await Tool(provider, context, "ApplyCreateInferenceProvider")
            .InvokeAsync(CreateArguments("Second", "m2", accountId, kind: "OpenAICompatible", endpoint: PublicEndpoint, confirmationToken: newAccountToken)));
        ErrorOf(refused).Should().Be(AssistantAdminTools.StaleTokenMessage);

        // ...and the reverse.
        string attachToken = await ProposeTokenAsync(provider, context,
            CreateArguments("Second", "m2", accountId, kind: "OpenAICompatible", endpoint: PublicEndpoint));
        string refusedAgain = AsText(await Tool(provider, context, "ApplyCreateInferenceProvider")
            .InvokeAsync(CreateArguments("Second", "m2", null, kind: "OpenAICompatible", endpoint: PublicEndpoint, confirmationToken: attachToken)));
        ErrorOf(refusedAgain).Should().Be(AssistantAdminTools.StaleTokenMessage);

        db.SaveCount.Should().Be(0);
        (await db.InferenceModels.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Create_WithProviderId_RefusesArgumentsThatBelongToTheAccount_AndAnAccountThatIsNotThePlatforms()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView seeded = SeedProvider(db, "Primary", plaintextKey: "sk-live-key-4321");
        (Guid accountId, _) = db.ParentsOf(seeded.Id);
        ModelCapabilityView byo = SeedProvider(db, "Workspace own", organizationId: TestTenancy.OrgOf(adminId));
        (Guid byoAccountId, _) = db.ParentsOf(byo.Id);
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);
        AIFunction propose = Tool(provider, context, "ProposeCreateInferenceProvider");

        ErrorOf(AsText(await propose.InvokeAsync(CreateArguments("x", "m", accountId, endpoint: "https://8.8.4.4/v1")))).Should().Contain("endpoint");
        ErrorOf(AsText(await propose.InvokeAsync(CreateArguments("x", "m", accountId, apiKey: "sk-other")))).Should().Contain("key");
        ErrorOf(AsText(await propose.InvokeAsync(CreateArguments("x", "m", accountId, kind: "Anthropic")))).Should().Contain("OpenAICompatible");
        ErrorOf(AsText(await propose.InvokeAsync(CreateArguments("x", "m", accountId, capability: "VideoGeneration"))))
            .Should().Contain("VideoGeneration", "the account's kind decides what a model under it may serve");
        ErrorOf(AsText(await propose.InvokeAsync(CreateArguments("x", "m", byoAccountId)))).Should().Be(AssistantAdminTools.ProviderNotFoundMessage);
        ErrorOf(AsText(await propose.InvokeAsync(CreateArguments("x", "m", Guid.NewGuid())))).Should().Be(AssistantAdminTools.ProviderNotFoundMessage);
    }

    [Fact]
    public async Task Update_TheKindBelongsToTheAccount_SoItCannotChangeWhileOtherModelsShareIt()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        ModelCapabilityView first = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            ProviderName = "Shared",
            Name = "First",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "m1"
        };
        ModelCapabilityView second = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            ProviderName = "Shared",
            Name = "Second",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "m2"
        };
        db.AddFlat(first, second);
        db.SaveChanges();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string refused = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(first.Id, kind: "DeepSeek")));

        ErrorOf(refused).Should().Contain("\"Second\"");
        (await db.InferenceProviders.SingleAsync()).Kind.Should().Be(InferenceProviderKind.OpenAICompatible);
    }

    [Fact]
    public async Task Update_AnEndpointOrKeyChange_NamesTheOtherModelsOfTheAccountInTheSummary()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        ModelCapabilityView first = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            ProviderName = "Shared",
            Name = "First",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "m1"
        };
        ModelCapabilityView second = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            ProviderName = "Shared",
            Name = "Second",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Embedding,
            Endpoint = PublicEndpoint,
            ModelName = "m2"
        };
        db.AddFlat(first, second);
        db.SaveChanges();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        using JsonDocument keyChange = JsonDocument.Parse(AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(first.Id, apiKey: "sk-rotated-0000"))));
        keyChange.RootElement.GetProperty("summary").GetString().Should().Contain("\"Second\"");
        keyChange.RootElement.GetProperty("alsoAffectsModels").EnumerateArray().Select(e => e.GetString()).Should().Equal("Second");

        using JsonDocument defaultOnly = JsonDocument.Parse(AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(first.Id, isDefault: true))));
        defaultOnly.RootElement.GetProperty("alsoAffectsModels").GetArrayLength().Should().Be(0, "a capability-level change touches nothing else");

        // Switching ON also switches the account on, which brings back every model under it.
        using JsonDocument switchedOn = JsonDocument.Parse(AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(first.Id, isEnabled: true))));
        switchedOn.RootElement.GetProperty("alsoAffectsModels").EnumerateArray().Select(e => e.GetString()).Should().Equal("Second");
        switchedOn.RootElement.GetProperty("summary").GetString().Should().Contain("\"Second\"");
        using JsonDocument switchedOff = JsonDocument.Parse(AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(first.Id, isEnabled: false))));
        switchedOff.RootElement.GetProperty("alsoAffectsModels").GetArrayLength().Should().Be(0, "switching off touches only this capability");

        // The kind is the ACCOUNT's too: setting it names the account's other models like any account-level change.
        using JsonDocument kindSet = JsonDocument.Parse(AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider")
            .InvokeAsync(UpdateArguments(first.Id, kind: nameof(InferenceProviderKind.OpenAICompatible)))));
        kindSet.RootElement.GetProperty("alsoAffectsModels").EnumerateArray().Select(e => e.GetString()).Should().Equal("Second");
        kindSet.RootElement.GetProperty("summary").GetString().Should().Contain("\"Second\"");
    }

    [Fact]
    public async Task Names_and_model_codes_longer_than_200_characters_are_refused_in_plain_words()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView existing = new()
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "m1"
        };
        db.AddFlat(existing);
        db.SaveChanges();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);
        string tooLong = new('x', 201);
        string longest = new('y', 200);

        string longName = AsText(await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(
            CreateArguments(tooLong, "m", null, kind: "OpenAICompatible", endpoint: PublicEndpoint)));
        string longCode = AsText(await Tool(provider, context, "ProposeCreateInferenceProvider").InvokeAsync(
            CreateArguments("Fine", tooLong, null, kind: "OpenAICompatible", endpoint: PublicEndpoint)));
        string longRename = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(existing.Id, name: tooLong)));
        string longModel = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(existing.Id, modelName: tooLong)));
        string atTheLimit = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(existing.Id, name: longest, modelName: longest)));

        ErrorOf(longName).Should().Contain("200");
        ErrorOf(longCode).Should().Contain("200");
        ErrorOf(longRename).Should().Contain("200");
        ErrorOf(longModel).Should().Contain("200");
        atTheLimit.Should().Contain("confirmationToken", "200 characters is still allowed");
    }

    [Fact]
    public async Task A_video_row_that_already_names_a_talking_head_can_be_edited_with_its_values_re_sent_but_not_switched_to_one()
    {
        const string talking = "minimax/h3-max/lip-sync/image-to-video";
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ModelCapabilityView stale = new()
        {
            Id = Guid.NewGuid(),
            Name = "Stale",
            Kind = InferenceProviderKind.FalAi,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://queue.fal.run",
            ModelName = talking
        };
        ModelCapabilityView plain = new()
        {
            Id = Guid.NewGuid(),
            Name = "Plain",
            Kind = InferenceProviderKind.FalAi,
            Capability = InferenceProviderCapability.VideoGeneration,
            Endpoint = "https://queue.fal.run",
            ModelName = "fal-ai/kling-video/v3/turbo/pro/image-to-video"
        };
        db.AddFlat(stale);
        db.AddFlat(plain);
        db.SaveChanges();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        string resent = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(stale.Id, name: "Renamed", kind: "FalAi", modelName: talking, capability: "VideoGeneration")));
        string switched = AsText(await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(
            UpdateArguments(plain.Id, modelName: talking)));

        resent.Should().Contain("confirmationToken", "nothing changed that the model rule judges");
        ErrorOf(switched).Should().Contain("Lip-sync");
    }

    [Fact]
    public async Task Delete_RemovesTheCapability_ThenTheModelAndTheAccountOnlyOnceEmpty()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        Guid accountId = Guid.NewGuid();
        Guid modelId = Guid.NewGuid();
        ModelCapabilityView chat = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            ModelId = modelId,
            ProviderName = "Shared",
            Name = "Shared model",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Chat,
            Endpoint = PublicEndpoint,
            ModelName = "m1"
        };
        ModelCapabilityView sibling = new()
        {
            Id = Guid.NewGuid(),
            ProviderId = accountId,
            ProviderName = "Shared",
            Name = "Other model",
            Kind = InferenceProviderKind.OpenAICompatible,
            Capability = InferenceProviderCapability.Embedding,
            Endpoint = PublicEndpoint,
            ModelName = "m2"
        };
        db.AddFlat(chat, sibling);
        db.SaveChanges();
        var provider = AdminProvider(db, adminId);
        var context = new AssistantToolContext(adminId);

        async Task DeleteAsync(Guid id)
        {
            using JsonDocument proposal = JsonDocument.Parse(AsText(await Tool(provider, context, "ProposeDeleteInferenceProvider")
                .InvokeAsync(new AIFunctionArguments { ["providerId"] = id })));
            string token = proposal.RootElement.GetProperty("confirmationToken").GetString()!;
            AsText(await Tool(provider, context, "ApplyDeleteInferenceProvider")
                .InvokeAsync(new AIFunctionArguments { ["providerId"] = id, ["confirmationToken"] = token })).Should().Contain("\"deleted\":true");
        }

        await DeleteAsync(chat.Id);
        (await db.InferenceModels.CountAsync()).Should().Be(1, "the emptied model goes");
        (await db.InferenceProviders.CountAsync()).Should().Be(1, "the account still holds the other model");

        await DeleteAsync(sibling.Id);
        (await db.InferenceModels.CountAsync()).Should().Be(0);
        (await db.InferenceProviders.CountAsync()).Should().Be(0, "the account goes once it has no model left");
    }

    [Fact]
    public async Task ApplyCreate_PrePricesTheNewPlatformCapabilityFromThePriceBook()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid adminId = Guid.NewGuid();
        ICurrentUser admin = new StubCurrentUser(adminId, () => true, TestTenancy.OrgOf(adminId), OrganizationRole.Owner);
        IConfirmationTokenService tokens = ConfirmationTokenService.CreateEphemeral();
        AssistantAdminTools tools = new(db, tokens, admin, new FakeSecretProtector(),
            priceBook: new ReelBolt.Inference.Api.Services.Billing.PriceBook(db));
        AssistantToolContext context = new(adminId);
        AIFunction Fn(string name) => tools.GetTools(context).Single(t => t.Name == name);

        AIFunctionArguments args = CreateArguments("Opus", "claude-opus-5-5", null, kind: "Anthropic", endpoint: "https://8.8.8.8", apiKey: "sk-ant-0000");
        using JsonDocument proposal = JsonDocument.Parse(AsText(await Fn("ProposeCreateInferenceProvider").InvokeAsync(args)));
        args["confirmationToken"] = proposal.RootElement.GetProperty("confirmationToken").GetString();
        AsText(await Fn("ApplyCreateInferenceProvider").InvokeAsync(args)).Should().Contain("\"created\":true");

        Guid capabilityId = (await db.FlatProviders().SingleAsync()).Id;
        (await db.ModelCapabilityPrices.Where(p => p.ModelCapabilityId == capabilityId).Select(p => p.Unit).ToListAsync())
            .Should().Contain([UsageKind.LlmInputTokens, UsageKind.LlmOutputTokens]);
    }

    private static async Task ApplyUpdateAsync(
        AssistantToolProvider provider, AssistantToolContext context, InferenceApiDbContext db,
        Guid providerId, string? name, string? apiKey, bool? isDefault = null)
    {
        var proposeArgs = new AIFunctionArguments
        {
            ["providerId"] = providerId,
            ["name"] = name,
            ["kind"] = (string?)null,
            ["endpoint"] = (string?)null,
            ["modelName"] = (string?)null,
            ["apiKey"] = apiKey,
            ["isDefault"] = isDefault,
            ["isEnabled"] = (bool?)null,
            ["timeoutSeconds"] = (int?)null,
            ["capability"] = (string?)null,
            ["personalOAuthMode"] = (bool?)null
        };

        object? proposed = await Tool(provider, context, "ProposeUpdateInferenceProvider").InvokeAsync(proposeArgs);
        using JsonDocument proposal = JsonDocument.Parse(AsText(proposed));
        proposal.RootElement.TryGetProperty("confirmationToken", out JsonElement tokenElement)
            .Should().BeTrue($"the update must be proposable, but got: {AsText(proposed)}");

        await Tool(provider, context, "ApplyUpdateInferenceProvider").InvokeAsync(new AIFunctionArguments
        {
            ["providerId"] = providerId,
            ["name"] = name,
            ["kind"] = (string?)null,
            ["endpoint"] = (string?)null,
            ["modelName"] = (string?)null,
            ["apiKey"] = apiKey,
            ["isDefault"] = isDefault,
            ["isEnabled"] = (bool?)null,
            ["timeoutSeconds"] = (int?)null,
            ["capability"] = (string?)null,
            ["personalOAuthMode"] = (bool?)null,
            ["confirmationToken"] = tokenElement.GetString()!
        });
    }

    // -- A minimal IL walker, for the "first statement is the gate" assertion --------

    private static readonly OpCode[] SingleByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] DoubleByteOpCodes = new OpCode[0x100];

    static AssistantAdminToolTests()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
            {
                continue;
            }

            ushort value = unchecked((ushort)op.Value);
            if (value < 0x100)
            {
                SingleByteOpCodes[value] = op;
            }
            else if ((value & 0xff00) == 0xfe00)
            {
                DoubleByteOpCodes[value & 0xff] = op;
            }
        }
    }

    /// <summary>
    /// The name of the first method this body <em>calls</em>, or <c>null</c> if it calls none. Used
    /// to prove a tool's gate is the first thing it does rather than merely somewhere in it.
    /// </summary>
    private static string? FirstCalledMethodName(MethodInfo method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return null;
        }

        int position = 0;
        while (position < il.Length)
        {
            OpCode op;
            if (il[position] == 0xfe)
            {
                op = DoubleByteOpCodes[il[position + 1]];
                position += 2;
            }
            else
            {
                op = SingleByteOpCodes[il[position]];
                position += 1;
            }

            if (op == OpCodes.Call || op == OpCodes.Callvirt)
            {
                int token = BitConverter.ToInt32(il, position);
                MethodBase? target = method.Module.ResolveMethod(token, null, null);
                return target?.Name;
            }

            position += OperandSize(op.OperandType, il, position);
        }

        return null;
    }

    private static int OperandSize(OperandType type, byte[] il, int position) => type switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineI or OperandType.ShortInlineVar or OperandType.ShortInlineBrTarget => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI or OperandType.InlineBrTarget or OperandType.InlineMethod
            or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok
            or OperandType.InlineString or OperandType.InlineSig or OperandType.ShortInlineR => 4,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
        _ => 0
    };
}

/// <summary>
/// A reversible in-memory <see cref="ISecretProtector"/>. Reversible on purpose: a test that asserts
/// the plaintext key is absent from a result is only meaningful if the stored value provably is not
/// the plaintext, and the redaction test also greps for the ciphertext itself.
/// </summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    private const string Prefix = "ciphertext-of-";

    public string Protect(string plaintext) => Prefix + plaintext;

    public bool TryUnprotect(string? ciphertext, out string plaintext)
    {
        plaintext = string.Empty;
        if (string.IsNullOrEmpty(ciphertext) || !ciphertext.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        plaintext = ciphertext[Prefix.Length..];
        return true;
    }
}
