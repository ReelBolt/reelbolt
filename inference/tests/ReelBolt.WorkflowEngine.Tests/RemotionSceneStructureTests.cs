using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The deterministic half of the authored-scene design: reading a Remotion composition's OWN
/// <c>&lt;Sequence&gt;</c> list, and reconciling what the code says with what the authoring model
/// declared at render time.
/// </summary>
/// <remarks>
/// The bar these tests hold is the one the design depends on. A wrong split is worse than no split —
/// an editor that cuts on a fabricated boundary lands mid-animation — so every ambiguity must be a
/// refusal (<see cref="RemotionSceneStructure.Read"/> returning <c>Resolved: false</c>), and every
/// disagreement between a declaration and the code must be REPORTED rather than silently resolved.
/// </remarks>
public class RemotionSceneStructureTests
{
    /// <summary>The sandbox template's own root.tsx shape, which is what the Author agent writes.</summary>
    private const string TemplateSource = """
        import { Composition, Sequence, AbsoluteFill } from 'remotion';
        import { SceneWordmark } from './scenes/wordmark';
        import { ScenePills } from './scenes/pills';

        const LaunchPromo: React.FC = () => (
          <AbsoluteFill style={{ background: '#09090d' }}>
            <Sequence from={0} durationInFrames={100}>
              <SceneWordmark />
            </Sequence>
            <Sequence from={90} durationInFrames={100}>
              <ScenePills />
            </Sequence>
            <Sequence from={180} durationInFrames={120}>
              <ScenePills />
            </Sequence>
          </AbsoluteFill>
        );

        export const Root: React.FC = () => (
          <Composition
            id="LaunchPromo"
            component={LaunchPromo}
            durationInFrames={300}
            fps={30}
            width={1920}
            height={1080}
          />
        );
        """;

    // ---- Reading the composition's own structure ----

    [Fact]
    public void The_compositions_own_sequences_are_read_with_their_exact_frames()
    {
        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(TemplateSource, "LaunchPromo");

        result.Resolved.Should().BeTrue(string.Join(" ", result.Reasons));
        result.Spans.Select(s => (s.FromFrame, s.DurationInFrames)).Should().Equal((0, 100), (90, 100), (180, 120));
        result.Fps.Should().Be(30);
        result.DurationInFrames.Should().Be(300);
    }

