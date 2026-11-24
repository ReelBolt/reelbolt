using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The second half of T-304: ownership and the trust boundary. A derivative must never be produced
/// for — or readable by — a project that does not own the source, and the only thing a caller can
/// select must be a closed-enum slug plus a variant index.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the ownership check actually lives.</b> <see cref="MediaDerivativeDispatcher"/> has no
/// ownership test of its own: it hands the message's storage key straight to
/// <see cref="IProjectFileWorkspace.DownloadStorageKeyToFileAsync"/>, whose contract (and whose one
/// production implementation, <see cref="ProjectFileWorkspace"/>) requires the key to lie under
/// <c>projects/{projectId}/</c>. Both seams are exercised below — the workspace's own refusal, and the
/// dispatcher's observable outcome when that refusal fires — so the claim is tested at the boundary
/// the production path really uses rather than at a seam invented for the test.
/// </para>
/// <para>
/// <b>EF InMemory does not enforce unique indexes.</b> These tests say so explicitly, and the tests
/// that would need a real constraint say what they do and do not prove instead. There is no
/// Postgres-backed integration test anywhere in this repository, so no test here claims database
/// enforcement it did not exercise.
/// </para>
/// </remarks>
public class MediaDerivativeOwnershipVideoTests
{
    // =========================================================================================
    // Ownership — the workspace's refusal, and what the dispatcher does with it
    // =========================================================================================

    [Fact]
    public async Task A_storage_key_outside_the_requesting_projects_prefix_is_refused_before_any_store_call()
    {
        // A STRICT mock: any unexpected call to the object store throws a MockException instead of
        // returning a default, so "no S3 call happened" is proven by the mock never being reached
        // rather than only by the exception type.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);
        ProjectFileWorkspace workspace = BuildWorkspace(s3);

        Guid projectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid otherProject = Guid.Parse("99999999-8888-7777-6666-555555555555");

        string[] foreignKeys =
        [
            // Another project's object — the review focus case verbatim.
            $"projects/{otherProject:D}/userFiles/a.mp4",
            // A sibling of THIS project's prefix: the check is on the full `projects/{id}/` prefix,
            // so a project whose id merely starts with ours is not ours.
            $"projects/{projectId:D}-2/userFiles/a.mp4",
            // No project prefix at all.
            "userFiles/a.mp4",
            // A bare relative key, i.e. what a traversal-flavoured storage key would look like.
            "../" + $"projects/{otherProject:D}/userFiles/a.mp4",
            string.Empty
        ];

        foreach (string foreignKey in foreignKeys)
        {
            string destination = Path.Combine(Path.GetTempPath(), "reelforge-scope-" + Guid.NewGuid().ToString("N"));

            Func<Task> download = () => workspace.DownloadStorageKeyToFileAsync(
                projectId, foreignKey, destination, CancellationToken.None);

            await download.Should().ThrowAsync<UnauthorizedAccessException>(
                $"'{foreignKey}' does not lie under projects/{projectId:D}/ and must be refused");

            File.Exists(destination).Should().BeFalse("nothing may be written for a refused key");
        }

        s3.Invocations.Should().BeEmpty(
            "the refusal must happen BEFORE any object-store call; a check that ran after the read would " +
            "already have disclosed another project's bytes");
    }

