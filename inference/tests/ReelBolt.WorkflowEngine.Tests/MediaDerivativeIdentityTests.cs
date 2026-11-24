using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Unit coverage for the single canonical media-derivative identity — the fix for the collision
/// T-100's three-column index permitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these tests pin.</b> <c>ux_media_derivatives_identity</c> was over
/// <c>(source_storage_key, source_etag, params_hash)</c>. The parameter hash is a hash over a spec's
/// canonical parameter JSON and its builder version, so it does <i>not</i> carry the derivative kind
/// or the variant: two kinds of one source whose parameter JSON happened to coincide produced the
/// same triple, and the unique index then rejected the second derivative outright. The tests below
/// construct exactly that case — same source, same parameter hash, different kind / different
/// variant — and require two keys.
/// </para>
/// <para>
/// <b>The identity is a mirror of a function another department owns</b>, so the load-bearing
/// assertion here is byte-equality with <c>MediaDerivativeCacheKey.ComputeCacheKey</c> rather than a
/// pinned literal: a literal would freeze today's value while letting the two implementations drift
/// apart on the next edit, which is the failure mode that matters (a DB <c>cache_key</c> that no
/// consumer ever computes is a cache that silently never hits). That test drives every slug of the
/// media-owned <c>MediaDerivativeKindSlug</c>.
/// </para>
/// </remarks>
public class MediaDerivativeIdentityTests
{
    private const string StorageKey = "projects/6f1a3d2e/agentFiles/uploads/interview.mp4";

    /// <summary>A quoted S3 ETag, i.e. the shape the column actually holds.</summary>
    private const string ETag = "\"9f8e7d6c5b4a39281706f5e4d3c2b1a0\"";

    private const string ParamsHash = "3b1f0a9c8d7e6f5a4b3c2d1e0f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f10";

    // =========================================================================================
    // The collision rule: kind and variant are components, not decoration
    // =========================================================================================