    [Fact]
    public void Sequences_are_returned_in_frame_order_whatever_order_the_source_lists_them_in()
    {
        const string source = """
            const Film = () => (
              <AbsoluteFill>
                <Sequence from={180} durationInFrames={120}><C /></Sequence>
                <Sequence from={0} durationInFrames={100}><A /></Sequence>
                <Sequence from={90} durationInFrames={100}><B /></Sequence>
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeTrue(string.Join(" ", result.Reasons));
        result.Spans.Select(s => s.FromFrame).Should().Equal(0, 90, 180);
    }

    [Fact]
    public void A_file_registering_a_different_composition_is_refused()
    {
        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(TemplateSource, "SomethingElse");

        result.Resolved.Should().BeFalse();
        result.Reasons.Should().ContainSingle().Which.Should().Contain("SomethingElse");
        result.Spans.Should().BeEmpty("a refusal must never carry a partial split");
    }

    // ---- Fail closed: every ambiguity is a refusal, never a guess ----

    [Fact]
    public void A_sequence_whose_timing_is_computed_is_refused_rather_than_guessed()
    {
        const string source = """
            const FILM_FPS = 30;
            const Film = () => (
              <AbsoluteFill>
                {scenes.map((scene, i) => (
                  <Sequence key={i} from={i * scene.frames} durationInFrames={scene.frames}><Scene /></Sequence>
                ))}
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeFalse();
        result.Spans.Should().BeEmpty();
        result.Reasons.Should().Contain(r => r.Contains("literal from"));
    }

    [Fact]
    public void A_nested_sequence_is_refused_because_its_timing_is_relative()
    {
        const string source = """
            const Film = () => (
              <Sequence from={0} durationInFrames={300}>
                <Sequence from={30} durationInFrames={60}><Inside /></Sequence>
              </Sequence>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeFalse();
        result.Spans.Should().BeEmpty();
        result.Reasons.Should().Contain(r => r.Contains("inside another"));
    }

    [Fact]
    public void Overlapping_sequences_are_kept_because_that_is_how_a_composition_crossfades()
    {
        // The shipped sandbox template's own shape: scene 2 starts at frame 90 while scene 1 still
        // has ten frames to run, and the overlap is the crossfade.
        const string source = """
            const Film = () => (
              <AbsoluteFill>
                <Sequence from={0} durationInFrames={100}><A /></Sequence>
                <Sequence from={90} durationInFrames={100}><B /></Sequence>
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={190} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeTrue(string.Join(" ", result.Reasons));
        result.Spans.Select(s => s.FromFrame).Should().Equal(0, 90);
    }

    [Fact]
    public void Two_sequences_starting_at_the_same_frame_are_refused_as_a_nested_read()
    {
        const string source = """
            const Film = () => (
              <AbsoluteFill>
                <Sequence from={0} durationInFrames={300}><A /></Sequence>
                <Sequence from={0} durationInFrames={60}><B /></Sequence>
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Contains("both start at frame 0"));
    }

    [Fact]
    public void A_sequence_running_past_the_compositions_own_length_is_refused()
    {
        const string source = """
            const Film = () => (
              <AbsoluteFill>
                <Sequence from={0} durationInFrames={100}><A /></Sequence>
                <Sequence from={100} durationInFrames={400}><B /></Sequence>
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Contains("past the composition's own"));
    }

    [Fact]
    public void Series_is_refused_rather_than_read_as_sequences()
    {
        const string source = """
            const Film = () => (
              <Series>
                <Series.Sequence durationInFrames={100}><A /></Series.Sequence>
                <Series.Sequence durationInFrames={100}><B /></Series.Sequence>
              </Series>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={200} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Contains("<Series>"));
    }

    [Fact]
    public void A_single_sequence_is_not_a_split_so_the_existing_path_runs()
    {
        const string source = """
            const Film = () => (
              <Sequence from={0} durationInFrames={300}><One /></Sequence>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeFalse(because: "one sequence says the film is one scene, which is exactly where pixel shot detection may still see more");
        result.Spans.Should().BeEmpty();
    }

    [Fact]
    public void A_sequence_written_inside_a_comment_or_a_string_is_not_a_sequence()
    {
        const string source = """
            // <Sequence from={0} durationInFrames={150}> a commented-out scene
            /* <Sequence from={150} durationInFrames={150}> and a block comment one */
            const help = "<Sequence from={0} durationInFrames={300}>";
            const Film = () => (
              <AbsoluteFill>
                <Sequence from={0} durationInFrames={150}><A /></Sequence>
                <Sequence from={150} durationInFrames={150}><B /></Sequence>
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={30} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeTrue(string.Join(" ", result.Reasons));
        result.Spans.Select(s => s.FromFrame).Should().Equal(0, 150);
    }

    [Fact]
    public void An_empty_source_is_refused_rather_than_treated_as_no_scenes()
    {
        RemotionSceneStructure.Read(null).Resolved.Should().BeFalse();
        RemotionSceneStructure.Read("   ").Resolved.Should().BeFalse();
        RemotionSceneStructure.Read("export const X = 1;").Reasons.Should().Contain(r => r.Contains("no <Composition>"));
    }

    [Fact]
    public void Two_compositions_in_one_module_are_refused_because_their_sequences_cannot_be_told_apart()
    {
        const string source = """
            export const Root = () => (
              <>
                <Composition id="A" component={A} durationInFrames={300} fps={30} />
                <Composition id="B" component={B} durationInFrames={300} fps={30} />
              </>
            );
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "A");

        result.Resolved.Should().BeFalse();
        result.Reasons.Should().Contain(r => r.Contains("2 <Composition> registrations"));
    }

    [Fact]
    public void A_non_literal_fps_narrows_the_check_without_losing_the_split()
    {
        const string source = """
            const Film = () => (
              <AbsoluteFill>
                <Sequence from={0} durationInFrames={150}><A /></Sequence>
                <Sequence from={150} durationInFrames={150}><B /></Sequence>
              </AbsoluteFill>
            );
            export const Root = () => <Composition id="Film" component={Film} durationInFrames={300} fps={FPS} />;
            """;

        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "Film");

        result.Resolved.Should().BeTrue(string.Join(" ", result.Reasons));
        result.Fps.Should().BeNull(because: "the rate is held in a constant, so the split is still the code's but its timing is the declaration's");
        result.Spans.Should().HaveCount(2);
    }

    // ---- Reconciling a declaration against the code ----

    private static CompositionSceneManifest Declare(params DeclaredScene[] scenes)
    {
        (CompositionSceneManifest? manifest, IReadOnlyList<string> errors) =
            CompositionSceneManifest.FromDeclaration("LaunchPromo", 30, scenes);
        errors.Should().BeEmpty();
        return manifest!;
    }

    [Fact]
    public void The_code_supplies_the_split_and_the_declaration_supplies_the_descriptions()
    {
        // The declaration states each scene's VISIBLE HOLD (0..90, 90..180, 180..300) while the
        // composition's own sequences crossfade (0..100, 90..190, 180..300). They agree on every
        // scene's START, which is the split, and that is what is asked of them.
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 90, "The wordmark lands on violet rings", ["REELFORGE"]),
            new DeclaredScene("Feature", 90, 90, "Feature pills spring in"),
            new DeclaredScene("Close", 180, 120, "The call to action"));

