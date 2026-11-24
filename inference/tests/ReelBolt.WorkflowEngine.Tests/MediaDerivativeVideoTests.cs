using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Unit coverage for the media-derivative core — the closed parameter catalogue, the
/// length-prefixed cache identity, the exact argv every builder emits, the pure peak/filmstrip
/// arithmetic — plus the two-class priority admission tests, which do use real ffmpeg because the
/// thing under test is the scheduling of real child processes.
/// </summary>
/// <remarks>
/// The builder assertions are EXACT STRING comparisons, house style
/// (<c>FfmpegArgvBuilderTests</c>, <c>ColorGradeFilterBuilderTests</c>): an argv is a contract, and
/// a "<c>Should().Contain("scale")</c>" style test cannot tell a reordered <c>-g</c> from a dropped
/// one.
/// </remarks>
public class MediaDerivativeVideoTests
{
    // =========================================================================================
    // MediaDerivativeSpec — the closed catalogue
    // =========================================================================================

    [Theory]
    [InlineData(1080, 540)]
    [InlineData(480, 540)]
    [InlineData(2160, 720)]
    [InlineData(4320, 720)]
    public void Proxy_target_height_depends_only_on_whether_the_source_is_UHD(int sourceHeight, int expected)
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForProxy(sourceHeight, 30, 1, hasAudio: true, threads: 4);