    [Fact]
    public async Task The_dispatcher_writes_no_row_and_never_produces_for_a_source_outside_the_projects_prefix()
    {
        // The end-to-end shape of the ownership rule at the seam the writer exposes: a real
        // ProjectFileWorkspace in front of the real dispatcher. Nothing downstream of the download may
        // run — no probe, no encode, no upload, no row.
        var s3 = new Mock<IAmazonS3>(MockBehavior.Strict);

        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string scratchRoot = Path.Combine(directory, "scratch");
            Directory.CreateDirectory(scratchRoot);

            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(scratchRoot);
            var runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
            MediaDerivativeService service = MediaDerivativeFfmpegFixture.BuildService(
                runner, MediaDerivativeFfmpegFixture.BuildProbe(runner, options), options);

            var db = new SharedInMemoryDbFactory();
            var sink = new RecordingProgressSink();

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge-test" })
                .Build();

            var dispatcher = new MediaDerivativeDispatcher(
                db,
                BuildWorkspace(s3),
                service,
                runner,
                sink,
                Options.Create(options),
                configuration,
                NullLogger<MediaDerivativeDispatcher>.Instance);

            Guid requestingProject = Guid.NewGuid();
            Guid owningProject = Guid.NewGuid();

            Func<Task> run = () => dispatcher.RunAsync(
                new MediaDerivativesRequested(
                    requestingProject,
                    new MediaSourceRef(MediaSourceKind.ProjectFile, Guid.NewGuid()),
                    Guid.NewGuid(),
                    $"projects/{owningProject:D}/userFiles/a.mp4",
                    "\"etag-1\"",
                    "video/mp4",
                    [MediaDerivativeKind.Proxy],
                    "upload",
                    DateTime.UtcNow),
                CancellationToken.None);

            await run.Should().NotThrowAsync("a derivative failure must never fail a workflow");

            using WorkflowEngineDbContext context = db.CreateDbContext();
            context.MediaDerivatives.AsNoTracking().Should().BeEmpty(
                "a source owned by another project must produce no derivative row at all");

            sink.Stages.Should().Contain(MediaDerivativeProgressStage.Failed);
            sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Done);
            sink.Stages.Should().NotContain(MediaDerivativeProgressStage.Proxy,
                "the refusal must land before production starts, not somewhere inside it");

            sink.Updates.Last(u => u.Stage == MediaDerivativeProgressStage.Failed).Detail
                .Should().Contain("not in the project's storage scope",
                    "the refusal must be the ownership check, not an unrelated failure");

            s3.Invocations.Should().BeEmpty("no object-store call may be made for a foreign source");
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [Fact]
    public void A_derivative_object_is_addressable_only_under_its_owning_projects_prefix()
    {
        // The row is scoped by ProjectId AND its object lives under projects/{thatProject}/…
        // ComputeStoragePrefix is what the service and the dispatcher both build the object key from,
        // so it is the pure seam where "a project can only address its own derivatives" is decidable.
        Guid owner = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Guid other = Guid.Parse("99999999-8888-7777-6666-555555555555");
        const string sourceKey = "projects/11111111-2222-3333-4444-555555555555/userFiles/a.mp4";

        string ownerPrefix = MediaDerivativeCacheKey.ComputeStoragePrefix(owner, sourceKey, "\"etag\"");
        string otherPrefix = MediaDerivativeCacheKey.ComputeStoragePrefix(other, sourceKey, "\"etag\"");

        ownerPrefix.Should().StartWith($"projects/{owner:D}/");
        ownerPrefix.Should().NotStartWith($"projects/{other:D}/");
        ownerPrefix.Should().NotBe(otherPrefix,
            "the same source read by two projects must not resolve to one object directory");

        // And the row the writer builds agrees: one ProjectId, the message's own.
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForKeyframeIndex();

        MediaDerivative row = MediaDerivativeDispatcher.BuildRow(
            new MediaDerivativesRequested(
                projectId,
                new MediaSourceRef(MediaSourceKind.ProjectFile, fileId),
                fileId,
                $"projects/{projectId:D}/userFiles/a.mp4",
                "\"etag\"",
                "video/mp4",
                [MediaDerivativeKind.KeyframeIndex],
                "upload",
                DateTime.UtcNow),
            spec,
            Result(spec, "keyframes-abc.json", "\"etag\""),
            "\"etag\"",
            $"projects/{projectId:D}/agentFiles/media-derivatives/abc/keyframes-abc.json",
            "application/json",
            12,
            "reelforge-test");

        row.ProjectId.Should().Be(projectId);
        row.StorageKey.Should().StartWith($"projects/{projectId:D}/");
    }

    // =========================================================================================
    // Trust boundary (plan §3.7 / Global Constraint #12)
    // =========================================================================================