        RemotionSceneStructure.Result structure = RemotionSceneStructure.Read(TemplateSource, "LaunchPromo");

        (CompositionSceneManifest? manifest, IReadOnlyList<string> findings) =
            CompositionSceneManifest.Reconcile(declared, structure, "LaunchPromo");

        manifest.Should().NotBeNull();
        manifest!.Origin.Should().Be(CompositionSceneOrigin.Reconciled);
        manifest.Scenes.Select(s => s.Name).Should().Equal("Hook", "Feature", "Close");
        manifest.Scenes.Select(s => s.Description).Should().Equal(
            "The wordmark lands on violet rings", "Feature pills spring in", "The call to action");
        manifest.Scenes.Select(s => s.FromFrame).Should().Equal(0, 90, 180);
        manifest.Scenes.Select(s => s.DurationInFrames).Should().Equal(100, 100, 120);
        manifest.Scenes.Should().OnlyContain(s => s.Described);
        findings.Should().BeEmpty("the declaration and the code agree on every scene boundary");
    }

    [Fact]
    public void A_declaration_that_disagrees_with_the_renders_own_frames_loses_the_split_and_is_reported()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 100, "The wordmark lands"),
            new DeclaredScene("Feature", 110, 100, "Feature pills spring in"),
            new DeclaredScene("Close", 210, 90, "The call to action"));

        RemotionSceneStructure.Result structure = RemotionSceneStructure.Read(TemplateSource, "LaunchPromo");

        (CompositionSceneManifest? manifest, IReadOnlyList<string> findings) =
            CompositionSceneManifest.Reconcile(declared, structure, "LaunchPromo");

        manifest.Should().NotBeNull();
        // The code wins the split: these are the frames the renderer actually used.
        manifest!.Scenes.Select(s => s.FromFrame).Should().Equal(0, 90, 180);
        manifest.Scenes.Select(s => s.Name).Should().Equal("Hook", "scene 2", "scene 3");

        // Nothing about the disagreement is silent: both directions are reported.
        findings.Should().Contain(f => f.Contains("'Feature'") && f.Contains("matches no <Sequence>"));
        findings.Should().Contain(f => f.Contains("'Close'") && f.Contains("matches no <Sequence>"));
        findings.Should().Contain(f => f.Contains("frames 90..190") && f.Contains("marked as undescribed"));
    }

    [Fact]
    public void A_scene_the_code_places_but_nobody_described_is_kept_and_marked_undescribed()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 100, "The wordmark lands"),
            new DeclaredScene("Close", 180, 120, "The call to action"));

        RemotionSceneStructure.Result structure = RemotionSceneStructure.Read(TemplateSource, "LaunchPromo");

        (CompositionSceneManifest? manifest, IReadOnlyList<string> findings) =
            CompositionSceneManifest.Reconcile(declared, structure, "LaunchPromo");

        manifest!.Scenes.Should().HaveCount(3, "the code places three sequences, so the film has three scenes");
        manifest.Scenes[1].Described.Should().BeFalse();
        manifest.Scenes[1].Description.Should().BeEmpty();
        manifest.Scenes[1].FromFrame.Should().Be(90, "the undescribed scene still gets the code's own boundaries");
        findings.Should().Contain(f => f.Contains("frames 90..190") && f.Contains("does not describe"));
    }

    [Fact]
    public void A_declared_scene_the_code_places_nowhere_is_dropped_and_reported()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 90, "The wordmark lands"),
            new DeclaredScene("Feature", 90, 90, "Feature pills spring in"),
            new DeclaredScene("Close", 180, 120, "The call to action"),
            new DeclaredScene("Ghost", 400, 100, "A scene that is not in the film"));

        // The composition is longer than the declaration's last real scene, so "Ghost" is inside the
        // composition's own length and can only be rejected by matching, not by a range check.
        RemotionSceneStructure.Result structure = RemotionSceneStructure.Read(
            TemplateSource.Replace("durationInFrames={300}", "durationInFrames={600}", System.StringComparison.Ordinal),
            "LaunchPromo");

        (CompositionSceneManifest? manifest, IReadOnlyList<string> findings) =
            CompositionSceneManifest.Reconcile(declared, structure, "LaunchPromo");

        manifest!.Scenes.Select(s => s.Name).Should().NotContain("Ghost");
        findings.Should().Contain(f => f.Contains("'Ghost'") && f.Contains("matches no <Sequence>"));
    }

    [Fact]
    public void With_no_declaration_the_compositions_own_structure_still_splits_the_film()
    {
        RemotionSceneStructure.Result structure = RemotionSceneStructure.Read(TemplateSource, "LaunchPromo");

        (CompositionSceneManifest? manifest, IReadOnlyList<string> findings) =
            CompositionSceneManifest.Reconcile(null, structure, "LaunchPromo");

        manifest.Should().NotBeNull(because: "this is the case the owner reported: a composition we rendered coming back as one clip");
        manifest!.Origin.Should().Be(CompositionSceneOrigin.Structure);
        manifest.IsComposition.Should().BeTrue();
        manifest.Fps.Should().Be(30);
        manifest.Scenes.Select(s => s.FromFrame).Should().Equal(0, 90, 180);
        manifest.Scenes.Should().OnlyContain(s => !s.Described);
        manifest.Scenes.Select(s => s.Handle).Should().Equal("scene-1", "scene-2", "scene-3");
        findings.Should().Contain(f => f.Contains("no scene description"));
    }

    [Fact]
    public void An_unreadable_composition_leaves_the_declaration_standing_and_stamped_as_a_claim()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 100, "The wordmark lands"),
            new DeclaredScene("Close", 180, 120, "The call to action"));

        (CompositionSceneManifest? manifest, _) = CompositionSceneManifest.Reconcile(
            declared, RemotionSceneStructure.Result.Unresolved("computed timings"), "LaunchPromo");

        manifest.Should().NotBeNull();
        manifest!.Origin.Should().Be(CompositionSceneOrigin.Author,
            because: "with nothing to check it against, the declaration is a claim and must say so");
        manifest.Scenes.Select(s => s.FromFrame).Should().Equal(0, 180);
    }

    [Fact]
    public void Nothing_declared_and_nothing_readable_yields_no_manifest_at_all()
    {
        (CompositionSceneManifest? manifest, IReadOnlyList<string> findings) =
            CompositionSceneManifest.Reconcile(null, RemotionSceneStructure.Result.Unresolved("nope"), "LaunchPromo");

        manifest.Should().BeNull(because: "the third-party-footage path must be reached unchanged, not with an invented manifest");
        findings.Should().BeEmpty();
    }

    // ---- Honesty: a claim about a render, checked against the render ----

    [Fact]
    public void A_manifest_that_covers_only_part_of_the_render_says_so()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 100, "The wordmark lands"));

        // Three of the thirty seconds are described; the rest is not.
        IReadOnlyList<string> findings = declared.DescribeAgainstMedia(30.0, 30.0);

        findings.Should().ContainSingle().Which.Should().Contain("cover 3").And.Contain("folded into the last one");
    }

    [Fact]
    public void A_manifest_that_runs_past_the_end_of_the_render_says_so()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 300, "The wordmark lands"),
            new DeclaredScene("Close", 300, 300, "The call to action"));

        IReadOnlyList<string> findings = declared.DescribeAgainstMedia(8.0, 30.0);

        findings.Should().Contain(f => f.Contains("run to 20") && f.Contains("past the end"));
        findings.Should().Contain(f => f.Contains("1 declared scene(s) start at or past the end"));
    }

    [Fact]
    public void A_manifest_timed_at_a_rate_the_render_does_not_use_says_so()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 300, "The wordmark lands"));

        IReadOnlyList<string> findings = declared.DescribeAgainstMedia(10.0, 60.0);

        findings.Should().Contain(f => f.Contains("timed at 30 fps") && f.Contains("60 fps"));
    }

    [Fact]
    public void A_manifest_that_matches_the_render_reports_nothing()
    {
        CompositionSceneManifest declared = Declare(
            new DeclaredScene("Hook", 0, 150, "The wordmark lands"),
            new DeclaredScene("Close", 150, 150, "The call to action"));

        declared.DescribeAgainstMedia(10.0, 30.0).Should().BeEmpty();
    }
    // ---- Against the composition we actually ship ----

    /// <summary>
    /// The sandbox template every Author agent starts from, found from the test binary by walking up
    /// to the repository root. Reading the REAL file rather than a copy of its shape is the point:
    /// this is the composition our own pipeline renders, so if the deterministic arm cannot read it,
    /// the deterministic arm does not work.
    /// </summary>
    private static string ShippedTemplateSource()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "sandbox", "template", "src", "root.tsx");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "sandbox/template/src/root.tsx was not found above the test binary; this test must run from inside the repository.");
    }

    [Fact]
    public void The_composition_the_sandbox_template_ships_is_read_from_its_own_sequences()
    {
        string source = ShippedTemplateSource();

        // What the template actually writes, per its own header comment: three <Sequence>s at 30 fps
        // over a 300-frame composition, with the second starting before the first has ended — the
        // crossfade the overlap rule has to keep.
        RemotionSceneStructure.Result result = RemotionSceneStructure.Read(source, "LaunchPromo");

        result.Resolved.Should().BeTrue(string.Join(" ", result.Reasons));
        result.Fps.Should().Be(30);
        result.DurationInFrames.Should().Be(300);
        result.Spans.Select(s => (s.FromFrame, s.DurationInFrames)).Should().Equal((0, 100), (90, 100), (180, 120));

        // ... and it turns into a three-scene split without anyone declaring anything.
        (CompositionSceneManifest? manifest, _) = CompositionSceneManifest.Reconcile(null, result, "LaunchPromo");
        manifest.Should().NotBeNull();

        var (spans, scenes) = manifest!.ToShotSpans(result.DurationInFrames!.Value / (double)result.Fps!.Value);
        spans.Should().Equal((0.0, 3.0), (3.0, 6.0), (6.0, 10.0));
        scenes.Should().HaveCount(3);
        scenes.Should().OnlyContain(s => !s.Described, because: "nobody described these; the code only placed them");
    }
}
