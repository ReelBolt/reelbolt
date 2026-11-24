using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Drift;

/// <summary>
/// P3-01: the drift service stages 0/1 — changed/added/removed sources, locks, suppression and the
/// product-map debounce and threshold. No chat client is anywhere near this path.
/// </summary>
public sealed class DesignSystemDriftServiceTests : IDisposable
{
    private static readonly Guid HomeOrg = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000031");

    private readonly InferenceApiDbContext _db;
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Mock<IFileStorageService> _storage = new();
    private readonly IConfiguration _config = new ConfigurationBuilder().Build();

    public DesignSystemDriftServiceTests()
    {
        _db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase($"design-system-drift-{Guid.NewGuid():N}")
            .Options);

        _storage
            .Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, string key, CancellationToken _) =>
                _objects.TryGetValue(key, out byte[]? bytes) ? new MemoryStream(bytes) : new MemoryStream([]));
    }

    public void Dispose() => _db.Dispose();

    // -------------------------------------------------------------------------------------------------
    // Design document
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Design_change_creates_a_proposal_with_expected_diff_and_score()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (DesignSystem system, DesignSystemVersion current) =
            await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        _objects[css.StorageKey] = Css("#7c3aed");

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);

        outcome.Result.Should().Be("proposal");
        outcome.ProposalId.Should().NotBeNull();

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        proposal.Status.Should().Be(DesignProposalStatus.Pending);
        proposal.Trigger.Should().Be(DesignProposalTrigger.Manual);
        proposal.BaseVersionId.Should().Be(current.Id);
        proposal.DriftScore.Should().BeApproximately(0.25, 0.001);

        JsonObject diff = JsonNode.Parse(proposal.DiffJson)!.AsObject();
        diff["counts"]!["changed"]!.GetValue<int>().Should().Be(1);
        JsonObject changed = diff["changed"]!.AsArray()[0]!.AsObject();
        changed["path"]!.GetValue<string>().Should().Be("color.brand.primary");
        changed["before"]!["hex"]!.GetValue<string>().Should().Be("#6d28d9");
        changed["after"]!["hex"]!.GetValue<string>().Should().Be("#7c3aed");

        DesignSystemVersion draft = _db.DesignSystemVersions.Single(v => v.Id == proposal.ProposedVersionId);
        draft.State.Should().Be(DesignVersionState.Draft);
        draft.Origin.Should().Be(DesignVersionOrigin.StaticExtract);

        // D-7: drift only drafts. The live version is the same one before and after, and still Approved.
        _db.DesignSystems.Single(s => s.Id == system.Id).CurrentVersionId.Should().Be(current.Id);
        _db.DesignSystemVersions.Single(v => v.Id == current.Id).State.Should().Be(DesignVersionState.Approved);
        draft.BaseVersionId.Should().Be(current.Id);
        draft.DocumentDigest.Should().Be(proposal.ProposedDigest);
    }

    [Fact]
    public async Task Identical_rerun_is_suppressed()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        _objects[css.StorageKey] = Css("#7c3aed");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("proposal");
        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None))
            .Result.Should().Be("nochange", "the live proposal already covers this exact content");
    }

    [Fact]
    public async Task Rejected_proposal_with_the_same_digest_is_suppressed()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        _objects[css.StorageKey] = Css("#7c3aed");

        DesignDriftOutcome first = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == first.ProposalId);
        proposal.Status = DesignProposalStatus.Rejected;
        proposal.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange", "inside the rejection window the same content raises nothing");
        _db.DesignSystemChangeProposals.Count().Should().Be(1);
    }

    [Fact]
    public async Task Locked_token_contradiction_reports_conflict_and_keeps_the_base_value()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (DesignSystem system, DesignSystemVersion current) =
            await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        JsonObject baseDoc = JsonNode.Parse(current.DocumentJson)!.AsObject();
        JsonObject locked = DesignDocument.FindToken(baseDoc, "color.brand.primary")!;
        locked["$extensions"]!["com.reelbolt"]!["locked"] = true;
        string canonical = CanonicalJson.Serialize(baseDoc);
        current.DocumentJson = canonical;
        current.DocumentDigest = CanonicalJson.Sha256Hex(canonical);
        await _db.SaveChangesAsync();

        _objects[css.StorageKey] = Encoding.UTF8.GetBytes(
            ":root { --color-brand-primary: #7c3aed; --color-brand-accent: #f59e0b; }");

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("proposal");

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        JsonObject diff = JsonNode.Parse(proposal.DiffJson)!.AsObject();
        JsonObject changed = diff["changed"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(n => n["path"]!.GetValue<string>() == "color.brand.primary");
        changed["locked"]!.GetValue<bool>().Should().BeTrue();
        changed["conflict"]!.GetValue<bool>().Should().BeTrue();

        DesignSystemVersion draft = _db.DesignSystemVersions.Single(v => v.Id == proposal.ProposedVersionId);
        JsonObject proposedDoc = JsonNode.Parse(draft.DocumentJson)!.AsObject();
        DesignDocument.FindToken(proposedDoc, "color.brand.primary")!["$value"]!["hex"]!
            .GetValue<string>().Should().Be("#6d28d9");
    }

    [Fact]
    public async Task New_matching_file_is_added()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        SeedFile(
            project.Id,
            "brand/theme.tokens.json",
            Encoding.UTF8.GetBytes(
                "{\"color\":{\"brand\":{\"accent\":{\"$type\":\"color\",\"$value\":{\"hex\":\"#f59e0b\",\"colorSpace\":\"srgb\"}}}}}"));

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("proposal");

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        JsonObject diff = JsonNode.Parse(proposal.DiffJson)!.AsObject();
        diff["added"]!.AsArray()
            .Select(n => n!["path"]!.GetValue<string>())
            .Should().Contain("color.brand.accent");
    }

    [Fact]
    public async Task Removed_source_is_marked_missing()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (DesignSystem system, DesignSystemVersion current) =
            await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        _db.ProjectFiles.Remove(css);
        await _db.SaveChangesAsync();
        _objects.Remove(css.StorageKey);

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("nochange");

        DesignSystemSource source = _db.DesignSystemSources
            .Single(s => s.VersionId == current.Id && s.Path == "app/globals.css");
        source.Status.Should().Be(DesignSourceStatus.Missing);
    }

    [Fact]
    public async Task Design_diff_describes_the_proposed_document_not_the_raw_import()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(
            project.Id, "app/globals.css",
            Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; --color-brand-accent: #f59e0b; }"));
        (_, DesignSystemVersion current) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        // The accent token leaves the file and the primary changes. Merge keeps the unlocked accent, so
        // the draft still carries it and the diff a human approves must not call it removed.
        _objects[css.StorageKey] = Css("#7c3aed");

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("proposal");

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        JsonObject diff = JsonNode.Parse(proposal.DiffJson)!.AsObject();
        diff["removed"]!.AsArray().Should().BeEmpty();
        diff["counts"]!["removed"]!.GetValue<int>().Should().Be(0);

        JsonObject baseDoc = JsonNode.Parse(current.DocumentJson)!.AsObject();
        DesignSystemVersion draft = _db.DesignSystemVersions.Single(v => v.Id == proposal.ProposedVersionId);
        JsonObject draftDoc = JsonNode.Parse(draft.DocumentJson)!.AsObject();
        DesignDocument.FindToken(draftDoc, "color.brand.accent").Should().NotBeNull();
        DesignDocument.FindToken(baseDoc, "color.brand.accent").Should().NotBeNull();
    }

    [Fact]
    public async Task Design_removal_only_change_is_nochange_because_the_merged_document_is_unchanged()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(
            project.Id, "app/globals.css",
            Encoding.UTF8.GetBytes(":root { --color-brand-primary: #6d28d9; --color-brand-accent: #f59e0b; }"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        _objects[css.StorageKey] = Css("#6d28d9");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");
        _db.DesignSystemChangeProposals.Should().BeEmpty();
    }

    [Fact]
    public async Task Design_provenance_only_change_does_not_create_a_zero_change_proposal()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        // Same tokens, but a leading comment moves every token's source location.
        _objects[css.StorageKey] = Encoding.UTF8.GetBytes(
            "/* a\n b\n c */\n\n:root { --color-brand-primary: #6d28d9; }");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");
        _db.DesignSystemChangeProposals.Should().BeEmpty();
    }

    [Fact]
    public async Task Product_proposal_keeps_base_llm_fields_and_the_diff_reports_what_changes()
    {
        Project project = SeedProject();
        ProjectFile package = SeedFile(
            project.Id, "package.json", Encoding.UTF8.GetBytes("{\"dependencies\":{\"next\":\"15.0.0\"}}"));
        ProjectFile home = SeedFile(project.Id, "app/page.tsx", Page("Home"));
        (_, DesignSystemVersion current) =
            await ImportAndApproveAsync(project, DesignSystemKinds.ProductMap, package.Id, home.Id);

        // Model-derived fields: an entry point the static extractor cannot see, and an architecture note
        // the static extraction will override (static wins a section it has).
        ProductMapDocument baseMap = ProductMapJson.Parse(current.DocumentJson)!;
        baseMap.EntryPoints.Should().BeEmpty("the static extractor finds no entry point in this project");
        baseMap = baseMap with { Architecture = "modular monolith", EntryPoints = [new("server/main.ts", "api")] };
        current.DocumentJson = ProductMapJson.Serialize(baseMap);
        current.DocumentDigest = CanonicalJson.Sha256Hex(current.DocumentJson);
        await _db.SaveChangesAsync();

        SeedFile(project.Id, "app/pricing/page.tsx", Page("Pricing"));
        SeedFile(project.Id, "app/about/page.tsx", Page("About"));
        SeedFile(project.Id, "app/blog/page.tsx", Page("Blog"));

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("proposal");

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        DesignSystemVersion draft = _db.DesignSystemVersions.Single(v => v.Id == proposal.ProposedVersionId);
        ProductMapDocument proposed = ProductMapJson.Parse(draft.DocumentJson)!;
        proposed.EntryPoints.Select(e => e.Path).Should().Contain("server/main.ts");

        // The static architecture replaces the model's note, and the diff says so instead of staying silent.
        JsonObject product = JsonNode.Parse(proposal.DiffJson)!.AsObject()["product"]!.AsObject();
        JsonObject field = product["fields"]!.AsArray().Select(n => n!.AsObject())
            .Single(n => n["name"]!.GetValue<string>() == "architecture");
        field["before"]!.GetValue<string>().Should().Be("modular monolith");
        proposal.ProposedDigest.Should().Be(draft.DocumentDigest);
    }

    private static byte[] Palette(string hex, string pad = "") =>
        Encoding.UTF8.GetBytes(
            pad + "{\"color\":{\"brand\":{\"accent\":{\"$type\":\"color\",\"$value\":{\"hex\":\"" + hex + "\",\"colorSpace\":\"srgb\"}}}}}");

    [Fact]
    public async Task Source_outside_the_six_globs_is_reread_and_a_real_change_proposes()
    {
        Project project = SeedProject();
        ProjectFile palette = SeedFile(project.Id, "design/palette.json", Palette("#f59e0b"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, palette.Id);

        _objects[palette.StorageKey] = Palette("#10b981");

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("proposal");

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        JsonNode.Parse(proposal.DiffJson)!["changed"]!.AsArray()
            .Select(n => n!["path"]!.GetValue<string>()).Should().Contain("color.brand.accent");
    }

    [Fact]
    public async Task Changed_source_outside_the_globs_that_re_reads_identical_is_resolved_to_current()
    {
        Project project = SeedProject();
        ProjectFile palette = SeedFile(project.Id, "design/palette.json", Palette("#f59e0b"));
        (_, DesignSystemVersion current) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, palette.Id);

        _objects[palette.StorageKey] = Palette("#f59e0b", pad: "\n\n");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");

        _db.DesignSystemSources.Single(s => s.VersionId == current.Id && s.Path == "design/palette.json")
            .Status.Should().Be(DesignSourceStatus.Current, "a source whose re-read changes nothing must not stay Changed");
    }

    [Fact]
    public async Task Already_missing_source_does_not_rerun_stage_one_on_every_check()
    {
        Project project = SeedProject();
        ProjectFile a = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        ProjectFile b = SeedFile(
            project.Id, "app/extra.css", Encoding.UTF8.GetBytes(":root { --color-brand-accent: #f59e0b; }"));
        (_, DesignSystemVersion current) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, a.Id, b.Id);

        _db.ProjectFiles.Remove(b);
        await _db.SaveChangesAsync();
        _objects.Remove(b.StorageKey);

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");
        _db.DesignSystemSources.Single(s => s.VersionId == current.Id && s.Path == "app/extra.css")
            .Status.Should().Be(DesignSourceStatus.Missing);

        _storage.Invocations.Clear();
        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");

        // Stage 0 re-hashes the one surviving source; a rerun of stage 1 would download it again.
        _storage.Invocations.Count(i => i.Method.Name == nameof(IFileStorageService.DownloadAsync))
            .Should().Be(1);
    }

    [Fact]
    public async Task Unparsed_matching_file_is_recorded_as_a_seen_source_and_is_not_new_on_every_check()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (_, DesignSystemVersion current) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        ProjectFile logo = SeedFile(project.Id, "brand/logo.svg", Encoding.UTF8.GetBytes("<svg/>"), sourceRevision: "rev-1");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");

        DesignSystemSource seen = _db.DesignSystemSources.Single(s => s.VersionId == current.Id && s.Path == "brand/logo.svg");
        seen.Importer.Should().Be("seen");
        seen.Status.Should().Be(DesignSourceStatus.Current);
        seen.ProjectFileId.Should().Be(logo.Id);

        _storage.Invocations.Clear();
        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");

        // Only the stage-0 re-hash of the stylesheet (it carries no revision); no stage 1.
        _storage.Invocations.Count(i => i.Method.Name == nameof(IFileStorageService.DownloadAsync))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_superseded_proposal_rejects_its_draft_and_the_newest_draft_stays()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        _objects[css.StorageKey] = Css("#7c3aed");
        DesignDriftOutcome first = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        _objects[css.StorageKey] = Css("#10b981");
        DesignDriftOutcome second = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);

        DesignSystemChangeProposal older = _db.DesignSystemChangeProposals.Single(p => p.Id == first.ProposalId);
        DesignSystemChangeProposal newer = _db.DesignSystemChangeProposals.Single(p => p.Id == second.ProposalId);
        older.Status.Should().Be(DesignProposalStatus.Superseded);
        newer.Status.Should().Be(DesignProposalStatus.Pending);

        _db.DesignSystemVersions.Single(v => v.Id == older.ProposedVersionId).State
            .Should().Be(DesignVersionState.Rejected, "a superseded proposal must not leave a Draft behind");
        _db.DesignSystemVersions.Single(v => v.Id == newer.ProposedVersionId).State
            .Should().Be(DesignVersionState.Draft);
    }

    [Fact]
    public async Task Drafts_per_system_are_capped_oldest_first_and_a_pending_proposals_draft_is_kept()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (DesignSystem system, DesignSystemVersion current) =
            await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        // The oldest draft belongs to a Pending proposal, so the cap must leave it alone.
        _objects[css.StorageKey] = Css("#7c3aed");
        DesignDriftOutcome pending = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        Guid protectedDraft = _db.DesignSystemChangeProposals.Single(p => p.Id == pending.ProposalId).ProposedVersionId;

        for (int i = 0; i < 30; i++)
        {
            JsonObject document = JsonNode.Parse(current.DocumentJson)!.AsObject();
            document["name"] = $"manual draft {i}";
            DesignSystemWriteResult write = await DesignService().CreateDraftAsync(
                project.Id, _ownerId, DesignSystemKinds.Design, document, DesignVersionOrigin.Manual,
                null, null, [], current.Id, CancellationToken.None);
            write.Status.Should().Be(DesignSystemWriteStatus.Ok);
        }

        List<DesignSystemVersion> drafts = _db.DesignSystemVersions
            .Where(v => v.DesignSystemId == system.Id && v.State == DesignVersionState.Draft)
            .ToList();
        drafts.Count.Should().BeLessThanOrEqualTo(20);
        drafts.Select(v => v.Id).Should().Contain(protectedDraft);
        _db.DesignSystemVersions.Count(v => v.DesignSystemId == system.Id && v.State == DesignVersionState.Rejected)
            .Should().BeGreaterThan(0, "the overflow is rejected, not silently kept");
        _db.DesignSystemVersions.Single(v => v.Id == current.Id).State.Should().Be(DesignVersionState.Approved);
    }

    [Fact]
    public async Task A_pending_proposal_does_not_make_every_later_push_rerun_stage_one()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        _objects[css.StorageKey] = Css("#7c3aed");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("proposal");

        _storage.Invocations.Clear();
        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None))
            .Result.Should().Be("nochange", "the proposal already covers this exact content");

        // Only the stage-0 re-hash; the pending duplicate does not drag stage 1 along.
        _storage.Invocations.Count(i => i.Method.Name == nameof(IFileStorageService.DownloadAsync)).Should().Be(1);
        _db.DesignSystemChangeProposals.Count().Should().Be(1);
    }

    [Fact]
    public async Task The_same_drift_is_proposed_again_once_the_pending_proposal_expires()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (_, DesignSystemVersion current) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        _objects[css.StorageKey] = Css("#7c3aed");
        DesignDriftOutcome first = await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None);

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == first.ProposalId);
        proposal.Status = DesignProposalStatus.Expired;
        await _db.SaveChangesAsync();

        DesignDriftOutcome again = await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None);

        again.Result.Should().Be("proposal", "an expired proposal no longer covers the drift");
        _db.DesignSystemSources.Single(s => s.VersionId == current.Id && s.Path == "app/globals.css")
            .Status.Should().Be(DesignSourceStatus.Changed, "the approved version's source row is never overwritten by a pending proposal");
    }

    [Fact]
    public async Task The_same_drift_is_proposed_again_once_the_rejection_window_ends_but_not_inside_it()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        _objects[css.StorageKey] = Css("#7c3aed");
        DesignDriftOutcome first = await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == first.ProposalId);
        proposal.Status = DesignProposalStatus.Rejected;
        proposal.DecidedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None))
            .Result.Should().NotBe("proposal", "inside the rejection window");

        proposal.DecidedAt = DateTime.UtcNow.AddDays(-40);
        await _db.SaveChangesAsync();

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None))
            .Result.Should().Be("proposal", "the window has ended");
    }

    [Fact]
    public async Task Changed_files_that_import_to_nothing_leave_the_approved_versions_digest_alone()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (_, DesignSystemVersion current) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);
        string approvedSha = _db.DesignSystemSources.Single(s => s.VersionId == current.Id && s.Path == "app/globals.css").ContentSha256;
        _objects[css.StorageKey] = Encoding.UTF8.GetBytes("/* every token was deleted */");

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("nochange");

        DesignSystemSource row = _db.DesignSystemSources.Single(s => s.VersionId == current.Id && s.Path == "app/globals.css");
        row.ContentSha256.Should().Be(approvedSha, "the digest stage 0 compares against is the approved version's bytes");
        row.Status.Should().Be(DesignSourceStatus.Changed);
    }

    [Fact]
    public async Task Superseding_never_rejects_a_version_that_is_the_systems_live_version()
    {
        Project project = SeedProject();
        ProjectFile css = SeedFile(project.Id, "app/globals.css", Css("#6d28d9"));
        (DesignSystem system, _) = await ImportAndApproveAsync(project, DesignSystemKinds.Design, css.Id);

        _objects[css.StorageKey] = Css("#7c3aed");
        DesignDriftOutcome first = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        Guid draftId = _db.DesignSystemChangeProposals.Single(p => p.Id == first.ProposalId).ProposedVersionId;

        // The draft went live (a person approved it by another route) while its proposal was still Pending.
        _db.DesignSystems.Single(s => s.Id == system.Id).CurrentVersionId = draftId;
        await _db.SaveChangesAsync();

        _objects[css.StorageKey] = Css("#10b981");
        await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);

        _db.DesignSystemVersions.AsNoTracking().Single(v => v.Id == draftId).State
            .Should().Be(DesignVersionState.Draft, "the live version is never rejected by a supersede");
    }

    // -------------------------------------------------------------------------------------------------
    // Product map
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Product_three_new_routes_create_a_proposal()
    {
        Project project = SeedProject();
        ProjectFile package = SeedFile(
            project.Id, "package.json", Encoding.UTF8.GetBytes("{\"dependencies\":{\"next\":\"15.0.0\"}}"));
        ProjectFile home = SeedFile(project.Id, "app/page.tsx", Page("Home"));
        await ImportAndApproveAsync(project, DesignSystemKinds.ProductMap, package.Id, home.Id);

        SeedFile(project.Id, "app/pricing/page.tsx", Page("Pricing"));
        SeedFile(project.Id, "app/about/page.tsx", Page("About"));
        SeedFile(project.Id, "app/blog/page.tsx", Page("Blog"));

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("proposal");

        DesignSystemChangeProposal proposal = _db.DesignSystemChangeProposals.Single(p => p.Id == outcome.ProposalId);
        proposal.DriftScore.Should().BeGreaterThanOrEqualTo(0.05);

        JsonObject product = JsonNode.Parse(proposal.DiffJson)!.AsObject()["product"]!.AsObject();
        product["routes"]!["added"]!.AsArray()
            .Select(n => n!.GetValue<string>())
            .Should().Contain(["/pricing", "/about", "/blog"]);
    }

    [Fact]
    public async Task Product_one_new_component_stays_below_the_threshold()
    {
        Project project = SeedProject();
        ProjectFile package = SeedFile(
            project.Id, "package.json", Encoding.UTF8.GetBytes("{\"dependencies\":{\"next\":\"15.0.0\"}}"));
        ProjectFile home = SeedFile(project.Id, "app/page.tsx", Page("Home"));
        await ImportAndApproveAsync(project, DesignSystemKinds.ProductMap, package.Id, home.Id);

        SeedFile(
            project.Id,
            "components/PriceCard.tsx",
            Encoding.UTF8.GetBytes("export default function PriceCard() { return null; }"));

        DesignDriftOutcome outcome = await Drift().CheckAsync(
            project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None);
        outcome.Result.Should().Be("nochange");
        _db.DesignSystemChangeProposals.Should().BeEmpty();
    }

    [Fact]
    public async Task Product_debounce_skips_a_sweep_but_manual_bypasses_it()
    {
        Project project = SeedProject();
        ProjectFile package = SeedFile(
            project.Id, "package.json", Encoding.UTF8.GetBytes("{\"dependencies\":{\"next\":\"15.0.0\"}}"));
        ProjectFile home = SeedFile(project.Id, "app/page.tsx", Page("Home"));
        await ImportAndApproveAsync(project, DesignSystemKinds.ProductMap, package.Id, home.Id);

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None))
            .Result.Should().Be("nochange");

        SeedFile(project.Id, "app/pricing/page.tsx", Page("Pricing"));

        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Sweep, null, CancellationToken.None))
            .Result.Should().Be("skipped");
        (await Drift().CheckAsync(project.Id, DesignDriftTrigger.Manual, null, CancellationToken.None))
            .Result.Should().Be("proposal");
    }

    // -------------------------------------------------------------------------------------------------
    // No LLM anywhere near drift
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Service_constructor_has_no_chat_dependency()
    {
        List<Type> parameterTypes = typeof(DesignSystemDriftService).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        parameterTypes.Should().NotBeEmpty();
        foreach (Type parameterType in parameterTypes)
        {
            parameterType.FullName.Should().NotBe("Microsoft.Extensions.AI.IChatClient");
            parameterType.Name.Contains("Chat", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            (parameterType.FullName ?? string.Empty)
                .Contains("Chat", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------------------------------

    private static byte[] Css(string hex) =>
        Encoding.UTF8.GetBytes($":root {{ --color-brand-primary: {hex}; }}");

    private static byte[] Page(string name) =>
        Encoding.UTF8.GetBytes($"export default function {name}() {{ return null; }}");

    private static Mock<IPlanGateService> Gate()
    {
        var gate = new Mock<IPlanGateService>();
        gate.Setup(g => g.CheckUploadAsync(It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PlanGateViolation?)null);
        return gate;
    }

    private DesignSystemService DesignService() =>
        new(_db, _storage.Object, Gate().Object, NullLogger<DesignSystemService>.Instance);

    private DesignSystemDriftService Drift(IConfiguration? config = null) =>
        new(
            _db,
            DesignService(),
            _storage.Object,
            Mock.Of<ITenantContext>(),
            config ?? _config,
            NullLogger<DesignSystemDriftService>.Instance);

    private async Task<(DesignSystem System, DesignSystemVersion Version)> ImportAndApproveAsync(
        Project project, string kind, params Guid[] fileIds)
    {
        DesignSystemImportResult import = await DesignService().ImportAsync(
            project.Id, _ownerId, [kind], fileIds, CancellationToken.None);
        import.Status.Should().Be(DesignSystemImportStatus.Ok, import.Status.ToString());

        Guid versionId = import.Versions![0].Id;
        DesignSystemWriteResult approved = await DesignService().ApproveAsync(
            project.Id, versionId, _ownerId, CancellationToken.None);
        approved.Status.Should().Be(DesignSystemWriteStatus.Ok);

        DesignSystem system = _db.DesignSystems.Single(s => s.ProjectId == project.Id && s.Kind == kind);
        DesignSystemVersion version = _db.DesignSystemVersions.Single(v => v.Id == versionId);
        return (system, version);
    }

    private Project SeedProject()
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Drift",
            OwnerId = _ownerId,
            OrganizationId = HomeOrg,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Projects.Add(project);
        _db.SaveChanges();
        return project;
    }

    private ProjectFile SeedFile(Guid projectId, string path, byte[] bytes, string? sourceRevision = null)
    {
        var file = new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalPath = path,
            OriginalFileName = path,
            StorageKey = $"projects/{projectId}/{path}",
            SizeBytes = bytes.Length,
            SourceRevision = sourceRevision,
            MimeType = "text/plain",
            UploadedAt = DateTime.UtcNow,
        };
        _db.ProjectFiles.Add(file);
        _db.SaveChanges();
        _objects[file.StorageKey] = bytes;
        return file;
    }
}
