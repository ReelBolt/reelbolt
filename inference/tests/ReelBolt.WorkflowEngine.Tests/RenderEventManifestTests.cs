using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-110: the event manifest an Author writes next to its render — validation, caps, file format, sidecar key.</summary>
public class RenderEventManifestTests
{
    private static RenderEvent Ev(string name, int frame, string label = "A label") => new(name, frame, label);

    [Fact]
    public void A_valid_declaration_is_accepted_and_ordered_by_frame()
    {
        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Validate(
            [Ev("stamp", 90, "Stamp lands"), Ev("typing_start", 12, "Typing starts")]);

        errors.Should().BeEmpty();
        manifest!.Events.Select(e => e.Name).Should().Equal("typing_start", "stamp");
        manifest.Version.Should().Be(RenderEventManifest.CurrentVersion);
    }

    [Theory]
    [InlineData("Stamp")]
    [InlineData("stamp lands")]
    [InlineData("")]
    [InlineData("a234567890123456789012345678901234567890x")]
    [InlineData("stamp/../x")]
    public void A_name_outside_the_allowed_shape_is_refused(string name)
    {
        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Validate([Ev(name, 1)]);

        manifest.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("name");
    }

    [Fact]
    public void A_negative_frame_is_refused()
    {
        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Validate([Ev("slam", -1)]);

        manifest.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("frame");
    }

    [Fact]
    public void More_than_the_cap_is_refused_and_exactly_the_cap_is_accepted()
    {
        List<RenderEvent> atCap = Enumerable.Range(0, RenderEventManifest.MaxEvents).Select(i => Ev($"row_{i}", i)).ToList();
        RenderEventManifest.Validate(atCap).Manifest.Should().NotBeNull();

        (RenderEventManifest? over, IReadOnlyList<string> errors) = RenderEventManifest.Validate([.. atCap, Ev("one_more", 999)]);
        over.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("200");
    }

    [Fact]
    public void An_empty_declaration_is_refused()
    {
        RenderEventManifest.Validate([]).Manifest.Should().BeNull();
        RenderEventManifest.Validate(null).Manifest.Should().BeNull();
    }

    [Fact]
    public void Labels_are_sanitized_capped_and_stripped_of_times_and_fall_back_to_the_name()
    {
        (RenderEventManifest? manifest, _) = RenderEventManifest.Validate(
        [
            Ev("a", 0, "Stamp <b>lands</b> at 00:03 after 1.5s {x}"),
            Ev("b", 1, new string('w', 200)),
            Ev("c", 2, "   "),
        ]);

        manifest!.Events[0].Label.Should().NotContain("<").And.NotContain("{").And.NotContain("00:03").And.NotContain("1.5s");
        manifest.Events[0].Label.Should().Contain("Stamp").And.Contain("lands");
        manifest.Events[1].Label.Length.Should().BeLessThanOrEqualTo(RenderEventManifest.MaxLabelChars);
        manifest.Events[2].Label.Should().Be("c");
    }

    [Fact]
    public void Every_problem_is_reported_in_one_go()
    {
        (_, IReadOnlyList<string> errors) = RenderEventManifest.Validate([Ev("Bad", 1), Ev("ok", -4), Ev("x y", 2)]);

        errors.Should().HaveCount(3);
    }

    [Fact]
    public void The_file_parses_as_an_object_or_a_bare_array()
    {
        RenderEventManifest.Parse("""{"events":[{"name":"slam","frame":30,"label":"Door slams"}]}""")
            .Manifest!.Events.Should().ContainSingle().Which.Should().Be(new RenderEvent("slam", 30, "Door slams"));
        RenderEventManifest.Parse("""[{"name":"slam","frame":30,"label":"Door slams"}]""")
            .Manifest!.Events.Should().ContainSingle();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"events":"nope"}""")]
    [InlineData("""{"events":[{"name":"slam","frame":"thirty"}]}""")]
    [InlineData("42")]
    public void An_unreadable_file_is_an_error_never_a_throw(string json)
    {
        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Parse(json);

        manifest.Should().BeNull();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Manifest_json_round_trips()
    {
        RenderEventManifest manifest = RenderEventManifest.Validate([Ev("slam", 30, "Door slams")]).Manifest! with { Fps = 30 };

        RenderEventManifest? back = RenderEventManifest.Parse(manifest.ToJson()).Manifest;

        back!.Fps.Should().Be(30);
        back.Events.Should().Equal(manifest.Events);
    }

    [Fact]
    public void The_sidecar_lives_under_agentFiles_keyed_by_the_render_output_key()
    {
        string key = "projects/11111111-1111-1111-1111-111111111111/outputFiles/22222222-2222-2222-2222-222222222222/film.mp4";

        RenderEventManifest.SidecarKeyFor(key).Should().Be(
            "projects/11111111-1111-1111-1111-111111111111/agentFiles/render-events/22222222-2222-2222-2222-222222222222/film.mp4.events.json");
    }

    /// <summary>QA-P1-12: a render COPY saved into <c>userFiles/</c> mirrors its own key the same way.</summary>
    [Fact]
    public void A_render_copy_under_userFiles_mirrors_its_own_key_to_a_sidecar()
    {
        string key = "projects/11111111-1111-1111-1111-111111111111/userFiles/renders/film.mp4";

        RenderEventManifest.SidecarKeyFor(key).Should().Be(
            "projects/11111111-1111-1111-1111-111111111111/agentFiles/render-events/renders/film.mp4.events.json");
    }

    [Theory]
    [InlineData("projects/11111111-1111-1111-1111-111111111111/uploads/film.mp4")]
    [InlineData("projects/11111111-1111-1111-1111-111111111111/outputFiles/")]
    [InlineData("projects/11111111-1111-1111-1111-111111111111/outputFiles/../x.mp4")]
    [InlineData("")]
    public void A_key_that_is_not_a_render_output_has_no_sidecar(string key) =>
        RenderEventManifest.SidecarKeyFor(key).Should().BeNull();

    // ================================================================ S-69: bounded before any regex

    [Fact]
    public void S69_an_adversarial_label_is_sanitized_in_linear_time()
    {
        // ~100 KiB of '<' with no '>' made "<[^>]*>" quadratic: every '<' scanned to the end of the label.
        string label = new('<', 100_000);
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Validate([Ev("stamp", 1, label)]);

        watch.Stop();
        errors.Should().BeEmpty();
        manifest!.Events.Single().Label.Length.Should().BeLessThanOrEqualTo(RenderEventManifest.MaxLabelChars);
        watch.ElapsedMilliseconds.Should().BeLessThan(1000, "the label is capped before any regex runs");
    }

    [Fact]
    public void S69_a_time_shaped_adversarial_label_is_also_linear()
    {
        string label = string.Concat(Enumerable.Repeat("1:", 50_000)) + "x";
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        RenderEventManifest.SanitizeLabel(label);

        watch.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Fact]
    public void S69_a_manifest_file_over_the_size_cap_is_refused_before_it_is_parsed()
    {
        string json = "{\"events\":[{\"name\":\"stamp\",\"frame\":1,\"label\":\"" + new string('a', RenderEventManifest.MaxManifestChars) + "\"}]}";

        (RenderEventManifest? manifest, IReadOnlyList<string> errors) = RenderEventManifest.Parse(json);

        manifest.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("too large");
    }

    [Fact]
    public void S69_a_long_label_still_keeps_its_leading_prose()
    {
        string label = "Stamp lands " + new string('x', 5_000);

        RenderEventManifest.SanitizeLabel(label).Should().StartWith("Stamp lands");
    }
}