        spec.Slug.Should().Be(MediaDerivativeKindSlug.Proxy);
        spec.Variant.Should().Be(0);
        spec.BuilderVersion.Should().Be(MediaDerivativeSpec.ProxyBuilderVersion);
        spec.ParamsJson.Should().Contain($"\"targetHeight\":{expected}");
    }

    [Theory]
    [InlineData(30, 1, 15)]
    [InlineData(24, 1, 12)]
    [InlineData(25, 1, 13)]     // 12.5 rounds away from zero
    [InlineData(120, 1, 60)]
    [InlineData(30000, 1001, 15)] // 29.97 -> 14.985 -> 15
    [InlineData(1, 1, 1)]        // 0.5 would round to 0; clamped to at least one frame
    public void Gop_is_derived_from_the_sources_real_frame_rate(int fpsNum, int fpsDen, int expected)
    {
        MediaDerivativeSpec.ComputeProxyGopFrames(fpsNum, fpsDen).Should().Be(expected);
    }

    [Fact]
    public void Gop_is_never_zero_even_for_a_degenerate_frame_rate()
    {
        MediaDerivativeSpec.ComputeProxyGopFrames(0, 0).Should().Be(1);
        MediaDerivativeSpec.ComputeProxyGopFrames(0, 1).Should().Be(1);
    }

    [Fact]
    public void Proxy_params_carry_the_audio_expectation_so_a_silent_source_keys_differently()
    {
        string withAudio = MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4).ParamsJson;
        string without = MediaDerivativeSpec.ForProxy(1080, 30, 1, false, 4).ParamsJson;

        withAudio.Should().Contain("\"audioCodec\":true");
        without.Should().Contain("\"audioCodec\":false");
        withAudio.Should().NotBe(without);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(99, 30)] // clamps rather than throwing
    public void Filmstrip_variant_selects_the_documented_density(int variant, int expectedDensity)
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(variant);

        spec.Slug.Should().Be(MediaDerivativeKindSlug.Filmstrip);
        spec.ParamsJson.Should().Contain($"\"densitySec\":{expectedDensity}");
        spec.ParamsJson.Should().Contain("\"thumbWidth\":160");
        spec.ParamsJson.Should().Contain("\"cols\":10");
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(1, 100)]
    [InlineData(2, 1000)]
    public void Peaks_variant_selects_the_documented_lod(int variant, int expectedLod)
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForWaveformPeaks(variant);

        spec.Variant.Should().Be(variant);
        spec.ParamsJson.Should().Contain("\"lodsMs\":[20,100,1000]");
        spec.ParamsJson.Should().Contain("\"sampleRate\":48000");
        spec.ParamsJson.Should().Contain("\"channels\":2");
        expectedLod.Should().Be(MediaDerivativeSpec.PeaksLodMs[variant]);
    }

    [Fact]
    public void Keyframe_index_spec_is_the_packet_probe_and_has_one_variant()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForKeyframeIndex();

        spec.Variant.Should().Be(0);
        spec.ParamsJson.Should().Be("{\"source\":\"packets\",\"stream\":\"v:0\"}");
    }

    [Fact]
    public void Params_json_is_canonical_key_sorted_whitespace_free_and_invariant()
    {
        string json = MediaDerivativeSpec.ForProxy(1080, 30000, 1001, true, 4).ParamsJson;

        json.Should().NotContain(" ");
        json.Should().NotContain("\n");
        // A comma is a legitimate JSON separator; a comma-DECIMAL is not. Under a comma-decimal
        // culture 12.5 would be emitted as "12,5", which is always a comma followed by a digit.
        System.Text.RegularExpressions.Regex.IsMatch(json, @",\d").Should().BeFalse(
            $"the params JSON must stay invariant-culture: {json}");
        json.Should().StartWith("{\"audioBitrateKbps\":128,\"audioChannels\":2,");
        json.Should().EndWith("}");

        // Keys are ordinal-sorted, so the sequence of quoted key names must itself be ascending.
        List<string> keys = System.Text.RegularExpressions.Regex
            .Matches(json, "\"([a-zA-Z]+)\":")
            .Select(m => m.Groups[1].Value)
            .ToList();
        keys.Should().Equal(keys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Specs_are_value_equal_so_the_cache_key_cannot_depend_on_reference_identity()
    {
        MediaDerivativeSpec first = MediaDerivativeSpec.ForFilmstrip(1);
        MediaDerivativeSpec second = MediaDerivativeSpec.ForFilmstrip(1);

        first.Should().Be(second);
        first.GetHashCode().Should().Be(second.GetHashCode());
    }

    // =========================================================================================
    // MediaDerivativeCacheKey — the collision rule
    // =========================================================================================

    [Fact]
    public void Cache_key_is_lowercase_hex_sha256()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForProxy(1080, 30, 1, true, 4);

        string key = MediaDerivativeCacheKey.ComputeCacheKey("projects/p/source.mp4", "etag-1", spec);

        key.Should().HaveLength(64);
        key.Should().Be(key.ToLowerInvariant());
        System.Text.RegularExpressions.Regex.IsMatch(key, "^[0-9a-f]{64}$").Should().BeTrue();
    }

    [Fact]
    public void The_two_arrangements_of_a_separator_bearing_pair_do_not_collide()
    {
        MediaDerivativeSpec spec = MediaDerivativeSpec.ForFilmstrip(0);

        string shifted = MediaDerivativeCacheKey.ComputeCacheKey("a", "b|c", spec);
        string other = MediaDerivativeCacheKey.ComputeCacheKey("a|b", "c", spec);

        shifted.Should().NotBe(other,
            "a bare |-join would make these two different sources share one derivative object");
    }

    [Fact]
    public void The_join_is_injective_over_an_adversarial_component_matrix()
    {
        // Every token is chosen to be hostile to a naive join: empty (a zero-length component), the
        // separator itself, the length delimiter, and near-miss pairs that only differ in how the
        // characters are split across the two components.
        string[] tokens =
        [
            "", "a", "|", ":", "a|b", "b|c", "0:0|", "1:a|", ":|:", "a|1:", "10:x|", "x|10:", "|a", "a|"
        ];

        MediaDerivativeSpec spec = MediaDerivativeSpec.ForWaveformPeaks(1);
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        int pairs = 0;

        foreach (string storageKey in tokens)
        {
            foreach (string etag in tokens)
            {
                pairs++;
                string key = MediaDerivativeCacheKey.ComputeCacheKey(storageKey, etag, spec);
                string owner = $"{storageKey}\u0000{etag}";

                owners.Should().NotContainKey(key,
                    $"'{owner}' collided with '{owners.GetValueOrDefault(key, string.Empty)}'");
                owners[key] = owner;
            }
        }

        pairs.Should().Be(tokens.Length * tokens.Length,
            "the matrix must actually have been exercised, not skipped");
        owners.Should().HaveCount(pairs);
    }

    [Fact]
    public void The_join_is_injective_across_slug_variant_params_and_builder_version_too()
    {
        var specA = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{}", 1);
        var specB = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{}", 2);
        var specC = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 1, "{}", 1);
        var specD = new MediaDerivativeSpec(MediaDerivativeKindSlug.Filmstrip, 0, "{}", 1);
        var specE = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"x\":1}", 1);

        string[] keys = new[] { specA, specB, specC, specD, specE }
            .Select(s => MediaDerivativeCacheKey.ComputeCacheKey("k", "e", s))
            .ToArray();

        keys.Distinct(StringComparer.Ordinal).Should().HaveCount(5);
    }

    [Fact]
    public void Params_hash_covers_both_the_params_json_and_the_builder_version()
    {
        var specA = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"a\":1}", 1);
        var specB = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"a\":1}", 2);
        var specC = new MediaDerivativeSpec(MediaDerivativeKindSlug.Proxy, 0, "{\"a\":2}", 1);

        string hashA = MediaDerivativeCacheKey.ComputeParamsHash(specA);

        hashA.Should().Be(MediaDerivativeCacheKey.ComputeParamsHash(specA));
        hashA.Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(specB));
        hashA.Should().NotBe(MediaDerivativeCacheKey.ComputeParamsHash(specC));
    }

    [Fact]
    public void Params_hash_is_independent_of_the_slug_and_variant()
    {
        // Deliberate: the leaf name carries this hash, so two variants of one kind must differ by
        // their (different) params, and the same params must hash the same wherever they appear.
        MediaDerivativeSpec a = MediaDerivativeSpec.ForFilmstrip(0);
        var b = new MediaDerivativeSpec(MediaDerivativeKindSlug.Filmstrip, 7, a.ParamsJson, a.BuilderVersion);

        MediaDerivativeCacheKey.ComputeParamsHash(a).Should().Be(MediaDerivativeCacheKey.ComputeParamsHash(b));
    }

    [Fact]
    public void Source_hash_is_sixteen_lowercase_hex_characters_and_groups_by_source()
    {
        string hash = MediaDerivativeCacheKey.ComputeSourceHash("projects/p/a.mp4", "etag");

        hash.Should().HaveLength(MediaDerivativeCacheKey.SourceHashLength);
        hash.Should().MatchRegex("^[0-9a-f]{16}$");

        MediaDerivativeCacheKey.ComputeSourceHash("projects/p/a.mp4", "etag").Should().Be(hash);
        MediaDerivativeCacheKey.ComputeSourceHash("projects/p/b.mp4", "etag").Should().NotBe(hash);
        MediaDerivativeCacheKey.ComputeSourceHash("projects/p/a.mp4", "etag2").Should().NotBe(hash);
    }

    [Fact]
    public void Storage_prefix_is_the_documented_layout_and_is_not_swayed_by_the_separator_join()
    {
        Guid projectId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        string prefix = MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, "a|b", "c");
        string other = MediaDerivativeCacheKey.ComputeStoragePrefix(projectId, "a", "b|c");

        prefix.Should().Be(
            $"projects/{projectId:D}/agentFiles/media-derivatives/" +
            MediaDerivativeCacheKey.ComputeSourceHash("a|b", "c"));
        prefix.Should().NotBe(other);
    }

    // =========================================================================================
    // Builders — exact argv
    // =========================================================================================

    [Fact]
    public void Proxy_argv_is_exact_with_audio()
    {
        string[] args = ProxyArgvBuilder.Build("/in/src.mp4", "/out/proxy.mp4", 540, 15, hasAudio: true, threads: 4);

        args.Should().Equal(
        [
            "-y", "-nostdin", "-hide_banner", "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/in/src.mp4",
            "-vf", "scale=-2:540:flags=bicubic",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-g", "15", "-keyint_min", "15", "-sc_threshold", "0",
            "-c:a", "aac", "-b:a", "128k", "-ar", "48000", "-ac", "2",
            "-movflags", "+faststart",
            "-threads", "4",
            "-progress", "pipe:1", "-nostats",
            "-y", "/out/proxy.mp4"
        ]);
    }

    [Fact]
    public void Proxy_argv_drops_the_aac_arguments_entirely_when_the_source_has_no_audio()
    {
        string[] args = ProxyArgvBuilder.Build("/in/src.mp4", "/out/proxy.mp4", 540, 15, hasAudio: false, threads: 4);

        args.Should().NotContain("-c:a");
        args.Should().NotContain("aac");
        args.Should().NotContain("128k");
        args.Should().NotContain("-b:a");
        args.Should().NotContain("-ar");

        args.Should().Equal(
        [
            "-y", "-nostdin", "-hide_banner", "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/in/src.mp4",
            "-vf", "scale=-2:540:flags=bicubic",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "main", "-pix_fmt", "yuv420p",
            "-g", "15", "-keyint_min", "15", "-sc_threshold", "0",
            "-movflags", "+faststart",
            "-threads", "4",
            "-progress", "pipe:1", "-nostats",
            "-y", "/out/proxy.mp4"
        ]);
    }

    [Fact]
    public void Proxy_argv_clamps_a_degenerate_height_and_gop()
    {
        string[] args = ProxyArgvBuilder.Build("/in", "/out", 0, 0, hasAudio: false, threads: 0);

        args.Should().ContainInOrder("-vf", "scale=-2:2:flags=bicubic");
        args.Should().ContainInOrder("-g", "1");
        args.Should().ContainInOrder("-keyint_min", "1");
        args.Should().ContainInOrder("-threads", "1");
    }

    [Fact]
    public void Filmstrip_multi_density_argv_is_exact()
    {
        string[] args = FilmstripArgvBuilder.BuildMultiDensity(
            "/in/src.mp4", [(1, "/out/d1/sheet-%03d.jpg"), (5, "/out/d5/sheet-%03d.jpg")]);

        args.Should().Equal(
        [
            "-y", "-nostdin", "-hide_banner", "-loglevel", "info", "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/in/src.mp4",
            "-an",
            "-filter_complex",
            "[0:v:0]split=2[v1][v5];" +
            "[v1]fps=1/1,split=2[a1][b1];[a1]scale=160:-2,tile=10x10[s1];[b1]showinfo@d1,nullsink;" +
            "[v5]fps=1/5,split=2[a5][b5];[a5]scale=160:-2,tile=10x10[s5];[b5]showinfo@d5,nullsink",
            "-map", "[s1]", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2", "/out/d1/sheet-%03d.jpg",
            "-map", "[s5]", "-c:v", "mjpeg", "-q:v", "5", "-f", "image2", "/out/d5/sheet-%03d.jpg"
        ]);
    }

    [Fact]
    public void Filmstrip_keyframes_only_adds_skip_frame_nokey_before_the_input_and_nothing_else()
    {
        string[] plain = FilmstripArgvBuilder.BuildMultiDensity("/in/p.mp4", [(30, "/out/sheet-%03d.jpg")]);
        string[] keyed = FilmstripArgvBuilder.BuildMultiDensity("/in/p.mp4", [(30, "/out/sheet-%03d.jpg")], keyframesOnly: true);

        plain.Should().NotContain("-skip_frame");
        keyed.Should().ContainInOrder("-skip_frame", "nokey", "-i", "/in/p.mp4");
        keyed.Where(a => a != "-skip_frame" && a != "nokey").Should().Equal(plain);
    }

    [Fact]
    public void Filmstrip_hdr_tone_map_sits_between_scale_and_tile_in_every_density_chain()
    {
        string[] args = FilmstripArgvBuilder.BuildMultiDensity("/in", [(1, "/o1"), (30, "/o30")], "pq");
        string graph = args[Array.IndexOf(args, "-filter_complex") + 1];

        graph.Should().Contain("[a1]scale=160:-2," + ReelBolt.WorkflowEngine.Services.Video.TimelineRender.HdrToneMap.Filter("pq") + ",tile=10x10[s1]");
        graph.Should().Contain("[a30]scale=160:-2," + ReelBolt.WorkflowEngine.Services.Video.TimelineRender.HdrToneMap.Filter("pq") + ",tile=10x10[s30]");
    }

    [Fact]
    public void Filmstrip_inventory_line_prefix_never_matches_a_longer_density()
    {
        FilmstripArgvBuilder.InventoryLinePrefix(1).Should().Be("[showinfo@d1 @");
        "[showinfo@d10 @ 0x1] n: 0".Contains(FilmstripArgvBuilder.InventoryLinePrefix(1)).Should().BeFalse();
    }

    [Fact]
    public void Filmstrip_argv_never_pins_frames_v_1()
    {
        // -frames:v 1 would silently cap every filmstrip at the first 100 thumbnails.
        FilmstripArgvBuilder.BuildMultiDensity("/in", [(1, "/out/sheet-%03d.jpg")]).Should().NotContain("-frames:v");
    }

    [Fact]
    public void Peaks_decode_argv_is_exact_and_writes_a_raw_file()
    {
        string[] args = MediaDerivativeService.BuildPeaksDecodeArgs("/in/src.mp4", "/out/peaks.raw");

        args.Should().Equal(
        [
            "-y", "-nostdin", "-hide_banner", "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "-i", "/in/src.mp4",
            "-vn", "-ac", "2", "-ar", "48000", "-sample_fmt", "s16", "-f", "s16le",
            "/out/peaks.raw"
        ]);
    }

    [Fact]
    public void Keyframe_index_probe_argv_is_exact()
    {
        string[] args = KeyframeIndexArgvBuilder.BuildProbeArgs("/in/src.mp4");

        args.Should().Equal(
        [
            "-v", "error", "-print_format", "json",
            "-select_streams", "v:0",
            "-show_entries", "packet=pts_time,flags",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "/in/src.mp4"
        ]);
    }

    [Fact]
    public void Every_builder_locks_the_input_to_the_file_protocol()
    {
        ProxyArgvBuilder.Build("/in", "/out", 540, 15, true, 4).Should().ContainInOrder("-protocol_whitelist", "file");
        FilmstripArgvBuilder.BuildMultiDensity("/in", [(1, "/out")]).Should().ContainInOrder("-protocol_whitelist", "file");
        MediaDerivativeService.BuildPeaksDecodeArgs("/in", "/out").Should().ContainInOrder("-protocol_whitelist", "file");
        KeyframeIndexArgvBuilder.BuildProbeArgs("/in").Should().ContainInOrder("-protocol_whitelist", "file");
    }

    [Fact]
    public void Every_derivative_argv_pins_the_demuxer_to_media_containers()
    {
        // Security MI-2: the protocol whitelist alone still lets a crafted HLS playlist or concat
        // list open other local files; the demuxer whitelist refuses those formats outright.
        List<string[]> argvs =
        [
            ProxyArgvBuilder.Build("/in", "/out", 540, 15, true, 4),
            FilmstripArgvBuilder.BuildMultiDensity("/in", [(1, "/out")]),
            MediaDerivativeService.BuildPeaksDecodeArgs("/in", "/out"),
            KeyframeIndexArgvBuilder.BuildProbeArgs("/in"),
            MediaDerivativeInputFormats.BuildSourceProbeArgs("/in")
        ];

        foreach (string[] argv in argvs)
        {
            argv.Should().ContainInOrder("-format_whitelist", MediaDerivativeInputFormats.Whitelist);
            int input = Array.LastIndexOf(argv, "/in");
            Array.IndexOf(argv, "-format_whitelist").Should().BeLessThan(input, "it is an input option");
        }

        string[] formats = MediaDerivativeInputFormats.Whitelist.Split(',');
        formats.Should().Contain(["mov", "matroska", "mp3", "wav"]);
        formats.Should().Contain("mxf", "Sony XDCAM and Canon camera files are MXF");
        formats.Should().NotContain(["hls", "concat", "dash", "sdp", "image2", "ffmetadata", "tee"]);
    }

    [Fact]
    public async Task The_service_probes_the_untrusted_source_with_the_demuxer_whitelist_not_the_generic_probe()
    {
        // The generic IMediaProbe argv has no -format_whitelist (it also probes trusted engine
        // output); the source of a derivative is a user upload and must not go through it.
        var probed = new ConcurrentQueue<string[]>();
        var runner = new Mock<IVideoToolRunner>();
        runner
            .Setup(r => r.RunFfprobeAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(), It.IsAny<VideoToolPriority>()))
            .Callback((IReadOnlyList<string> args, TimeSpan _, CancellationToken _, VideoToolPriority _) =>
                probed.Enqueue(args.ToArray()))
            .ReturnsAsync(new VideoToolResult(1, string.Empty, "refused", false));
        var probe = new Mock<IMediaProbe>();
        probe
            .Setup(p => p.ProbeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the generic probe was used for the source"));

        var service = new MediaDerivativeService(
            runner.Object,
            probe.Object,
            Microsoft.Extensions.Options.Options.Create(MediaDerivativeFfmpegFixture.BuildOptions(Path.GetTempPath())),
            NullLogger<MediaDerivativeService>.Instance);

        Func<Task> resolve = () => service.ResolveSpecAsync(
            MediaDerivativeKindSlug.Proxy, 0, "/in/src.mp4", CancellationToken.None);
        Func<Task> produce = () => service.ProduceAsync(
            Guid.NewGuid(), "projects/p/userFiles/src.mp4", "\"etag\"",
            MediaDerivativeSpec.ForKeyframeIndex(), "/in/src.mp4", null, CancellationToken.None);

        (await resolve.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ffprobe");
        (await produce.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ffprobe");

        probed.Should().HaveCount(2);
        probed.Should().AllSatisfy(args =>
            args.Should().Equal(MediaDerivativeInputFormats.BuildSourceProbeArgs("/in/src.mp4")));
    }

    [Fact]
    public void Derivative_source_probe_argv_is_exact()
    {
        MediaDerivativeInputFormats.BuildSourceProbeArgs("/in/src.mp4").Should().Equal(
        [
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams",
            "-protocol_whitelist", "file",
            "-format_whitelist", MediaDerivativeInputFormats.Whitelist,
            "/in/src.mp4"
        ]);
    }

    [Fact]
    public void Builders_reject_a_blank_path_rather_than_emitting_an_argv_with_a_hole_in_it()
    {
        Action proxy = () => ProxyArgvBuilder.Build("  ", "/out", 540, 15, true, 4);
        Action filmstrip = () => FilmstripArgvBuilder.BuildMultiDensity("/in", [(1, "")]);
        Action peaks = () => MediaDerivativeService.BuildPeaksDecodeArgs("/in", "");
        Action keyframes = () => KeyframeIndexArgvBuilder.BuildProbeArgs("");

        proxy.Should().Throw<ArgumentException>();
        filmstrip.Should().Throw<ArgumentException>();
        peaks.Should().Throw<ArgumentException>();
        keyframes.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_builder_ever_formats_a_number_through_the_current_culture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            List<string[]> argvs =
            [
                ProxyArgvBuilder.Build("/in", "/out", 540, 15, true, 4),
                FilmstripArgvBuilder.BuildMultiDensity("/in", [(5, "/out")]),
                MediaDerivativeService.BuildPeaksDecodeArgs("/in", "/out"),
                KeyframeIndexArgvBuilder.BuildProbeArgs("/in")
            ];

            foreach (string[] argv in argvs)
            {
                foreach (string token in argv)
                {
                    // A comma is a legitimate filter separator ("fps=1/5,scale=160:-2"); a
                    // comma-DECIMAL always shows up as a comma followed by a digit.
                    System.Text.RegularExpressions.Regex.IsMatch(token, @",\d").Should().BeFalse(
                        $"token '{token}' carries a comma-decimal under de-DE");
                }
            }

            string paramsJson = MediaDerivativeSpec.ForProxy(1080, 30000, 1001, true, 4).ParamsJson;
            System.Text.RegularExpressions.Regex.IsMatch(paramsJson, @",\d").Should().BeFalse();
            paramsJson.Should().Contain("\"gopFrames\":15");

            FilmstripIndex.Build(1, 160, 120, FilmstripIndex.Plan(1, new[] { new FilmstripSheetFrames("a.jpg", 3) }))
                .Should().Contain("\"firstTimeSec\":0");

            PcmPeakSampler.BuildSidecarJson(PcmPeakSampler.Sample(new byte[400], 48000, 2))
                .Should().Contain("\"sampleRate\":48000");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // =========================================================================================
    // PcmPeakSampler — pure
    // =========================================================================================

    [Fact]
    public void Peaks_are_per_channel_min_max_pairs_at_the_documented_bucket_counts()
    {
        // 0.1 s of 2-channel 48 kHz s16le = 4800 frames.
        byte[] pcm = BuildPcm(4800, 2, (frame, channel) => channel == 0 ? (short)-100 : (short)200);

        PcmPeakResult result = PcmPeakSampler.Sample(pcm, 48000, 2);

        result.Lods.Should().HaveCount(3);
        result.Lods.Select(l => l.LodMs).Should().Equal(20, 100, 1000);
        result.Lods.Select(l => l.BucketCount).Should().Equal(5, 1, 1);

        result.Lods[0].Channels.Should().HaveCount(2);
        result.Lods[0].Channels[0].Should().OnlyContain(b => b.Min == -100 && b.Max == -100);
        result.Lods[0].Channels[1].Should().OnlyContain(b => b.Min == 200 && b.Max == 200);
    }

    [Fact]
    public void Peaks_track_the_actual_minimum_and_maximum_inside_a_bucket()
    {
        // One 20 ms bucket (960 frames) whose channel-0 samples ramp from -1000 to +999.
        byte[] pcm = BuildPcm(960, 1, (frame, _) => (short)(frame - 1000));

        PcmPeakResult result = PcmPeakSampler.Sample(pcm, 48000, 1, [20]);

        result.Lods[0].BucketCount.Should().Be(1);
        result.Lods[0].Channels[0][0].Min.Should().Be(-1000);
        result.Lods[0].Channels[0][0].Max.Should().Be(-41);
    }

    [Fact]
    public void The_last_bucket_is_short_rather_than_padded()
    {
        // 1000 frames at a 960-frame bucket = one full bucket plus a 40-frame remainder.
        byte[] pcm = BuildPcm(1000, 1, (frame, _) => (short)(frame % 10));

        PcmPeakResult result = PcmPeakSampler.Sample(pcm, 48000, 1, [20]);

        result.Lods[0].BucketCount.Should().Be(2);
    }

    [Theory]
    [InlineData(0, 48000, 2)]   // empty
    [InlineData(4, 0, 2)]       // no sample rate
    [InlineData(4, 48000, 0)]   // no channels
    [InlineData(1, 48000, 2)]   // less than one whole frame
    public void Malformed_input_yields_an_empty_result_and_never_throws(int byteCount, int sampleRate, int channels)
    {
        PcmPeakResult result = PcmPeakSampler.Sample(new byte[byteCount], sampleRate, channels);

        result.Lods.Should().BeEmpty();
    }

    [Fact]
    public void Null_input_and_an_empty_lod_list_yield_an_empty_result()
    {
        PcmPeakSampler.Sample(null, 48000, 2).Lods.Should().BeEmpty();
        PcmPeakSampler.Sample(new byte[400], 48000, 2, []).Lods.Should().BeEmpty();
        PcmPeakSampler.Sample(new byte[400], 48000, 2, [0]).Lods.Should().BeEmpty();
    }

    [Fact]
    public void A_trailing_partial_frame_is_ignored_rather_than_shifting_every_sample()
    {
        byte[] pcm = BuildPcm(10, 2, (_, _) => (short)5);
        byte[] withDanglingBytes = new byte[pcm.Length + 3];
        Array.Copy(pcm, withDanglingBytes, pcm.Length);

        PcmPeakResult result = PcmPeakSampler.Sample(withDanglingBytes, 48000, 2, [20]);

        result.Lods.Should().NotBeEmpty();
        result.Lods[0].Channels[1][0].Max.Should().Be(5);
    }

    [Fact]
    public void Lod_bytes_are_channel_major_int16_min_max_little_endian()
    {
        var lod = new PcmPeakLod(
            LodMs: 20,
            BucketCount: 2,
            Channels:
            [
                [new PcmPeakBucket(-1, 258), new PcmPeakBucket(-2, 3)],
                [new PcmPeakBucket(1000, 2000), new PcmPeakBucket(0, 0)]
            ]);

        byte[] bytes = PcmPeakSampler.SerializeLod(lod);

        bytes.Should().HaveCount(2 * 2 * 4);
        bytes.Should().Equal(
        [
            // channel 0: (-1, 258), (-2, 3)
            0xFF, 0xFF, 0x02, 0x01,
            0xFE, 0xFF, 0x03, 0x00,
            // channel 1: (1000, 2000), (0, 0)
            0xE8, 0x03, 0xD0, 0x07,
            0x00, 0x00, 0x00, 0x00
        ]);
    }

    [Fact]
    public void Peaks_sidecar_documents_the_layout()
    {
        byte[] pcm = BuildPcm(4800, 2, (_, _) => 0);
        PcmPeakResult result = PcmPeakSampler.Sample(pcm, 48000, 2);

        string json = PcmPeakSampler.BuildSidecarJson(result);

        json.Should().Be(
            "{\"sampleRate\":48000,\"channels\":2,\"lodsMs\":[20,100,1000]," +
            "\"bucketCounts\":[5,1,1],\"layout\":\"ch,min,max\",\"byteOrder\":\"le\"}");
    }

    [Fact]
    public void Peaks_leaves_carry_the_params_hash_and_the_lod()
    {
        PcmPeakSampler.LodLeaf("0123456789abcdef", 20).Should().Be("peaks-0123456789abcdef-lod20ms.raw");
        PcmPeakSampler.LodLeaf("0123456789abcdef", 1000).Should().Be("peaks-0123456789abcdef-lod1000ms.raw");
        PcmPeakSampler.IndexLeaf("0123456789abcdef").Should().Be("peaks-0123456789abcdef-index.json");
    }

    // =========================================================================================
    // FilmstripIndex — pure
    // =========================================================================================

    [Fact]
    public void Sheet_plan_accumulates_first_thumb_index_and_first_time()
    {
        IReadOnlyList<FilmstripSheet> sheets = FilmstripIndex.Plan(5,
        [
            new FilmstripSheetFrames("a.jpg", 100),
            new FilmstripSheetFrames("b.jpg", 100),
            new FilmstripSheetFrames("c.jpg", 50)
        ]);

        sheets.Should().HaveCount(3);
        sheets.Select(s => s.FirstThumbIndex).Should().Equal(0, 100, 200);
        sheets.Select(s => s.FirstTimeSec).Should().Equal(0d, 500d, 1000d);
        sheets.Select(s => s.ThumbCount).Should().Equal(100, 100, 50);
    }

    [Fact]
    public void Sheet_index_json_matches_the_documented_shape()
    {
        IReadOnlyList<FilmstripSheet> sheets = FilmstripIndex.Plan(1,
        [
            new FilmstripSheetFrames("f-sheet-001.jpg", 100),
            new FilmstripSheetFrames("f-sheet-002.jpg", 50)
        ]);

        string json = FilmstripIndex.Build(1, 160, 120, sheets);

        json.Should().Be(
            "{\"densitySec\":1,\"thumbWidth\":160,\"thumbHeight\":120,\"cols\":10,\"rows\":10,\"sheetCount\":2," +
            "\"sheets\":[" +
            "{\"storageLeaf\":\"f-sheet-001.jpg\",\"firstThumbIndex\":0,\"thumbCount\":100,\"firstTimeSec\":0}," +
            "{\"storageLeaf\":\"f-sheet-002.jpg\",\"firstThumbIndex\":100,\"thumbCount\":50,\"firstTimeSec\":100}" +
            "]}");
    }

    [Fact]
    public void Filmstrip_leaves_are_one_based_zero_padded_and_hash_scoped()
    {
        FilmstripIndex.SheetLeaf("deadbeefdeadbeef", 1).Should().Be("filmstrip-deadbeefdeadbeef-sheet-001.jpg");
        FilmstripIndex.SheetLeaf("deadbeefdeadbeef", 12).Should().Be("filmstrip-deadbeefdeadbeef-sheet-012.jpg");
        FilmstripIndex.IndexLeaf("deadbeefdeadbeef").Should().Be("filmstrip-deadbeefdeadbeef-index.json");
    }

    [Theory]
    [InlineData(250, 3, new[] { 100, 100, 50 })]
    [InlineData(100, 1, new[] { 100 })]
    [InlineData(350, 4, new[] { 100, 100, 100, 50 })]
    [InlineData(1, 1, new[] { 1 })]
    public void Measured_thumbnails_distribute_over_measured_sheets(int total, int sheets, int[] expected)
    {
        MediaDerivativeService.DistributeThumbs(total, sheets).Should().Equal(expected);
    }

    [Theory]
    [InlineData(250, 2)]  // 250 thumbnails cannot fit two sheets
    [InlineData(101, 1)]
    [InlineData(0, 1)]
    public void A_sheet_and_thumbnail_measurement_that_disagree_throws_rather_than_rounding(int total, int sheets)
    {
        Action act = () => MediaDerivativeService.DistributeThumbs(total, sheets);

        act.Should().Throw<InvalidOperationException>();
    }

    // =========================================================================================
    // KeyframeIndexArgvBuilder — pure parsing
    // =========================================================================================

    [Fact]
    public void Keyframe_times_are_taken_only_from_K_flagged_packets()
    {
        const string json = """
            {"packets":[
              {"pts_time":"0.000000","flags":"K__"},
              {"pts_time":"0.040000","flags":"___"},
              {"pts_time":"2.000000","flags":"K__"},
              {"pts_time":"2.040000","flags":"___"},
              {"pts_time":"4.000000","flags":"K__"}
            ]}
            """;

        KeyframeIndexArgvBuilder.ParseKeyframeTimes(json).Should().Equal(0d, 2d, 4d);
    }

    [Fact]
    public void Unparseable_or_missing_timestamps_are_skipped_not_coerced_to_zero()
    {
        const string json = """
            {"packets":[
              {"pts_time":"N/A","flags":"K__"},
              {"flags":"K__"},
              {"pts_time":"-1.0","flags":"K__"},
              {"pts_time":"1.5","flags":"K__"}
            ]}
            """;

        KeyframeIndexArgvBuilder.ParseKeyframeTimes(json).Should().Equal(1.5d);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("{\"packets\":{}}")]
    public void A_malformed_keyframe_document_yields_an_empty_list_rather_than_throwing(string json)
    {
        KeyframeIndexArgvBuilder.ParseKeyframeTimes(json).Should().BeEmpty();
    }

    [Fact]
    public void Gop_hint_is_the_median_delta_and_zero_for_a_single_keyframe()
    {
        KeyframeIndexArgvBuilder.ComputeGopHintSec([0, 2, 4, 100]).Should().Be(2);
        KeyframeIndexArgvBuilder.ComputeGopHintSec([0]).Should().Be(0);
        KeyframeIndexArgvBuilder.ComputeGopHintSec([]).Should().Be(0);
    }

    [Fact]
    public void Keyframe_index_json_matches_the_documented_shape()
    {
        KeyframeIndexArgvBuilder.BuildIndexJson([0, 0.5, 1], 0.5)
            .Should().Be("{\"timesSec\":[0,0.5,1],\"count\":3,\"gopHintSec\":0.5}");

        KeyframeIndexArgvBuilder.BuildIndexJson([], 0)
            .Should().Be("{\"timesSec\":[],\"count\":0,\"gopHintSec\":0}");
    }

    [Fact]
    public void Keyframe_leaf_carries_the_params_hash()
    {
        KeyframeIndexArgvBuilder.IndexLeaf("cafebabecafebabe").Should().Be("keyframes-cafebabecafebabe.json");
    }

    // =========================================================================================
    // The priority gate — real ffmpeg, because the thing under test schedules real processes
    // =========================================================================================

    [FfmpegToolsFact]
    public async Task A_derivative_never_waits_for_an_interactive_job_and_an_interactive_job_never_waits_for_a_derivative()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "hold.mp4", seconds: 4, fps: 5, width: 160, height: 120, withAudio: false);

            // One interactive slot and one derivative slot: separate lanes.
            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);

            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
            string[] probeArgs = FfmpegArgvBuilder.BuildProbeArgs(source);
            string[] hold = ["-y", "-nostdin", "-hide_banner", "-re", "-i", source, "-f", "null", "-"];

            // Normal holds the only interactive slot: a derivative still runs at once.
            Task<VideoToolResult> normalHolder = runner.RunFfmpegAsync(hold, TimeSpan.FromMinutes(1), cts.Token);
            await Task.Delay(400, cts.Token);
            normalHolder.IsCompleted.Should().BeFalse();
            (await runner.RunFfprobeAsync(probeArgs, TimeSpan.FromMinutes(1), cts.Token, VideoToolPriority.Low))
                .Succeeded.Should().BeTrue();
            normalHolder.IsCompleted.Should().BeFalse("the derivative must not have waited for the interactive job");
            await normalHolder;

            // A Low derivative holds its lane: a Normal job still runs at once.
            Task<VideoToolResult> lowHolder = runner.RunFfmpegAsync(hold, TimeSpan.FromMinutes(1), cts.Token, VideoToolPriority.Low);
            await Task.Delay(400, cts.Token);
            lowHolder.IsCompleted.Should().BeFalse();
            (await runner.RunFfprobeAsync(probeArgs, TimeSpan.FromMinutes(1), cts.Token, VideoToolPriority.Normal))
                .Succeeded.Should().BeTrue();
            lowHolder.IsCompleted.Should().BeFalse("the interactive job must not have waited for the derivative");
            await lowHolder;
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_pass_on_a_big_source_takes_the_heavy_lane_and_a_short_job_still_runs()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "hold.mp4", seconds: 4, fps: 5, width: 160, height: 120, withAudio: false);
            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
            string[] probeArgs = FfmpegArgvBuilder.BuildProbeArgs(source);
            string[] hold = ["-y", "-nostdin", "-hide_banner", "-re", "-i", source, "-f", "null", "-"];

            Task<VideoToolResult> heavyOne;
            Task<VideoToolResult> heavyTwo;
            using (SourceTimeoutPolicy.Begin(600, 3840, 2160, 10_000_000_000))
            {
                heavyOne = runner.RunFfmpegAsync(hold, TimeSpan.FromMinutes(1), cts.Token);
                await Task.Delay(400, cts.Token);
                heavyTwo = runner.RunFfprobeAsync(probeArgs, TimeSpan.FromMinutes(1), cts.Token);
            }

            // A short job (no scope) takes the normal lane and finishes while the heavy ones queue in order.
            await Task.Delay(300, cts.Token);
            (await runner.RunFfprobeAsync(probeArgs, TimeSpan.FromMinutes(1), cts.Token)).Succeeded.Should().BeTrue();
            heavyOne.IsCompleted.Should().BeFalse();
            heavyTwo.IsCompleted.Should().BeFalse("a second heavy pass queues behind the first");

            await Task.WhenAll(heavyOne, heavyTwo);
            heavyTwo.Result.Succeeded.Should().BeTrue();
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task Derivatives_are_capped_by_DerivativeMaxConcurrentJobs_even_when_slots_are_free()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "hold.mp4", seconds: 4, fps: 5, width: 160, height: 120, withAudio: false);

            // Two main slots, but only one derivative slot.
            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(
                directory, maxConcurrent: 2, derivativeMaxConcurrent: 1);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);

            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
            string[] probeArgs = FfmpegArgvBuilder.BuildProbeArgs(source);

            Task<VideoToolResult> first = runner.RunFfmpegAsync(
                ["-y", "-nostdin", "-hide_banner", "-re", "-i", source, "-f", "null", "-"],
                TimeSpan.FromMinutes(1),
                cts.Token,
                VideoToolPriority.Low);

            await Task.Delay(400, cts.Token);
            first.IsCompleted.Should().BeFalse();

            Task<VideoToolResult> second = runner.RunFfprobeAsync(
                probeArgs, TimeSpan.FromMinutes(1), cts.Token, VideoToolPriority.Low);

            Task finished = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(1.5), cts.Token));

            finished.Should().NotBeSameAs(second,
                "a second derivative must not start while the derivative cap of 1 is taken, even though " +
                "MaxConcurrentJobs left a free slot");

            await first;
            VideoToolResult secondResult = await second;
            secondResult.Succeeded.Should().BeTrue();
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task The_gate_stays_cancellable_while_a_caller_is_waiting()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "hold.mp4", seconds: 4, fps: 5, width: 160, height: 120, withAudio: false);

            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
            string[] probeArgs = FfmpegArgvBuilder.BuildProbeArgs(source);

            using CancellationTokenSource holderCts = new(TimeSpan.FromMinutes(2));
            Task<VideoToolResult> holder = runner.RunFfmpegAsync(
                ["-y", "-nostdin", "-hide_banner", "-re", "-i", source, "-f", "null", "-"],
                TimeSpan.FromMinutes(1),
                holderCts.Token);

            await Task.Delay(400, holderCts.Token);

            using CancellationTokenSource waiterCts = new(TimeSpan.FromMilliseconds(200));
            Func<Task> waiting = () => runner.RunFfprobeAsync(
                probeArgs, TimeSpan.FromMinutes(1), waiterCts.Token, VideoToolPriority.Normal);

            await waiting.Should().ThrowAsync<OperationCanceledException>();

            await holder;
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [FfmpegToolsFact]
    public async Task A_cancelled_waiter_does_not_leak_its_slot_for_the_next_caller()
    {
        string directory = MediaDerivativeFfmpegFixture.NewDirectory();
        try
        {
            string source = MediaDerivativeFfmpegFixture.CreateSource(
                directory, "hold.mp4", seconds: 3, fps: 5, width: 160, height: 120, withAudio: false);

            VideoEditingOptions options = MediaDerivativeFfmpegFixture.BuildOptions(directory);
            FfmpegVideoToolRunner runner = MediaDerivativeFfmpegFixture.BuildRunner(options);
            string[] probeArgs = FfmpegArgvBuilder.BuildProbeArgs(source);

            using CancellationTokenSource cts = new(TimeSpan.FromMinutes(2));
            Task<VideoToolResult> holder = runner.RunFfmpegAsync(
                ["-y", "-nostdin", "-hide_banner", "-re", "-i", source, "-f", "null", "-"],
                TimeSpan.FromMinutes(1),
                cts.Token);

            await Task.Delay(300, cts.Token);

            using CancellationTokenSource waiterCts = new(TimeSpan.FromMilliseconds(100));
            Func<Task> waiting = () => runner.RunFfprobeAsync(
                probeArgs, TimeSpan.FromMinutes(1), waiterCts.Token, VideoToolPriority.Normal);
            await waiting.Should().ThrowAsync<OperationCanceledException>();

            await holder;

            // If the cancelled waiter had leaked a permit, this probe would never be admitted.
            Task<VideoToolResult> afterCancel = runner.RunFfprobeAsync(
                probeArgs, TimeSpan.FromSeconds(20), cts.Token, VideoToolPriority.Normal);

            Task completed = await Task.WhenAny(afterCancel, Task.Delay(TimeSpan.FromSeconds(10), cts.Token));
            completed.Should().BeSameAs(afterCancel);
            (await afterCancel).Succeeded.Should().BeTrue();
        }
        finally
        {
            MediaDerivativeFfmpegFixture.TryDelete(directory);
        }
    }

    [Fact]
    public void The_new_overloads_leave_the_three_pre_existing_signatures_untouched()
    {
        Type iface = typeof(IVideoToolRunner);

        iface.GetMethods().Should().Contain(m =>
            m.Name == "RunFfmpegAsync" &&
            m.GetParameters().Length == 3 &&
            m.GetParameters()[0].ParameterType == typeof(IReadOnlyList<string>) &&
            m.GetParameters()[1].ParameterType == typeof(TimeSpan) &&
            m.GetParameters()[2].ParameterType == typeof(CancellationToken));

        iface.GetMethods().Should().Contain(m =>
            m.Name == "RunFfmpegAsync" &&
            m.GetParameters().Length == 4 &&
            m.GetParameters()[3].ParameterType == typeof(Action<string>));

        iface.GetMethods().Should().Contain(m =>
            m.Name == "RunFfprobeAsync" &&
            m.GetParameters().Length == 3);

        iface.GetMethods().Count(m => m.GetParameters().Any(p => p.ParameterType == typeof(VideoToolPriority)))
            .Should().Be(3, "exactly three priority-carrying overloads were added");
    }

    [Fact]
    public void DerivativeMaxConcurrentJobs_defaults_to_one()
    {
        new VideoEditingOptions().DerivativeMaxConcurrentJobs.Should().Be(1);
    }

    // =========================================================================================

    private static byte[] BuildPcm(int frames, int channels, Func<int, int, short> sample)
    {
        byte[] bytes = new byte[frames * channels * 2];
        int offset = 0;
        for (int f = 0; f < frames; f++)
        {
            for (int c = 0; c < channels; c++)
            {
                short value = sample(f, c);
                bytes[offset++] = (byte)(value & 0xFF);
                bytes[offset++] = (byte)((value >> 8) & 0xFF);
            }
        }

        return bytes;
    }
}

