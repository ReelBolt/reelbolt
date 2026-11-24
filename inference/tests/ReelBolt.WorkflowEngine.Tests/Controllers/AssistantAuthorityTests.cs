using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;
using AssistantToolTestFactory = ReelBolt.WorkflowEngine.Tests.Agents.AssistantToolTestFactory;

// NAMESPACE NOTE (T-QA-02): this file lives in Controllers/ per the plan even though its subject is
// Agents/Tools/AssistantToolProvider.cs, so its namespace follows its DIRECTORY NEIGHBOURS
// (AssistantChatControllerTests, AssistantThreadsControllerTests, ...) rather than its subject — the
// test assembly is organised by folder and a lone file in another namespace would read as a mistake.
// The one consequence worth knowing: the class below is named AssistantAuthorityTests and so is the
// class in Agents/AssistantAuthorityTests.cs. They are DIFFERENT TYPES in different namespaces and
// both run. The alias import above exists precisely so the Agents namespace is never imported
// wholesale — a blanket `using ...Tests.Agents;` would make a bare reference to
// "AssistantAuthorityTests" ambiguous.
namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// T-QA-02 / plan Task 013 Step 2 — the authority MATRIX, driven off the provider's OWN tool
/// registration rather than off a list written here.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this adds over the tests that already exist.</b> <c>Agents/AssistantAuthorityTests.cs</c>
/// and <c>Agents/AssistantAdminToolTests.cs</c> already pin the same two axes for particular shapes;
/// this file pins them as MATRICES over the whole registered set, and adds the property neither of
/// them covers — that the <em>project</em> axis is enumerated from the registration too. The overlap
/// is deliberate and called out per test below rather than hidden; nothing here replaces, weakens or
/// copies verbatim what those files assert, and the two authoritative per-name theories there remain
/// the primary coverage.
/// </para>
/// <para>
/// <b>Observed inventory at the frozen head</b> (<c>35cd0a18</c>): the provider registers <b>34</b>
/// tools, of which <b>11</b> are admin-gated, and <b>19</b> answer the <c>"no project in context"</c>
/// refusal — i.e. 19 are project-scoped under the behavioural definition this file uses (see
/// <see cref="ProjectScopedToolsAsync"/>). Those numbers are asserted below as a tripwire so the
/// inventory this matrix was reviewed against cannot drift silently; the authoritative NAME-by-name
/// inventory lives in
/// <c>Agents/AssistantAgentToolScopeTests.AssistantToolProvider_GrantsExactlyTheClosedToolSet</c>.
/// </para>
/// <para>
/// <b>The property QA exists to defend.</b> A matrix over a hardcoded list cannot catch a tool added
/// LATER that forgets its gate or its ownership re-check; a matrix read from
/// <c>GetTools(context)</c> / <c>GetAdminToolNames(context)</c> can. That is why no test below
/// contains a list of tool names at all, and why the project-scoped set is discovered by BEHAVIOUR
/// rather than declared.
/// </para>
/// </remarks>
public class AssistantAuthorityTests
{
    /// <summary>
    /// The exact refusal a project-scoped tool returns when the caller has no usable project — the
    /// literal the existing tests assert and the literal the model is shown. Deliberately a literal
    /// rather than a reference to <c>AssistantProjectTools.NoProjectMessage</c>: a test that read the
    /// constant would keep passing if someone changed the wire text the model actually receives.
    /// </summary>
    private const string NoProjectMessage = "no project in context";

    /// <summary>The refusal every admin-gated tool returns to a caller whose live claim is not admin.</summary>
    private const string AdminRequiredMessage = "admin privileges required";

    // ---------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------

    /// <summary>
    /// Counts <c>SaveChanges</c>/<c>SaveChangesAsync</c> so a matrix can assert a call wrote NOTHING,
    /// rather than only inspecting the response string it handed back. A refusal that returns an
    /// error <em>after</em> writing is still a privilege-escalation bug.
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
    /// An <see cref="ICurrentUser"/> whose TWO authorities (A11) are MUTABLE PROPERTIES, not counters:
    /// the platform claim, and the caller's role in the active workspace. The matrices below flip them
    /// between calls on one shared provider instance, so the property under test is exactly "authority
    /// is read at call time, per call" — for either authority.
    /// </summary>
    private sealed class MutableCurrentUser(Guid userId) : ICurrentUser
    {
        public bool Admin { get; set; }