    [Fact]
    public void TwoKindsOfOneSource_WithTheSameParamsHash_ProduceTwoDifferentKeys()
    {
        // The same THREE values T-100's index covered, deliberately. If the kind were not a
        // component these two would be one key — and one row could never be written.
        string proxy = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Proxy, 0, ParamsHash, 1);
        string filmstrip = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Filmstrip, 0, ParamsHash, 1);

        proxy.Should().NotBe(filmstrip);
    }

    [Fact]
    public void TwoVariantsOfOneKindOfOneSource_WithTheSameParamsHash_ProduceTwoDifferentKeys()
    {
        // 1 s and 5 s filmstrips of one source. Their REAL parameter JSON differs too, but this test
        // holds the parameter hash fixed on purpose: the variant must separate them on its own,
        // otherwise the identity depends on a second component happening to differ.
        string dense = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Filmstrip, 0, ParamsHash, 1);
        string sparse = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Filmstrip, 1, ParamsHash, 1);

        dense.Should().NotBe(sparse);
    }

    [Fact]
    public void EveryComponent_ChangesTheKey()
    {
        string baseline = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.WaveformPeaks, 1, ParamsHash, 1);

        string otherSource = MediaDerivativeIdentity.Compute(
            StorageKey + "-2.mp4", ETag, MediaDerivativeKind.WaveformPeaks, 1, ParamsHash, 1);
        string otherEtag = MediaDerivativeIdentity.Compute(
            StorageKey, "\"0000000000000000000000000000dead\"", MediaDerivativeKind.WaveformPeaks, 1, ParamsHash, 1);
        string otherKind = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Proxy, 1, ParamsHash, 1);
        string otherVariant = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.WaveformPeaks, 2, ParamsHash, 1);
        string otherParams = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.WaveformPeaks, 1, new string('a', 64), 1);
        string otherBuilder = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.WaveformPeaks, 1, ParamsHash, 2);

        var all = new[] { baseline, otherSource, otherEtag, otherKind, otherVariant, otherParams, otherBuilder };
        all.Distinct(StringComparer.Ordinal).Should().HaveCount(all.Length);
    }

    // =========================================================================================
    // Injectivity: the separator cannot be traded between components
    // =========================================================================================

    [Fact]
    public void AComponentContainingTheSeparator_CannotBeTradedWithItsNeighbour()
    {
        // Without length prefixes these two sets of components produce the SAME material —
        // "a" | "b|c" and "a|b" | "c" — so two genuinely different sources would share one
        // derivative object and one row. Each component is emitted as <length>:<value>| precisely so
        // that cannot happen: a value containing the separator changes its own length.
        string oneSourceSplitTwoWays = MediaDerivativeIdentity.Compute(
            "a", "b|c", MediaDerivativeKind.Proxy, 0, ParamsHash, 1);
        string theOtherSplit = MediaDerivativeIdentity.Compute(
            "a|b", "c", MediaDerivativeKind.Proxy, 0, ParamsHash, 1);

        oneSourceSplitTwoWays.Should().NotBe(theOtherSplit);

        // The same trade one component further along: a parameter hash that ends where the builder
        // version begins. "12" + 3 and "1" + 23 are the same characters in a bare join.
        string paramsTwelveVersionThree = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Proxy, 0, "12", 3);
        string paramsOneVersionTwentyThree = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Proxy, 0, "1", 23);

        paramsTwelveVersionThree.Should().NotBe(paramsOneVersionTwentyThree);
    }

    [Fact]
    public void AValueEndingInTheSeparator_CannotImpersonateAnEmptyNextComponent()
    {
        // "a|" + "" and "a" + "|" collide under a bare join, and both are reachable inputs: a
        // storage key may legitimately end in '|', and an empty etag is what a store that reports
        // none yields.
        string trailingSeparatorThenEmpty = MediaDerivativeIdentity.Compute(
            "a|", string.Empty, MediaDerivativeKind.Proxy, 0, ParamsHash, 1);
        string bareThenSeparator = MediaDerivativeIdentity.Compute(
            "a", "|", MediaDerivativeKind.Proxy, 0, ParamsHash, 1);

        trailingSeparatorThenEmpty.Should().NotBe(bareThenSeparator);
    }

    // =========================================================================================
    // Byte-equality with the consumer that writes the row
    // =========================================================================================

    [Fact]
    public void ForEveryMediaSlug_TheIdentityEqualsTheConsumersComputedCacheKey()
    {
        string sourceKey = StorageKey;
        string etag = ETag;

        var produced = new List<string>();

        foreach ((MediaDerivativeKind kind, MediaDerivativeSpec spec) in EverySpec())
        {
            // The component the consumer feeds in is `spec.Slug.ToString()`, so the shared enum's
            // member name has to be the slug name — asserted rather than assumed, because a rename on
            // either side would silently make the two functions agree on the empty set.
            Enum.Parse<MediaDerivativeKind>(spec.Slug.ToString()).Should().Be(kind);

            string identity = MediaDerivativeIdentity.Compute(
                sourceKey,
                etag,
                kind,
                spec.Variant,
                MediaDerivativeCacheKey.ComputeParamsHash(spec),
                spec.BuilderVersion);

            string consumersKey = MediaDerivativeCacheKey.ComputeCacheKey(sourceKey, etag, spec);

            identity.Should().Be(consumersKey);
            produced.Add(identity);
        }

        produced.Should().HaveCount(4);
        produced.Distinct(StringComparer.Ordinal).Should().HaveCount(4);
    }

    [Fact]
    public void EveryMediaSlug_HasAMemberInTheSharedKindEnum()
    {
        var slugs = Enum.GetValues<MediaDerivativeKindSlug>().Select(s => s.ToString()).ToArray();
        var kinds = Enum.GetValues<MediaDerivativeKind>().Select(k => k.ToString()).ToArray();

        // The four slugs the builders emit must all exist here, name for name. This is the drift
        // guard for the mapping the identity and the consumer rely on; a new slug is a deliberate
        // act that must add its member here in the same change.
        slugs.Should().BeSubsetOf(kinds);
        slugs.Should().Contain(MediaDerivativeKind.WaveformPeaks.ToString());
    }

    // =========================================================================================
    // Shape and purity
    // =========================================================================================

    [Fact]
    public void Compute_IsPureAndReturnsSixtyFourLowercaseHexCharacters()
    {
        string first = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.KeyframeIndex, 0, ParamsHash, 1);
        string second = MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.KeyframeIndex, 0, ParamsHash, 1);

        second.Should().Be(first);
        first.Should().HaveLength(MediaDerivativeIdentity.HexLength);
        Regex.IsMatch(first, "^[0-9a-f]{64}$").Should().BeTrue(
            "the key is a storage-key path segment and object stores are case-sensitive, so the " +
            "case is pinned to lowercase to match MediaDerivativeCacheKey");

        // Nothing ambient — culture, time, process state — may enter the material: a component is
        // formatted with InvariantCulture so a machine in a comma-decimal locale computes the same
        // key as one that is not. Restored in the finally so no other test inherits the locale.
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            MediaDerivativeIdentity.Compute(
                StorageKey, ETag, MediaDerivativeKind.KeyframeIndex, 0, ParamsHash, 1).Should().Be(first);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Compute_RejectsANullComponent()
    {
        Action nullKey = () => MediaDerivativeIdentity.Compute(
            null!, ETag, MediaDerivativeKind.Proxy, 0, ParamsHash, 1);
        Action nullEtag = () => MediaDerivativeIdentity.Compute(
            StorageKey, null!, MediaDerivativeKind.Proxy, 0, ParamsHash, 1);
        Action nullParams = () => MediaDerivativeIdentity.Compute(
            StorageKey, ETag, MediaDerivativeKind.Proxy, 0, null!, 1);

        // A null component must fail loudly rather than be folded in as an empty string: an empty
        // storage key is a legal (if useless) value, so silently substituting one would produce a
        // plausible-looking key for a row that has no source identity at all.
        nullKey.Should().Throw<ArgumentNullException>();
        nullEtag.Should().Throw<ArgumentNullException>();
        nullParams.Should().Throw<ArgumentNullException>();
    }

    // =========================================================================================
    // Both contexts map the same identity
    // =========================================================================================

    [Fact]
    public void BothContexts_MapTheSameSingleColumnIdentityIndex()
    {
        // Sqlite rather than InMemory, deliberately: table names, index filters and
        // ExcludeFromMigrations are RELATIONAL concepts, and the in-memory provider does not run the
        // relational conventions that give an entity its table name. Asserting them there would test
        // a model neither service ever builds.
        using var api = new InferenceApiDbContext(
            new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseSqlite("DataSource=:memory:").Options);
        using var engine = new WorkflowEngineDbContext(
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseSqlite("DataSource=:memory:").Options);

        var contexts = new (string Name, IEntityType Entity)[]
        {
            ("InferenceApiDbContext", MediaDerivativeEntity(api)),
            ("WorkflowEngineDbContext", MediaDerivativeEntity(engine)),
        };

        foreach ((string name, IEntityType entity) in contexts)
        {
            entity.GetTableName().Should().Be("media_derivatives", $"{name} maps the shared table");

            // The old three-column index must be gone from BOTH models, not merely renamed in one:
            // a leftover mapping would have EF create or expect a constraint the migrations no
            // longer produce.
            entity.GetIndexes()
                .Select(i => i.GetDatabaseName())
                .Should().NotContain("ux_media_derivatives_identity", $"{name} drops the old index");

            List<IIndex> identityIndexes = entity.GetIndexes()
                .Where(i => i.GetDatabaseName() == "ux_media_derivatives_cache_key")
                .ToList();
            identityIndexes.Should().ContainSingle(
                $"{name} maps the identity as one column, so as one index");
            IIndex identity = identityIndexes[0];
            identity.IsUnique.Should().BeTrue($"{name}'s index is unique");
            identity.GetFilter().Should().Be("deleted_at IS NULL", $"{name}'s index is partial");
            identity.Properties.Select(p => p.Name)
                .Should().Equal(new[] { nameof(MediaDerivative.CacheKey) }, $"{name}'s index covers the key alone");

            Property(entity, nameof(MediaDerivative.SourceETag)).GetMaxLength().Should().Be(200);
            Property(entity, nameof(MediaDerivative.ParamsHash)).GetMaxLength().Should().Be(64);
            Property(entity, nameof(MediaDerivative.CacheKey)).GetMaxLength().Should().Be(64);
            Property(entity, nameof(MediaDerivative.CacheKey)).IsNullable.Should().BeFalse();
            Property(entity, nameof(MediaDerivative.BuilderVersion)).ClrType.Should().Be<int>();
            Property(entity, nameof(MediaDerivative.BuilderVersion)).IsNullable.Should().BeFalse();

            // Kind stays string-persisted, which is what makes the new WaveformPeaks member a
            // code-only change with no data migration behind it.
            Property(entity, nameof(MediaDerivative.Kind))
                .GetTypeMapping().Converter.Should().NotBeNull($"{name} persists Kind as a string");
        }

        // The table is API-owned and the engine only reads it; the mirror must never migrate it.
        // ExcludeFromMigrations() is deliberately NOT asserted here. EF Core 10 records it neither
        // as an annotation on the entity type nor as a mapping fragment — verified against
        // workflow_definitions in this very model, which IS excluded and reads identically to one
        // that is not — so a test could only assert a piece of EF internals that happens to be
        // visible today. It is verified where it is actually load-bearing instead:
        // `dotnet ef migrations has-pending-model-changes --context WorkflowEngineDbContext` reports
        // no change, and no migration for media_derivatives is generated in the engine's assembly.
    }

    // =========================================================================================

    /// <summary>One spec per slug the media department's builders emit, with the shared kind it maps to.</summary>
    private static IEnumerable<(MediaDerivativeKind Kind, MediaDerivativeSpec Spec)> EverySpec()
    {
        yield return (MediaDerivativeKind.Proxy, MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4));
        yield return (MediaDerivativeKind.Filmstrip, MediaDerivativeSpec.ForFilmstrip(0));
        yield return (MediaDerivativeKind.WaveformPeaks, MediaDerivativeSpec.ForWaveformPeaks(1));
        yield return (MediaDerivativeKind.KeyframeIndex, MediaDerivativeSpec.ForKeyframeIndex());
    }

    private static IEntityType MediaDerivativeEntity(DbContext context) =>
        context.Model.FindEntityType(typeof(MediaDerivative))
        ?? throw new InvalidOperationException("media_derivatives is not mapped by this context");

    private static IProperty Property(IEntityType entity, string name) =>
        entity.FindProperty(name) ?? throw new InvalidOperationException($"{name} is not mapped");
}