/// <summary>
/// Real-ffmpeg fixtures shared by this ticket's four test files: a runner/probe/service wired the
/// way the DI container wires them, generated source media, and independent ffprobe readers used
/// to check the produced artifacts without going back through the code under test.
/// </summary>
internal static class MediaDerivativeFfmpegFixture
{
    public static string NewDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "reelforge-media-derivative-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — a leaked temp directory must never fail a test.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static VideoEditingOptions BuildOptions(
        string scratchRoot, int maxConcurrent = 1, int derivativeMaxConcurrent = 1) => new()
        {
            ScratchPath = scratchRoot,
            FfmpegPath = "ffmpeg",
            FfprobePath = "ffprobe",
            MaxConcurrentJobs = maxConcurrent,
            DerivativeMaxConcurrentJobs = derivativeMaxConcurrent,
            FfmpegThreads = 2,
            AnalyzeTimeoutSeconds = 300,
            CompileTimeoutSeconds = 300
        };

    public static FfmpegVideoToolRunner BuildRunner(VideoEditingOptions options) =>
        new(Microsoft.Extensions.Options.Options.Create(options), NullLogger<FfmpegVideoToolRunner>.Instance);

    public static FfprobeMediaProbe BuildProbe(FfmpegVideoToolRunner runner, VideoEditingOptions options) =>
        new(runner, Microsoft.Extensions.Options.Options.Create(options), NullLogger<FfprobeMediaProbe>.Instance);

    public static MediaDerivativeService BuildService(
        FfmpegVideoToolRunner runner, IMediaProbe probe, VideoEditingOptions options) =>
        new(runner, probe, Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<MediaDerivativeService>.Instance);

    /// <summary>
    /// Synthesizes real source media with the local ffmpeg binary. Test-only fixture generation —
    /// <c>-f lavfi</c> never appears in a production argv.
    /// </summary>
    public static string CreateSource(
        string directory,
        string name,
        double seconds,
        int fps,
        int width,
        int height,
        bool withAudio,
        int audioRate = 48000)
    {
        string path = Path.Combine(directory, name);

        List<string> args =
        [
            "-y", "-nostdin", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate={fps}"
        ];

        if (withAudio)
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency=440:sample_rate={audioRate}"]);

        args.AddRange(
        [
            "-t", seconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"
        ]);

        if (withAudio)
            args.AddRange(["-c:a", "aac", "-b:a", "128k", "-ac", "2"]);

        args.Add(path);

        RunProcess("ffmpeg", args);
        return path;
    }

    /// <summary>Independent keyframe times, read straight from ffprobe — not through the code under test.</summary>
    public static IReadOnlyList<double> ProbeKeyframeTimes(string path)
    {
        string json = RunProcess(
            "ffprobe",
            ["-v", "error", "-print_format", "json", "-select_streams", "v:0",
             "-show_entries", "packet=pts_time,flags", "-protocol_whitelist", "file", path]);

        using JsonDocument document = JsonDocument.Parse(json);
        var times = new List<double>();
        foreach (JsonElement packet in document.RootElement.GetProperty("packets").EnumerateArray())
        {
            if (!packet.GetProperty("flags").GetString()!.Contains('K', StringComparison.Ordinal))
                continue;

            times.Add(double.Parse(
                packet.GetProperty("pts_time").GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
        }

        times.Sort();
        return times;
    }

    public static int ProbeAudioStreamCount(string path)
    {
        string json = RunProcess(
            "ffprobe",
            ["-v", "error", "-print_format", "json", "-show_streams", "-select_streams", "a",
             "-protocol_whitelist", "file", path]);

        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("streams", out JsonElement streams)
            ? streams.GetArrayLength()
            : 0;
    }

    public static (int Width, int Height) ProbeDimensions(string path)
    {
        string csv = RunProcess(
            "ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
             "-of", "csv=p=0", "-protocol_whitelist", "file", path]).Trim();

        string[] parts = csv.Split(',');
        return (int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    /// <summary>Top-level MP4 box types in file order — the exact test for <c>+faststart</c>.</summary>
    public static IReadOnlyList<string> TopLevelBoxTypes(string path)
    {
        var types = new List<string>();
        using FileStream stream = File.OpenRead(path);
        long position = 0;

        while (position + 8 <= stream.Length)
        {
            stream.Position = position;
            byte[] header = new byte[8];
            if (stream.Read(header, 0, 8) != 8)
                break;

            long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            types.Add(Encoding.ASCII.GetString(header, 4, 4));

            if (size == 1)
            {
                byte[] extended = new byte[8];
                if (stream.Read(extended, 0, 8) != 8)
                    break;

                size = 0;
                foreach (byte b in extended)
                    size = (size << 8) | b;
            }

            if (size <= 0)
                break;

            position += size;
        }

        return types;
    }

    private static string RunProcess(string fileName, IReadOnlyList<string> args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = Process.Start(startInfo)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', args)} failed with exit code {process.ExitCode}: {error}");
        }

        return output;
    }
}