    [Fact]
    public void The_caller_selectable_parts_of_a_spec_are_a_closed_enum_slug_and_a_variant_index()
    {
        // The closed catalogue, asserted member for member.
        Enum.GetValues<MediaDerivativeKindSlug>().Select(s => s.ToString())
            .Should().Equal("Proxy", "Filmstrip", "WaveformPeaks", "KeyframeIndex", "Stills", "StabilizedProxy");

        // No spec factory takes free-form text: a factory with a string parameter would be the path a
        // filter name, a pixel value or a caller-supplied filtergraph could travel in through.
        MethodInfo[] factories = [.. typeof(MediaDerivativeSpec)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => !m.IsSpecialName && m.DeclaringType == typeof(MediaDerivativeSpec))];

        factories.Select(m => m.Name).Should().Contain("ForProxy");
        factories.Select(m => m.Name).Should().Contain("ForFilmstrip");
        factories.Select(m => m.Name).Should().Contain("ForWaveformPeaks");
        factories.Select(m => m.Name).Should().Contain("ForKeyframeIndex");
        factories.Select(m => m.Name).Should().Contain("ForStills");
        factories.Select(m => m.Name).Should().Contain("ForStabilizedProxy");

        // T-101 widened the catalogue deliberately by ONE numeric channel: ForStills takes the requested
        // still times as whole milliseconds (a list of int, normalized server-side), never text.
        // StillTimesMsOf reads a spec back and is not a factory input.
        foreach (MethodInfo factory in factories.Where(m => m.Name != nameof(MediaDerivativeSpec.StillTimesMsOf)))
        {
            foreach (ParameterInfo parameter in factory.GetParameters())
            {
                (parameter.ParameterType == typeof(int) || parameter.ParameterType == typeof(bool)
                 || parameter.ParameterType == typeof(IReadOnlyList<int>))
                    .Should().BeTrue(
                        $"{factory.Name} takes a {parameter.ParameterType.Name} ('{parameter.Name}'); the " +
                        "closed catalogue admits numbers and flags only, never a string");
            }
        }

