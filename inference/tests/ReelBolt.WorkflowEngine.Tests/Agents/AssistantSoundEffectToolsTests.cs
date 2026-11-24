using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.SfxLibrary;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>SearchSoundEffects and the ProposeAddSoundEffect / ApplyAddSoundEffect pair (docs/platform-assistant-mcp.md).</summary>
public class AssistantSoundEffectToolsTests
{
    private sealed class User(Guid id) : ICurrentUser
    {
        public Guid UserId { get; } = id;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
        public Guid? OrganizationId => TestTenancy.OrgOf(UserId);
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => ReelBolt.Shared.Tenancy.OrganizationRole.Member;
    }

    private sealed class Rig : IDisposable
    {
        public InferenceApiDbContext Db { get; } = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public FakeSfxCopier Copier { get; } = new();
        public Guid Me { get; } = Guid.NewGuid();
        public Project Project { get; }
        public AssistantProjectTools Tools { get; }
        public AssistantToolContext Ctx { get; }

        public Rig(int maxPerProject = 200, int perMinute = 60)
        {
            Project = new Project { Id = Guid.NewGuid(), Name = "Launch reel", OwnerId = Me, OrganizationId = TestTenancy.OrgOf(Me), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            Db.Projects.Add(Project);
            Db.SaveChanges();
            var user = new User(Me);
            Mock<IFileStorageService> storage = new();
            storage.SetupGet(s => s.BucketName).Returns("bucket");
            IConfiguration cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["SfxLibrary:MaxPerProject"] = maxPerProject.ToString(), ["SfxLibrary:MaxAddsPerMinute"] = perMinute.ToString() }).Build();
            var adder = new SfxLibraryProjectAdder(Db, Copier, new SfxLibraryAddRateLimiter(cfg), storage.Object, new Mock<IPublishEndpoint>().Object, cfg, NullLogger<SfxLibraryProjectAdder>.Instance);
            Tools = new AssistantProjectTools(Db, ConfirmationTokenService.CreateEphemeral(),
                new WorkflowTemplateProvisioningService(Db, NullLogger<WorkflowTemplateProvisioningService>.Instance, new ConfigurationBuilder().Build()),
                user, TestTenancy.Access(Db, user), sfxLibrary: adder);
            Ctx = new AssistantToolContext(Me, Project.Id);
        }

