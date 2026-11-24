using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The ADVERSARIAL half of the derivative cache identity: written by an executor who did not author
/// <c>MediaDerivativeSpec</c>/<c>MediaDerivativeCacheKey</c>, and deliberately composed of the cases a
/// naive implementation gets wrong rather than of the cases any implementation gets right.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this file is attacking.</b> Wave A's review focus is that two different source files, or
/// two different encoder parameter sets, must never collide onto one <c>media_derivatives</c> row.
/// The cheap implementation of "join six components and hash them" is
/// <c>string.Join("|", parts)</c> — and that implementation cannot tell
/// <c>key="a", etag="b|c"</c> from <c>key="a|b", etag="c"</c>: both produce the material
/// <c>"a|b|c"</c>. Every test below is built so that a <c>|</c>-join would fail it, and the
/// self-verifying ones assert that premise explicitly before asserting the real property, so a later
/// reader can see the collision was genuine rather than tolerated.
/// </para>
/// <para>
/// <b>Deliberately not a copy of the implementer's tests.</b> <c>MediaDerivativeVideoTests</c> pins a
/// fixed token matrix and <c>MediaDerivativeIdentityTests</c> (backend-owned) pins
/// <c>MediaDerivativeIdentity.Compute</c> for the four real specs. This file instead uses a
/// <b>seeded</b> generator whose cases are derived from the collision premise itself, splits the
/// per-component requirement into one test per component, drives the culture/key-order
/// canonicalization question, and cross-checks the two independent implementations over
/// separator-bearing, empty-etag and hand-built inputs that neither existing file reaches.
/// </para>
/// <para>
/// <b>Canonicalization lives at the authoring boundary, and the digest is faithful to it.</b> The
/// positive invariant is asserted by
/// <c>The_params_hash_is_a_faithful_digest_of_a_canonically_authored_params_json</c>; the limit that
/// follows from it — a hand-authored, non-canonical <c>ParamsJson</c> hashes verbatim — is asserted
/// explicitly and passes, by
/// <c>A_hand_authored_non_canonical_params_json_is_hashed_verbatim_an_accepted_limit</c>. That limit
/// is unreachable in production today (the factories are the only construction site) and was recorded
/// as T-304 FINDING-1, whose resolution was to amend the criterion rather than add a second
/// canonicalizer inside the hash.
/// </para>
/// </remarks>
public class MediaDerivativeCacheIdentityVideoTests
{
    /// <summary>Seed for every generated sweep. Fixed so a failure is reproducible.</summary>
    private const int Seed = 20262;

    private const string StorageKey = "projects/11111111-2222-3333-4444-555555555555/userFiles/interview.mp4";

    /// <summary>A quoted S3 ETag, i.e. the shape the column holds.</summary>
    private const string ETag = "\"9f8e7d6c5b4a39281706f5e4d3c2b1a0\"";

    private static readonly Regex LowercaseHex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>The spec every "everything else held constant" test starts from.</summary>
    private static MediaDerivativeSpec Baseline() => MediaDerivativeSpec.ForWaveformPeaks(1);

    // =========================================================================================
    // 1. The join is injective — attacked directly, with the collision premise asserted
    // =========================================================================================