        public ReelBolt.Shared.Tenancy.OrganizationRole? Role { get; set; } =
            ReelBolt.Shared.Tenancy.OrganizationRole.Member;

        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;

        public bool IsPlatformAdmin => Admin;

        public System.Guid? OrganizationId => ReelBolt.WorkflowEngine.Tests.TestTenancy.OrgOf(UserId);

        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => Role;

        /// <summary>Neither authority: the starting state of every refusal assertion below.</summary>
        public void GrantNothing()
        {
            Admin = false;
            Role = ReelBolt.Shared.Tenancy.OrganizationRole.Member;
        }

        /// <summary>Both authorities at once — what "a caller who is allowed" means here.</summary>
        public void GrantEverything()
        {
            Admin = true;
            Role = ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
        }
    }

    private static AssistantToolProvider CreateProvider(InferenceApiDbContext db, ICurrentUser currentUser) =>
        AssistantToolTestFactory.CreateProvider(db, currentUser);

    private static Project SeedProject(InferenceApiDbContext db, Guid ownerId, string name)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"{name} description",
            OwnerId = ownerId,
            OrganizationId = TestTenancy.OrgOf(ownerId),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Projects.Add(project);
        db.SaveChanges();
        return project;
    }

    private static ProjectFile SeedFile(InferenceApiDbContext db, Guid projectId, string fileName)
    {
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = fileName,
            OriginalPath = fileName,
            StorageKey = $"projects/{projectId}/userFiles/{fileName}",
            StorageBucket = "bucket",
            MimeType = "text/markdown",
            SizeBytes = 5,
            UploadedAt = DateTime.UtcNow
        };
        db.ProjectFiles.Add(file);
        db.SaveChanges();
        return file;
    }

    /// <summary>
    /// The text an <see cref="AIFunction"/> invocation produced, whether the runtime handed back a
    /// JSON string, a <see cref="JsonElement"/> or a materialized object.
    /// </summary>
    private static string AsText(object? raw) => raw switch
    {
        null => string.Empty,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString()!,
        JsonElement element => element.GetRawText(),
        _ => raw.ToString()!
    };

    /// <summary>
    /// The <c>error</c> field of a JSON-object result, or <c>null</c> when the result is not a JSON
    /// object carrying a string <c>error</c> — so <c>null</c> means "this call did not refuse".
    /// </summary>
    private static string? ErrorOf(string text)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // A non-JSON result cannot be a refusal.
        }

        return null;
    }

    private static string Truncate(string text) =>
        text.Length <= 160 ? text : text[..160] + "…";

    // ---------------------------------------------------------------------
    // Arguments, synthesised from each tool's OWN JSON schema
    // ---------------------------------------------------------------------

    /// <summary>
    /// A well-typed argument set for <paramref name="tool"/>, built from the JSON schema
    /// <c>AIFunctionFactory</c> published for that very tool — so this file needs no per-tool recipe
    /// table anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This matters for the same reason the matrices do: a hand-written recipe map is a second list
    /// that can silently omit a newly added tool.
    /// <c>Agents/AssistantAdminToolTests.ArgumentsFor</c> is such a map, and documents its own
    /// limitation (it ends with a <c>_ =&gt; throw</c> arm plus a guard theory purely so that an
    /// omission fails by name). Reading the schema removes the list entirely.
    /// </para>
    /// <para>
    /// The values are chosen to exercise the <em>fallback</em> path as well as being inert: a
    /// parameter whose schema admits <c>null</c> is passed <c>null</c>, which is what makes the
    /// project tools fall back to <em>this invocation's</em> context project.
    /// <c>AssistantProjectTools.ResolveProject</c> consults the context only when no explicit id was
    /// supplied; an explicit non-empty id would yield "project not found" instead of the
    /// "no project in context" this file is about.
    /// </para>
    /// </remarks>
    private static AIFunctionArguments ArgumentsFor(AIFunction tool)
    {
        var arguments = new AIFunctionArguments();

        JsonElement schema = tool.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("required", out JsonElement required)
            || required.ValueKind != JsonValueKind.Array)
        {
            // No required parameters: the empty set is the only well-typed call.
            return arguments;
        }

        JsonElement properties = schema.TryGetProperty("properties", out JsonElement props)
            ? props
            : default;

        foreach (JsonElement nameElement in required.EnumerateArray())
        {
            string name = nameElement.GetString()!;
            JsonElement propertySchema = properties.ValueKind == JsonValueKind.Object
                && properties.TryGetProperty(name, out JsonElement declared)
                    ? declared
                    : default;

            arguments[name] = InertValueFor(propertySchema);
        }

        return arguments;
    }

    private static object? InertValueFor(JsonElement propertySchema)
    {
        if (propertySchema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Nullable => pass null, so the tool takes its "not supplied" branch (see ArgumentsFor).
        if (IsNullable(propertySchema))
        {
            return null;
        }

        return DeclaredType(propertySchema) switch
        {
            "array" => JsonSerializer.SerializeToElement(Array.Empty<object>()),
            "boolean" => JsonSerializer.SerializeToElement(false),
            "integer" => JsonSerializer.SerializeToElement(1),
            "string" => JsonSerializer.SerializeToElement(
                LooksLikeAGuid(propertySchema) ? Guid.NewGuid().ToString() : "probe"),
            _ => null
        };
    }

    private static bool IsNullable(JsonElement propertySchema) =>
        propertySchema.TryGetProperty("type", out JsonElement type)
        && ((type.ValueKind == JsonValueKind.Array
                && type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "null"))
            || (type.ValueKind == JsonValueKind.String && type.GetString() == "null"));

    private static string? DeclaredType(JsonElement propertySchema)
    {
        if (!propertySchema.TryGetProperty("type", out JsonElement type))
        {
            return null;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            return type.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString())
                .FirstOrDefault(t => t is not null and not "null");
        }

        return null;
    }

    private static bool LooksLikeAGuid(JsonElement propertySchema) =>
        propertySchema.TryGetProperty("format", out JsonElement format)
        && format.ValueKind == JsonValueKind.String
        && format.GetString() == "uuid";

    // ---------------------------------------------------------------------
    // AXIS 1 — the admin gate, exhaustive over the gated set (matrix)
    // ---------------------------------------------------------------------

    /// <summary>
    /// The whole ADMIN AXIS in one pass: for every name the provider reports as admin-gated, a
    /// non-admin caller is refused AND writes nothing; and the SAME provider instance, with the live
    /// claim flipped to admin, is NOT refused — so a tool that refuses unconditionally cannot pass
    /// the first half by accident.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Overlap, stated plainly.</b> Both halves are independently covered today by
    /// <c>Agents/AssistantAdminToolTests.AdminTool_RefusesANonAdmin_AndWritesNothing</c> (per-name
    /// theory: refusal + <c>SaveCount</c> + change tracker) and
    /// <c>Agents/AssistantAdminToolTests.AdminTool_ReadsIsAdminOnEveryCall_NotOnceAtConstruction</c>
    /// (per-name theory: non-admin then admin on one instance). Those theories are untouched and
    /// remain the authoritative per-name coverage; nothing here replaces or weakens them. What this
    /// matrix adds is the shape the ticket asks for — a single pass that reports the WHOLE set at
    /// once and pins the inventory counts, so "how many admin tools are there, and were they all
    /// checked?" is answerable from one test rather than inferred from a theory's case count.
    /// </para>
    /// <para>
    /// <b>Why the positive control is load-bearing.</b> A tool that refuses unconditionally — a gate
    /// reading a constant, an early <c>return</c> left in during debugging — passes the refusal half
    /// perfectly. Only flipping the same instance to admin and requiring the refusal to disappear
    /// catches it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AdminAxis_EveryGatedToolRefusesANonAdmin_WritesNothing_AndServesAnAdmin()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();

        // A project the caller owns, so no project-scoped tool can be refused for the wrong reason
        // while the admin axis is being measured.
        Project project = SeedProject(db, callerId, "Owned");

        var currentUser = new MutableCurrentUser(callerId);
        currentUser.GrantNothing();
        AssistantToolProvider provider = CreateProvider(db, currentUser);
        var context = new AssistantToolContext(callerId, project.Id);

        IReadOnlyList<string> registered = provider.GetTools(context).Select(t => t.Name).ToList();
        IReadOnlyList<string> gated = provider.GetAdminToolNames(context);

        // Guards on the guard: an empty or total gate set would make every assertion below vacuous.
        registered.Should().NotBeEmpty(
            "the matrix is derived from the registration, so an empty registration would check nothing");
        registered.Should().OnlyHaveUniqueItems();
        gated.Should().NotBeEmpty("the provider must be able to enumerate its admin-gated subset");
        gated.Should().OnlyHaveUniqueItems();
        gated.Should().BeSubsetOf(registered,
            "every gated name must also be a registered name — GetAdminToolNames intersects the two");
        gated.Count.Should().BeLessThan(registered.Count,
            "the admin surface is a part of the tool set, not all of it");

        // The inventory this matrix was reviewed against at the frozen head. Asserting the exact
        // numbers is a TRIPWIRE, not a second copy of the name-by-name closed-set assertion in
        // Agents/AssistantAgentToolScopeTests: a new tool — gated or not — should be a conscious act
        // that shows up here as a failing count, prompting the author to confirm it landed in the
        // right surface and is covered by this matrix. Bump these only alongside that confirmation.
        registered.Count.Should().Be(152, "this context has no chat thread, so the chat-only WatchExecution is not registered: 152 here, 153 with a thread (the cleanup pair, the voice tools, the twelve avatar tools, the two video event read tools and the six project-history tools and GetMusicBeats included)");
        gated.Count.Should().Be(13, "exactly 13 registered tools are admin-gated at this commit");

        var failures = new List<string>();

        foreach (string toolName in gated)
        {
            AIFunction MakeTool() => provider.GetTools(context).Single(t => t.Name == toolName);
            AIFunctionArguments arguments = ArgumentsFor(MakeTool());

            // -- Half 1: neither authority => the refusal, and inert --------------------
            currentUser.GrantNothing();
            db.ResetSaveCount();
            string refusedText = AsText(await MakeTool().InvokeAsync(arguments));

            if (ErrorOf(refusedText) != AdminRequiredMessage)
            {
                failures.Add(
                    $"'{toolName}': a non-admin got '{Truncate(refusedText)}' instead of the "
                    + $"'{AdminRequiredMessage}' refusal");
            }

            if (db.SaveCount != 0)
            {
                failures.Add($"'{toolName}': a refused call wrote to the database ({db.SaveCount} SaveChanges)");
            }

            int dirty = db.ChangeTracker.Entries()
                .Count(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);

            if (dirty != 0)
            {
                failures.Add($"'{toolName}': a refused call left {dirty} entr(ies) dirty in the change tracker");
            }

            // -- Half 2: exactly ONE authority governs each tool (A11) -------------------
            // The same provider instance, the same arguments, the same live caller object: the only
            // thing that changes between these three calls is WHICH authority is held. A tool must
            // ignore the authority that does not govern it — a platform admin is not an admin of the
            // workspace they are sitting in, and a workspace Owner is not a platform admin.
            currentUser.Admin = true;
            currentUser.Role = ReelBolt.Shared.Tenancy.OrganizationRole.Member;
            bool servedByPlatformClaimOnly =
                ErrorOf(AsText(await MakeTool().InvokeAsync(arguments))) != AdminRequiredMessage;

            currentUser.Admin = false;
            currentUser.Role = ReelBolt.Shared.Tenancy.OrganizationRole.Owner;
            bool servedByOrgRoleOnly =
                ErrorOf(AsText(await MakeTool().InvokeAsync(arguments))) != AdminRequiredMessage;

            if (servedByPlatformClaimOnly == servedByOrgRoleOnly)
            {
                failures.Add(
                    $"'{toolName}': exactly one of the two authorities must govern it, but with the "
                    + $"platform claim alone it was {(servedByPlatformClaimOnly ? "served" : "refused")} "
                    + $"and with the workspace Owner role alone it was {(servedByOrgRoleOnly ? "served" : "refused")}"
                    + (servedByPlatformClaimOnly
                        ? " — it appears to answer to BOTH, so the two authorities are not independent"
                        : " — it answers to NEITHER, so its gate is unconditional or reads something else"));
            }

            // Both together must always serve it, so the refusals above are about the authority and
            // not about a tool that refuses unconditionally.
            currentUser.GrantEverything();
            string allowedText = AsText(await MakeTool().InvokeAsync(arguments));

            if (ErrorOf(allowedText) == AdminRequiredMessage)
            {
                failures.Add(
                    $"'{toolName}': the refusal was still returned with BOTH authorities held — the "
                    + "gate is unconditional, not a gate");
            }
        }

        failures.Should().BeEmpty(
            "every admin-gated tool must refuse a caller with no authority inertly, answer to exactly "
            + "one of the two authorities, and serve a caller who holds both, on one provider "
            + "instance:\n" + string.Join("\n", failures));

        // The positive-control half must not have quietly mutated anything either: every gated tool
        // was called with this file's inert probe arguments, none of which is a valid write.
        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("the admin matrix's probe arguments must not perform any write");
    }

    // ---------------------------------------------------------------------
    // AXIS 2 — the project axis, ENUMERATED from the registration
    // ---------------------------------------------------------------------

    /// <summary>
    /// Discovers which registered tools are project-scoped <b>behaviourally</b>, from the
    /// registration, instead of from a hand-written list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How "project-scoped" is identified, and why this definition.</b> A tool is project-scoped
    /// here iff, invoked through its own <see cref="AIFunction"/> with a context naming a project the
    /// caller does NOT own and with every nullable parameter passed as <c>null</c>, it answers the
    /// <c>"no project in context"</c> refusal. That is not a proxy for the property under test — it
    /// <em>is</em> the property: the rule is that a caller with no usable project gets that one
    /// error and never a distinct "forbidden", so a tool that resolved ownership any other way would
    /// simply not appear in the set (and the set would be visibly smaller than the 19 observed).
    /// </para>
    /// <para>
    /// The caller holds no authority on purpose, so the 13 gated tools refuse at their first statement
    /// and the probe cannot reach any of their bodies. The probe is therefore inherently inert —
    /// which the tests below assert rather than assume.
    /// </para>
    /// </remarks>
    private static async Task<List<string>> ProjectScopedToolsAsync(
        AssistantToolProvider provider, AssistantToolContext context)
    {
        var projectScoped = new List<string>();

        foreach (AIFunction tool in provider.GetTools(context))
        {
            string text = AsText(await tool.InvokeAsync(ArgumentsFor(tool)));
            if (ErrorOf(text) == NoProjectMessage)
            {
                projectScoped.Add(tool.Name);
            }
        }

        return projectScoped;
    }

    [Fact]
    public async Task ProjectAxis_IsEnumeratedFromTheRegistration_NotFromAListInThisFile()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid strangerId = Guid.NewGuid();
        Project theirs = SeedProject(db, strangerId, "Somebody Elses Project");

        var currentUser = new MutableCurrentUser(callerId) { Admin = false };
        AssistantToolProvider provider = CreateProvider(db, currentUser);
        var context = new AssistantToolContext(callerId, theirs.Id);

        IReadOnlyList<string> registered = provider.GetTools(context).Select(t => t.Name).ToList();

        db.ResetSaveCount();
        List<string> projectScoped = await ProjectScopedToolsAsync(provider, context);

        // The probe invokes every registered tool, so if it wrote anything the axis tests below
        // would be measuring a mutated database.
        db.SaveCount.Should().Be(0, "discovering the project-scoped set must not write anything");

        projectScoped.Should().NotBeEmpty(
            "an empty project-scoped set would make the axis below vacuous, and would mean the probe "
            + "stopped recognising the refusal rather than that no tool is project-scoped");
        projectScoped.Should().OnlyHaveUniqueItems();
        projectScoped.Should().BeSubsetOf(registered);
        projectScoped.Count.Should().BeLessThan(registered.Count,
            "not every tool is project-scoped — a set equal to the registration would mean the probe "
            + "is matching something other than the project refusal");

        // The count reviewed at this commit. Same tripwire rationale as the admin counts above.
        projectScoped.Count.Should().Be(103, "103 registered tools answer the project refusal at this commit (ListAvatarLibrary and the ten avatar and object pair tools, which resolve the project before the avatar switches; the two video event read tools, WritePostText, the six project-history tools, which resolve the project before validating scope, plus 30 + the stop-run pair, the save-pronunciation pair, the three change-request tools, the output, LookAtOutput, upload, import, run-waiting, revision, patch, clone, picture-document, green-screen, media-overlay, object-effect and clean-audio tools, and the four recipe tools, and the two marketplace copy tools (Propose and Apply); plus the two sound-effect add tools (Propose and Apply; SearchSoundEffects needs no project); plus GetMusicBeats; plus the social schedule pair; ApplyUploadFile takes its project from its ticket, so it answers a bad ticket instead)");

        // Anchors, so the probe cannot silently degenerate into matching the wrong string and still
        // look non-empty. These three are core tools whose project scoping is the whole point of the
        // "no project in context" contract; a change to any of them should be a deliberate act.
        projectScoped.Should().Contain(new[] { "ListAgents", "ListProjectFiles", "ValidateWorkflow" });
    }

    [Fact]
    public async Task ProjectAxis_NonOwnerIsRefusedWithTheNoProjectError_AndAnOwnerIsNot_ForEveryProjectScopedTool()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Guid strangerId = Guid.NewGuid();

        // The stranger's project carries a marker string, so a refusal that leaked its contents — or
        // confirmed it exists — is caught by content as well as by the error message.
        Project theirs = SeedProject(db, strangerId, "Strangers Project");
        SeedFile(db, theirs.Id, "stranger-secret.mp4");

        // The caller's own project, for the positive control.
        Project mine = SeedProject(db, callerId, "My Project");
        SeedFile(db, mine.Id, "my-notes.md");

        var currentUser = new MutableCurrentUser(callerId) { Admin = false };
        AssistantToolProvider provider = CreateProvider(db, currentUser);

        var strangerContext = new AssistantToolContext(callerId, theirs.Id);

        db.ResetSaveCount();
        List<string> projectScoped = await ProjectScopedToolsAsync(provider, strangerContext);
        projectScoped.Should().NotBeEmpty();

        var failures = new List<string>();

        foreach (string toolName in projectScoped)
        {
            // ---- NON-OWNER: the same error a nonexistent project gets, and nothing leaked ----
            AIFunction strangerTool = provider.GetTools(strangerContext).Single(t => t.Name == toolName);
            string refusedText = AsText(await strangerTool.InvokeAsync(ArgumentsFor(strangerTool)));

            string? error = ErrorOf(refusedText);
            if (error != NoProjectMessage)
            {
                failures.Add(
                    $"'{toolName}': a caller pointing at a stranger's project got "
                    + $"{(error is null ? "no error at all" : $"'{Truncate(error)}'")} instead of "
                    + $"'{NoProjectMessage}' — an owner-only refusal must be indistinguishable from "
                    + "'does not exist'");
            }

            if (refusedText.Contains("Strangers Project", StringComparison.Ordinal)
                || refusedText.Contains("stranger-secret.mp4", StringComparison.Ordinal))
            {
                failures.Add($"'{toolName}': the refusal leaked content from the stranger's project");
            }

            // ---- OWNER: the SAME tool, same context shape, must NOT be refused that way ----
            var ownerContext = new AssistantToolContext(callerId, mine.Id);
            AIFunction ownerTool = provider.GetTools(ownerContext).Single(t => t.Name == toolName);
            string ownedText = AsText(await ownerTool.InvokeAsync(ArgumentsFor(ownerTool)));

            if (ErrorOf(ownedText) == NoProjectMessage)
            {
                failures.Add(
                    $"'{toolName}': an OWNER of the context project was still refused with "
                    + $"'{NoProjectMessage}' — the ownership re-check is over-blocking, not checking");
            }
        }

        failures.Should().BeEmpty(
            "every project-scoped tool must treat \"not yours\" exactly like \"does not exist\", and "
            + "must serve the owner:\n" + string.Join("\n", failures));

        db.SaveCount.Should().Be(0, "the project axis must be measurable without any write");
    }

    /// <summary>
    /// The stronger positive control for the four project-scoped READ tools that need no extra id:
    /// for an owned project they return genuine, non-error JSON — an array or an object — rather than
    /// merely "some other error". A gate that allowed the call through but then failed internally
    /// would pass the weaker "not the refusal" check above and fail here.
    /// </summary>
    /// <remarks>
    /// The four names are the ones <see cref="ProjectScopedToolsAsync"/> discovers whose schema has
    /// no required parameter other than the context itself; they are listed here as the positive
    /// control's subject, not as the axis's enumeration (that stays behavioural, above).
    /// </remarks>
    [Fact]
    public async Task ProjectAxis_AnOwnerGetsRealPayloads_FromTheProjectScopedReadTools()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project mine = SeedProject(db, callerId, "My Project");
        SeedFile(db, mine.Id, "my-notes.md");

        var currentUser = new MutableCurrentUser(callerId) { Admin = false };
        AssistantToolProvider provider = CreateProvider(db, currentUser);
        var context = new AssistantToolContext(callerId, mine.Id);

        foreach (string toolName in new[] { "ListAgents", "ListProjectFiles", "ListWorkflows", "GetProject" })
        {
            AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
            string text = AsText(await tool.InvokeAsync(ArgumentsFor(tool)));

            ErrorOf(text).Should().BeNull(
                $"'{toolName}' must return a real payload for the owner of the context project, but returned '{Truncate(text)}'");

            using JsonDocument doc = JsonDocument.Parse(text);
            doc.RootElement.ValueKind.Should().BeOneOf(JsonValueKind.Array, JsonValueKind.Object);
        }

        // The file the owner seeded is genuinely reachable through the owner's context — proof the
        // payload above is real data and not an empty-but-valid envelope.
        AIFunction files = provider.GetTools(context).Single(t => t.Name == "ListProjectFiles");
        AsText(await files.InvokeAsync(ArgumentsFor(files))).Should().Contain("my-notes.md");
    }

    /// <summary>
    /// Closes the gap the ticket calls out as the residual: <c>GetAdminToolNames</c> is
    /// <b>self-referential</b> — it names the gated subset from <c>AssistantAdminTools</c>' own
    /// registration list, so a tool that is admin-gated but registered in some OTHER surface (or
    /// directly in <see cref="AssistantToolProvider.GetTools"/>) would never be enumerated, and no
    /// list-driven test could notice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This test does not trust the enumeration to be complete. It takes the registered set MINUS the
    /// enumerated one and asks the only authority that cannot lie about it — behaviour: does the tool,
    /// invoked by a NON-ADMIN, answer with the shared admin refusal? If it does, it is admin-gated in
    /// fact but absent from <c>GetAdminToolNames</c>, which is exactly the escape the ticket is about,
    /// and it fails here by name.
    /// </para>
    /// <para>
    /// <b>This is detection, not a fix.</b> The provider's enumeration stays self-referential; what
    /// changes is that the gap is now observable from the suite rather than only from a code review.
    /// Closing it structurally would mean the provider deriving the gated set from something other
    /// than the surface's own list, which is outside this ticket's write surface.
    /// </para>
    /// <para>
    /// A false positive — an ungated tool that returns that exact string for an unrelated reason —
    /// would be worth investigating on its own terms, so failing loudly is the right default.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AdminAxis_NoToolOutsideTheEnumeratedSet_BehavesAsIfItWereGated()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project project = SeedProject(db, callerId, "Owned");
        SeedFile(db, project.Id, "notes.md");

        var currentUser = new MutableCurrentUser(callerId) { Admin = false };
        AssistantToolProvider provider = CreateProvider(db, currentUser);
        var context = new AssistantToolContext(callerId, project.Id);

        IReadOnlyList<string> registered = provider.GetTools(context).Select(t => t.Name).ToList();
        IReadOnlyList<string> gated = provider.GetAdminToolNames(context);

        // The caller is a NON-ADMIN throughout, so a tool that IS gated must refuse.
        currentUser.Admin = false;

        var escaped = new List<string>();

        foreach (string toolName in registered.Except(gated, StringComparer.Ordinal))
        {
            AIFunction tool = provider.GetTools(context).Single(t => t.Name == toolName);
            string text = AsText(await tool.InvokeAsync(ArgumentsFor(tool)));

            if (ErrorOf(text) == AdminRequiredMessage)
            {
                escaped.Add(
                    $"'{toolName}' is registered but NOT listed by GetAdminToolNames, yet it refused a "
                    + "non-admin with the admin refusal — it is gated in fact and escaped the enumeration");
            }
        }

        escaped.Should().BeEmpty(
            "GetAdminToolNames is derived from AssistantAdminTools' own registration, so a tool gated "
            + "elsewhere would escape it; this check finds such a tool by behaviour instead:\n"
            + string.Join("\n", escaped));

        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // ANTI-CACHE — authority is read per call, in BOTH orders
    // ---------------------------------------------------------------------

    /// <summary>
    /// One provider instance, one admin-gated tool, two calls, both orders: the outcome must follow
    /// the LIVE claim each time, so pinning that authority is read per call and not captured at
    /// construction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is a new assertion rather than a copy.</b> The neighbouring
    /// <c>Agents/AssistantAuthorityTests.AdminTool_ActuallyReadsIsAdminOnEachCall_NotOnceAtConstruction</c>
    /// covers the non-admin-then-admin direction for a single tool (<c>ListAgentsAdmin</c>) using a
    /// counter-backed delegate, and
    /// <c>Agents/AssistantAdminToolTests.AdminTool_ReadsIsAdminOnEveryCall_NotOnceAtConstruction</c>
    /// covers that same direction per admin tool. Neither exercises the <b>admin-then-non-admin</b>
    /// direction, which is the one a memoized GRANT breaks: a provider that cached "this caller is an
    /// admin" would keep serving after the claim was withdrawn, and neither existing test would
    /// notice. This matrix runs both orders over the whole gated set, with a mutable flag rather than
    /// a call counter, and requires the two outcomes to DIFFER.
    /// </para>
    /// <para>
    /// Both orders run against the SAME instance, deliberately: a fresh provider per call would test
    /// nothing but the constructor.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AdminAxis_AuthorityIsReadPerCall_NotCapturedAtConstruction_InBothOrders()
    {
        using WriteCountingDbContext db = CreateContext();
        Guid callerId = Guid.NewGuid();
        Project project = SeedProject(db, callerId, "Owned");

        var currentUser = new MutableCurrentUser(callerId);
        currentUser.GrantNothing();
        AssistantToolProvider provider = CreateProvider(db, currentUser);
        var context = new AssistantToolContext(callerId, project.Id);

        IReadOnlyList<string> gated = provider.GetAdminToolNames(context);
        gated.Should().NotBeEmpty();

        var failures = new List<string>();

        foreach (string toolName in gated)
        {
            AIFunction MakeTool() => provider.GetTools(context).Single(t => t.Name == toolName);
            AIFunctionArguments arguments = ArgumentsFor(MakeTool());

            // Both authorities are granted and withdrawn TOGETHER here: this matrix is about WHEN
            // authority is read, not about which of the two governs a given tool (the matrix above
            // pins that). Either order with either authority would do; using both keeps it honest
            // for every tool in the set without classifying them.

            // -- Order A: no authority first, then both --------------------------------
            currentUser.GrantNothing();
            string nonAdminFirst = AsText(await MakeTool().InvokeAsync(arguments));

            currentUser.GrantEverything();
            string adminSecond = AsText(await MakeTool().InvokeAsync(arguments));

            if (ErrorOf(nonAdminFirst) != AdminRequiredMessage)
            {
                failures.Add($"'{toolName}' [order A]: the non-admin call was not refused");
            }

            if (ErrorOf(adminSecond) == AdminRequiredMessage)
            {
                failures.Add(
                    $"'{toolName}' [order A]: the admin call was still refused — the claim is captured "
                    + "at construction rather than read per call");
            }

            if (ErrorOf(nonAdminFirst) == ErrorOf(adminSecond))
            {
                failures.Add(
                    $"'{toolName}' [order A]: the two calls returned the same outcome, so the live "
                    + "claim is not what decides the result");
            }

            // -- Order B: both first, then none (the direction a cached GRANT breaks) ----
            currentUser.GrantEverything();
            string adminFirst = AsText(await MakeTool().InvokeAsync(arguments));

            currentUser.GrantNothing();
            string nonAdminSecond = AsText(await MakeTool().InvokeAsync(arguments));

            if (ErrorOf(adminFirst) == AdminRequiredMessage)
            {
                failures.Add($"'{toolName}' [order B]: the admin call was refused");
            }

            if (ErrorOf(nonAdminSecond) != AdminRequiredMessage)
            {
                failures.Add(
                    $"'{toolName}' [order B]: after the claim was WITHDRAWN the call was still served "
                    + $"(got '{Truncate(nonAdminSecond)}') — a memoized grant is a privilege-escalation bug");
            }

            if (ErrorOf(adminFirst) == ErrorOf(nonAdminSecond))
            {
                failures.Add(
                    $"'{toolName}' [order B]: the two calls returned the same outcome, so the live "
                    + "claim is not what decides the result");
            }
        }

        failures.Should().BeEmpty(
            "authority must be re-read on every call, in both directions, on one provider instance:\n"
            + string.Join("\n", failures));

        // The probe arguments are inert, so the whole both-orders matrix must have written nothing.
        db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty();
    }
}