        public void AddItem(string slug, string category, int durationMs = 400, bool enabled = true)
        {
            Db.SfxLibraryItems.Add(new SfxLibraryItem
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                Title = slug.Replace('-', ' '),
                Category = category,
                Tags = [category, "hit"],
                DurationMs = durationMs,
                SizeBytes = 100,
                Sha256 = new string('a', 64),
                StorageKey = SfxLibraryKeys.KeyFor(slug, "ogg"),
                MimeType = "audio/ogg",
                Licence = "CC0 1.0",
                IsEnabled = enabled,
                AddedAt = DateTimeOffset.UtcNow
            });
            Db.SaveChanges();
        }

        public async Task<JsonElement> InvokeAsync(string name, params (string Key, object? Value)[] args)
        {
            AIFunction tool = Tools.GetTools(Ctx).Single(t => t.Name == name);
            var arguments = new AIFunctionArguments();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await tool.InvokeAsync(arguments);
            string text = raw switch
            {
                JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString()!,
                JsonElement e => e.GetRawText(),
                _ => raw!.ToString()!
            };
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Search_returns_names_not_ids_and_filters_by_query_category_and_length()
    {
        using Rig r = new();
        r.AddItem("whoosh-short", "whoosh", 300);
        r.AddItem("whoosh-long", "whoosh", 2000);
        r.AddItem("pop-soft", "pop", 150);
        r.AddItem("off-item", "pop", 150, enabled: false);

        JsonElement all = await r.InvokeAsync("SearchSoundEffects");
        all.GetProperty("total").GetInt32().Should().Be(3, "a disabled item is never listed");
        all.GetRawText().Should().NotContain("\"id\"").And.NotContain("storage");
        all.GetProperty("sounds")[0].TryGetProperty("name", out _).Should().BeTrue();

        (await r.InvokeAsync("SearchSoundEffects", ("category", "whoosh"), ("maxDurationMs", 500))).GetProperty("sounds").GetArrayLength().Should().Be(1);
        (await r.InvokeAsync("SearchSoundEffects", ("query", "pop"))).GetProperty("sounds")[0].GetProperty("name").GetString().Should().Be("pop-soft");
    }

    [Fact]
    public async Task Apply_goes_through_the_same_per_user_rate_limit_as_the_rest_route()
    {
        using Rig r = new(perMinute: 1);
        r.AddItem("pop-soft", "pop");
        r.AddItem("click-a", "click");

        string t1 = (await r.InvokeAsync("ProposeAddSoundEffect", ("name", "pop-soft"))).GetProperty("confirmationToken").GetString()!;
        (await r.InvokeAsync("ApplyAddSoundEffect", ("name", "pop-soft"), ("confirmationToken", t1))).GetProperty("added").GetBoolean().Should().BeTrue();

        string t2 = (await r.InvokeAsync("ProposeAddSoundEffect", ("name", "click-a"))).GetProperty("confirmationToken").GetString()!;
        (await r.InvokeAsync("ApplyAddSoundEffect", ("name", "click-a"), ("confirmationToken", t2))).GetProperty("error").GetString().Should().Contain("try again");
        r.Db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Apply_needs_the_exact_token_then_adds_once_and_is_idempotent()
    {
        using Rig r = new();
        r.AddItem("pop-soft", "pop");

        JsonElement proposal = await r.InvokeAsync("ProposeAddSoundEffect", ("name", "pop-soft"));
        r.Db.ProjectFiles.Should().BeEmpty("propose writes nothing");
        proposal.GetProperty("summary").GetString().Should().Contain("Launch reel");
        string token = proposal.GetProperty("confirmationToken").GetString()!;

        (await r.InvokeAsync("ApplyAddSoundEffect", ("name", "pop-soft"), ("confirmationToken", "forged"))).GetProperty("error").GetString().Should().Contain("stale");
        r.Db.ProjectFiles.Should().BeEmpty();

        JsonElement done = await r.InvokeAsync("ApplyAddSoundEffect", ("name", "pop-soft"), ("confirmationToken", token));
        done.GetProperty("added").GetBoolean().Should().BeTrue();
        done.GetProperty("alreadyInProject").GetBoolean().Should().BeFalse();
        ProjectFile file = r.Db.ProjectFiles.Single();
        file.SourceKind.Should().Be(SfxLibraryProjectAdder.SourceKind);
        file.ProjectId.Should().Be(r.Project.Id);

        JsonElement again = await r.InvokeAsync("ApplyAddSoundEffect", ("name", "pop-soft"), ("confirmationToken", token));
        again.GetProperty("alreadyInProject").GetBoolean().Should().BeTrue();
        r.Db.ProjectFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task A_changed_name_voids_the_token_and_an_unknown_name_is_refused()
    {
        using Rig r = new();
        r.AddItem("pop-soft", "pop");
        r.AddItem("click-a", "click");
        string token = (await r.InvokeAsync("ProposeAddSoundEffect", ("name", "pop-soft"))).GetProperty("confirmationToken").GetString()!;

        (await r.InvokeAsync("ApplyAddSoundEffect", ("name", "click-a"), ("confirmationToken", token))).GetProperty("error").GetString().Should().Contain("stale");
        (await r.InvokeAsync("ProposeAddSoundEffect", ("name", "nope"))).GetProperty("error").GetString().Should().Contain("no sound");
        (await r.InvokeAsync("ProposeAddSoundEffect")).GetProperty("error").GetString().Should().Contain("starterSet");
        r.Db.ProjectFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task The_starter_set_is_one_approval_and_a_project_the_caller_cannot_edit_is_refused()
    {
        using Rig r = new();
        string[] cats = ["pop", "whoosh", "click"];
        for (int i = 0; i < 30; i++)
            r.AddItem($"s-{i}", cats[i % 3]);

        JsonElement proposal = await r.InvokeAsync("ProposeAddSoundEffect", ("starterSet", true));
        string token = proposal.GetProperty("confirmationToken").GetString()!;
        JsonElement done = await r.InvokeAsync("ApplyAddSoundEffect", ("starterSet", true), ("confirmationToken", token));
        done.GetProperty("newSounds").GetInt32().Should().Be(SfxLibraryProjectAdder.StarterSetSize);
        r.Db.ProjectFiles.Count().Should().Be(SfxLibraryProjectAdder.StarterSetSize);

        Project theirs = new() { Id = Guid.NewGuid(), Name = "Theirs", OwnerId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        r.Db.Projects.Add(theirs);
        r.Db.SaveChanges();
        (await r.InvokeAsync("ProposeAddSoundEffect", ("starterSet", true), ("projectId", theirs.Id))).GetProperty("error").GetString().Should().Contain("not found");
    }
}