    [Fact]
    public void The_separator_cannot_be_traded_between_the_storage_key_and_the_etag()
    {
        // The premise, asserted rather than assumed: these two DIFFERENT source identities produce
        // the SAME material under a bare |-join. Whatever the implementation does, it must not
        // produce the same key for them.
        string naiveOne = "a" + "|" + "b|c";
        string naiveOther = "a|b" + "|" + "c";
        naiveOne.Should().Be(naiveOther,
            "the pair is only a witness if a |-join really does collide on it");

        MediaDerivativeSpec spec = Baseline();

        string oneSourceSplitTwoWays = MediaDerivativeCacheKey.ComputeCacheKey("a", "b|c", spec);
        string theOtherSplit = MediaDerivativeCacheKey.ComputeCacheKey("a|b", "c", spec);

        oneSourceSplitTwoWays.Should().NotBe(theOtherSplit,
            "key=\"a\", etag=\"b|c\" and key=\"a|b\", etag=\"c\" are two different sources and must " +
            "never share one derivative object or one media_derivatives row");

        // The same trade at the same boundary, one level down: the SOURCE hash is the storage
        // directory segment, so a collision there would put two sources' objects in one directory
        // even if the row keys somehow differed.
        MediaDerivativeCacheKey.ComputeSourceHash("a", "b|c")
            .Should().NotBe(MediaDerivativeCacheKey.ComputeSourceHash("a|b", "c"));

        Guid projectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, "a", "b|c")
            .Should().NotBe(MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, "a|b", "c"));
    }

    [Fact]
    public void The_params_hash_cannot_be_traded_across_the_params_json_and_builder_version_boundary()
    {
        // The parameter JSON is the ONE component of the join whose content a caller can shape, and
        // it sits next to the builder version. A params hash built by concatenating the two with
        // nothing between them cannot tell ("x", 12) from ("x1", 2): both produce the material "x12",
        // so two genuinely different parameter sets — same file, different encoder settings — would
        // share one params hash, one cache key and one object.
        string naiveOne = "x" + 12.ToString(CultureInfo.InvariantCulture);
        string naiveOther = "x1" + 2.ToString(CultureInfo.InvariantCulture);
        naiveOne.Should().Be(naiveOther,
            "the pair is only a witness if the naive concatenation really does collide on it");

        var twelve = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "x", 12);
        var two = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "x1", 2);

        MediaDerivativeCacheKey.ComputeParamsHash(twelve)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(two),
                "two different parameter sets must never share a params hash — the leaf name carries " +
                "it, so a collision would also put two encoder settings on one object");

        // And the same for the full key, where the version is a component in its own right.
        MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, twelve)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, two));

        // The separator-borne form of the same trade, at the same boundary: a parameter document that
        // ends where the version begins ("x|1" + 2 against "x" + 1|2) cannot be arranged because the
        // version is a decimal integer and so never carries the separator — which is exactly why the
        // separator-less concatenation above, not a `|`-join, is the collision that reaches here.
        new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "x", 1).BuilderVersion
            .ToString(CultureInfo.InvariantCulture).Should().NotContain("|");

        var separatorBearing = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{|}", 1);
        MediaDerivativeCacheKey.ComputeParamsHash(separatorBearing)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(
                new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{|}1", 1)),
                "a separator inside the parameter document must not be able to absorb the version");
    }

    [Fact]
    public void Only_the_storage_key_and_the_etag_boundary_can_collide_under_a_naive_join()
    {
        // Documents WHY the two witnesses above are the load-bearing ones: the trailing four
        // components (slug, variant, params hash, builder version) are all fixed-shape — an enum
        // name and a 64-hex-digit hash — so their character boundaries cannot be traded with each
        // other, and a naive join does not collide on them. That is a fact about the component
        // shapes, not a licence: it is asserted here so that a future component that is NOT
        // fixed-shape (a free-form variant name, a truncated hash) fails this test and forces the
        // injectivity question to be re-asked rather than assumed.
        MediaDerivativeSpec spec = Baseline();
        string paramsHash = MediaDerivativeCacheKey.ComputeParamsHash(spec);

        paramsHash.Should().MatchRegex("^[0-9a-f]{64}$");
        Enum.GetNames<MediaDerivativeKindSlug>().Should().OnlyContain(name => !name.Contains('|'),
            "the slug component is a closed enum member name, so it can never carry the separator");
        spec.Variant.ToString(CultureInfo.InvariantCulture).Should().MatchRegex("^-?[0-9]+$");
        spec.BuilderVersion.ToString(CultureInfo.InvariantCulture).Should().MatchRegex("^-?[0-9]+$");
    }

    [Fact]
    public void A_seeded_sweep_of_separator_bearing_source_pairs_never_collides()
    {
        // A property-style sweep with no new package: a fixed-seed generator, derived from the
        // collision PREMISE rather than from arbitrary strings. Each base material contains at least
        // two separators; splitting it at ANY of them yields a different (key, etag) pair that a bare
        // |-join flattens back to the very same material. So every case in one base is a genuine
        // naive collision, and the assertion is that the real keys are all distinct anyway.
        var random = new Random(Seed);

        MediaDerivativeSpec[] specs =
        [
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForFilmstrip(0),
            MediaDerivativeSpec.ForFilmstrip(2),
            MediaDerivativeSpec.ForWaveformPeaks(0),
            MediaDerivativeSpec.ForWaveformPeaks(2),
            MediaDerivativeSpec.ForKeyframeIndex()
        ];

        var seenTriples = new HashSet<string>(StringComparer.Ordinal);
        var keyOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        int bases = 0;
        int basesWithARealCollision = 0;
        int cases = 0;

        for (int b = 0; b < 160; b++)
        {
            string material = BuildFlatMaterial(random);
            int[] separators = [.. Enumerable.Range(0, material.Length).Where(i => material[i] == '|')];

            if (separators.Length < 2)
                continue;

            bases++;
            basesWithARealCollision++;

            for (int s = 0; s < separators.Length; s++)
            {
                int at = separators[s];
                string storageKey = material[..at];
                string etag = material[(at + 1)..];

                // The premise, asserted per case: a bare |-join cannot tell this split from any other
                // split of the same material.
                (storageKey + "|" + etag).Should().Be(material);

                MediaDerivativeSpec spec = specs[(b + s) % specs.Length];
                string triple = storageKey + "\u0000" + etag + "\u0000" + spec.Slug + "\u0000" +
                                spec.Variant.ToString(CultureInfo.InvariantCulture);

                if (!seenTriples.Add(triple))
                    continue;

                cases++;

                string key = MediaDerivativeCacheKey.ComputeCacheKey(storageKey, etag, spec);
                keyOwners.Should().NotContainKey(key,
                    $"the naive-collision case '{Describe(storageKey)}'/'{Describe(etag)}' under {spec.Slug} " +
                    $"variant {spec.Variant} collided with '{keyOwners.GetValueOrDefault(key, string.Empty)}'");

                keyOwners[key] = triple;
            }
        }

        bases.Should().BeGreaterThanOrEqualTo(100, "the sweep must actually have produced collision bases");
        basesWithARealCollision.Should().Be(bases);
        cases.Should().BeGreaterThanOrEqualTo(400, "the sweep must have exercised a few hundred cases");
        keyOwners.Should().HaveCount(cases, "every distinct (source, spec) triple must own a distinct key");
    }

    [Fact]
    public void A_seeded_sweep_of_separator_bearing_specs_on_one_source_never_collides()
    {
        // The other half of the same sweep: ONE source, many hand-built parameter sets whose JSON and
        // whose builder version are chosen to make the params-hash/builder-version boundary abut a
        // separator. Two parameter sets that hash the same would be exactly the "two different
        // encoder params hashes" collision the review focus names.
        var random = new Random(Seed + 1);

        var specs = new List<MediaDerivativeSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        MediaDerivativeKindSlug[] slugs = Enum.GetValues<MediaDerivativeKindSlug>();

        for (int i = 0; i < 400; i++)
        {
            string json = BuildFlatMaterial(random);
            int variant = random.Next(0, 40);
            int builderVersion = random.Next(0, 40);

            string identity = json + "\u0000" + variant.ToString(CultureInfo.InvariantCulture) + "\u0000" +
                              builderVersion.ToString(CultureInfo.InvariantCulture);

            if (!seen.Add(identity))
                continue;

            specs.Add(new MediaDerivativeSpec(slugs[i % slugs.Length], variant, json, builderVersion));
        }

        specs.Should().HaveCountGreaterThanOrEqualTo(300, "the generator must produce distinct specs");

        string[] keys = [.. specs.Select(s => MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, s))];

        keys.Distinct(StringComparer.Ordinal).Should().HaveCount(specs.Count,
            "two different parameter sets of one source must never collide onto one cache key");
    }

    // =========================================================================================
    // 2. One test per component: the key changes when that component ALONE changes
    // =========================================================================================

    [Fact]
    public void The_key_changes_when_only_the_storage_key_changes()
    {
        MediaDerivativeSpec spec = Baseline();

        string baseline = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, spec);
        string moved = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey + ".moved", ETag, spec);
        string reprefixed = MediaDerivativeCacheKey.ComputeCacheKey(
            "projects/22222222-3333-4444-5555-666666666666/userFiles/interview.mp4", ETag, spec);

        baseline.Should().NotBe(moved, "the storage key is a component of the identity");
        baseline.Should().NotBe(reprefixed, "a file in another project is another source");
        moved.Should().NotBe(reprefixed);
    }

    [Fact]
    public void The_key_changes_when_only_the_etag_changes()
    {
        MediaDerivativeSpec spec = Baseline();

        // The review focus, verbatim: two sources differing ONLY by etag. Same storage key, so a
        // naive implementation keyed on the storage key alone would serve the first file's
        // derivative for the second file's bytes.
        string baseline = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, spec);
        string other = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, "\"0000deadbeef0000\"", spec);
        string empty = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, string.Empty, spec);

        baseline.Should().NotBe(other, "the etag is content identity and must be a component");
        baseline.Should().NotBe(empty, "an absent etag is not the same source as a present one");
        other.Should().NotBe(empty);

        MediaDerivativeCacheKey.ComputeSourceHash(StorageKey, ETag)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeSourceHash(StorageKey, "\"0000deadbeef0000\""));
    }

    [Fact]
    public void The_key_changes_when_only_the_slug_changes()
    {
        string paramsJson = Baseline().ParamsJson;
        int builderVersion = Baseline().BuilderVersion;

        string[] keys = [.. Enum.GetValues<MediaDerivativeKindSlug>()
            .Select(slug => MediaDerivativeCacheKey.ComputeCacheKey(
                StorageKey, ETag, new MediaDerivativeSpec(slug, 0, paramsJson, builderVersion)))];

        keys.Distinct(StringComparer.Ordinal).Should().HaveCount(Enum.GetValues<MediaDerivativeKindSlug>().Length,
            "two kinds of one source whose parameter JSON happens to coincide must still be two identities");
    }

    [Fact]
    public void The_key_changes_when_only_the_variant_changes()
    {
        // The variant is the ONE component a caller selects besides the slug, so it must separate on
        // its own with the params hash held artificially fixed — otherwise two filmstrip densities
        // would be distinguished only by their JSON happening to differ.
        string paramsJson = Baseline().ParamsJson;
        int builderVersion = Baseline().BuilderVersion;

        string[] keys = [.. Enumerable.Range(0, 12)
            .Select(variant => MediaDerivativeCacheKey.ComputeCacheKey(
                StorageKey, ETag, new MediaDerivativeSpec(MediaDerivativeKindSlug.Filmstrip, variant, paramsJson, builderVersion)))];

        keys.Distinct(StringComparer.Ordinal).Should().HaveCount(12);
    }

    [Fact]
    public void The_key_changes_when_only_the_params_json_changes()
    {
        var a = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"crf\":23}", 1);
        var b = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"crf\":24}", 1);

        MediaDerivativeCacheKey.ComputeParamsHash(a).Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(b));
        MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, a)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, b),
                "two encoder parameter sets of one source must be two rows");
    }

    [Fact]
    public void The_key_changes_when_only_the_builder_version_changes()
    {
        var current = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"crf\":23}", 1);
        var bumped = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"crf\":23}", 2);

        MediaDerivativeCacheKey.ComputeParamsHash(current)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(bumped),
                "the params hash covers the builder version, so an argv change invalidates the cache");

        MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, current)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, bumped),
                "a builder bump must invalidate the key, not merely be recorded");
    }

    [Fact]
    public void A_builder_bump_moves_every_kind_and_never_only_one_of_them()
    {
        // The reverse of the tempting optimisation ("fold the version in only for the proxy"): a
        // builder version that participated for one slug but not another would leave the other
        // slug's stale objects served forever after its own argv changed.
        MediaDerivativeSpec[] current =
        [
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForFilmstrip(0),
            MediaDerivativeSpec.ForFilmstrip(1),
            MediaDerivativeSpec.ForFilmstrip(2),
            MediaDerivativeSpec.ForWaveformPeaks(0),
            MediaDerivativeSpec.ForWaveformPeaks(1),
            MediaDerivativeSpec.ForWaveformPeaks(2),
            MediaDerivativeSpec.ForKeyframeIndex()
        ];

        string[] bumped = [.. current.Select(s => MediaDerivativeCacheKey.ComputeCacheKey(
            StorageKey, ETag, s with { BuilderVersion = s.BuilderVersion + 1 }))];

        string[] unbumped = [.. current.Select(s => MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, s))];

        bumped.Should().HaveCount(unbumped.Length);
        bumped.Intersect(unbumped, StringComparer.Ordinal).Should().BeEmpty(
            "every slug's identity must move when its builder version moves");
    }

    [Fact]
    public void An_HLG_derivative_cached_under_the_old_knee_is_not_the_current_identity()
    {
        // hdr-tonemap-v3 moved the HLG delivery chain to mobius knee 0.2. The kind is not in
        // ParamsJson (the factories describe encoder knobs; the tone-map kind is resolved from the
        // source's probe when the argv is built), so the builder version is the ONLY component that
        // can invalidate a proxy, filmstrip or still cached while the knee was 0.5. This pins the
        // version bump that makes the docs' "regenerates every cached derivative once" true.
        MediaDerivativeSpec[] hlgDerivatives =
        [
            MediaDerivativeSpec.ForProxy(2160, 30, 1, true, 4),
            MediaDerivativeSpec.ForFilmstrip(0),
            MediaDerivativeSpec.ForStills(Array.Empty<int>())
        ];

        foreach (MediaDerivativeSpec current in hlgDerivatives)
        {
            // 3 is the post-knee-change build; 2 is the identity an HLG derivative had under knee 0.5.
            current.BuilderVersion.Should().Be(3,
                $"{current.Slug} takes the same HdrToneMap.Filter chain, so its builder had to move");

            MediaDerivativeSpec underTheOldKnee = current with { BuilderVersion = 2 };

            MediaDerivativeCacheKey.ComputeParamsHash(current)
                .Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(underTheOldKnee),
                    "the params hash covers the builder version");
            MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, current)
                .Should().NotBe(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, underTheOldKnee),
                    "an HLG derivative cached under knee 0.5 must not be served after the knee change");
        }
    }

    // =========================================================================================
    // 3. The converse: a cache that can never hit is also a bug
    // =========================================================================================

    [Fact]
    public void The_converse_an_identical_source_identity_yields_an_identical_key()
    {
        string first = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, Baseline());
        string second = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, Baseline());

        second.Should().Be(first, "an identical source must be a cache HIT, not a permanent miss");

        // Two independently built specs of the same parameters are one identity, not two: the spec is
        // a value, and a second factory call must not mint a second key.
        MediaDerivativeSpec[] rebuilt =
        [
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4)
        ];

        rebuilt.Select(s => MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, s))
            .Distinct(StringComparer.Ordinal).Should().HaveCount(1);

        // ...and an empty etag is an identity too, not a random value: a source whose HEAD failed must
        // still be a cache hit on the next request rather than a new row every time.
        MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, string.Empty, Baseline())
            .Should().Be(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, string.Empty, Baseline()));
    }

    [Fact]
    public void The_key_is_sixty_four_lowercase_hex_and_stable_across_calls()
    {
        MediaDerivativeSpec spec = Baseline();

        string first = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, spec);
        string second = MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, spec);

        first.Should().HaveLength(64);
        LowercaseHex64.IsMatch(first).Should().BeTrue(
            "the key is a storage-key path segment and object stores are case-sensitive, so a mixed-case " +
            "form would address a different object than the one written");
        second.Should().Be(first);

        MediaDerivativeCacheKey.ComputeParamsHash(spec).Should().MatchRegex("^[0-9a-f]{64}$");
        MediaDerivativeCacheKey.ComputeSourceHash(StorageKey, ETag)
            .Should().MatchRegex("^[0-9a-f]{" + MediaDerivativeCacheKey.SourceHashLength + "}$");
    }

    // =========================================================================================
    // 4. Canonicalization — key order and numeric culture
    // =========================================================================================

    [Fact]
    public void Params_json_and_the_params_hash_are_invariant_to_the_current_numeric_culture()
    {
        // The catalogue must be byte-identical on a machine whose locale writes decimals with a comma:
        // the params hash is the identity of an encoder parameter set, so a locale-dependent hash
        // would give one source two rows depending on which host produced each.
        CultureInfo original = CultureInfo.CurrentCulture;

        (string Json, string Hash, string Key)[] expected = BuildUnder(CultureInfo.InvariantCulture);
        (string Json, string Hash, string Key)[] actual = BuildUnder(new CultureInfo("de-DE"));

        actual.Should().HaveCount(expected.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            actual[i].Json.Should().Be(expected[i].Json,
                "spec {0}'s parameters must be formatted with InvariantCulture, never the ambient locale", i);
            actual[i].Hash.Should().Be(expected[i].Hash);
            actual[i].Key.Should().Be(expected[i].Key);
        }

        // ...and the reason the culture cannot reach a number at all: every knob in the closed
        // catalogue is an integer, so no catalogue contains a decimal separator to render — a comma
        // in this JSON can only ever be a member or element separator.
        expected.Should().OnlyContain(entry => !entry.Json.Contains('.'));

        CultureInfo.CurrentCulture.Should().Be(original);
    }

    /// <summary>Builds the four factory specs and their three derived strings under one culture.</summary>
    private static (string Json, string Hash, string Key)[] BuildUnder(CultureInfo culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;

        try
        {
            MediaDerivativeSpec[] specs =
            [
                MediaDerivativeSpec.ForProxy(1080, 30000, 1001, true, 4),
                MediaDerivativeSpec.ForFilmstrip(1),
                MediaDerivativeSpec.ForWaveformPeaks(1),
                MediaDerivativeSpec.ForKeyframeIndex()
            ];

            return [.. specs.Select(spec => (
                spec.ParamsJson,
                MediaDerivativeCacheKey.ComputeParamsHash(spec),
                MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, spec)))];
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// The canonicalization invariant the design actually ships, asserted positively: canonicalization
    /// happens at the <b>authoring boundary</b> — <c>CanonicalJsonObject</c> inside the factories —
    /// and <see cref="MediaDerivativeCacheKey.ComputeParamsHash"/> is then a faithful digest of the
    /// exact document that described the encode.
    /// </summary>
    /// <remarks>
    /// This is the amended form of T-304's original criterion. The earlier wording ("the same
    /// parameters in a different key order produce the same params hash") assumed the hash was the
    /// canonicalizer; it is not, and hardening it into one would mean re-parsing JSON and
    /// re-canonicalizing inside the WorkflowEngine — a <b>second</b> canonicalizer that can disagree
    /// with the first, where a disagreement is a silent permanent cache miss. One canonicalizer, at
    /// the point where the document is authored, is the design T-301 shipped; see
    /// <c>A_hand_authored_non_canonical_params_json_is_hashed_verbatim_an_accepted_limit</c> for the
    /// limit that follows from it.
    /// </remarks>
    [Fact]
    public void The_params_hash_is_a_faithful_digest_of_a_canonically_authored_params_json()
    {
        // Two independent authoring passes over the same parameters — from different call sites, in a
        // different construction order, with inputs whose key order would differ if a human wrote the
        // JSON by hand (the proxy's source-derived knobs come first naturally, its alphabetically first
        // knob does not) — must agree on the document AND on the digest.
        MediaDerivativeSpec[][] pairs =
        [
            [MediaDerivativeSpec.ForProxy(1080, 30, 1, hasAudio: true, threads: 4),
             MediaDerivativeSpec.ForProxy(1080, 30, 1, hasAudio: true, threads: 4)],
            [MediaDerivativeSpec.ForProxy(2160, 30000, 1001, hasAudio: false, threads: 2),
             MediaDerivativeSpec.ForProxy(2160, 30000, 1001, hasAudio: false, threads: 2)],
            [MediaDerivativeSpec.ForFilmstrip(1), MediaDerivativeSpec.ForFilmstrip(1)],
            [MediaDerivativeSpec.ForWaveformPeaks(2), MediaDerivativeSpec.ForWaveformPeaks(2)],
            [MediaDerivativeSpec.ForKeyframeIndex(), MediaDerivativeSpec.ForKeyframeIndex()]
        ];

        foreach (MediaDerivativeSpec[] pair in pairs)
        {
            pair[0].ParamsJson.Should().Be(pair[1].ParamsJson,
                $"{pair[0].Slug} must author exactly one document for one parameter set");

            MediaDerivativeCacheKey.ComputeParamsHash(pair[0])
                .Should().Be(MediaDerivativeCacheKey.ComputeParamsHash(pair[1]),
                    $"{pair[0].Slug}'s digest must be a function of the authored document alone, so a " +
                    "second call site cannot mint a second key for the same parameters");

            MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, pair[0])
                .Should().Be(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, pair[1]));
        }

        // And the boundary really IS canonicalizing, which is what makes the verbatim digest stable:
        // the proxy catalogue is emitted in ordinal order, so it does NOT begin with the source-derived
        // knobs a hand-author would naturally write first.
        string canonical = MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4).ParamsJson;
        string naturalAuthoringOrder = "{\"targetHeight\":540,\"widthSentinel\":-2,\"crf\":23}";

        canonical.Should().StartWith("{\"audioBitrateKbps\":",
            "the authoring boundary sorts ordinally — this is the only canonicalizer in the path");
        canonical.Should().NotBe(naturalAuthoringOrder);
        canonical.Should().NotContain("\"targetHeight\":540,\"widthSentinel\"");

        // The digest is taken over the AUTHORED text, never over a caller's equivalent rewrite.
        MediaDerivativeCacheKey.ComputeParamsHash(
                new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, canonical, MediaDerivativeSpec.ProxyBuilderVersion))
            .Should().Be(MediaDerivativeCacheKey.ComputeParamsHash(
                MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4)));
    }

    /// <summary>
    /// <b>Accepted limit (T-304 FINDING-1; the criterion was amended, not the hash).</b>
    /// <c>ComputeParamsHash</c> hashes <c>spec.ParamsJson</c> <b>verbatim</b>: it canonicalizes
    /// nothing. Two semantically equal but textually different hand-authored documents therefore
    /// produce two different params hashes, and two different cache keys.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is unreachable today.</b> No production path constructs a <c>MediaDerivativeSpec</c>
    /// outside the four factories in <c>MediaDerivativeSpec.cs</c>, and those always emit the canonical
    /// document, so the verbatim digest is stable for everything the system can actually author. It is
    /// a defence-in-depth gap, not a live collision, and it is asserted here rather than left to a
    /// comment so it stays visible.
    /// </para>
    /// <para>
    /// <b>The resolution, if it is ever wanted, is one canonicalizer at the authoring boundary — not a
    /// second one here.</b> Re-parsing and re-canonicalizing inside the hash would create two things
    /// that can disagree, and a disagreement between them is a silent permanent cache miss: precisely
    /// the class of bug this wave is reviewed for.
    /// </para>
    /// </remarks>
    [Fact]
    public void A_hand_authored_non_canonical_params_json_is_hashed_verbatim_an_accepted_limit()
    {
        var sorted = new MediaDerivativeSpec(
            MediaDerivativeKindSlug.Proxy, 0, "{\"a\":1,\"b\":2,\"crf\":23}", 1);
        var reordered = new MediaDerivativeSpec(
            MediaDerivativeKindSlug.Proxy, 0, "{\"crf\":23,\"b\":2,\"a\":1}", 1);

        // The accepted limit, asserted as the DIVERGENCE it is: the hash digests the document it was
        // given, so a hand-authored rewrite of the same parameters is a different identity. Do not turn
        // this into an equality assertion without also moving canonicalization — that would be asking
        // the hash to do a job it deliberately does not have.
        MediaDerivativeCacheKey.ComputeParamsHash(reordered)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(sorted),
                "the params hash digests a canonically authored document verbatim and is not itself a " +
                "canonicalizer; T-304's original criterion was amended to say so");

        MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, reordered)
            .Should().NotBe(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, sorted));

        // ...and the shape it takes to reach that limit is one no factory can produce.
        reordered.ParamsJson.Should().NotBe(sorted.ParamsJson);
        sorted.ParamsJson.Should().Be("{\"a\":1,\"b\":2,\"crf\":23}");
    }

    [Fact]
    public void Params_json_is_whitespace_free_so_a_reformatted_document_cannot_key_differently()
    {
        // The other half of canonicalization that the factories DO own, pinned independently: a JSON
        // document with incidental whitespace would hash differently from the same document without
        // it, so whitespace must never reach ParamsJson in the first place.
        foreach (MediaDerivativeSpec spec in new[]
        {
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForFilmstrip(0),
            MediaDerivativeSpec.ForWaveformPeaks(2),
            MediaDerivativeSpec.ForKeyframeIndex()
        })
        {
            spec.ParamsJson.Should().StartWith("{").And.EndWith("}");
            spec.ParamsJson.Should().NotContain(" ").And.NotContain("\n").And.NotContain("\t");

            List<string> keys = [.. Regex.Matches(spec.ParamsJson, "\"([A-Za-z0-9]+)\":")
                .Select(m => m.Groups[1].Value)];

            keys.Should().Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
                $"{spec.Slug}'s members must be ordinally sorted: {spec.ParamsJson}");
        }
    }

    // =========================================================================================
    // 5. Cross-implementation agreement — the two independent copies of the same key
    // =========================================================================================

    [Fact]
    public void The_two_independent_implementations_agree_byte_for_byte_on_separator_bearing_inputs()
    {
        // MediaDerivativeCacheKey (WorkflowEngine, this department) writes the row;
        // MediaDerivativeIdentity (Shared, backend-owned) is what the rest of the system reads it
        // with. They are separate implementations of one value, so a single character of divergence
        // would produce a cache that silently never hits — no error, no log, just a permanent miss.
        // The separator-bearing and empty cases below are the ones neither existing test file reaches.
        (string Key, string Etag)[] sources =
        [
            (StorageKey, ETag),
            ("a", "b|c"),
            ("a|b", "c"),
            ("a|", string.Empty),
            ("a", "|"),
            (string.Empty, ETag),
            (StorageKey, string.Empty),
            ("projects/p/agentFiles/media-derivatives/0:0|", "1:a|"),
            ("|", "|"),
            ("::", "||")
        ];

        MediaDerivativeSpec[] specs =
        [
            MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4),
            MediaDerivativeSpec.ForProxy(2160, 24, 1, false, 2),
            MediaDerivativeSpec.ForFilmstrip(0),
            MediaDerivativeSpec.ForFilmstrip(2),
            MediaDerivativeSpec.ForWaveformPeaks(1),
            MediaDerivativeSpec.ForKeyframeIndex(),
            new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 7, "{\"x\":\"a|b\"}", 12),
            new MediaDerivativeSpec(MediaDerivativeKindSlug.Filmstrip, 0, "{}", 0)
        ];

        int compared = 0;

        foreach ((string key, string etag) in sources)
        {
            foreach (MediaDerivativeSpec spec in specs)
            {
                // A rename on either side would make the two functions agree on the empty set, so the
                // slug-to-enum mapping is asserted rather than assumed.
                MediaDerivativeKind kind = Enum.Parse<MediaDerivativeKind>(spec.Slug.ToString());
                kind.ToString().Should().Be(spec.Slug.ToString());

                string identity = MediaDerivativeIdentity.Compute(
                    key,
                    etag,
                    kind,
                    spec.Variant,
                    MediaDerivativeCacheKey.ComputeParamsHash(spec),
                    spec.BuilderVersion);

                identity.Should().Be(
                    MediaDerivativeCacheKey.ComputeCacheKey(key, etag, spec),
                    $"the row is written with one and looked up with the other: key='{Describe(key)}', " +
                    $"etag='{Describe(etag)}', slug={spec.Slug}, variant={spec.Variant}, " +
                    $"builderVersion={spec.BuilderVersion}");

                compared++;
            }
        }

        compared.Should().Be(sources.Length * specs.Length);
    }

    [Fact]
    public void The_backend_identity_accepts_the_builder_versions_and_variants_the_catalogue_can_produce()
    {
        // A hand-built spec with a multi-digit variant and a large builder version — the shape a
        // future variant index or a long-lived builder would have. Both implementations format those
        // with InvariantCulture; a divergence here would be a permanent miss for exactly the
        // derivatives nobody has written yet.
        for (int variant = 0; variant <= 12; variant++)
        {
            for (int version = 0; version <= 12; version++)
            {
                var spec = new MediaDerivativeSpec(MediaDerivativeKindSlug.Filmstrip, variant, "{}", version);

                MediaDerivativeIdentity.Compute(
                        StorageKey,
                        ETag,
                        MediaDerivativeKind.Filmstrip,
                        variant,
                        MediaDerivativeCacheKey.ComputeParamsHash(spec),
                        version)
                    .Should().Be(MediaDerivativeCacheKey.ComputeCacheKey(StorageKey, ETag, spec));
            }
        }
    }

    [Fact]
    public void The_source_hash_and_storage_prefix_are_the_first_characters_of_no_other_sources_hash()
    {
        // The storage directory is a 16-character TRUNCATION of the source identity, so a collision
        // there would put two sources' objects in one directory even when their cache keys differ.
        // Truncation cannot be made injective, so what is asserted is the property the design relies
        // on instead: the prefix is derived from BOTH halves, never from the storage key alone.
        string fromEtagA = MediaDerivativeCacheKey.ComputeSourceHash(StorageKey, "\"etag-A\"");
        string fromEtagB = MediaDerivativeCacheKey.ComputeSourceHash(StorageKey, "\"etag-B\"");

        fromEtagA.Should().NotBe(fromEtagB,
            "a directory keyed on the storage key alone would let a replaced file's derivative collide " +
            "with the object still on disk");

        Guid projectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, StorageKey, "\"etag-A\"")
            .Should().Be($"projects/{projectId:D}/agentFiles/media-derivatives/{fromEtagA}");
    }

    // =========================================================================================

    /// <summary>
    /// A separator-bearing string built from a seeded generator: fragments of letters, digits and the
    /// two characters the join itself uses ('|' and ':'), so a component can carry a partial
    /// length-prefix and impersonate one.
    /// </summary>
    private static string BuildFlatMaterial(Random random)
    {
        string[] fragments = ["a", "b", "zz", "0", "12", "9", "x", ":|:", "0:0|", "1:a|", "10:x|", "|a", "a|", ":", "-1"];

        int count = random.Next(3, 7);
        var builder = new StringBuilder();

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                builder.Append('|');

            builder.Append(fragments[random.Next(fragments.Length)]);
        }

        return builder.ToString();
    }

    /// <summary>A component rendered so an empty one and a whitespace one are distinguishable in a message.</summary>
    private static string Describe(string value) => $"<{value.Length}>{value.Replace("|", "\\|", StringComparison.Ordinal)}";
}
