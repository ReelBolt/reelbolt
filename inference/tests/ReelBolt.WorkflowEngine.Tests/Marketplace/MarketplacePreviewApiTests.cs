using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Shared.Auth;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Consumers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.Marketplace;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

public sealed class MarketplacePreviewApiTests
{
    private static readonly byte[] PreviewBytes = [1, 2, 3, 4, 5];
    private static readonly string PreviewSha = Convert.ToHexString(SHA256.HashData(PreviewBytes)).ToLowerInvariant();

    private sealed class FakeObjects : IMarketplacePreviewObjects
    {
        public readonly Dictionary<string, byte[]> Store = new();
        public readonly List<string> Prefixes = [];
        public readonly List<(string From, string To)> Copies = [];
        public bool FailMove;
        public bool CrashOnCopy;
        public readonly List<string> Deleted = [];

        public Task CopyAsync(string fromKey, string toKey, CancellationToken ct)
        {
            Copies.Add((fromKey, toKey));
            if (CrashOnCopy && MarketplacePreviewKeys.IsQuarantine(toKey))
                throw new OperationCanceledException();
            if (FailMove && MarketplacePreviewKeys.IsQuarantine(toKey))
                throw new IOException("copy to quarantine failed");
            Store[toKey] = Store[fromKey];
            return Task.CompletedTask;
        }

        public Task<Stream> OpenReadAsync(string key, CancellationToken ct) =>
            Store.TryGetValue(key, out byte[]? b) ? Task.FromResult<Stream>(new MemoryStream(b)) : throw new FileNotFoundException(key);

        public Task DeleteAsync(IReadOnlyCollection<string> keys, CancellationToken ct, IReadOnlySet<string>? exclude = null)
        {
            foreach (string k in keys.Where(k => exclude?.Contains(k) != true && !MarketplacePreviewKeys.IsQuarantine(k)))
            {
                Deleted.Add(k);
                Store.Remove(k);
            }
            return Task.CompletedTask;
        }

        public Task MoveAsync(string fromKey, string toKey, CancellationToken ct)
        {
            if (FailMove)
                throw new IOException("move failed");
            Store[toKey] = Store[fromKey];
            Store.Remove(fromKey);
            return Task.CompletedTask;
        }

        public Task DeletePrefixAsync(string prefix, CancellationToken ct, IReadOnlySet<string>? exclude = null)
        {
            Prefixes.Add(prefix);
            foreach (string k in Store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && !MarketplacePreviewKeys.IsQuarantine(k) && exclude?.Contains(k) != true).ToList())
                Store.Remove(k);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCsam : IMarketplacePreviewCsamGate
    {
        public PreviewCsamResult Result = PreviewCsamResult.Clean;
        public int Calls;
        public Action? OnScan;
        public Task<PreviewCsamResult> ScanAsync(string previewKey, string posterKey, CancellationToken ct)
        {
            Calls++;
            OnScan?.Invoke();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeModerator : IMarketplacePreviewModerator
    {
        public PreviewModerationResult Result = new(PreviewModerationOutcome.Allow, null);
        public int Calls;
        public Task<PreviewModerationResult> CheckAsync(string previewKey, Guid organizationId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    /// <summary>When armed, edits the recipe through another context just before each save, so every save of the tracked recipe conflicts.</summary>
    private sealed class ConflictingSaves : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public Func<InferenceApiDbContext>? Other;
        public Guid? RecipeId;
        private bool _inside;

        public override Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> SavingChanges(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result)
        {
            if (RecipeId is { } id && Other is not null && !_inside)
            {
                _inside = true;
                try
                {
                    using InferenceApiDbContext other = Other();
                    other.MarketplaceRecipes.Single(r => r.Id == id).Description = Guid.NewGuid().ToString();
                    other.SaveChanges();
                }
                finally
                {
                    _inside = false;
                }
            }

            return result;
        }

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(SavingChanges(eventData, result));
    }

    private sealed class Harness
    {
        public readonly ConflictingSaves Conflicts = new();
        public readonly DbContextOptions<InferenceApiDbContext> Opts;
        public readonly InferenceApiDbContext Db;
        public InferenceApiDbContext Other() => new(Opts);
        public readonly FakeObjects Objects = new();
        public readonly FakeCsam Csam = new();
        public readonly FakeModerator Moderator = new();
        public readonly Guid RecipeId = Guid.NewGuid();
        public readonly Guid VersionId = Guid.NewGuid();
        public readonly Guid OrgId = Guid.NewGuid();
        public readonly Guid AuthorId = Guid.NewGuid();
        public readonly string Nonce = MarketplacePreviewKeys.NewNonce();

        public Harness(bool trusted = true)
        {
            Opts = new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(Conflicts).Options;
            Conflicts.Other = Other;
            Db = new InferenceApiDbContext(Opts);
            Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = RecipeId, AuthorUserId = AuthorId, AuthorOrgId = OrgId, Status = RecipeStatus.Draft, Name = "R" });
            if (trusted)
                Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = Guid.NewGuid(), AuthorUserId = AuthorId, AuthorOrgId = OrgId, Status = RecipeStatus.Listed, Name = "Old", EverListedAt = DateTime.UtcNow.AddDays(-3) });
            Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion
            {
                Id = VersionId,
                RecipeId = RecipeId,
                N = 1,
                CheckJson = """{"text":"allow"}""",
                PreviewStatus = PreviewStatus.Preparing,
                PreviewRunNonce = Nonce,
                CreatedAt = DateTime.UtcNow
            });
            Db.SaveChanges();
            Objects.Store[MarketplacePreviewKeys.StagingPreview(RecipeId, VersionId, Nonce)] = PreviewBytes;
            Objects.Store[MarketplacePreviewKeys.StagingPoster(RecipeId, VersionId, Nonce)] = [7, 7];
        }

