using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Assistant;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// <see cref="AssistantReplySanitizer"/> is the last deterministic line of the "names, never ids" rule: whatever
/// the model wrote, a customer never reads a GUID or a confirmation token, and a GUID a tool returned alongside a
/// name reads as that name.
/// </summary>
public class AssistantReplySanitizerTests
{
    private const string ProjectId = "3f2b8c1e-4a5d-4e6f-8a9b-0c1d2e3f4a5b";
    private const string FileId = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
    private const string WorkflowId = "11111111-2222-3333-4444-555555555555";
    private const string Unknown = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Token = "Zm9vLWJhci10b2tlbi0xMjM0NTY3ODkw";

    private static AssistantReplySanitizer Learned()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""[{"id":"{{ProjectId}}","name":"Launch Teaser","description":"d"}]""");
        sanitizer.Learn($$"""{"files":[{"id":"{{FileId}}","name":"intro.mp4","size":10}],"projectId":"{{ProjectId}}"}""");
        return sanitizer;
    }

    // ---------------------------------------------------------------------
    // Learning
    // ---------------------------------------------------------------------

    [Fact]
    public void An_id_beside_a_name_is_remembered_through_arrays_and_nested_objects()
    {
        AssistantReplySanitizer sanitizer = Learned();

        sanitizer.KnownNameCount.Should().Be(2);
        sanitizer.Sanitize($"Opening {ProjectId} now").Should().Be("Opening “Launch Teaser” now");
        sanitizer.Sanitize($"File {FileId}.").Should().Be("File “intro.mp4”.");
    }

    [Theory]
    [InlineData("fileName", "clip.mov")]
    [InlineData("originalFileName", "clip.mov")]
    [InlineData("title", "clip.mov")]
    [InlineData("label", "clip.mov")]
    [InlineData("displayName", "clip.mov")]
    public void Every_name_like_property_names_a_plain_id(string property, string name)
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""{"id":"{{FileId}}","{{property}}":"{{name}}"}""");

        sanitizer.Sanitize($"See {FileId}").Should().Be($"See “{name}”");
    }

    [Fact]
    public void A_prefixed_id_pairs_with_its_own_prefixed_name_and_not_with_a_neighbours()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""
            {"id":"{{Unknown}}","status":"Failed","workflowId":"{{WorkflowId}}","workflowName":"Phone tracking",
             "projectId":"{{ProjectId}}","projectName":"Launch Teaser"}
            """);

        sanitizer.Sanitize($"The run of {WorkflowId} in {ProjectId} failed").Should()
            .Be("The run of “Phone tracking” in “Launch Teaser” failed");

        // The run's own id has no name of its own; it must not borrow the workflow's.
        sanitizer.Sanitize($"Run {Unknown} failed").Should().Be("Run failed");
    }

    [Fact]
    public void A_sole_prefixed_id_may_use_the_generic_name_beside_it()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""{"providerId":"{{WorkflowId}}","name":"Studio provider","tested":true}""");

        sanitizer.Sanitize($"Tested {WorkflowId}").Should().Be("Tested “Studio provider”");
    }

    [Fact]
    public void Two_prefixed_ids_never_share_the_generic_name()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""{"projectId":"{{ProjectId}}","fileId":"{{FileId}}","name":"x"}""");

        sanitizer.KnownNameCount.Should().Be(0);
    }

    [Fact]
    public void Snake_case_properties_pair_too()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""{"project_id":"{{ProjectId}}","project_name":"Launch Teaser"}""");

        sanitizer.Sanitize($"In {ProjectId}").Should().Be("In “Launch Teaser”");
    }

    [Fact]
    public void A_later_result_renames_what_an_earlier_one_named()
    {
        AssistantReplySanitizer sanitizer = Learned();
        sanitizer.Learn($$"""{"id":"{{ProjectId}}","name":"Launch Teaser v2"}""");

        sanitizer.Sanitize(ProjectId).Should().Be("“Launch Teaser v2”");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"id\": ")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"id\":\"not-a-guid\",\"name\":\"x\"}")]
    [InlineData("{\"id\":123,\"name\":\"x\"}")]
    public void Anything_that_is_not_a_usable_result_teaches_nothing_and_never_throws(string? result)
    {
        var sanitizer = new AssistantReplySanitizer();

        Action learn = () => sanitizer.Learn(result);

        learn.Should().NotThrow();
        sanitizer.KnownNameCount.Should().Be(0);
    }

    [Fact]
    public void A_name_that_is_itself_an_id_or_blank_is_never_remembered()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""[{"id":"{{ProjectId}}","name":"{{Unknown}}"},{"id":"{{FileId}}","name":"   "}]""");

        sanitizer.KnownNameCount.Should().Be(0);
        sanitizer.Sanitize($"A {ProjectId} B").Should().Be("A B");
    }

    [Fact]
    public void A_name_is_flattened_to_one_short_line_without_curly_quotes()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""{"id":"{{ProjectId}}","name":"My “best”\nlaunch\t video"}""");

        sanitizer.Sanitize(ProjectId).Should().Be("“My \"best\" launch video”");

        var longName = new AssistantReplySanitizer();
        longName.Learn($$"""{"id":"{{ProjectId}}","name":"{{new string('x', 500)}}"}""");
        longName.Sanitize(ProjectId).Length.Should().BeLessThan(130);
    }

    [Fact]
    public void A_json_string_holding_json_is_read_too()
    {
        var sanitizer = new AssistantReplySanitizer();
        string inner = $$"""{"id":"{{ProjectId}}","name":"Launch Teaser","confirmationToken":"{{Token}}"}""";
        sanitizer.Learn(System.Text.Json.JsonSerializer.Serialize(new { result = inner }));

        sanitizer.Sanitize($"{ProjectId} {Token}").Should().Be("“Launch Teaser”");
    }

    // ---------------------------------------------------------------------
    // Sanitizing: GUIDs
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("Launch Teaser ({0})", "Launch Teaser")]
    [InlineData("Launch Teaser (id: {0})", "Launch Teaser")]
    [InlineData("Launch Teaser (ID {0})", "Launch Teaser")]
    [InlineData("Launch Teaser (with id {0}).", "Launch Teaser.")]
    [InlineData("Launch Teaser [{0}]", "Launch Teaser")]
    [InlineData("Launch Teaser {{{0}}}", "Launch Teaser")]
    [InlineData("Launch Teaser (`{0}`)", "Launch Teaser")]
    [InlineData("Launch Teaser ( {0} ), done", "Launch Teaser, done")]
    public void A_bracketed_id_is_removed_with_its_label_and_the_space_before_it(string template, string expected)
    {
        AssistantReplySanitizer sanitizer = Learned();

        sanitizer.Sanitize(string.Format(template, ProjectId)).Should().Be(expected);
        // Unknown ids go the same way.
        sanitizer.Sanitize(string.Format(template, Unknown)).Should().Be(expected);
    }

    [Fact]
    public void A_bare_known_id_becomes_its_name_in_curly_quotes()
    {
        AssistantReplySanitizer sanitizer = Learned();

        sanitizer.Sanitize($"I renamed {ProjectId} and moved {FileId}.").Should()
            .Be("I renamed “Launch Teaser” and moved “intro.mp4”.");
        sanitizer.Sanitize($"**{ProjectId}** is ready").Should().Be("**“Launch Teaser”** is ready");
        sanitizer.Sanitize($"{ProjectId.ToUpperInvariant()} is ready").Should().Be("“Launch Teaser” is ready");
    }

    [Fact]
    public void A_labelled_known_id_becomes_the_name_without_the_label()
    {
        AssistantReplySanitizer sanitizer = Learned();

        sanitizer.Sanitize($"the project with id {ProjectId} is open").Should()
            .Be("the project “Launch Teaser” is open");
        sanitizer.Sanitize($"project id: {ProjectId}").Should().Be("project “Launch Teaser”");
        sanitizer.Sanitize($"Project ID {Unknown} is gone").Should().Be("Project is gone");
    }

    [Fact]
    public void A_backticked_known_id_becomes_the_name_unless_the_name_already_stands_before_it()
    {
        AssistantReplySanitizer sanitizer = Learned();

        sanitizer.Sanitize($"Opened `{ProjectId}`.").Should().Be("Opened “Launch Teaser”.");
        sanitizer.Sanitize($"“Launch Teaser” `{ProjectId}` is ready").Should().Be("“Launch Teaser” is ready");
        sanitizer.Sanitize($"Launch Teaser: {ProjectId}").Should().Be("Launch Teaser");
        sanitizer.Sanitize($"**Launch Teaser** — {ProjectId}").Should().Be("**Launch Teaser**");
    }

    [Fact]
    public void An_unknown_bare_id_is_removed_and_the_punctuation_around_it_is_tidied()
    {
        var sanitizer = new AssistantReplySanitizer();

        sanitizer.Sanitize($"The run {Unknown} failed").Should().Be("The run failed");
        sanitizer.Sanitize($"The run failed: {Unknown}.").Should().Be("The run failed.");
        sanitizer.Sanitize($"Runs: A, B, {Unknown}.").Should().Be("Runs: A, B.");
        sanitizer.Sanitize($"Runs: {Unknown}, B, C").Should().Be("Runs: B, C");
        sanitizer.Sanitize($"({Unknown}, {WorkflowId})").Should().Be("");
        sanitizer.Sanitize($"See ({Unknown}, the second one)").Should().Be("See (the second one)");
        sanitizer.Sanitize($"Done {Unknown}!").Should().Be("Done!");
        sanitizer.Sanitize($"{Unknown} is the one").Should().Be("is the one");
        sanitizer.Sanitize($"one {Unknown} space").Should().Be("one space");
    }

    [Fact]
    public void A_list_item_or_line_that_held_only_an_id_disappears_whole()
    {
        var sanitizer = new AssistantReplySanitizer();

        sanitizer.Sanitize($"Your runs:\n- {Unknown}\n- Nightly render\n").Should().Be("Your runs:\n- Nightly render\n");
        sanitizer.Sanitize($"Steps:\n1. {Unknown}\n2. Cut scenes").Should().Be("Steps:\n2. Cut scenes");
        sanitizer.Sanitize($"Ends with:\n* {Unknown}").Should().Be("Ends with:");
        sanitizer.Sanitize($"A\n\n{Unknown}\n\nB").Should().Be("A\n\nB");
        sanitizer.Sanitize($"- {Unknown}, {WorkflowId}\n- Keep").Should().Be("- Keep");
    }

    [Fact]
    public void Several_ids_in_one_reply_are_each_dealt_with()
    {
        AssistantReplySanitizer sanitizer = Learned();

        string reply = $"Moved {FileId} into {ProjectId} ({ProjectId}); also saw {Unknown}, `{WorkflowId}`.";

        sanitizer.Sanitize(reply).Should().Be("Moved \u201Cintro.mp4\u201D into \u201CLaunch Teaser\u201D; also saw.");
    }

    [Fact]
    public void An_id_inside_a_markdown_link_target_is_left_alone_but_visible_ids_are_not()
    {
        AssistantReplySanitizer sanitizer = Learned();

        sanitizer.Sanitize($"[Open Launch Teaser](/app/projects/{ProjectId})").Should()
            .Be($"[Open Launch Teaser](/app/projects/{ProjectId})");
        sanitizer.Sanitize($"[Open it](/app/projects/{ProjectId}/workflows/{WorkflowId}) ({ProjectId})").Should()
            .Be($"[Open it](/app/projects/{ProjectId}/workflows/{WorkflowId})");
    }

    [Fact]
    public void Ids_inside_a_fenced_block_are_cleaned_but_the_fence_structure_is_kept()
    {
        AssistantReplySanitizer sanitizer = Learned();

        string reply = $"Here:\n```json\n{{\"projectId\": \"{ProjectId}\"}}\n```\nDone.";

        sanitizer.Sanitize(reply).Should().Be("Here:\n```json\n{\"projectId\": \"“Launch Teaser”\"}\n```\nDone.");
    }

    [Fact]
    public void Something_that_only_looks_like_an_id_is_left_alone()
    {
        var sanitizer = new AssistantReplySanitizer();

        string[] untouched =
        [
            "Version 3f2b8c1e-4a5d is a build tag",
            "A phone 555-1234-5678",
            "Hash 3f2b8c1e4a5d4e6f8a9b0c1d2e3f4a5b is not dashed",
            "Too long 3f2b8c1e-4a5d-4e6f-8a9b-0c1d2e3f4a5b0 here",
            "Plain text with nothing to clean."
        ];

        foreach (string reply in untouched)
            sanitizer.Sanitize(reply).Should().Be(reply);
    }

    // ---------------------------------------------------------------------
    // Sanitizing: confirmation tokens
    // ---------------------------------------------------------------------

    [Fact]
    public void A_remembered_confirmation_token_is_removed_wherever_it_appears()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($$"""{"confirmationToken":"{{Token}}","summary":"Rename the project"}""");

        sanitizer.Sanitize($"Use token {Token} to apply").Should().Be("Use token to apply");
        sanitizer.Sanitize($"The token is `{Token}`.").Should().Be("The token is.");
        sanitizer.Sanitize($"Apply with {Token}").Should().Be("Apply with");
    }

    [Fact]
    public void A_token_in_a_result_that_is_not_clean_json_is_still_learned()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn($"Tool said: {{\"confirmationToken\":\"{Token}\", oops");

        sanitizer.Sanitize($"here: {Token}").Should().Be("here:");
    }

    [Fact]
    public void A_short_token_is_not_remembered_so_ordinary_words_are_never_wiped()
    {
        var sanitizer = new AssistantReplySanitizer();
        sanitizer.Learn("""{"confirmationToken":"ok"}""");

        sanitizer.Sanitize("That is ok.").Should().Be("That is ok.");
    }

    // ---------------------------------------------------------------------
    // Robustness
    // ---------------------------------------------------------------------

    [Fact]
    public void Nothing_to_do_returns_the_very_same_string()
    {
        AssistantReplySanitizer sanitizer = Learned();
        sanitizer.Learn($$"""{"confirmationToken":"{{Token}}"}""");

        string reply = "I renamed it to “Launch Teaser” and the run starts shortly.";

        sanitizer.Sanitize(reply).Should().BeSameAs(reply);
    }

    [Fact]
    public void Null_and_empty_replies_come_back_as_they_are()
    {
        var sanitizer = new AssistantReplySanitizer();

        sanitizer.Sanitize(string.Empty).Should().BeEmpty();
        sanitizer.Sanitize(null!).Should().BeNull();
    }

    [Fact]
    public async Task Learning_and_sanitizing_from_many_threads_is_safe()
    {
        var sanitizer = new AssistantReplySanitizer();
        var tasks = new Task[8];
        for (int t = 0; t < tasks.Length; t++)
        {
            int worker = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    Guid id = Guid.NewGuid();
                    sanitizer.Learn($$"""{"id":"{{id}}","name":"Item {{worker}}-{{i}}"}""");
                    sanitizer.Sanitize($"see {id}").Should().Be($"see “Item {worker}-{i}”");
                }
            });
        }

        await Task.WhenAll(tasks);

        sanitizer.KnownNameCount.Should().Be(8 * 200);
    }
}