        // The public surface is the four documented members and nothing else. The one string on it is
        // the canonical parameter JSON — the OUTPUT of the factories, not something a caller sets
        // through any path this wave ships.
        Dictionary<string, Type> members = typeof(MediaDerivativeSpec)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.DeclaringType == typeof(MediaDerivativeSpec))
            .ToDictionary(p => p.Name, p => p.PropertyType);

        members.Should().HaveCount(4);
        members["Slug"].Should().Be(typeof(MediaDerivativeKindSlug));
        members["Variant"].Should().Be(typeof(int));
        members["ParamsJson"].Should().Be(typeof(string));
        members["BuilderVersion"].Should().Be(typeof(int));

        // The message's only selection channel is a closed enum array, never a string list.
        typeof(MediaDerivativesRequested).GetProperty(nameof(MediaDerivativesRequested.Kinds))!
            .PropertyType.GetGenericArguments().Single().IsEnum.Should().BeTrue(
                "Kinds must be a closed enum array; a string list would be a free-form kind name");

        // The one parameter a message carries (T-101) is numbers: still times in seconds.
        typeof(MediaDerivativesRequested).GetProperty(nameof(MediaDerivativesRequested.StillTimesSec))!
            .PropertyType.Should().Be(typeof(IReadOnlyList<double>));
    }

    [Fact]
    public void Every_string_in_the_params_catalogue_is_a_first_party_constant()
    {
        // The strongest form of "no caller value reaches argv": the string-valued parameters are
        // compared against the constants themselves, so a new free-form member fails here rather than
        // being spotted in review.
        string[] allowed =
        [
            MediaDerivativeSpec.ProxyPixFmt,
            MediaDerivativeSpec.ProxyPreset,
            MediaDerivativeSpec.ProxyProfile,
            MediaDerivativeSpec.ProxyScaleFlags,
            MediaDerivativeSpec.ProxyVideoCodec,
            MediaDerivativeSpec.FilmstripCodec,
            MediaDerivativeSpec.StillsCodec,
            MediaDerivativeSpec.KeyframeStreamSelector,
            "ch,min,max",
            "s16le",
            "packets"
        ];

        MediaDerivativeSpec[] specs =
        [
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForProxy(2160, 24, 1, false, 8),
            MediaDerivativeSpec.ForFilmstrip(0),
            MediaDerivativeSpec.ForFilmstrip(1),
            MediaDerivativeSpec.ForFilmstrip(2),
            MediaDerivativeSpec.ForWaveformPeaks(0),
            MediaDerivativeSpec.ForWaveformPeaks(1),
            MediaDerivativeSpec.ForWaveformPeaks(2),
            MediaDerivativeSpec.ForKeyframeIndex(),
            MediaDerivativeSpec.ForStills([]),
            MediaDerivativeSpec.ForStills([0, 1500, 60000])
        ];

        foreach (MediaDerivativeSpec spec in specs)
        {
            MatchCollection stringMembers = Regex.Matches(spec.ParamsJson, "\"([A-Za-z0-9]+)\":\"([^\"]*)\"");

            foreach (System.Text.RegularExpressions.Match member in stringMembers)
            {
                string value = member.Groups[2].Value;
                allowed.Should().Contain(value,
                    $"{spec.Slug}'s '{member.Groups[1].Value}' is '{value}', which is not a first-party " +
                    "constant — a free-form string in the catalogue is how a caller value reaches argv");
            }
        }
    }

    [Fact]
    public void The_planned_catalogue_is_a_function_of_the_mime_class_alone()
    {
        // A hostile MIME type is not stretched into a derivative set, and whatever plan a MIME does
        // select is the closed one — never a plan shaped by anything else on the message.
        string[] mimeTypesNamingNoMediaClass =
        [
            "\"; rm -rf /; |0:0|{\"filter\":\"evil\"}",
            "video",                     // no slash: not video/*
            "application/x-video",
            "../../etc/passwd",
            string.Empty
        ];

        foreach (string mimeType in mimeTypesNamingNoMediaClass)
        {
            IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan =
                MediaDerivativeDispatcher.ResolveSpecPlan(mimeType, null);

            plan.Should().BeEmpty(
                $"'{mimeType}' names no media class, so it gets no derivatives rather than a guess");

            // Exactly the same plan on a repeat call: no state, no ambient input.
            MediaDerivativeDispatcher.ResolveSpecPlan(mimeType, null).Should().Equal(plan);
        }

        // A MIME that DOES name a class gets exactly that class's closed plan, deterministically.
        foreach (string mimeType in new[] { "video/mp4", "VIDEO/MP4", "audio/wav", "image/png" })
        {
            IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan =
                MediaDerivativeDispatcher.ResolveSpecPlan(mimeType, null);

            foreach ((MediaDerivativeKindSlug slug, int variant) in plan)
            {
                slug.Should().BeOneOf(Enum.GetValues<MediaDerivativeKindSlug>());
                variant.Should().BeInRange(0, 2);
            }

            MediaDerivativeDispatcher.ResolveSpecPlan(mimeType, null).Should().Equal(plan);
        }

        MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", null).Should().HaveCount(8);
        MediaDerivativeDispatcher.ResolveSpecPlan("audio/wav", null).Should().HaveCount(3);
        MediaDerivativeDispatcher.ResolveSpecPlan("image/png", null).Should().BeEmpty();
    }

    // =========================================================================================
    // Row-level uniqueness — and the database enforcement that is NOT exercised here
    // =========================================================================================

    [Fact]
    public void Two_different_encoder_parameter_sets_of_one_source_produce_distinct_rows_and_keys()
    {
        // The review focus's second half, at the row level and without ffmpeg: one source, the eight
        // (kind, variant) pairs the closed plan can produce, twice over — once per etag. If two
        // parameter sets could share a cache key, this is where a row would be silently overwritten.
        var db = new SharedInMemoryDbFactory();
        Guid projectId = Guid.NewGuid();
        Guid fileId = Guid.NewGuid();
        const string sourceKey = "projects/11111111-2222-3333-4444-555555555555/userFiles/interview.mp4";

        IReadOnlyList<(MediaDerivativeKindSlug Slug, int Variant)> plan =
            MediaDerivativeDispatcher.ResolveSpecPlan("video/mp4", null);

        var specs = new List<MediaDerivativeSpec>();
        foreach ((MediaDerivativeKindSlug slug, int variant) in plan)
        {
            specs.Add(slug == MediaDerivativeKindSlug.Proxy
                ? MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4)
                : MediaDerivativeDispatcher.ResolveStaticSpec(slug, variant));
        }

        string[] etags = ["\"etag-A\"", "\"etag-B\""];
        var inserted = new List<MediaDerivative>();

        using (WorkflowEngineDbContext context = db.CreateDbContext())
        {
            foreach (string etag in etags)
            {
                foreach (MediaDerivativeSpec spec in specs)
                {
                    string prefix = MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, sourceKey, etag);
                    MediaDerivativeResult result = Result(spec, $"leaf-{spec.Slug}-{spec.Variant}", etag);

                    MediaDerivative row = MediaDerivativeDispatcher.BuildRow(
                        new MediaDerivativesRequested(
                            projectId,
                            new MediaSourceRef(MediaSourceKind.ProjectFile, fileId),
                            fileId,
                            sourceKey,
                            etag,
                            "video/mp4",
                            [MediaDerivativeKind.Proxy],
                            "upload",
                            DateTime.UtcNow),
                        spec,
                        result,
                        etag,
                        // A stand-in key: the real leaf names are the service's, and the assertion below
                        // is on the identity columns, not on the layout.
                        prefix,
                        "application/octet-stream",
                        1,
                        "reelforge-test");

                    row.CacheKey.Should().Be(result.CacheKey);
                    context.MediaDerivatives.Add(row);
                    inserted.Add(row);
                }
            }

            context.SaveChanges();
        }

        inserted.Should().HaveCount(16);

        using (WorkflowEngineDbContext context = db.CreateDbContext())
        {
            List<MediaDerivative> rows = [.. context.MediaDerivatives.AsNoTracking()];

            rows.Should().HaveCount(16, "eight parameter sets times two content identities is sixteen rows");
            rows.Select(r => r.CacheKey).Distinct(StringComparer.Ordinal).Should().HaveCount(16,
                "two different parameter sets, or two different source contents, must never share a key");

            // Every row's own identity columns describe its own key — the property that makes the
            // unique index meaningful rather than decorative.
            foreach (MediaDerivative row in rows)
            {
                MediaDerivativeKind kind = Enum.Parse<MediaDerivativeKind>(row.Kind.ToString());
                MediaDerivativeIdentity.Compute(
                    row.SourceStorageKey, row.SourceETag, kind, VariantOf(row), row.ParamsHash, row.BuilderVersion)
                    .Should().Be(row.CacheKey,
                        "a row whose cache_key does not describe its own bytes is the one failure this " +
                        "whole design exists to prevent");
            }

            // The two etags also separated by directory, so the objects cannot collide either.
            rows.Select(r => r.StorageKey).Distinct(StringComparer.Ordinal).Should().HaveCount(2);
        }
    }

    [Fact]
    public void EfInMemory_does_not_enforce_the_unique_cache_key_index()
    {
        // KNOWN LIMIT, asserted rather than claimed: the InMemory provider ignores unique indexes, so
        // a duplicate cache_key inserted through this fixture persists twice. That is why every test
        // in this file asserts the IDENTITY (two different sources ⇒ two different keys) and none of
        // them asserts that a duplicate is rejected — the database is the only thing that enforces
        // that, and there is no Postgres-backed integration test in this repository.
        var db = new SharedInMemoryDbFactory();
        const string cacheKey = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        using (WorkflowEngineDbContext context = db.CreateDbContext())
        {
            context.MediaDerivatives.Add(NewRow(Guid.NewGuid(), cacheKey));
            context.MediaDerivatives.Add(NewRow(Guid.NewGuid(), cacheKey));
            context.SaveChanges();
        }

        using (WorkflowEngineDbContext context = db.CreateDbContext())
        {
            context.MediaDerivatives.AsNoTracking().Count(r => r.CacheKey == cacheKey)
                .Should().Be(2,
                    "EF InMemory does not enforce the unique index; if this ever becomes 1, a real " +
                    "relational provider has been wired into this fixture and the duplicate-rejection " +
                    "case should be strengthened to assert the constraint");
        }
    }

    [Fact]
    public void The_index_the_database_would_enforce_is_unique_partial_and_on_the_cache_key_alone()
    {
        // Sqlite, not InMemory: index filters and uniqueness are RELATIONAL concepts and the InMemory
        // provider runs none of the relational conventions. This is the constraint the engine context
        // DECLARES — the real Postgres table carries the same partial unique index
        // (ux_media_derivatives_cache_key, WHERE deleted_at IS NULL), which is what actually makes the
        // collision impossible. Nothing in this repository exercises it against Postgres.
        using var context = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite("DataSource=:memory:").Options);

        var entity = context.Model.FindEntityType(typeof(MediaDerivative));
        entity.Should().NotBeNull("the engine maps media_derivatives read/write, so it must see the index");

        var index = entity!.GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == "ux_media_derivatives_cache_key");

        index.Should().NotBeNull("the cache key is what the table must refuse to duplicate");
        index!.IsUnique.Should().BeTrue();
        index.GetFilter().Should().Be("deleted_at IS NULL", "a soft-deleted row must not block a re-production");
        index.Properties.Should().HaveCount(1);
        index.Properties.Single().Name.Should().Be(nameof(MediaDerivative.CacheKey),
            "the index covers the key alone: kind and variant are components of the key, not separate columns");
    }

    // =========================================================================================

    private static ProjectFileWorkspace BuildWorkspace(Mock<IAmazonS3> s3)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MinIO:BucketName"] = "reelforge-test" })
            .Build();

        return new ProjectFileWorkspace(
            s3.Object,
            new Mock<IServiceScopeFactory>().Object,
            configuration,
            NullLogger<ProjectFileWorkspace>.Instance);
    }

    private static MediaDerivativeResult Result(MediaDerivativeSpec spec, string fileName, string etag) =>
        new(
            spec.Slug,
            spec.Variant,
            spec.ParamsJson,
            MediaDerivativeCacheKey.ComputeCacheKey(
                "projects/11111111-2222-3333-4444-555555555555/userFiles/interview.mp4",
                etag,
                spec),
            "projects/11111111-2222-3333-4444-555555555555/agentFiles/media-derivatives/hash",
            0,
            0,
            1.0,
            11,
            [new MediaDerivativeArtifact("/tmp/" + fileName, fileName, "application/octet-stream")],
            SidecarJson: null);

    private static MediaDerivative NewRow(Guid projectId, string cacheKey) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        SourceStorageKey = "projects/11111111-2222-3333-4444-555555555555/userFiles/a.mp4",
        SourceETag = "\"etag\"",
        ParamsHash = new string('a', 64),
        ParamsJson = "{}",
        BuilderVersion = 1,
        CacheKey = cacheKey,
        Kind = MediaDerivativeKind.Proxy,
        StorageKey = "projects/11111111-2222-3333-4444-555555555555/agentFiles/media-derivatives/x/y.mp4",
        StorageBucket = "reelforge-test",
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>
    /// The variant the row was written under. <see cref="MediaDerivative"/> does not persist the
    /// variant as its own column — it is a component of <c>cache_key</c> — so it is recovered from
    /// the key the test itself built, which is exactly the property being checked.
    /// </summary>
    private static int VariantOf(MediaDerivative row)
    {
        foreach (int variant in Enumerable.Range(0, 3))
        {
            if (string.Equals(
                    MediaDerivativeCacheKey.ComputeCacheKey(
                        row.SourceStorageKey,
                        row.SourceETag,
                        new MediaDerivativeSpec(
                            Enum.Parse<MediaDerivativeKindSlug>(row.Kind.ToString()),
                            variant,
                            row.ParamsJson,
                            row.BuilderVersion)),
                    row.CacheKey,
                    StringComparison.Ordinal))
            {
                return variant;
            }
        }

        throw new InvalidOperationException(
            $"No variant reproduces cache key '{row.CacheKey}' for {row.Kind}; the row's identity columns " +
            "do not describe its own key.");
    }
}