        private sealed class NullStore : IMarketplaceObjectStore
        {
            public Task<int> DeleteRecipeObjectsAsync(Guid recipeId, RecipeObjectProtection protection, CancellationToken ct) => Task.FromResult(0);
            public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(false);
        }

        public MarketplaceSafetyService Safety() => new(Db, new NullStore(), NullLogger<MarketplaceSafetyService>.Instance);

        public MarketplacePreviewFinalizer Finalizer(bool withSafety = false)
        {
            MarketplacePublishService publish = new(
                Db, Mock.Of<ICurrentUser>(), Mock.Of<IProjectAccessService>(), Mock.Of<IMarketplaceTextCheck>(),
                Mock.Of<IMarketplacePreviewDispatcher>(), Mock.Of<ICreditBalanceReader>(), Mock.Of<IMarketplaceCapLock>(),
                Options.Create(new MarketplaceOptions { Enabled = true }), NullLogger<MarketplacePublishService>.Instance);
            return new MarketplacePreviewFinalizer(Db, publish, Objects, Csam, Moderator, NullLogger<MarketplacePreviewFinalizer>.Instance, withSafety ? Safety() : null);
        }

        public MarketplacePreviewFinished Success(string? sha = null, string? nonce = null) => new(
            VersionId, RecipeId, true, null,
            MarketplacePreviewKeys.StagingPreview(RecipeId, VersionId, nonce ?? Nonce), MarketplacePreviewKeys.StagingPoster(RecipeId, VersionId, nonce ?? Nonce),
            sha ?? PreviewSha, 12.5, PreviewBytes.Length, nonce ?? Nonce);

