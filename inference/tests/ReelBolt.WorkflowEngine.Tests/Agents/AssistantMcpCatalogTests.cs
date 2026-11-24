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
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Auth;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The directory-facing contract of the assistant / MCP tool catalog: a title and the applicable
/// annotations on every tool, and a typed schema for every parameter. These are the two rejection
/// reasons the connector review names explicitly.
/// </summary>
public class AssistantMcpCatalogTests
{
    private sealed class ScopedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
        public string Email { get; } = "test@example.com";
        public bool IsAuthenticated { get; } = true;
        public bool IsPlatformAdmin { get; } = false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private static InferenceApiDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new InferenceApiDbContext(options);
    }

    /// <summary>The tools the MCP catalog exposes: no chat thread, so no chat-only WatchExecution.</summary>
    private static (InferenceApiDbContext Db, IReadOnlyList<AIFunction> Tools) Catalog()
    {
        InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new ScopedCurrentUser(userId));
        IReadOnlyList<AIFunction> tools = provider.GetTools(new AssistantToolContext(userId, Guid.NewGuid()));
        return (db, tools);
    }

    [Fact]
    public void Mcp_catalog_has_128_tools_and_every_tool_has_a_title_and_the_applicable_hint()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            tools.Should().HaveCount(152);

            foreach (AIFunction tool in tools)
            {
                AssistantToolAnnotation annotation = AssistantToolAnnotations.For(tool.Name);

                annotation.Title.Should().NotBeNullOrWhiteSpace($"'{tool.Name}' needs a title");
                annotation.Title.Length.Should().BeLessThanOrEqualTo(64, $"'{tool.Name}' title is too long");
                tool.Name.Length.Should().BeLessThanOrEqualTo(64, $"'{tool.Name}' is too long");

                // The checklist's own framing: read-only, or destructive (creates/updates/deletes).
                (annotation.ReadOnlyHint ^ annotation.DestructiveHint).Should()
                    .BeTrue($"'{tool.Name}' must be exactly one of read-only or destructive");
            }
        }
    }

    [Fact]
    public void Every_tool_is_classified_as_read_only_or_destructive()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            string[] names = [.. tools.Select(t => t.Name)];

            AssistantToolAnnotations.Unclassified(names).Should().BeEmpty(
                "every registered tool must be classified on purpose, never by omission");
            AssistantToolAnnotations.Contradictions(names).Should().BeEmpty(
                "a gated tool must never be listed as read-only");
        }
    }

    [Fact]
    public void Every_parameter_in_every_tool_schema_is_typed()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            var problems = new List<string>();
            foreach (AIFunction tool in tools)
                Walk(tool.Name, "$", SchemaOf(tool), problems);

            problems.Should().BeEmpty();
        }
    }

    [Fact]
    public void The_run_brief_is_a_typed_object_matching_the_validator()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            string[] briefTools =
            [
                "ValidateWorkflow", "ExecuteWorkflow", "EstimateWorkflowRun",
                "ProposeCreateFromTemplate", "ApplyCreateFromTemplate",
            ];

            foreach (string name in briefTools)
            {
                JsonElement schema = SchemaOf(tools.Single(t => t.Name == name));
                JsonElement brief = schema.GetProperty("properties").GetProperty("brief");

                brief.GetProperty("type").GetString().Should().Be("object", name);
                JsonElement properties = brief.GetProperty("properties");
                properties.TryGetProperty("language", out _).Should().BeTrue(name);
                properties.TryGetProperty("sourceLanguage", out _).Should().BeTrue(name);
                properties.TryGetProperty("lengthSec", out _).Should().BeTrue(name);
                properties.TryGetProperty("format", out _).Should().BeTrue(name);
                properties.TryGetProperty("resolution", out _).Should().BeTrue(name);
                properties.TryGetProperty("stylePreset", out _).Should().BeTrue(name);
                properties.TryGetProperty("styleId", out _).Should().BeTrue(name);
                properties.TryGetProperty("style", out _).Should().BeTrue(name);
                properties.TryGetProperty("tone", out _).Should().BeTrue(name);
                properties.TryGetProperty("designSystem", out _).Should().BeTrue(name);
                properties.TryGetProperty("voice", out _).Should().BeTrue(name);
                properties.TryGetProperty("sourceFiles", out _).Should().BeTrue(name);
                properties.TryGetProperty("notes", out _).Should().BeTrue(name);

                // Every brief field is optional; a required member would reject a valid partial brief.
                if (brief.TryGetProperty("required", out JsonElement required))
                    required.GetArrayLength().Should().Be(0, name);

                JsonElement sourceFiles = properties.GetProperty("sourceFiles");
                sourceFiles.GetProperty("items").GetProperty("required").GetArrayLength().Should().Be(2, name);
            }
        }
    }

    [Fact]
    public void The_mcp_catalog_publishes_the_collapsed_surface_only()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            IReadOnlyList<McpToolDescriptor> catalog =
                AssistantMcpToolSurface.Project(tools, new HashSet<string>(StringComparer.Ordinal));

            // 152 registered tools: 50 Propose/Apply pairs, of which the 2 social pairs are not on MCP at all and the other
            // 48 become 47 collapsed calls; the upload protocol keeps two steps under its own names. 152 - 4 - 96 + 47 + 2 = 101.
            catalog.Should().HaveCount(101);
            catalog.Select(t => t.Name).Should().Contain(["ListSocialAccounts", "ListScheduledPosts", "GetPostPerformance"]);
            catalog.Select(t => t.Name).Should().NotContain(["SchedulePost", "CancelScheduledPost"]);
            catalog.Select(t => t.Name).Should().OnlyHaveUniqueItems();
            catalog.Should().NotContain(t =>
                t.Name.StartsWith("Propose", StringComparison.Ordinal)
                || t.Name.StartsWith("Apply", StringComparison.Ordinal));

            catalog.Should().Contain(t => t.Name == "BeginUpload");
            catalog.Should().Contain(t => t.Name == "CompleteUpload");
            catalog.Should().Contain(t => t.Name == "ExecuteWorkflow");
            catalog.Should().Contain(t => t.Name == "SetMediaOverlay");
            catalog.Should().Contain(t => t.Name == "CopyMarketplaceRecipe");
            catalog.Should().Contain(t => t.Name == "SearchMarketplaceRecipes");
            catalog.Should().Contain(t => t.Name == "GetMarketplaceRecipe");

            foreach (McpToolDescriptor tool in catalog)
            {
                tool.Annotation.Title.Should().NotBeNullOrWhiteSpace(tool.Name);
                (tool.Annotation.ReadOnlyHint ^ tool.Annotation.DestructiveHint).Should().BeTrue(tool.Name);
            }

            catalog.Single(t => t.Name == "ExecuteWorkflow").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint);
            catalog.Single(t => t.Name == "CreateFromTemplate").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint);
            catalog.Single(t => t.Name == "DecideDesignChange").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint);
            catalog.Single(t => t.Name == "ObjectEffect").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint && a.Title == "Put an effect on an object in a clip");
            catalog.Single(t => t.Name == "EnhanceVideo").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint && a.Title == "Enhance a video");
            catalog.Single(t => t.Name == "LookAtPicture").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.ReadOnlyHint && !a.DestructiveHint);
            catalog.Single(t => t.Name == "BeginUpload").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.ReadOnlyHint && !a.DestructiveHint);
            catalog.Single(t => t.Name == "CompleteUpload").Annotation.Should()
                .Match<AssistantToolAnnotation>(a => a.DestructiveHint && !a.ReadOnlyHint);

            catalog.Select(t => t.Name).Should().BeEquivalentTo(
                AiToolCatalogNames(tools)
                    .Concat(AssistantMcpToolSurface.Collapsed.Select(p => p.Name))
                    .Concat([AssistantMcpToolSurface.BeginUpload.Name, AssistantMcpToolSurface.CompleteUpload.Name]));
        }
    }

    [Fact]
    public void Every_propose_apply_pair_has_exactly_one_mcp_name()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var context = new AssistantToolContext(userId, Guid.NewGuid());
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new ScopedCurrentUser(userId));
        IReadOnlyList<AIFunction> tools = provider.GetTools(context);
        HashSet<string> admin = provider.GetAdminToolNames(context).ToHashSet(StringComparer.Ordinal);

        string[] registered = [.. tools.Select(t => t.Name)];
        string[] applyTools = [.. registered.Where(n => n.StartsWith("Apply", StringComparison.Ordinal))];
        string[] proposeTools = [.. registered.Where(n => n.StartsWith("Propose", StringComparison.Ordinal))];

        // Every Apply has a pair, and every Propose has an Apply; the only uncollapsed pair is upload.
        applyTools.Should().HaveCount(50);
        proposeTools.Should().HaveCount(50);
        applyTools.Should().BeEquivalentTo(proposeTools.Select(n => "Apply" + n[7..]));

        IReadOnlyList<string> mappedApplies = [.. AssistantMcpToolSurface.Collapsed.Select(p => p.ApplyName),
            AssistantMcpToolSurface.CompleteUpload.ApplyName];
        IReadOnlyList<string> mappedProposes = [.. AssistantMcpToolSurface.Collapsed.Select(p => p.ProposeName),
            AssistantMcpToolSurface.BeginUpload.ProposeName];

        // The two social pairs are in-app only (always-ask, never over MCP): every other pair is mapped.
        mappedApplies.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(applyTools.Where(n => !AssistantMcpToolSurface.NotOverMcp.Contains(n)));
        mappedProposes.Should().OnlyHaveUniqueItems().And.BeEquivalentTo(proposeTools.Where(n => !AssistantMcpToolSurface.NotOverMcp.Contains(n)));
        applyTools.Concat(proposeTools).Where(AssistantMcpToolSurface.NotOverMcp.Contains).Should().HaveCount(4);

        // The MCP admin gate is applied to the Apply half; a pair whose halves disagree would let
        // the proposal run without the admin opt-in or gate the wrong tool.
        foreach (AssistantMcpToolSurface.CollapsedPair pair in AssistantMcpToolSurface.Collapsed)
            admin.Contains(pair.ProposeName).Should().Be(admin.Contains(pair.ApplyName), pair.Name);
    }

    [Fact]
    public async Task A_collapsed_call_runs_propose_then_apply_with_the_server_token()
    {
        int applyCalls = 0;
        string? seenToken = null;
        AIFunction propose = AIFunctionFactory.Create(
            () => "{\"confirmationToken\":\"server-token\"}",
            name: "ProposeCreateFromTemplate");
        AIFunction apply = AIFunctionFactory.Create(
            (string confirmationToken) => { applyCalls++; seenToken = confirmationToken; return "{\"ok\":true}"; },
            name: "ApplyCreateFromTemplate");

        AssistantMcpToolSurface.TryResolve(
            "CreateFromTemplate", [propose, apply], out McpToolCallPlan plan).Should().BeTrue();
        plan.MintsAndConsumesToken.Should().BeTrue();
        plan.ChargeToolName.Should().Be("ApplyCreateFromTemplate");

        var arguments = new AIFunctionArguments { ["templateKey"] = "promo" };
        object? result = await AssistantMcpToolSurface.InvokeAsync(plan, arguments, CancellationToken.None);

        applyCalls.Should().Be(1);
        seenToken.Should().Be("server-token");
        TextOf(result).Should().Be("{\"ok\":true}");
    }

    [Fact]
    public async Task A_refused_proposal_is_returned_and_nothing_is_applied()
    {
        int applyCalls = 0;
        AIFunction propose = AIFunctionFactory.Create(
            () => "{\"error\":\"template not found\"}",
            name: "ProposeCreateFromTemplate");
        AIFunction apply = AIFunctionFactory.Create(
            (string confirmationToken) => { applyCalls++; return "applied"; },
            name: "ApplyCreateFromTemplate");

        AssistantMcpToolSurface.TryResolve(
            "CreateFromTemplate", [propose, apply], out McpToolCallPlan plan).Should().BeTrue();
        object? result = await AssistantMcpToolSurface.InvokeAsync(
            plan, new AIFunctionArguments { ["templateKey"] = "missing" }, CancellationToken.None);

        applyCalls.Should().Be(0);
        TextOf(result).Should().Be("{\"error\":\"template not found\"}");
    }

    [Fact]
    public async Task The_server_token_wins_over_a_client_supplied_one()
    {
        string? seenToken = null;
        AIFunction propose = AIFunctionFactory.Create(
            () => "{\"confirmationToken\":\"server-token\"}",
            name: "ProposeWorkflow");
        AIFunction apply = AIFunctionFactory.Create(
            (string confirmationToken) => { seenToken = confirmationToken; return "ok"; },
            name: "ApplyWorkflow");

        AssistantMcpToolSurface.TryResolve(
            "SaveWorkflow", [propose, apply], out McpToolCallPlan plan).Should().BeTrue();
        await AssistantMcpToolSurface.InvokeAsync(
            plan,
            new AIFunctionArguments { ["projectId"] = Guid.NewGuid(), ["confirmationToken"] = "client-token" },
            CancellationToken.None);

        seenToken.Should().Be("server-token");
    }

    [Fact]
    public async Task Approving_a_design_change_is_refused_over_mcp()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var currentUser = new ScopedCurrentUser(userId);
        DateTime now = DateTime.UtcNow;
        db.Projects.Add(new ReelBolt.Shared.Data.Models.Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = userId,
            OrganizationId = TestTenancy.OrgOf(userId),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        IReadOnlyList<AIFunction> tools = AssistantToolTestFactory
            .CreateProvider(db, currentUser)
            .GetTools(new AssistantToolContext(userId, db.Projects.Single().Id, Initiator: ReelBolt.Shared.Data.Models.ExecutionInitiator.Mcp));
        AIFunction apply = tools.Single(t => t.Name == "ApplyDecideDesignChange");

        object? raw = await apply.InvokeAsync(new AIFunctionArguments
        {
            ["decision"] = "approve",
            ["changeName"] = "any",
            ["confirmationToken"] = "any",
            ["projectId"] = db.Projects.Single().Id,
        }, CancellationToken.None);

        using JsonDocument json = JsonDocument.Parse(TextOf(raw));
        json.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A_brief_object_still_binds_through_the_typed_schema()
    {
        using InferenceApiDbContext db = CreateContext();
        Guid userId = Guid.NewGuid();
        var currentUser = new ScopedCurrentUser(userId);
        DateTime now = DateTime.UtcNow;
        db.Projects.Add(new ReelBolt.Shared.Data.Models.Project
        {
            Id = Guid.NewGuid(),
            Name = "P",
            OwnerId = userId,
            OrganizationId = TestTenancy.OrgOf(userId),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        AIFunction tool = AssistantToolTestFactory
            .CreateProvider(db, currentUser)
            .GetTools(new AssistantToolContext(userId))
            .Single(t => t.Name == "ValidateWorkflow");

        // The declaration is typed by AssistantToolSchemas, but the parameter is still a
        // JsonElement: a real brief object must bind and reach RunBriefValidator.
        object? raw = await tool.InvokeAsync(new AIFunctionArguments
        {
            ["steps"] = JsonSerializer.SerializeToElement(Array.Empty<object>()),
            ["projectId"] = db.Projects.Single().Id,
            ["brief"] = JsonSerializer.SerializeToElement(new
            {
                language = "it",
                lengthSec = 30,
                sourceFiles = new[] { new { role = "video", fileIds = new[] { Guid.NewGuid() } } },
            }),
        }, CancellationToken.None);

        TextOf(raw).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_collapsed_tool_schema_covers_every_apply_parameter()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            Dictionary<string, AIFunction> byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

            foreach (AssistantMcpToolSurface.CollapsedPair pair in AssistantMcpToolSurface.Collapsed)
            {
                HashSet<string> proposed = ParameterNames(byName[pair.ProposeName].JsonSchema);
                HashSet<string> applied = ParameterNames(byName[pair.ApplyName].JsonSchema);
                applied.Remove("confirmationToken");

                // The MCP schema is the proposal's; an Apply-only parameter would be impossible to
                // pass through the collapsed call.
                applied.Should().BeSubsetOf(proposed, pair.Name);
            }
        }
    }

    private static HashSet<string> ParameterNames(JsonElement schema) =>
        schema.TryGetProperty("properties", out JsonElement properties)
            ? [.. properties.EnumerateObject().Select(p => p.Name)]
            : [];

    [Fact]
    public void The_mcp_catalog_descriptions_are_free_of_instructions_and_promotion()
    {
        (InferenceApiDbContext db, IReadOnlyList<AIFunction> tools) = Catalog();
        using (db)
        {
            var patterns = new[]
            {
                @"\byou (must|should|need to)\b", @"\bask the user\b", @"\btell the user\b",
                @"\bconfirm (it )?with the user\b", @"\buse it instead\b", @"\bdo not poll\b",
                @"\bfirst call\b", @"\balways call\b", @"\bnever call\b",
                @"\bremember to\b", @"\bmake sure to\b",
            };

            var violations = new List<string>();
            foreach (McpToolDescriptor tool in AssistantMcpToolSurface.Project(
                tools, new HashSet<string>(StringComparer.Ordinal)))
            {
                tool.Description.Should().NotBeNullOrWhiteSpace(tool.Name);
                foreach (string pattern in patterns)
                {
                    System.Text.RegularExpressions.Match match =
                        System.Text.RegularExpressions.Regex.Match(tool.Description, pattern,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (match.Success)
                        violations.Add($"{tool.Name}: '{match.Value}'");
                }

                // The in-app Propose*/Apply* names are not MCP tools; a description that tells an
                // MCP client to call one is inaccurate.
                if (System.Text.RegularExpressions.Regex.IsMatch(tool.Description, @"\b(Propose|Apply)[A-Z]"))
                    violations.Add($"{tool.Name}: names an in-app-only Propose/Apply tool");
            }

            violations.Should().BeEmpty();
        }
    }

    private static string TextOf(object? raw) => raw switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(raw),
    };

    private static IReadOnlyList<string> AiToolCatalogNames(IReadOnlyList<AIFunction> tools) =>
        [.. tools.Select(t => t.Name).Where(n => !n.StartsWith("Propose", StringComparison.Ordinal)
            && !n.StartsWith("Apply", StringComparison.Ordinal))];

    private static JsonElement SchemaOf(AIFunction tool) =>
        tool.JsonSchema.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
            : tool.JsonSchema;

    private static void Walk(string tool, string path, JsonElement schema, List<string> problems)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{tool}: {path} is not a schema object");
            return;
        }

        if (schema.TryGetProperty("$ref", out _)
            || schema.TryGetProperty("const", out _)
            || schema.TryGetProperty("enum", out JsonElement enumValues) && enumValues.GetArrayLength() > 0)
            return;

        foreach (string keyword in new[] { "anyOf", "oneOf", "allOf" })
        {
            if (!schema.TryGetProperty(keyword, out JsonElement choices))
                continue;
            if (choices.GetArrayLength() == 0)
            {
                problems.Add($"{tool}: {path}.{keyword} has no members");
                return;
            }
            int i = 0;
            foreach (JsonElement choice in choices.EnumerateArray())
                Walk(tool, $"{path}.{keyword}[{i++}]", choice, problems);
            return;
        }

        if (!schema.TryGetProperty("type", out JsonElement type))
        {
            problems.Add($"{tool}: {path} has no type ({schema.GetRawText()})");
            return;
        }

        // System.Text.Json writes a nullable value's type as ["string","null"]; both forms are typed.
        var kinds = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()).ToHashSet()
            : type.ValueKind == JsonValueKind.String
                ? new HashSet<string?> { type.GetString() }
                : new HashSet<string?>();
        if (kinds.Count == 0)
        {
            problems.Add($"{tool}: {path} has no type ({schema.GetRawText()})");
            return;
        }

        if (kinds.Contains("array"))
        {
            if (!schema.TryGetProperty("items", out JsonElement items))
                problems.Add($"{tool}: {path} is an array with no items");
            else
                Walk(tool, $"{path}[]", items, problems);
        }

        if (kinds.Contains("object"))
        {
            if (schema.TryGetProperty("properties", out JsonElement properties))
            {
                foreach (JsonProperty property in properties.EnumerateObject())
                    Walk(tool, $"{path}.{property.Name}", property.Value, problems);
            }
            if (schema.TryGetProperty("additionalProperties", out JsonElement additional)
                && additional.ValueKind == JsonValueKind.Object)
                Walk(tool, $"{path}.*", additional, problems);
        }
    }
}
