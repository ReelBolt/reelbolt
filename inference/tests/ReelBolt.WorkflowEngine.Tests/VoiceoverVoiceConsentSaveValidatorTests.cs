using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>A Voiceover config naming a project voice (by id, or by its vendor id) that is unselectable now is refused at save.</summary>
public class VoiceoverVoiceConsentSaveValidatorTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.NewGuid();
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private static readonly Guid OrgA = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("bbbbbbbb-0000-0000-0000-00000000000b");

    private async Task<ProjectVoice> AddVoice(string remote, Action<ProjectVoice>? tweak = null, Guid? project = null, Guid? org = null, bool platformProvider = false, bool unresolvableCapability = false)
    {
        Guid owning = project ?? ProjectId;
        if (!await _db.Projects.AnyAsync(p => p.Id == ProjectId))
            _db.Projects.Add(new Project { Id = ProjectId, Name = "here", OrganizationId = OrgA });
        if (owning != ProjectId && !await _db.Projects.AnyAsync(p => p.Id == owning))
            _db.Projects.Add(new Project { Id = owning, Name = "p", OrganizationId = org ?? OrgA });
        var v = new ProjectVoice
        {
            Id = Guid.NewGuid(),
            ProjectId = project ?? ProjectId,
            InferenceProviderId = Guid.NewGuid(),
            Name = "v",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = remote,
            ConsentAttestedAt = DateTime.UtcNow.AddDays(-1),
            ConsentAttestedByUserId = Guid.NewGuid(),
            ConsentStatement = "ok",
            ConsentSubjectName = "s",
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };
        tweak?.Invoke(v);
        // InferenceProviderId is a capability id: build capability -> model -> account with three different ids.
        if (v.InferenceProviderId is Guid capabilityId && !unresolvableCapability)
        {
            var account = new InferenceProvider { Id = Guid.NewGuid(), Name = "p", Endpoint = "https://example.invalid", OrganizationId = platformProvider ? null : (org ?? OrgA) };
            var model = new InferenceModel { Id = Guid.NewGuid(), ProviderId = account.Id, ModelCode = "m", DisplayName = "m" };
            _db.InferenceProviders.Add(account);
            _db.InferenceModels.Add(model);
            _db.ModelCapabilities.Add(new ModelCapability { Id = capabilityId, ModelId = model.Id, OrganizationId = account.OrganizationId });
        }

        _db.ProjectVoices.Add(v);
        await _db.SaveChangesAsync();
        return v;
    }

    private static List<CreateWorkflowStepRequest> Step(string configJson, string type = nameof(StepType.Voiceover)) =>
        [new(Guid.NewGuid(), 1, null, "VO", StepType: type, VoiceoverConfigJson: configJson)];

    private static string Config(Guid? projectVoiceId = null, string? defaultVoiceId = null, string? lineVoiceId = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            source = new { kind = "Inline", lines = new[] { new { text = "Hi", startSec = 0.0, voiceId = lineVoiceId } } },
            providerId = Guid.NewGuid(),
            model = "m",
            defaultVoiceId,
            projectVoiceId
        });

    private async Task<(IReadOnlyList<string> Async, IReadOnlyList<string> Sync)> Both(List<CreateWorkflowStepRequest> steps) =>
        (await VoiceoverVoiceConsentSaveValidator.ValidateAsync(_db, ProjectId, steps, CancellationToken.None),
         VoiceoverVoiceConsentSaveValidator.Validate(_db, ProjectId, steps));

    [Fact]
    public async Task A_usable_clone_a_raw_vendor_id_and_no_voice_are_accepted()
    {
        ProjectVoice ok = await AddVoice("ok-vendor");
        foreach (string config in new[] { Config(ok.Id), Config(defaultVoiceId: "ok-vendor"), Config(lineVoiceId: "raw-id"), Config() })
        {
            var (a, s) = await Both(Step(config));
            a.Should().BeEmpty();
            s.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task A_revoked_clone_named_by_id_default_voice_or_line_voice_is_refused()
    {
        ProjectVoice revoked = await AddVoice("rev-vendor", v => v.ConsentRevokedAt = DateTime.UtcNow);
        foreach (string config in new[] { Config(revoked.Id), Config(defaultVoiceId: "rev-vendor"), Config(lineVoiceId: "rev-vendor") })
        {
            var (a, s) = await Both(Step(config));
            a.Should().ContainSingle().Which.Should().StartWith("Step 1 (Voiceover): ").And.Contain("cannot be used");
            s.Should().Equal(a);
        }
    }

    [Fact]
    public async Task Expired_and_unattested_clones_are_refused_and_a_missing_project_voice_is_not_found()
    {
        ProjectVoice expired = await AddVoice("exp", v => v.RetentionUntil = DateTime.UtcNow.AddDays(-1));
        ProjectVoice unattested = await AddVoice("una", v => v.ConsentAttestedAt = null);
        (await Both(Step(Config(expired.Id)))).Async.Should().ContainSingle().Which.Should().Contain("retention_expired");
        (await Both(Step(Config(unattested.Id)))).Async.Should().ContainSingle().Which.Should().Contain("consent_missing");
        (await Both(Step(Config(Guid.NewGuid())))).Async.Should().ContainSingle().Which.Should().Contain("not found");
    }

    [Fact]
    public async Task Another_projects_voice_is_not_found_and_other_step_types_are_never_checked()
    {
        ProjectVoice foreign = await AddVoice("f", v => v.ConsentRevokedAt = DateTime.UtcNow, Guid.NewGuid());
        (await Both(Step(Config(foreign.Id)))).Async.Should().ContainSingle().Which.Should().Contain("not found");
        (await Both(Step(Config(defaultVoiceId: "f")))).Async.Should().ContainSingle("another project's clone is refused by vendor id");

        ProjectVoice revoked = await AddVoice("r2", v => v.ConsentRevokedAt = DateTime.UtcNow);
        (await Both(Step(Config(revoked.Id), nameof(StepType.Extract)))).Async.Should().BeEmpty();
    }

    [Fact]
    public async Task The_shared_save_validator_runs_the_check_on_both_its_paths()
    {
        ProjectVoice revoked = await AddVoice("r3", v => v.ConsentRevokedAt = DateTime.UtcNow);
        var validator = new StepProviderOwnershipValidator(_db);
        (await validator.ValidateAsync(Guid.NewGuid(), ProjectId, Step(Config(revoked.Id)), CancellationToken.None))
            .Should().Contain(e => e.Contains("consent_revoked"));
        validator.Validate(Guid.NewGuid(), ProjectId, Step(Config(revoked.Id))).Should().Contain(e => e.Contains("consent_revoked"));
    }

    [Fact]
    public async Task A_clone_of_another_project_is_refused_with_the_same_message_as_an_unusable_clone_here()
    {
        await AddVoice("foreign-ok", project: Guid.NewGuid());                       // usable, but its consent is another project's
        await AddVoice("here-revoked", v => v.ConsentRevokedAt = DateTime.UtcNow);   // unusable here
        string foreign = (await Both(Step(Config(defaultVoiceId: "foreign-ok")))).Async.Single();
        string here = (await Both(Step(Config(defaultVoiceId: "here-revoked")))).Async.Single();

        foreign.Replace("foreign-ok", "X").Should().Be(here.Replace("here-revoked", "X"));
        foreign.Should().NotContain("project voice");
    }

    [Fact]
    public async Task A_library_row_elsewhere_is_harmless_and_this_projects_usable_row_wins_over_anothers_unusable_clone()
    {
        await AddVoice("lib-id", v => { v.Source = ProjectVoiceSource.Library; v.ConsentAttestedAt = null; }, Guid.NewGuid());
        await AddVoice("shared", project: ProjectId);
        await AddVoice("shared", v => v.ConsentRevokedAt = DateTime.UtcNow, Guid.NewGuid());
        (await Both(Step(Config(defaultVoiceId: "lib-id")))).Async.Should().BeEmpty();
        (await Both(Step(Config(defaultVoiceId: "shared")))).Async.Should().BeEmpty();
    }

    /// <summary>AUTHZ-IA-1: a Library row here carrying another project's clone id must not stand in for that clone's consent.</summary>
    [Fact]
    public async Task An_own_library_row_does_not_win_over_an_in_scope_clone_of_another_project()
    {
        static void Library(ProjectVoice v) { v.Source = ProjectVoiceSource.Library; v.ConsentAttestedAt = null; }
        await AddVoice("laundered", Library);
        await AddVoice("laundered", project: Guid.NewGuid(), org: OrgA);          // a sibling project's usable clone
        await AddVoice("alien-clone", Library);
        await AddVoice("alien-clone", project: Guid.NewGuid(), org: OrgB);        // out of scope: another organization's private account
        await AddVoice("genuine", Library);

        (await Both(Step(Config(defaultVoiceId: "laundered")))).Async.Should().ContainSingle()
            .Which.Should().Contain("cannot be used");
        (await Both(Step(Config(lineVoiceId: "laundered")))).Async.Should().ContainSingle();
        (await Both(Step(Config(defaultVoiceId: "alien-clone")))).Async.Should().BeEmpty("an out-of-scope clone is not consulted");
        (await Both(Step(Config(defaultVoiceId: "genuine")))).Async.Should().BeEmpty("a genuine library voice keeps working");
    }

    [Fact]
    public async Task A_lowercase_step_type_is_still_validated_and_duplicate_config_keys_read_like_the_executor()
    {
        ProjectVoice usable = await AddVoice("u");
        ProjectVoice revoked = await AddVoice("r", v => v.ConsentRevokedAt = DateTime.UtcNow);
        (await Both(Step(Config(revoked.Id), "voiceover"))).Async.Should().ContainSingle();
        (await Both(Step(Config(revoked.Id), " VOICEOVER "))).Async.Should().ContainSingle();

        string dup(Guid first, Guid last) =>
            $"{{\"source\":{{\"kind\":\"Inline\",\"lines\":[]}},\"providerId\":\"{Guid.NewGuid()}\",\"model\":\"m\",\"projectVoiceId\":\"{first}\",\"ProjectVoiceId\":\"{last}\"}}";
        (await Both(Step(dup(usable.Id, revoked.Id)))).Async.Should().ContainSingle("the last key wins, as in the executor");
        (await Both(Step(dup(revoked.Id, usable.Id)))).Async.Should().BeEmpty();
    }

    [Fact]
    public async Task A_clone_in_another_organization_is_not_consulted_but_a_sibling_project_in_the_same_organization_is()
    {
        await AddVoice("alien", project: Guid.NewGuid(), org: OrgB);
        await AddVoice("sibling", project: Guid.NewGuid(), org: OrgA);
        (await Both(Step(Config(defaultVoiceId: "alien")))).Async.Should().BeEmpty("another organization's id is treated as no row");
        (await Both(Step(Config(lineVoiceId: "alien")))).Sync.Should().BeEmpty();
        (await Both(Step(Config(defaultVoiceId: "sibling")))).Async.Should().ContainSingle();
    }

    [Fact]
    public async Task Foreign_clones_are_judged_by_organization_and_by_the_account_behind_their_capability()
    {
        await AddVoice("plat", project: Guid.NewGuid(), org: OrgB, platformProvider: true);          // other org, platform account: refused
        await AddVoice("priv", project: Guid.NewGuid(), org: OrgB);                                   // other org, its private account: ignored
        await AddVoice("fnull", v => v.InferenceProviderId = null, Guid.NewGuid(), OrgB);             // other org, no provider: ignored
        await AddVoice("fgone", project: Guid.NewGuid(), org: OrgB, unresolvableCapability: true);    // other org, capability gone: ignored
        await AddVoice("snull", v => v.InferenceProviderId = null, Guid.NewGuid(), OrgA);             // same org: refused whatever the provider says
        await AddVoice("sgone", project: Guid.NewGuid(), org: OrgA, unresolvableCapability: true);
        await AddVoice("slive", project: Guid.NewGuid(), org: OrgA);

        foreach (string refused in new[] { "plat", "snull", "sgone", "slive" })
            (await Both(Step(Config(defaultVoiceId: refused)))).Async.Should().ContainSingle(refused);
        foreach (string ignored in new[] { "priv", "fnull", "fgone" })
            (await Both(Step(Config(lineVoiceId: ignored)))).Sync.Should().BeEmpty(ignored);
    }
}