        public MarketplaceRecipeVersion Version() => Db.MarketplaceRecipeVersions.AsNoTracking().Single(v => v.Id == VersionId);
        public MarketplaceRecipe Recipe() => Db.MarketplaceRecipes.AsNoTracking().Single(r => r.Id == RecipeId);
        public JsonObject Check() => JsonNode.Parse(Version().CheckJson!)!.AsObject();
    }

    // ---------------------------------------------------------------- finished consumer matrix

    [Fact]
    public async Task Allow_with_a_trusted_author_lists_and_stores_the_preview_on_the_version()
    {
        Harness h = new();
        await h.Finalizer().HandleAsync(h.Success(), default);

        MarketplaceRecipeVersion v = h.Version();
        v.PreviewStatus.Should().Be(PreviewStatus.Ready);
        v.PreviewKey.Should().Be(MarketplacePreviewKeys.Preview(h.RecipeId, h.VersionId));
        v.PosterKey.Should().Be(MarketplacePreviewKeys.Poster(h.RecipeId, h.VersionId));
        v.PreviewSha256.Should().Be(PreviewSha);
        v.PreviewSeconds.Should().Be(12.5);
        h.Recipe().Status.Should().Be(RecipeStatus.Listed);
        h.Check()["preview"]!["verdict"]!.GetValue<string>().Should().Be("allow");
        h.Check()["preview"]!["csam"]!.GetValue<string>().Should().Be("clean");
        h.Version().PosterSha256.Should().Be(Convert.ToHexString(SHA256.HashData(new byte[] { 7, 7 })).ToLowerInvariant());
        h.Csam.Calls.Should().Be(1);
        h.Moderator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Allow_for_an_untrusted_author_waits_for_a_human()
    {
        Harness h = new(trusted: false);
        await h.Finalizer().HandleAsync(h.Success(), default);
        h.Recipe().Status.Should().Be(RecipeStatus.Pending);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Ready);
    }

    [Fact]
    public async Task Review_band_goes_to_pending()
    {
        Harness h = new();
        h.Moderator.Result = new(PreviewModerationOutcome.Review, "violence");
        await h.Finalizer().HandleAsync(h.Success(), default);
        h.Recipe().Status.Should().Be(RecipeStatus.Pending);
        h.Check()["preview"]!["moderation"]!.GetValue<string>().Should().Be("review");
    }

    [Fact]
    public async Task Moderation_unavailable_never_auto_lists_it_goes_to_a_human()
    {
        Harness h = new();
        h.Moderator.Result = new(PreviewModerationOutcome.Unavailable, "moderation_disabled");
        await h.Finalizer().HandleAsync(h.Success(), default);
        h.Recipe().Status.Should().Be(RecipeStatus.Pending);
        // The version waits in the queue; the recipe's current version changes only when an admin approves it.
        h.Recipe().CurrentVersionId.Should().BeNull();
        h.Check()["decision"]!.GetValue<string>().Should().Be("pending");
        h.Check()["preview"]!["moderation"]!.GetValue<string>().Should().Be("unavailable");
    }

    [Fact]
    public async Task A_moderation_block_rejects_the_version_and_deletes_the_objects()
    {
        Harness h = new();
        h.Moderator.Result = new(PreviewModerationOutcome.Block, "sexual");
        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Recipe().Status.Should().Be(RecipeStatus.Rejected);
        h.Version().PreviewKey.Should().BeNull();
        h.Objects.Store.Keys.Should().NotContain(k => k.StartsWith("marketplace/", StringComparison.Ordinal));
        h.Objects.Copies.Should().BeEmpty("a blocked preview is never copied to the final key");
        h.Check()["reason"]!.GetValue<string>().Should().Be("preview_blocked");
    }

    [Fact]
    public async Task A_csam_match_quarantines_removes_the_public_objects_rejects_and_skips_moderation()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Match;
        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Recipe().Status.Should().Be(RecipeStatus.Rejected);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Version().PreviewKey.Should().BeNull();
        h.Objects.Store.Should().ContainKey(MarketplacePreviewKeys.QuarantinedPreview(h.RecipeId, h.VersionId));
        h.Objects.Store.Should().NotContainKey(MarketplacePreviewKeys.Preview(h.RecipeId, h.VersionId));
        h.Objects.Store.Should().NotContainKey(MarketplacePreviewKeys.Poster(h.RecipeId, h.VersionId));
        h.Moderator.Calls.Should().Be(0, "the CSAM gate comes first and suspected material never goes to the moderation vendor");
        h.Db.MarketplaceAudit.Any(a => a.Action == "preview_csam_match").Should().BeTrue();
    }

    [Fact]
    public async Task With_the_csam_control_off_an_allowed_preview_never_auto_lists()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.NotConfigured;
        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Recipe().Status.Should().Be(RecipeStatus.Pending);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Ready);
        h.Check()["preview"]!["unscanned"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_csam_match_removes_a_listed_recipe_sets_the_flags_and_holds_the_source_project()
    {
        Harness h = new();
        Guid project = Guid.NewGuid();
        h.Db.Projects.Add(new ReelBolt.Shared.Data.Models.Project { Id = project, Name = "P", OwnerId = h.AuthorId, OrganizationId = h.OrgId });
        h.Db.MarketplaceRecipeVersions.Single().PreviewSourceJson = new JsonObject { ["kind"] = "file", ["key"] = $"projects/{project}/userFiles/a.mp4" }.ToJsonString();
        MarketplaceRecipe r = h.Db.MarketplaceRecipes.Single(x => x.Id == h.RecipeId);
        r.Status = RecipeStatus.Listed;
        await h.Db.SaveChangesAsync();
        h.Csam.Result = PreviewCsamResult.Match;

        await h.Finalizer().HandleAsync(h.Success(), default);

        MarketplaceRecipe after = h.Recipe();
        after.Status.Should().Be(RecipeStatus.Removed);
        after.RemovedReason.Should().Be("safety");
        after.HumanRejectedAt.Should().NotBeNull();
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["evidence"]!.GetValue<string>().Should().Be("quarantined");
        var p = h.Db.Projects.AsNoTracking().IgnoreQueryFilters().Single(x => x.Id == project);
        p.CsamOutputHoldAt.Should().NotBeNull();
        p.ContentQuarantinedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_match_moves_both_objects_and_deletes_a_source_only_after_its_copy_is_verified()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Match;

        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Objects.Store.Should().ContainKey(MarketplacePreviewKeys.QuarantinedPreview(h.RecipeId, h.VersionId));
        h.Objects.Store.Should().ContainKey(MarketplacePreviewKeys.QuarantinedPoster(h.RecipeId, h.VersionId));
        h.Objects.Store.Keys.Should().NotContain(k => k.Contains("/staging/"));
        h.Objects.Deleted.Should().BeEquivalentTo(
            [MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, h.Nonce), MarketplacePreviewKeys.StagingPoster(h.RecipeId, h.VersionId, h.Nonce)]);
        h.Check()["evidence"]!.GetValue<string>().Should().Be("quarantined");
    }

    [Fact]
    public async Task A_held_version_is_skipped_by_the_sweeper_and_the_evidence_guard_reads_its_keys()
    {
        Harness h = new();
        MarketplaceRecipeVersion v = h.Db.MarketplaceRecipeVersions.Single();
        string held = MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, h.Nonce);
        v.CheckJson = new JsonObject
        {
            ["evidence"] = "staged",
            ["evidenceKeys"] = new JsonArray(JsonValue.Create(held)),
            ["previewRedispatchedAt"] = DateTime.UtcNow.AddHours(-3).ToString("O")
        }.ToJsonString();
        v.CreatedAt = DateTime.UtcNow.AddHours(-4);
        await h.Db.SaveChangesAsync();
        MarketplaceEvidenceGuard.IsHeld(v.CheckJson).Should().BeTrue();
        MarketplaceEvidenceGuard.HeldKeys(v.CheckJson).Should().Contain(held);
        MarketplaceEvidenceGuard.IsHeld("{\"evidence\":\"quarantined\"}").Should().BeFalse();

        await Sweeper().SweepAsync(h.Db, new RecordingDispatcher(), DateTime.UtcNow, default, h.Objects);

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Objects.Prefixes.Should().BeEmpty();
        h.Objects.Store.Should().ContainKey(held);
    }

    [Fact]
    public async Task The_store_prefix_delete_honours_an_exclusion_set()
    {
        Guid r = Guid.NewGuid();
        Guid v = Guid.NewGuid();
        string keep = MarketplacePreviewKeys.StagingPreview(r, v, MarketplacePreviewKeys.NewNonce());
        string drop = MarketplacePreviewKeys.Poster(r, v);
        var s3 = new Mock<Amazon.S3.IAmazonS3>();
        s3.Setup(x => x.ListObjectsV2Async(It.IsAny<Amazon.S3.Model.ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response { S3Objects = [new Amazon.S3.Model.S3Object { Key = keep }, new Amazon.S3.Model.S3Object { Key = drop }] });
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<Amazon.S3.Model.DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.DeleteObjectResponse());
        S3MarketplacePreviewObjects store = new(s3.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        await store.DeletePrefixAsync(MarketplacePreviewKeys.RecipePrefix(r), default, new HashSet<string> { keep });

        s3.Verify(x => x.DeleteObjectAsync(It.Is<Amazon.S3.Model.DeleteObjectRequest>(d => d.Key == keep), It.IsAny<CancellationToken>()), Times.Never);
        s3.Verify(x => x.DeleteObjectAsync(It.Is<Amazon.S3.Model.DeleteObjectRequest>(d => d.Key == drop), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_member_who_publishes_from_a_teammates_project_is_flagged_and_their_other_recipes_come_down()
    {
        Harness h = new();
        Guid member = Guid.NewGuid();
        Guid otherOrg = Guid.NewGuid();
        h.Db.MarketplaceRecipeVersions.Single().SubmittedByUserId = member;
        Guid otherRecipe = Guid.NewGuid();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = otherRecipe, AuthorUserId = Guid.NewGuid(), AuthorOrgId = otherOrg, Status = RecipeStatus.Listed, Name = "Elsewhere", EverListedAt = DateTime.UtcNow });
        h.Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion { Id = Guid.NewGuid(), RecipeId = otherRecipe, N = 1, SubmittedByUserId = member, PreviewStatus = PreviewStatus.Ready, CreatedAt = DateTime.UtcNow });
        Guid untouched = Guid.NewGuid();
        h.Db.MarketplaceRecipes.Add(new MarketplaceRecipe { Id = untouched, AuthorUserId = Guid.NewGuid(), AuthorOrgId = Guid.NewGuid(), Status = RecipeStatus.Listed, Name = "Clean", EverListedAt = DateTime.UtcNow });
        h.Db.MarketplaceRecipeVersions.Add(new MarketplaceRecipeVersion { Id = Guid.NewGuid(), RecipeId = untouched, N = 1, SubmittedByUserId = Guid.NewGuid(), PreviewStatus = PreviewStatus.Ready, CreatedAt = DateTime.UtcNow });
        await h.Db.SaveChangesAsync();
        h.Csam.Result = PreviewCsamResult.Match;

        await h.Finalizer(withSafety: true).HandleAsync(h.Success(), default);

        (await MarketplaceSafetyService.HasCsamMatchAsync(h.Db, member, default)).Should().BeTrue();
        (await MarketplaceSafetyService.HasCsamMatchAsync(h.Db, h.AuthorId, default)).Should().BeFalse();
        h.Db.MarketplaceRecipes.AsNoTracking().Single(r => r.Id == otherRecipe).Status.Should().Be(RecipeStatus.Removed);
        h.Db.MarketplaceRecipes.AsNoTracking().Single(r => r.Id == untouched).Status.Should().Be(RecipeStatus.Listed);
    }

    [Fact]
    public async Task The_csam_response_survives_a_concurrent_revision_conflict()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Match;
        h.Csam.OnScan = () =>
        {
            using InferenceApiDbContext other = h.Other();
            other.MarketplaceRecipes.Single(r => r.Id == h.RecipeId).Description = "edited elsewhere";
            other.SaveChanges();
        };

        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Recipe().Status.Should().Be(RecipeStatus.Rejected);
        h.Recipe().HumanRejectedAt.Should().NotBeNull();
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["evidence"]!.GetValue<string>().Should().Be("quarantined");
    }

    [Fact]
    public async Task When_every_retry_of_the_first_csam_save_fails_the_staged_evidence_is_not_deleted()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Match;
        h.Csam.OnScan = () => h.Conflicts.RecipeId = h.RecipeId;
        string staged = MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, h.Nonce);

        Func<Task> act = () => h.Finalizer().HandleAsync(h.Success(), default);
        await act.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>();

        h.Objects.Store.Should().ContainKey(staged);
        h.Objects.Store.Should().ContainKey(MarketplacePreviewKeys.StagingPoster(h.RecipeId, h.VersionId, h.Nonce));
        h.Objects.Deleted.Should().BeEmpty();
        h.Objects.Prefixes.Should().BeEmpty();
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Preparing);
    }

    [Fact]
    public async Task The_evidence_marker_is_saved_before_any_copy_so_a_crash_in_between_leaves_staged_evidence_guarded()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Match;
        h.Objects.CrashOnCopy = true;

        Func<Task> act = () => h.Finalizer().HandleAsync(h.Success(), default);
        await act.Should().ThrowAsync<OperationCanceledException>();

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["evidence"]!.GetValue<string>().Should().Be("staged");
        h.Check()["evidenceKeys"]!.AsArray().Should().HaveCount(2);
        h.Recipe().Status.Should().Be(RecipeStatus.Rejected);
        h.Objects.Store.Keys.Should().Contain(MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, h.Nonce));

        await h.Finalizer().HandleAsync(h.Success(), default);
        await Sweeper().SweepAsync(h.Db, new RecordingDispatcher(), DateTime.UtcNow.AddDays(1), default, h.Objects);
        h.Objects.Deleted.Should().BeEmpty();
        h.Objects.Prefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_quarantine_move_deletes_nothing_and_keeps_the_staged_evidence()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Match;
        h.Objects.FailMove = true;
        string staged = MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, h.Nonce);

        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Objects.Store.Should().ContainKey(staged);
        h.Objects.Deleted.Should().BeEmpty();
        h.Objects.Prefixes.Should().BeEmpty();
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["evidence"]!.GetValue<string>().Should().Be("staged");
        h.Check()["evidenceKeys"]!.AsArray().Select(k => k!.GetValue<string>()).Should().BeEquivalentTo(
            [staged, MarketplacePreviewKeys.StagingPoster(h.RecipeId, h.VersionId, h.Nonce)]);
        h.Objects.Store.Should().ContainKey(MarketplacePreviewKeys.StagingPoster(h.RecipeId, h.VersionId, h.Nonce));

        // A redelivered finished message for the now-Failed version, and a late one of another run, delete nothing.
        await h.Finalizer().HandleAsync(h.Success(), default);
        await h.Finalizer().HandleAsync(h.Success(nonce: MarketplacePreviewKeys.NewNonce()), default);
        h.Objects.Store.Should().ContainKey(staged);
        h.Objects.Deleted.Should().BeEmpty();
        h.Objects.Prefixes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_object_store_never_deletes_or_moves_a_quarantine_key()
    {
        Guid r = Guid.NewGuid();
        Guid v = Guid.NewGuid();
        string evidence = MarketplacePreviewKeys.QuarantinedPreview(r, v);
        MarketplacePreviewKeys.IsQuarantine(evidence).Should().BeTrue();
        MarketplacePreviewKeys.IsQuarantine(MarketplacePreviewKeys.Preview(r, v)).Should().BeFalse();

        var s3 = new Mock<Amazon.S3.IAmazonS3>();
        s3.Setup(x => x.ListObjectsV2Async(It.IsAny<Amazon.S3.Model.ListObjectsV2Request>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.ListObjectsV2Response
            {
                S3Objects = [new Amazon.S3.Model.S3Object { Key = evidence }, new Amazon.S3.Model.S3Object { Key = MarketplacePreviewKeys.Preview(r, v) }]
            });
        s3.Setup(x => x.DeleteObjectAsync(It.IsAny<Amazon.S3.Model.DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Amazon.S3.Model.DeleteObjectResponse());
        S3MarketplacePreviewObjects store = new(s3.Object, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        await store.DeletePrefixAsync(MarketplacePreviewKeys.RecipePrefix(r), default);
        await store.DeleteAsync([evidence], default);
        Func<Task> move = () => store.MoveAsync(evidence, MarketplacePreviewKeys.Preview(r, v), default);
        await move.Should().ThrowAsync<InvalidOperationException>();

        s3.Verify(x => x.DeleteObjectAsync(It.Is<Amazon.S3.Model.DeleteObjectRequest>(d => d.Key == evidence), It.IsAny<CancellationToken>()), Times.Never);
        s3.Verify(x => x.DeleteObjectAsync(It.Is<Amazon.S3.Model.DeleteObjectRequest>(d => d.Key == MarketplacePreviewKeys.Preview(r, v)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_configured_scanner_that_does_not_answer_fails_closed()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Unavailable;
        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["previewFailure"]!.GetValue<string>().Should().Be("csam_scan_unavailable");
        h.Objects.Store.Should().BeEmpty();
        h.Moderator.Calls.Should().Be(0);
        h.Recipe().Status.Should().Be(RecipeStatus.Draft);
    }

    [Fact]
    public async Task An_engine_failure_marks_the_preview_failed_and_leaves_the_recipe_alone()
    {
        Harness h = new();
        await h.Finalizer().HandleAsync(new MarketplacePreviewFinished(h.VersionId, h.RecipeId, false, MarketplacePreviewFailureCodes.SourceTooLong, RunNonce: h.Nonce), default);

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["previewFailure"]!.GetValue<string>().Should().Be("source_too_long");
        h.Recipe().Status.Should().Be(RecipeStatus.Draft);
        h.Csam.Calls.Should().Be(0);
        h.Moderator.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_hash_that_does_not_match_the_stored_bytes_is_refused()
    {
        Harness h = new();
        await h.Finalizer().HandleAsync(h.Success(sha: new string('a', 64)), default);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["previewFailure"]!.GetValue<string>().Should().Be("sha_mismatch");
        h.Csam.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Keys_that_are_not_this_versions_own_are_refused()
    {
        Harness h = new();
        MarketplacePreviewFinished foreign = h.Success() with { PreviewKey = MarketplacePreviewKeys.Preview(h.RecipeId, h.VersionId) };
        await h.Finalizer().HandleAsync(foreign, default);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["previewFailure"]!.GetValue<string>().Should().Be("invalid_result");
    }

    [Fact]
    public async Task The_checked_bytes_are_copied_to_the_final_key_only_after_the_checks_and_staging_is_removed()
    {
        Harness h = new();
        await h.Finalizer().HandleAsync(h.Success(), default);

        h.Objects.Copies.Select(c => c.To).Should().BeEquivalentTo(
            [MarketplacePreviewKeys.Preview(h.RecipeId, h.VersionId), MarketplacePreviewKeys.Poster(h.RecipeId, h.VersionId)]);
        h.Objects.Copies.Should().OnlyContain(c => c.From.Contains("/staging/"));
        h.Objects.Store.Keys.Should().NotContain(k => k.Contains("/staging/"));
        h.Objects.Store[MarketplacePreviewKeys.Preview(h.RecipeId, h.VersionId)].Should().Equal(PreviewBytes);
    }

    [Fact]
    public async Task A_failed_check_never_reaches_the_final_key()
    {
        Harness h = new();
        h.Csam.Result = PreviewCsamResult.Unavailable;
        await h.Finalizer().HandleAsync(h.Success(), default);
        h.Objects.Copies.Should().BeEmpty();
    }

    [Fact]
    public async Task A_late_result_of_a_superseded_run_is_ignored_and_its_staging_deleted_while_the_current_run_goes_through()
    {
        Harness h = new();
        string stale = MarketplacePreviewKeys.NewNonce();
        h.Objects.Store[MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, stale)] = [66, 66];
        h.Objects.Store[MarketplacePreviewKeys.StagingPoster(h.RecipeId, h.VersionId, stale)] = [66];

        await h.Finalizer().HandleAsync(h.Success(nonce: stale, sha: "ab"), default);

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Preparing);
        h.Objects.Store.Keys.Should().NotContain(k => k.Contains(stale));
        h.Csam.Calls.Should().Be(0);
        h.Objects.Copies.Should().BeEmpty();

        await h.Finalizer().HandleAsync(h.Success(), default);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Ready);
        h.Version().PreviewSha256.Should().Be(PreviewSha);
    }

    [Fact]
    public async Task A_stale_nonce_event_for_a_ready_version_changes_nothing()
    {
        Harness h = new();
        await h.Finalizer().HandleAsync(h.Success(), default);
        string stale = MarketplacePreviewKeys.NewNonce();
        h.Objects.Store[MarketplacePreviewKeys.StagingPreview(h.RecipeId, h.VersionId, stale)] = [1];

        await h.Finalizer().HandleAsync(h.Success(nonce: stale), default);

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Ready);
        h.Objects.Store[MarketplacePreviewKeys.Preview(h.RecipeId, h.VersionId)].Should().Equal(PreviewBytes);
        h.Objects.Store.Keys.Should().NotContain(k => k.Contains(stale));
    }

    [Fact]
    public async Task Each_dispatch_stores_a_fresh_nonce_so_a_sweeper_redispatch_bumps_it()
    {
        Harness h = new();
        MarketplaceRecipeVersion v = h.Db.MarketplaceRecipeVersions.Single();
        v.PreviewSourceJson = new JsonObject { ["key"] = $"projects/{Guid.NewGuid()}/userFiles/a.mp4" }.ToJsonString();
        string first = v.PreviewRunNonce!;
        await h.Db.SaveChangesAsync();
        List<MarketplacePreviewRequested> sent = [];
        Mock<IPublishEndpoint> endpoint = new();
        endpoint.Setup(e => e.Publish(It.IsAny<MarketplacePreviewRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplacePreviewRequested, CancellationToken>((m, _) => sent.Add(m)).Returns(Task.CompletedTask);
        MassTransitMarketplacePreviewDispatcher dispatcher = new(h.Db, endpoint.Object, NullLogger<MassTransitMarketplacePreviewDispatcher>.Instance);

        await dispatcher.DispatchAsync(h.RecipeId, h.VersionId, default);
        await dispatcher.DispatchAsync(h.RecipeId, h.VersionId, default);

        sent.Select(m => m.RunNonce).Distinct().Should().HaveCount(2);
        sent.Should().OnlyContain(m => MarketplacePreviewKeys.IsValidNonce(m.RunNonce));
        h.Version().PreviewRunNonce.Should().Be(sent[1].RunNonce).And.NotBe(first);
    }

    [Fact]
    public async Task A_redelivered_result_changes_nothing()
    {
        Harness h = new();
        MarketplacePreviewFinalizer finalizer = h.Finalizer();
        await finalizer.HandleAsync(h.Success(), default);
        await finalizer.HandleAsync(h.Success(), default);
        await finalizer.HandleAsync(new MarketplacePreviewFinished(h.VersionId, h.RecipeId, false, "x", RunNonce: h.Nonce), default);

        h.Version().PreviewStatus.Should().Be(PreviewStatus.Ready);
        h.Csam.Calls.Should().Be(1);
        h.Moderator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task The_consumer_does_nothing_while_the_marketplace_is_off()
    {
        Harness h = new();
        Mock<ConsumeContext<MarketplacePreviewFinished>> context = new();
        context.SetupGet(c => c.Message).Returns(h.Success());
        context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        await new MarketplacePreviewFinishedConsumer(h.Finalizer(), Options.Create(new MarketplaceOptions { Enabled = false }), NullLogger<MarketplacePreviewFinishedConsumer>.Instance)
            .Consume(context.Object);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Preparing);

        await new MarketplacePreviewFinishedConsumer(h.Finalizer(), Options.Create(new MarketplaceOptions { Enabled = true }), NullLogger<MarketplacePreviewFinishedConsumer>.Instance)
            .Consume(context.Object);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Ready);
    }

    // ---------------------------------------------------------------- sweeper

    private sealed class RecordingDispatcher : IMarketplacePreviewDispatcher
    {
        public readonly List<Guid> Versions = [];
        public Task DispatchAsync(Guid recipeId, Guid versionId, CancellationToken ct)
        {
            Versions.Add(versionId);
            return Task.CompletedTask;
        }
    }

    private static MarketplacePreviewSweeper Sweeper() => new(
        Mock.Of<IServiceScopeFactory>(), Options.Create(new MarketplaceOptions { Enabled = true, PreviewStuckMinutes = 30 }), NullLogger<MarketplacePreviewSweeper>.Instance);

    [Fact]
    public async Task A_stuck_version_is_redispatched_once_then_failed()
    {
        Harness h = new();
        RecordingDispatcher dispatcher = new();
        MarketplacePreviewSweeper sweeper = Sweeper();
        DateTime t0 = DateTime.UtcNow;

        (await sweeper.SweepAsync(h.Db, dispatcher, t0, default)).Should().Be((0, 0), "a fresh version is left alone");

        (await sweeper.SweepAsync(h.Db, dispatcher, t0.AddMinutes(31), default)).Should().Be((1, 0));
        dispatcher.Versions.Should().Equal(h.VersionId);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Preparing);

        (await sweeper.SweepAsync(h.Db, dispatcher, t0.AddMinutes(40), default)).Should().Be((0, 0), "the second attempt has its own 30 minutes");

        (await sweeper.SweepAsync(h.Db, dispatcher, t0.AddMinutes(70), default)).Should().Be((0, 1));
        dispatcher.Versions.Should().HaveCount(1);
        h.Version().PreviewStatus.Should().Be(PreviewStatus.Failed);
        h.Check()["previewFailure"]!.GetValue<string>().Should().Be("timed_out");
    }

    [Fact]
    public async Task The_sweeper_ignores_versions_that_are_no_longer_preparing()
    {
        Harness h = new();
        await h.Finalizer().HandleAsync(h.Success(), default);
        RecordingDispatcher dispatcher = new();
        (await Sweeper().SweepAsync(h.Db, dispatcher, DateTime.UtcNow.AddHours(5), default)).Should().Be((0, 0));
        dispatcher.Versions.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- dispatcher

    [Fact]
    public async Task The_dispatcher_publishes_the_server_resolved_project_key_and_nothing_for_a_bad_source()
    {
        Harness h = new();
        Guid project = Guid.NewGuid();
        MarketplaceRecipeVersion v = h.Db.MarketplaceRecipeVersions.Single();
        v.PreviewSourceJson = new JsonObject { ["kind"] = "file", ["key"] = $"projects/{project}/userFiles/a.mp4" }.ToJsonString();
        await h.Db.SaveChangesAsync();

        List<MarketplacePreviewRequested> sent = [];
        Mock<IPublishEndpoint> endpoint = new();
        endpoint.Setup(e => e.Publish(It.IsAny<MarketplacePreviewRequested>(), It.IsAny<CancellationToken>()))
            .Callback<MarketplacePreviewRequested, CancellationToken>((m, _) => sent.Add(m)).Returns(Task.CompletedTask);
        MassTransitMarketplacePreviewDispatcher dispatcher = new(h.Db, endpoint.Object, NullLogger<MassTransitMarketplacePreviewDispatcher>.Instance);

        await dispatcher.DispatchAsync(h.RecipeId, h.VersionId, default);
        sent.Should().ContainSingle();
        sent[0].SourceStorageKey.Should().Be($"projects/{project}/userFiles/a.mp4");
        sent[0].OrganizationId.Should().Be(h.OrgId);

        v.PreviewSourceJson = new JsonObject { ["key"] = "marketplace/x/y/preview.mp4" }.ToJsonString();
        await h.Db.SaveChangesAsync();
        await dispatcher.DispatchAsync(h.RecipeId, h.VersionId, default);
        sent.Should().ContainSingle("a key outside projects/ is never forwarded");
    }

    // ---------------------------------------------------------------- csam gate wiring

    private sealed class FakeShield : IArachnidShieldClient
    {
        public string Classification = "no-match";
        public bool Throw;
        public int Calls;
        public Task<ArachnidScanResult> ScanPdqAsync(string base64Hash, CancellationToken ct) => Answer();
        public Task<ArachnidScanResult> ScanMediaAsync(Stream content, string contentType, CancellationToken ct) => Answer();
        public Task<string?> GetDatabaseVersionAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        private Task<ArachnidScanResult> Answer()
        {
            Calls++;
            return Throw ? throw new HttpRequestException("down") : Task.FromResult(new ArachnidScanResult(Classification, null, 0, null));
        }
    }

    private static (MarketplacePreviewCsamGate Gate, FakeShield Shield) Gate(bool enabled, string? user = "u")
    {
        FakeObjects objects = new();
        Guid r = Guid.NewGuid();
        Guid v = Guid.NewGuid();
        objects.Store[MarketplacePreviewKeys.Preview(r, v)] = PreviewBytes;
        objects.Store[MarketplacePreviewKeys.Poster(r, v)] = [7];
        FakeShield shield = new();
        CsamOptions options = new() { Arachnid = new ArachnidOptions { Enabled = enabled, Username = user, Password = user } };
        return (new MarketplacePreviewCsamGate(objects, shield, Mock.Of<IPdqHasher>(), Options.Create(options), NullLogger<MarketplacePreviewCsamGate>.Instance), shield);
    }

    private static Task<PreviewCsamResult> Scan(MarketplacePreviewCsamGate gate) =>
        gate.ScanAsync(MarketplacePreviewKeys.Preview(Guid.Empty, Guid.Empty), MarketplacePreviewKeys.Poster(Guid.Empty, Guid.Empty), default);

    [Fact]
    public async Task The_csam_gate_is_a_recorded_skip_when_the_control_is_off()
    {
        var (gate, shield) = Gate(enabled: false);
        (await Scan(gate)).Should().Be(PreviewCsamResult.NotConfigured);
        shield.Calls.Should().Be(0);
    }

    [Fact]
    public async Task The_csam_gate_fails_closed_when_enabled_without_credentials_or_when_the_provider_is_down()
    {
        (await Scan(Gate(enabled: true, user: null).Gate)).Should().Be(PreviewCsamResult.Unavailable);
        var (gate, shield) = Gate(enabled: true);
        shield.Throw = true;
        (await Scan(gate)).Should().Be(PreviewCsamResult.Unavailable);
    }

    [Fact]
    public async Task The_csam_gate_answers_clean_or_match_from_the_providers_classification()
    {
        // The objects live under the zero ids only in the dictionary of the gate built above, so rebuild with matching keys.
        FakeObjects objects = new();
        objects.Store[MarketplacePreviewKeys.Preview(Guid.Empty, Guid.Empty)] = PreviewBytes;
        objects.Store[MarketplacePreviewKeys.Poster(Guid.Empty, Guid.Empty)] = [7];
        FakeShield shield = new();
        MarketplacePreviewCsamGate gate = new(objects, shield, Mock.Of<IPdqHasher>(),
            Options.Create(new CsamOptions { Arachnid = new ArachnidOptions { Enabled = true, Username = "u", Password = "p" } }), NullLogger<MarketplacePreviewCsamGate>.Instance);

        (await Scan(gate)).Should().Be(PreviewCsamResult.Clean);
        shield.Calls.Should().Be(2, "the poster and the video are both scanned");

        shield.Classification = "csam";
        (await Scan(gate)).Should().Be(PreviewCsamResult.Match);
    }

    // ---------------------------------------------------------------- moderation wiring

    private static (MarketplacePreviewModerator Moderator, Mock<IContentModerationGate> Gate) Moderator(bool enabled, ModerationDecision? decision = null)
    {
        FakeObjects objects = new();
        objects.Store["marketplace/a/b/preview.mp4"] = PreviewBytes;
        Mock<IModerationMediaSampler> sampler = new();
        sampler.Setup(s => s.SampleAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModerationMediaSample([[1, 2, 3]], null, ModerationContentKind.Video, null));
        Mock<IContentModerationGate> gate = new();
        gate.Setup(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(decision ?? new ModerationDecision(ModerationAction.Allow, true, false, null, "", "h"));
        return (new MarketplacePreviewModerator(
            objects, sampler.Object, gate.Object, Mock.Of<IInferenceProviderResolver>(), Mock.Of<ITranscriptionClientFactory>(),
            Options.Create(new ContentModerationOptions { Enabled = enabled }), NullLogger<MarketplacePreviewModerator>.Instance), gate);
    }

    [Fact]
    public async Task Moderation_off_is_unavailable_and_sends_nothing()
    {
        var (moderator, gate) = Moderator(enabled: false);
        (await moderator.CheckAsync("marketplace/a/b/preview.mp4", Guid.NewGuid(), default)).Outcome.Should().Be(PreviewModerationOutcome.Unavailable);
        gate.Verify(g => g.ModerateAsync(It.IsAny<ModerationInput>(), It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(ModerationAction.Allow, true, PreviewModerationOutcome.Allow)]
    [InlineData(ModerationAction.Review, true, PreviewModerationOutcome.Review)]
    [InlineData(ModerationAction.Block, true, PreviewModerationOutcome.Block)]
    [InlineData(ModerationAction.Allow, false, PreviewModerationOutcome.Unavailable)]
    [InlineData(ModerationAction.Unavailable, false, PreviewModerationOutcome.Unavailable)]
    public async Task The_gate_decision_maps_to_a_preview_verdict_and_only_frames_are_sent(ModerationAction action, bool available, PreviewModerationOutcome expected)
    {
        var (moderator, gate) = Moderator(enabled: true, new ModerationDecision(action, available, false, "cat", "", "h"));
        (await moderator.CheckAsync("marketplace/a/b/preview.mp4", Guid.NewGuid(), default)).Outcome.Should().Be(expected);
        gate.Verify(g => g.ModerateAsync(
            It.Is<ModerationInput>(i => i.Kind == ModerationContentKind.Video && i.Images.Count == 1 && i.Images.All(x => x.ContentType == "image/jpeg")),
            It.IsAny<ModerationAttribution>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
