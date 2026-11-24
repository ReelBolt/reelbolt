using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The assistant's targeted file tools (ReadProjectFileLines, GetProjectFileOutline, Propose/ApplyEditProjectFile,
/// Propose/ApplyInsertProjectFileLines) and the approval cards for their Apply tools. In-memory fakes only.
/// </summary>
public sealed class AssistantTargetedFileToolsTests
{
    /// <summary>Wraps the real access service so a test can drop the caller's role between two calls.</summary>
    private sealed class SwitchableAccess(IProjectAccessService inner) : IProjectAccessService
    {
        public bool DenyEdit { get; set; }
        public bool DenyAll { get; set; }

        private ProjectAccessResult Check(Guid id, ProjectPermission permission) =>
            DenyAll || (DenyEdit && permission == ProjectPermission.Edit)
                ? new ProjectAccessResult(ProjectAccessOutcome.NotFound, null)
                : inner.Get(id, permission);

        public Task<ProjectAccessResult> GetAsync(Guid projectId, ProjectPermission permission, CancellationToken ct) =>
            Task.FromResult(Check(projectId, permission));

        public ProjectAccessResult Get(Guid projectId, ProjectPermission permission) => Check(projectId, permission);

        public ProjectAccessOutcome AuthorizeOrganization(ProjectPermission permission) => inner.AuthorizeOrganization(permission);
    }

    private sealed class Harness
    {
        public readonly InferenceApiDbContext Db;
        public readonly Guid Owner = Guid.NewGuid();
        public readonly Project Project;
        public readonly FakeFileStorage Storage = new();
        public readonly SwitchableAccess Access;
        public readonly AssistantFileTools Tools;
        public readonly Mock<IPublishEndpoint> Publish = new();
        public readonly AssistantToolContext Context;

        public Harness()
        {
            Db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Project = SeedProject(Owner, "Launch Teaser");
            var user = new FakeCurrentUser(Owner);
            Access = new SwitchableAccess(TestTenancy.Access(Db, user));
            Tools = new AssistantFileTools(
                Db, ConfirmationTokenService.CreateEphemeral(), Storage, Publish.Object, new RecordingSummarizationQueue(), Access);
            Context = new AssistantToolContext(Owner, Project.Id);
        }

        public Project SeedProject(Guid owner, string name)
        {
            var project = new Project
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = "seeded",
                OwnerId = owner,
                OrganizationId = TestTenancy.OrgOf(owner),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            Db.Projects.Add(project);
            Db.SaveChanges();
            return project;
        }

        public ProjectFile Seed(string path, string content, string mime = "text/plain", Guid? projectId = null) =>
            SeedBytes(path, Encoding.UTF8.GetBytes(content), mime, projectId);

        public ProjectFile SeedBytes(string path, byte[] bytes, string mime = "text/plain", Guid? projectId = null)
        {
            string key = $"projects/{projectId ?? Project.Id}/userFiles/{Guid.NewGuid()}";
            Storage.Objects[key] = bytes;
            int slash = path.LastIndexOf('/');
            var file = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId ?? Project.Id,
                OriginalFileName = path[(slash + 1)..],
                OriginalPath = path,
                DirectoryPath = slash > 0 ? path[..slash] : null,
                StorageKey = key,
                StorageBucket = "bucket",
                MimeType = mime,
                SizeBytes = bytes.Length,
                UploadedAt = DateTime.UtcNow,
                Category = "userFiles"
            };
            Db.ProjectFiles.Add(file);
            Db.SaveChanges();
            return file;
        }

        public string Content(ProjectFile file)
        {
            Db.Entry(file).Reload();
            return Encoding.UTF8.GetString(Storage.Objects[file.StorageKey]);
        }

        public async Task<string> Call(string tool, AssistantToolContext? context = null, params (string, object?)[] args)
        {
            AIFunction function = Tools.GetTools(context ?? Context).Single(t => t.Name == tool);
            var arguments = new AIFunctionArguments();
            foreach ((string key, object? value) in args)
                arguments[key] = value;
            object? raw = await function.InvokeAsync(arguments);
            return raw is JsonElement { ValueKind: JsonValueKind.String } s ? s.GetString()! : raw?.ToString() ?? string.Empty;
        }

        public async Task<string> Token(string tool, params (string, object?)[] args)
        {
            string proposal = await Call(tool, null, args);
            using JsonDocument doc = JsonDocument.Parse(proposal);
            return doc.RootElement.GetProperty("confirmationToken").GetString()!;
        }
    }

    private static JsonElement Edits(params (string Old, string New, bool All)[] edits) =>
        JsonSerializer.SerializeToElement(edits.Select(e => new { oldText = e.Old, newText = e.New, replaceAll = e.All }));

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static bool IsOk(string json) => Json(json).TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True;

    private static string Error(string json) => Json(json).GetProperty("error").GetString()!;

    // ------------------------------------------------------------------ reads

    [Fact]
    public async Task ReadProjectFileLines_returns_a_numbered_window_and_flags_truncation()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("notes.txt", "a\nb\nc\nd\ne\n");

        string json = await h.Call("ReadProjectFileLines", null, ("fileId", file.Id), ("startLine", 2), ("lineCount", 2));

        JsonElement r = Json(json);
        r.GetProperty("ok").GetBoolean().Should().BeTrue();
        r.GetProperty("content").GetString().Should().Be("     2| b\n     3| c\n");
        r.GetProperty("startLine").GetInt32().Should().Be(2);
        r.GetProperty("endLine").GetInt32().Should().Be(3);
        r.GetProperty("truncated").GetBoolean().Should().BeTrue();
        r.GetProperty("projectName").GetString().Should().Be("Launch Teaser");
        json.Should().NotContain(file.Id.ToString(), "the model is never handed ids");
    }

    [Fact]
    public async Task ReadProjectFileLines_by_path_clamps_the_count_and_handles_past_the_end()
    {
        var h = new Harness();
        h.Seed("docs/a.md", "x\ny\nz");

        JsonElement all = Json(await h.Call("ReadProjectFileLines", null, ("path", "docs/a.md"), ("lineCount", 99999)));
        all.GetProperty("endLine").GetInt32().Should().Be(3);
        all.GetProperty("truncated").GetBoolean().Should().BeFalse();

        JsonElement one = Json(await h.Call("ReadProjectFileLines", null, ("path", "docs/a.md"), ("lineCount", 0)));
        one.GetProperty("content").GetString().Should().Be("     1| x\n");

        JsonElement past = Json(await h.Call("ReadProjectFileLines", null, ("path", "docs/a.md"), ("startLine", 50)));
        past.GetProperty("content").GetString().Should().BeEmpty();
        past.GetProperty("totalLines").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task ReadProjectFileLines_bounds_its_output()
    {
        var h = new Harness();
        string line = new('x', 60);
        ProjectFile file = h.Seed("big.txt", string.Join('\n', Enumerable.Repeat(line, 2000)));

        JsonElement r = Json(await h.Call("ReadProjectFileLines", null, ("fileId", file.Id), ("lineCount", 2000)));

        r.GetProperty("content").GetString()!.Length.Should().BeLessThanOrEqualTo(ProjectFileMaxChars);
        r.GetProperty("truncated").GetBoolean().Should().BeTrue();
        r.GetProperty("endLine").GetInt32().Should().BeLessThan(2000);
    }

    private const int ProjectFileMaxChars = 60_000;

    [Fact]
    public async Task Reads_refuse_binary_and_treat_foreign_and_unknown_files_as_absent()
    {
        var h = new Harness();
        ProjectFile video = h.SeedBytes("clip.mp4", [1, 2, 3], "video/mp4");
        Guid stranger = Guid.NewGuid();
        Project theirs = h.SeedProject(stranger, "Theirs");
        ProjectFile foreign = h.Seed("secret.txt", "classified", projectId: theirs.Id);

        Error(await h.Call("ReadProjectFileLines", null, ("fileId", video.Id))).Should().Contain("not a text-editable asset");
        string viaFileId = await h.Call("ReadProjectFileLines", null, ("fileId", foreign.Id));
        string unknown = await h.Call("ReadProjectFileLines", null, ("fileId", Guid.NewGuid()));
        viaFileId.Should().Be(unknown, "a file of another project is indistinguishable from one that does not exist");
        string viaProject = await h.Call("ReadProjectFileLines", null, ("fileId", foreign.Id), ("projectId", theirs.Id));
        string missingProject = await h.Call("ReadProjectFileLines", null, ("fileId", foreign.Id), ("projectId", Guid.NewGuid()));
        viaProject.Should().Be(missingProject);
        viaProject.Should().NotContain("classified");
        (await h.Call("GetProjectFileOutline", null, ("fileId", foreign.Id))).Should().Be(unknown);
    }

    [Fact]
    public async Task Reads_re_check_access_on_every_call()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("notes.txt", "hello");
        IsOk(await h.Call("ReadProjectFileLines", null, ("fileId", file.Id))).Should().BeTrue();

        h.Access.DenyAll = true;

        IsOk(await h.Call("ReadProjectFileLines", null, ("fileId", file.Id))).Should().BeFalse();
        (await h.Call("GetProjectFileOutline", null, ("fileId", file.Id))).Should().NotContain("hello");
    }

    [Fact]
    public async Task GetProjectFileOutline_lists_markdown_headings()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("doc.md", "# Title\ntext\n## Part One\nmore\n", "text/markdown");

        string outline = await h.Call("GetProjectFileOutline", null, ("fileId", file.Id));

        outline.Should().Contain("Title").And.Contain("Part One");
    }

    // ------------------------------------------------------------------ edit

    [Fact]
    public async Task Propose_edit_writes_nothing_and_apply_writes_exactly_the_edited_bytes()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("script.txt", "one\ntwo\nthree\n");
        JsonElement edits = Edits(("two", "2", false), ("three", "3", false));

        string proposal = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", edits));

        h.Storage.Uploads.Should().BeEmpty();
        h.Content(file).Should().Be("one\ntwo\nthree\n");
        proposal.Should().NotContain(file.Id.ToString());
        string token = Json(proposal).GetProperty("confirmationToken").GetString()!;

        string applied = await h.Call("ApplyEditProjectFile", null, ("fileId", file.Id), ("edits", edits), ("confirmationToken", token));

        h.Storage.Uploads.Should().HaveCount(1);
        h.Storage.Uploads[0].Content.Should().Equal(Encoding.UTF8.GetBytes("one\n2\n3\n"));
        h.Content(file).Should().Be("one\n2\n3\n");
        file.SizeBytes.Should().Be(8);
        applied.Should().Contain("Launch Teaser");
        h.Publish.Invocations.Should().NotBeEmpty("an edited file is re-indexed like any other write");
    }

    [Fact]
    public async Task Edit_token_binds_the_edits_the_user_the_file_and_the_project()
    {
        var h = new Harness();
        ProjectFile a = h.Seed("a.txt", "alpha beta");
        ProjectFile b = h.Seed("b.txt", "alpha beta");
        JsonElement edits = Edits(("alpha", "ALPHA", false));
        string token = await h.Token("ProposeEditProjectFile", ("fileId", a.Id), ("edits", edits));

        string tampered = await h.Call("ApplyEditProjectFile", null, ("fileId", a.Id), ("edits", Edits(("alpha", "EVIL", false))), ("confirmationToken", token));
        string otherFile = await h.Call("ApplyEditProjectFile", null, ("fileId", b.Id), ("edits", edits), ("confirmationToken", token));
        string otherUser = await h.Call("ApplyEditProjectFile", new AssistantToolContext(Guid.NewGuid(), h.Project.Id),
            ("fileId", a.Id), ("edits", edits), ("confirmationToken", token));

        foreach (string refused in new[] { tampered, otherFile, otherUser })
            IsOk(refused).Should().BeFalse();
        Error(tampered).Should().Contain("stale");
        h.Storage.Uploads.Should().BeEmpty();
        h.Content(a).Should().Be("alpha beta");
        h.Content(b).Should().Be("alpha beta");
    }

    [Fact]
    public async Task Edit_is_refused_when_the_file_changed_since_the_proposal()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "alpha beta");
        JsonElement edits = Edits(("alpha", "ALPHA", false));
        string token = await h.Token("ProposeEditProjectFile", ("fileId", file.Id), ("edits", edits));
        h.Storage.Objects[file.StorageKey] = Encoding.UTF8.GetBytes("alpha beta gamma");

        string applied = await h.Call("ApplyEditProjectFile", null, ("fileId", file.Id), ("edits", edits), ("confirmationToken", token));

        IsOk(applied).Should().BeFalse();
        Error(applied).Should().Contain("changed since the proposal");
        h.Storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_failures_return_ok_false_and_write_nothing()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "a b a b c");
        h.Seed("big.txt", new string('a', 900_000));
        ProjectFile big = h.Db.ProjectFiles.Single(f => f.OriginalFileName == "big.txt");

        string notUnique = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", Edits(("a", "x", false))));
        string missing = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", Edits(("zzz", "x", false))));
        string overlap = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", Edits(("b c", "Q", false), ("c", "R", false))));
        string none = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", Edits()));
        string huge = await h.Call("ProposeEditProjectFile", null, ("fileId", big.Id), ("edits", Edits(("a", "bb", true))));
        string manyEdits = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id),
            ("edits", Edits(Enumerable.Range(0, 51).Select(_ => ("a b", "a b", false)).ToArray())));
        string bigText = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", Edits(("a b c", new string('x', 70_000), false))));

        foreach (string result in new[] { notUnique, missing, overlap, none, huge, manyEdits, bigText })
            IsOk(result).Should().BeFalse();
        Error(notUnique).Should().Contain("matched 2 times");
        Error(missing).Should().Contain("not found");
        Error(overlap).Should().Contain("edit 2");
        Error(huge).Should().Contain("limit");
        h.Storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_replaceAll_and_crlf_are_handled_like_the_agents_tool()
    {
        var h = new Harness();
        ProjectFile all = h.Seed("a.txt", "x y x");
        ProjectFile crlf = h.Seed("b.txt", "a\r\nb\r\nc\r\n");

        JsonElement e1 = Edits(("x", "z", true));
        string t1 = await h.Token("ProposeEditProjectFile", ("fileId", all.Id), ("edits", e1));
        await h.Call("ApplyEditProjectFile", null, ("fileId", all.Id), ("edits", e1), ("confirmationToken", t1));
        h.Content(all).Should().Be("z y z");

        JsonElement e2 = Edits(("a\nb", "A\nB", false));
        string t2 = await h.Token("ProposeEditProjectFile", ("fileId", crlf.Id), ("edits", e2));
        await h.Call("ApplyEditProjectFile", null, ("fileId", crlf.Id), ("edits", e2), ("confirmationToken", t2));
        h.Content(crlf).Should().Be("A\r\nB\r\nc\r\n");
    }

    [Fact]
    public async Task Edit_requires_the_edit_role_and_never_leaks_other_projects_files()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "alpha");
        Project theirs = h.SeedProject(Guid.NewGuid(), "Theirs");
        ProjectFile foreign = h.Seed("a.txt", "alpha", projectId: theirs.Id);
        JsonElement edits = Edits(("alpha", "beta", false));
        string token = await h.Token("ProposeEditProjectFile", ("fileId", file.Id), ("edits", edits));

        string foreignResult = await h.Call("ProposeEditProjectFile", null, ("fileId", foreign.Id), ("edits", edits));
        string unknownResult = await h.Call("ProposeEditProjectFile", null, ("fileId", Guid.NewGuid()), ("edits", edits));
        foreignResult.Should().Be(unknownResult);

        h.Access.DenyEdit = true; // role dropped to Viewer between propose and apply
        string viewerApply = await h.Call("ApplyEditProjectFile", null, ("fileId", file.Id), ("edits", edits), ("confirmationToken", token));
        string viewerPropose = await h.Call("ProposeEditProjectFile", null, ("fileId", file.Id), ("edits", edits));

        IsOk(viewerApply).Should().BeFalse();
        IsOk(viewerPropose).Should().BeFalse();
        h.Storage.Uploads.Should().BeEmpty();
        h.Content(file).Should().Be("alpha");
        h.Content(foreign).Should().Be("alpha");
    }

    [Fact]
    public async Task Edit_refuses_a_binary_file()
    {
        var h = new Harness();
        ProjectFile video = h.SeedBytes("clip.mp4", [0, 1, 2], "video/mp4");

        string result = await h.Call("ProposeEditProjectFile", null, ("fileId", video.Id), ("edits", Edits(("a", "b", false))));

        Error(result).Should().Contain("not a text-editable asset");
    }

    // ------------------------------------------------------------------ insert

    [Theory]
    [InlineData(0, "NEW\none\ntwo\n")]
    [InlineData(1, "one\nNEW\ntwo\n")]
    [InlineData(-1, "one\ntwo\nNEW\n")]
    public async Task Insert_places_the_text_after_the_requested_line(int afterLine, string expected)
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "one\ntwo\n");
        var args = new (string, object?)[] { ("fileId", file.Id), ("afterLine", afterLine), ("text", "NEW") };

        string token = await h.Token("ProposeInsertProjectFileLines", args);
        h.Content(file).Should().Be("one\ntwo\n");
        h.Storage.Uploads.Should().BeEmpty();
        await h.Call("ApplyInsertProjectFileLines", null, [.. args, ("confirmationToken", token)]);

        h.Content(file).Should().Be(expected);
    }

    [Fact]
    public async Task Insert_keeps_crlf_and_an_unterminated_last_line_and_rejects_bad_positions()
    {
        var h = new Harness();
        ProjectFile crlf = h.Seed("a.txt", "a\r\nb\r\n");
        ProjectFile bare = h.Seed("b.txt", "a\nb");

        var a1 = new (string, object?)[] { ("fileId", crlf.Id), ("afterLine", 1), ("text", "X\nY") };
        await h.Call("ApplyInsertProjectFileLines", null, [.. a1, ("confirmationToken", await h.Token("ProposeInsertProjectFileLines", a1))]);
        h.Content(crlf).Should().Be("a\r\nX\r\nY\r\nb\r\n");

        var a2 = new (string, object?)[] { ("fileId", bare.Id), ("afterLine", -1), ("text", "c") };
        await h.Call("ApplyInsertProjectFileLines", null, [.. a2, ("confirmationToken", await h.Token("ProposeInsertProjectFileLines", a2))]);
        h.Content(bare).Should().Be("a\nb\nc");

        int uploads = h.Storage.Uploads.Count;
        string outside = await h.Call("ProposeInsertProjectFileLines", null, ("fileId", bare.Id), ("afterLine", 99), ("text", "z"));
        string empty = await h.Call("ProposeInsertProjectFileLines", null, ("fileId", bare.Id), ("afterLine", 0), ("text", ""));
        IsOk(outside).Should().BeFalse();
        IsOk(empty).Should().BeFalse();
        h.Storage.Uploads.Should().HaveCount(uploads);
    }

    [Fact]
    public async Task Insert_token_binds_the_position_and_text()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "one\ntwo\n");
        string token = await h.Token("ProposeInsertProjectFileLines", ("fileId", file.Id), ("afterLine", 0), ("text", "NEW"));

        string moved = await h.Call("ApplyInsertProjectFileLines", null, ("fileId", file.Id), ("afterLine", 1), ("text", "NEW"), ("confirmationToken", token));
        string changed = await h.Call("ApplyInsertProjectFileLines", null, ("fileId", file.Id), ("afterLine", 0), ("text", "EVIL"), ("confirmationToken", token));

        IsOk(moved).Should().BeFalse();
        IsOk(changed).Should().BeFalse();
        h.Storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Size_cap_is_checked_before_each_replace_in_propose_and_apply_and_allows_the_exact_cap()
    {
        var h = new Harness();
        ProjectFile big = h.Seed("big.txt", new string('a', 600_000));
        ProjectFile chain = h.Seed("chain.txt", new string('a', 600_000));
        ProjectFile edge = h.Seed("edge.txt", new string('a', AssistantFileTools.MaxContentBytes / 2 + 1));
        JsonElement grow = Edits(("a", "bb", true));

        string propose = await h.Call("ProposeEditProjectFile", null, ("fileId", big.Id), ("edits", grow));
        string apply = await h.Call("ApplyEditProjectFile", null, ("fileId", big.Id), ("edits", grow), ("confirmationToken", "x"));
        string later = await h.Call("ProposeEditProjectFile", null, ("fileId", chain.Id),
            ("edits", Edits(("a", "b", true), ("b", "cc", true))));

        foreach (string r in new[] { propose, apply, later })
        {
            IsOk(r).Should().BeFalse();
            Error(r).Should().Contain("size limit");
        }

        Error(later).Should().Contain("edit 2");
        h.Storage.Uploads.Should().BeEmpty();
        IsOk(await h.Call("ProposeEditProjectFile", null, ("fileId", edge.Id), ("edits", grow))).Should().BeFalse("1 byte over");
        ProjectFile exact = h.Seed("exact.txt", new string('a', AssistantFileTools.MaxContentBytes / 2));
        (await h.Call("ProposeEditProjectFile", null, ("fileId", exact.Id), ("edits", grow))).Should().Contain("confirmationToken", "exactly at the cap passes");
    }

    [Fact]
    public async Task Card_never_hides_a_change_inside_a_long_line()
    {
        var h = new Harness();
        string line = new string('x', 3000) + "OLDVALUE" + new string('y', 1992);
        string tail = new string('z', 4990) + "OLDTAIL";
        ProjectFile file = h.Seed("long.txt", line + "\n" + tail + "\n");

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync(
            "ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("OLDVALUE", "NEWVALUE", false), ("OLDTAIL", "NEWTAIL", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        IReadOnlyList<AssistantActionChange> changes = verdict.Card!.Changes!;
        foreach (string needle in new[] { "OLDVALUE", "NEWVALUE", "OLDTAIL", "NEWTAIL" })
            changes.Should().Contain(c => c.Text.Contains(needle, StringComparison.Ordinal), needle + " must be visible");
        changes.Should().Contain(c => c.Kind == AssistantChangeKind.Remove && c.Text.Contains("change at char 3001", StringComparison.Ordinal));
        changes.Where(c => c.Kind != AssistantChangeKind.Info).Should().OnlyContain(c => c.Text.Contains("⟦+", StringComparison.Ordinal));
    }

    [Fact]
    public void Replace_all_over_the_cap_is_refused_without_allocating_the_result()
    {
        string original = new('a', 900_000);
        var edit = new ReelBolt.Shared.Text.SandboxEdit("a", new string('b', 60_000), true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        ReelBolt.Shared.Text.SandboxEditOutcome outcome = ReelBolt.Shared.Text.SandboxTextEditor.Apply(original, [edit], AssistantFileTools.MaxContentBytes);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        outcome.Ok.Should().BeFalse();
        outcome.Error.Should().Contain("size limit");
        allocated.Should().BeLessThan(2_000_000, "the projected length is checked before any replacement is built");
    }

    [Fact]
    public async Task Reads_cover_the_whole_file_up_to_one_mib_and_flag_a_larger_one()
    {
        var h = new Harness();
        string text = string.Join('\n', Enumerable.Range(1, 30_000).Select(i => $"line {i:D6}"));
        ProjectFile file = h.Seed("long.txt", text);
        ProjectFile huge = h.Seed("huge.txt", new string('x', AssistantFileTools.MaxContentBytes + 10));

        JsonElement r = Json(await h.Call("ReadProjectFileLines", null, ("fileId", file.Id), ("startLine", 29_990), ("lineCount", 5)));
        r.GetProperty("totalLines").GetInt32().Should().Be(30_000);
        r.GetProperty("content").GetString().Should().Contain("line 029990");

        JsonElement big = Json(await h.Call("ReadProjectFileLines", null, ("fileId", huge.Id)));
        big.GetProperty("fileTooLarge").GetBoolean().Should().BeTrue();
        big.GetProperty("note").GetString().Should().Contain("larger than");
        Error(await h.Call("GetProjectFileOutline", null, ("fileId", huge.Id))).Should().Contain("larger than");
    }

    [Fact]
    public async Task Path_lookup_prefers_one_exact_path_and_refuses_an_ambiguous_bare_name()
    {
        var h = new Harness();
        h.Seed("lib/a.ts", "lib");
        h.Seed("src/a.ts", "src");
        h.Seed("only/b.ts", "b");

        string ambiguous = await h.Call("ReadProjectFileLines", null, ("path", "a.ts"));
        IsOk(ambiguous).Should().BeFalse();
        Error(ambiguous).Should().Contain("full path").And.Contain("lib/a.ts").And.Contain("src/a.ts");
        Json(await h.Call("ReadProjectFileLines", null, ("path", "src/a.ts"))).GetProperty("content").GetString().Should().Contain("src");
        Json(await h.Call("ReadProjectFileLines", null, ("path", "b.ts"))).GetProperty("content").GetString().Should().Contain("b");
        h.Storage.Uploads.Should().BeEmpty();
    }

    [Fact]
    public async Task Insert_card_reads_afterLine_the_way_the_tool_does_and_total_rows_are_capped()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "one\ntwo\n");
        ApprovalVerdict asString = await CardBuilder(h).BuildAsync("ApplyInsertProjectFileLines",
            Args(("fileId", file.Id), ("afterLine", "1"), ("text", "X"), ("confirmationToken", "t")), h.Context, null, CancellationToken.None);
        asString.Card.Should().NotBeNull();

        string many = string.Join("\n", Enumerable.Range(0, 200).Select(i => i % 5 == 0 ? $"change{i}" : $"keep{i}"));
        ProjectFile spread = h.Seed("b.txt", many);
        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", spread.Id), ("edits", Edits(("change", "CHANGED", true))), ("confirmationToken", "t")), h.Context, null, CancellationToken.None);
        (verdict.Card?.Changes?.Count ?? 0).Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Card_shows_every_change_in_a_long_line_or_refuses_instead_of_truncating()
    {
        var h = new Harness();
        string near = new string('x', 1000) + "AAA" + new string('y', 200) + "BBB" + new string('z', 1000);
        string far = new string('x', 1000) + "AAA" + new string('y', 2000) + "BBB" + new string('z', 1000);
        ProjectFile nearFile = h.Seed("near.txt", near);
        ProjectFile farFile = h.Seed("far.txt", far);
        ProjectFile onlyFile = h.Seed("only.txt", "short\n" + new string('q', 1000) + "\n");

        ApprovalVerdict shown = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", nearFile.Id), ("edits", Edits(("AAA", "111", false), ("BBB", "222", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        string added = string.Join('\n', shown.Card!.Changes!.Where(c => c.Kind == AssistantChangeKind.Add).Select(c => c.Text));
        added.Should().Contain("111").And.Contain("222", "both changes in the line are inside the shown span");

        ApprovalVerdict tooWide = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", farFile.Id), ("edits", Edits(("AAA", "111", false), ("BBB", "222", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        tooWide.Card.Should().BeNull();
        tooWide.Refusal.Should().Contain("split it into smaller edits");

        ApprovalVerdict unpaired = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", onlyFile.Id), ("edits", Edits((new string('q', 1000) + "\n", "", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        // A long line with no counterpart is shown whole, in consecutive rows: nothing past the first 200 characters is hidden.
        unpaired.Card.Should().NotBeNull();
        string[] rows = unpaired.Card!.Changes!.Where(c => c.Kind == AssistantChangeKind.Remove).Select(c => c.Text).ToArray();
        rows.Sum(r => r.Count(ch => ch == 'q')).Should().Be(1000);
        rows.Take(rows.Length - 1).Should().OnlyContain(r => r.EndsWith("⟦line continues⟧", StringComparison.Ordinal));
        rows.Skip(1).Should().OnlyContain(r => r.StartsWith("⟦continued⟧", StringComparison.Ordinal));
        rows[^1].Should().NotContain("⟦line continues⟧");
        rows[0].Should().NotStartWith("⟦continued⟧");

        ProjectFile hugeLine = h.Seed("huge-line.txt", "short\n" + new string('r', 20_000) + "\n");
        ApprovalVerdict unreviewable = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", hugeLine.Id), ("edits", Edits((new string('r', 20_000) + "\n", "", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        unreviewable.Card.Should().BeNull("a line that cannot be shown whole inside the row budget is refused, never cut");
        unreviewable.Refusal.Should().Contain("split it into smaller edits");
    }

    [Fact]
    public void Escape_uses_unicode_categories_walks_code_points_and_escapes_the_backslash()
    {
        string bidi = AssistantApprovalCardBuilder.Escape("a\u202Eb\u2066c\u061Cd\u200Be\u2028f\u2029g\uE000h\u00ADi\u00A0j\u0007k");
        bidi.Should().Be("a\\u202Eb\\u2066c\\u061Cd\\u200Be\\u2028f\\u2029g\\uE000h\\u00ADi\\u00A0j\\u0007k");

        // Format and private-use characters above U+FFFF (tag characters, plane 15), written as one code point.
        AssistantApprovalCardBuilder.Escape("x" + char.ConvertFromUtf32(0xE0041) + "y" + char.ConvertFromUtf32(0xF0000))
            .Should().Be("x\\u{E0041}y\\u{F0000}");

        // A real emoji pair is shown as is and never split; an unpaired surrogate is escaped.
        AssistantApprovalCardBuilder.Escape("a\uD83D\uDE00b").Should().Be("a\uD83D\uDE00b");
        AssistantApprovalCardBuilder.Escape("a\uD83Db").Should().Be("a\\uD83Db");
        AssistantApprovalCardBuilder.Escape("a\uDE00b").Should().Be("a\\uDE00b");

        // A literal backslash is doubled, so typed text cannot pose as an escape token; tab and the card's marker brackets too.
        AssistantApprovalCardBuilder.Escape("\\u202E").Should().Be("\\\\u202E");
        AssistantApprovalCardBuilder.Escape("a\tb").Should().Be("a\\tb");
        AssistantApprovalCardBuilder.Escape("⟦continued⟧").Should().Be("\\u27E6continued\\u27E7");
    }

    [Fact]
    public async Task Card_chunks_of_a_long_line_keep_edge_spaces_and_surrogate_pairs_whole()
    {
        var h = new Harness();
        // 199 characters, then a surrogate pair straddling the 200-character cut, spaces at the chunk edges, and a tail.
        string line = new string('a', 199) + "😀" + new string(' ', 5) + new string('b', 150) + "  ";
        ProjectFile file = h.Seed("wide.txt", line + "\nkeep\n");

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits((line + "\n", "", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        string[] rows = verdict.Card!.Changes!.Where(c => c.Kind == AssistantChangeKind.Remove).Select(c => c.Text).ToArray();
        rows.Length.Should().BeGreaterThan(1);
        rows.Should().OnlyContain(r => !r.Contains('\uD83D') || r.Contains("😀"), "a surrogate pair is never split between rows");
        rows.Should().OnlyContain(r => !r.Contains("\\uD83D") && !r.Contains("\\uDE00"));
        string joined = string.Concat(rows).Replace("⟦line continues⟧", "").Replace("⟦continued⟧", "");
        joined.Should().Be(new string('a', 199) + "😀" + new string(' ', 5) + new string('b', 150) + "\\s\\s");
        // The cut moved one character left to keep the pair whole, so the second row starts with the emoji, not a space.
        rows[^1].Should().EndWith("\\s\\s");
    }

    [Theory]
    [InlineData(100)]  // exactly one row of astral characters (200 UTF-16 units)
    [InlineData(101)]  // one pair over the row size
    [InlineData(1000)] // many rows
    public async Task Card_chunks_a_line_of_only_astral_characters_and_terminates(int pairs)
    {
        var h = new Harness();
        string line = string.Concat(Enumerable.Repeat("😀", pairs));
        ProjectFile file = h.Seed("astral.txt", line + "\nkeep\n");

        Task<ApprovalVerdict> build = Task.Run(() => CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits((line + "\n", "", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None));
        (await Task.WhenAny(build, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(build, "the chunk loop always advances");

        if (pairs * 2 > 200 * 60)
            return;
        string[] rows = (await build).Card!.Changes!.Where(c => c.Kind == AssistantChangeKind.Remove).Select(c => c.Text).ToArray();
        string joined = string.Concat(rows).Replace("⟦line continues⟧", "").Replace("⟦continued⟧", "");
        joined.Should().Be(line, "every astral character is shown whole and in order");
        rows.Should().OnlyContain(r => r.Replace("⟦line continues⟧", "").Replace("⟦continued⟧", "").Length <= 200);
    }

    [Fact]
    public async Task Chunking_copes_with_a_lone_surrogate_at_a_row_edge()
    {
        // Stored files are UTF-8 and cannot hold a lone surrogate, so the chunker is driven directly.
        System.Reflection.MethodInfo addWhole = typeof(AssistantApprovalCardBuilder).GetMethod(
            "AddWhole", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string[] lines =
        [
            new string('a', 199) + "\uD83D" + new string('b', 150) + "\uDE00" + new string('c', 300),
            new string('a', 199) + "\uD83D",
            string.Concat(Enumerable.Repeat("\uD83D", 450)),
            string.Concat(Enumerable.Repeat("\uDE00", 450)),
            "\uD83D" + string.Concat(Enumerable.Repeat("😀", 250)),
        ];

        foreach (string line in lines)
        {
            var rows = new List<string>();
            Task run = Task.Run(() => addWhole.Invoke(null, [rows, line]));
            (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(run, "the chunk loop always advances");

            string joined = string.Concat(rows).Replace("⟦line continues⟧", "").Replace("⟦continued⟧", "");
            joined.Should().Be(AssistantApprovalCardBuilder.Escape(line), "every unit is shown, in order");
            rows.Should().OnlyContain(r => r.Replace("⟦line continues⟧", "").Replace("⟦continued⟧", "").Length <= 200 * 8);
            string shown = string.Concat(rows);
            for (int i = 0; i < shown.Length; i++)
            {
                if (char.IsHighSurrogate(shown[i]) && i + 1 < shown.Length && char.IsLowSurrogate(shown[i + 1]))
                    i++; // a whole pair is fine
                else
                    char.IsSurrogate(shown[i]).Should().BeFalse("a lone surrogate is shown as an escape, never raw");
            }
        }
    }

    [Fact]
    public async Task Card_makes_a_whitespace_only_or_line_ending_change_visible()
    {
        var h = new Harness();
        ProjectFile spaces = h.Seed("ws.txt", "alpha\nbeta\n");
        ApprovalVerdict trailing = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", spaces.Id), ("edits", Edits(("alpha", "alpha   ", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        trailing.Card!.Changes!.Should().Contain(c => c.Kind == AssistantChangeKind.Add && c.Text == "alpha\\s\\s\\s");

        ProjectFile lf = h.Seed("eol.txt", "one\r\ntwo\r\n");
        ApprovalVerdict crlfToLf = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", lf.Id), ("edits", Edits(("one\r\n", "one\n", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        crlfToLf.Card!.Changes!.Should().Contain(c => c.Kind == AssistantChangeKind.Info && c.Text.Contains("Line endings change: CRLF → mixed", StringComparison.Ordinal));

        ProjectFile mixed = h.Seed("mixed.txt", "a\r\nb\nc\n");
        ApprovalVerdict swapped = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", mixed.Id), ("edits", Edits(("a\r\nb\n", "a\nb\r\n", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        swapped.Card.Should().BeNull("a change the card cannot show is refused, not shown empty");
        swapped.Refusal.Should().Contain("invisible");

        ProjectFile crlf = h.Seed("crlf.txt", "one\r\ntwo\r\n");
        ApprovalVerdict ordinary = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", crlf.Id), ("edits", Edits(("two", "2", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        ordinary.Card!.Changes!.Where(c => c.Kind != AssistantChangeKind.Info).Should().OnlyContain(c => !c.Text.Contains("u000D"), "a plain CRLF file stays readable");
    }

    [Fact]
    public async Task Outline_survives_a_hostile_line_without_burning_cpu()
    {
        var h = new Harness();
        string spaces = new string(' ', 1900);
        // "# a" + many spaces + "x" makes the heading pattern backtrack; a very long line is skipped outright.
        ProjectFile md = h.Seed("hostile.md", "# Before\n# a" + spaces + "x\n# " + new string(' ', 300_000) + "y\n## After\n");
        ProjectFile ts = h.Seed("hostile.ts", "import a from 'a';\nimport " + new string(' ', 300_000) + "x\nexport const z = 1;\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string mdOutline = await h.Call("GetProjectFileOutline", null, ("fileId", md.Id));
        string tsOutline = await h.Call("GetProjectFileOutline", null, ("fileId", ts.Id));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        mdOutline.Should().Contain("Before").And.Contain("After");
        tsOutline.Should().Contain("z");
    }

    [Fact]
    public async Task Diff_beyond_the_bound_is_one_hunk_and_the_card_refuses_it()
    {
        var h = new Harness();
        string body = string.Join('\n', Enumerable.Range(0, 30_000).Select(i => $"row {i}"));
        ProjectFile file = h.Seed("big.txt", body);

        IReadOnlyList<ReelBolt.Inference.Api.Agents.Tools.DiffHunk> hunks =
            ReelBolt.Inference.Api.Agents.Tools.ProjectFileTargetedEdits.Diff(body, body.Replace("row", "ROW", StringComparison.Ordinal));
        hunks.Should().ContainSingle("past the bound the whole differing middle is one hunk, with no edit script");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("row", "ROW", true))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().Contain("split it into smaller edits");
    }

    [Fact]
    public async Task Outline_of_a_one_mebibyte_hostile_file_costs_time_linear_in_its_size()
    {
        var h = new Harness();
        // About 1 MiB of lines that make a backtracking heading/import pattern cubic: the whole call must stay fast.
        string mdLine = "# a" + new string(' ', 420) + "x";
        ProjectFile md = h.Seed("big-hostile.md", string.Join('\n', Enumerable.Repeat(mdLine, 2400)) + "\n## After\n");
        string tsLine = "import " + new string(' ', 420) + "x";
        ProjectFile ts = h.Seed("big-hostile.ts", string.Join('\n', Enumerable.Repeat(tsLine, 2400)) + "\nexport const z = 1;\n");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string mdOutline = await h.Call("GetProjectFileOutline", null, ("fileId", md.Id));
        string tsOutline = await h.Call("GetProjectFileOutline", null, ("fileId", ts.Id));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "pattern matching is linear, not per-line timeouts added up");
        mdOutline.Should().Contain("\"kind\":\"markdown\"").And.Contain("\"totalLines\":2402"); // the outline lists the first headings and flags the rest as truncated
        tsOutline.Should().Contain("\"totalLines\"");
    }

    [Fact]
    public void Escape_ignores_fff0_range_and_caps_stacked_marks()
    {
        AssistantApprovalCardBuilder.Escape("a\uFFF1b").Should().Be("a\\uFFF1b");
        string stacked = "e" + string.Concat(Enumerable.Repeat("\u0301", 10));
        string shown = AssistantApprovalCardBuilder.Escape(stacked);
        shown.Count(c => c == '\u0301').Should().Be(4, "at most four marks stay on one base");
        shown.Should().Contain("\\u0301");
    }

    [Fact]
    public async Task Card_shows_trailing_spaces_added_to_a_long_line()
    {
        var h = new Harness();
        string line = new string('x', 300);
        ProjectFile file = h.Seed("longws.txt", line + "\nkeep\n");
        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits((line + "\n", line + "   \n", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);
        verdict.Card!.Changes!.Should().Contain(c => c.Kind == AssistantChangeKind.Add && c.Text.EndsWith("\\s\\s\\s", StringComparison.Ordinal));
    }

    [Fact]
    public void Escape_shows_default_ignorable_characters_and_marks_without_a_visible_base()
    {
        foreach (int cp in new[] { 0x034F, 0x115F, 0x1160, 0x17B4, 0x17B5, 0x180B, 0x180F, 0x3164, 0xFE00, 0xFE0F, 0xFFA0, 0x2800, 0x2064, 0xE0100 })
        {
            string shown = AssistantApprovalCardBuilder.Escape("a" + char.ConvertFromUtf32(cp) + "b");
            shown.Should().StartWith("a\\u").And.EndWith("b", $"U+{cp:X4} must not pass as itself");
            shown.Should().NotContain(char.ConvertFromUtf32(cp));
        }

        AssistantApprovalCardBuilder.Escape("\u0301x").Should().Be("\\u0301x", "a mark at the start of a line has no base");
        AssistantApprovalCardBuilder.Escape("a \u0301").Should().Be("a \\u0301", "a mark after a space has no base");
        AssistantApprovalCardBuilder.Escape("a\u200B\u0301").Should().Be("a\\u200B\\u0301", "a mark after an escaped character has no base");
        AssistantApprovalCardBuilder.Escape("\u20DD").Should().Be("\\u20DD", "an enclosing mark with no base");
        AssistantApprovalCardBuilder.Escape("e\u0301").Should().Be("e\u0301", "a mark on a visible letter is ordinary text");
    }

    [Fact]
    public async Task Card_shows_hidden_and_bidirectional_characters_as_visible_escapes()
    {
        var h = new Harness();
        string line = new string('x', 300) + "a‮b​c" + new string('y', 300);
        ProjectFile file = h.Seed("bidi.txt", line + "\nplain\n");

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("a‮b​c", "safe", false), ("plain", "p⁦q", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        string all = string.Join('\n', verdict.Card!.Changes!.Select(c => c.Text));
        all.Should().Contain("\\u202E").And.Contain("\\u200B").And.Contain("\\u2066");
        all.Should().NotContain("‮").And.NotContain("​").And.NotContain("⁦");
    }

    [Fact]
    public async Task Card_is_destructive_and_says_the_file_is_edited_in_place()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "one\ntwo\n");

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("one", "1", false))), ("confirmationToken", "t")), h.Context, null, CancellationToken.None);

        verdict.Card!.Risk.Should().Be(AssistantActionRisk.Destructive);
        verdict.Card.Summary.Should().Contain("in place").And.Contain("can't be undone").And.NotContain("new version");
    }

    [Fact]
    public async Task Card_keeps_every_hunk_visible_and_refuses_when_there_are_too_many_to_review()
    {
        var h = new Harness();
        // Hunk 1 is huge, hunk 2 is a small harmful one behind it.
        string body = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"row{i}"));
        ProjectFile file = h.Seed("a.txt", body + "\nkeep1\nkeep2\nkeep3\nkeep4\nkeep5\nTARGET\n");
        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("row", "ROW", true), ("TARGET", "EVIL", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        IReadOnlyList<AssistantActionChange> changes = verdict.Card!.Changes!;
        changes.Should().Contain(c => c.Kind == AssistantChangeKind.Add && c.Text == "EVIL");
        changes.Should().Contain(c => c.Text.Contains("more changed lines in this change", StringComparison.Ordinal));
        changes.Count(c => c.Kind is AssistantChangeKind.Add or AssistantChangeKind.Remove).Should().BeLessThanOrEqualTo(60);

        // 40 separate one-line changes cannot each get their lines inside the card.
        string many = string.Join("\nsep\n", Enumerable.Range(0, 40).Select(i => $"item{i}"));
        ProjectFile spread = h.Seed("b.txt", many);
        ApprovalVerdict refused = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", spread.Id), ("edits", Edits(("item", "ITEM", true))), ("confirmationToken", "t")), h.Context, null, CancellationToken.None);
        refused.Card.Should().BeNull();
        refused.Refusal.Should().Contain("split it into smaller edits");
    }

    // ------------------------------------------------------------------ approval cards

    private static AssistantApprovalCardBuilder CardBuilder(Harness h) =>
        new(h.Db, h.Access, new FakeCurrentUser(h.Owner), fileStorage: h.Storage);

    private static AIFunctionArguments Args(params (string, object?)[] args)
    {
        var arguments = new AIFunctionArguments();
        foreach ((string key, object? value) in args)
            arguments[key] = value;
        return arguments;
    }

    [Fact]
    public async Task Edit_card_shows_the_path_project_and_the_real_before_and_after_lines()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("scripts/intro.txt", "alpha\nbeta\ngamma\ndelta\nepsilon\n");

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync(
            "ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("gamma", "GAMMA-ONE\nGAMMA-TWO", false))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        ApprovalCard card = verdict.Card!;
        card.Title.Should().Contain("scripts/intro.txt");
        card.ProjectName.Should().Be("Launch Teaser");
        card.Changes!.Should().Contain(c => c.Kind == AssistantChangeKind.Remove && c.Text == "gamma");
        card.Changes.Should().Contain(c => c.Kind == AssistantChangeKind.Add && c.Text == "GAMMA-ONE");
        card.Changes.Should().Contain(c => c.Kind == AssistantChangeKind.Add && c.Text == "GAMMA-TWO");
        card.Changes.Should().Contain(c => c.Text.StartsWith("Line 3", StringComparison.Ordinal));
        card.Changes.Should().Contain(c => c.Kind == AssistantChangeKind.Info && c.Text.Contains("beta", StringComparison.Ordinal), "unchanged context is shown");
        string.Join(' ', card.Changes.Select(c => c.Text)).Should().NotContain(file.Id.ToString());
    }

    [Fact]
    public async Task Insert_card_shows_the_added_lines_and_where()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "one\ntwo\n");

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync(
            "ApplyInsertProjectFileLines",
            Args(("fileId", file.Id), ("afterLine", 1), ("text", "INSERTED"), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        verdict.Card!.Changes.Should().Contain(c => c.Kind == AssistantChangeKind.Add && c.Text == "INSERTED");
        verdict.Card.Changes!.Should().Contain(c => c.Text.StartsWith("At line 2", StringComparison.Ordinal));
        verdict.Card.Title.Should().Contain("a.txt");
    }

    [Fact]
    public async Task Card_bounds_the_diff_and_counts_what_it_hides()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line{i}")));

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync(
            "ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("line", "LINE", true))), ("confirmationToken", "t")),
            h.Context, null, CancellationToken.None);

        IReadOnlyList<AssistantActionChange> changes = verdict.Card!.Changes!;
        changes.Count(c => c.Kind is AssistantChangeKind.Add or AssistantChangeKind.Remove).Should().BeLessThanOrEqualTo(60);
        changes.Last().Text.Should().Contain("more changed lines");
    }

    [Fact]
    public async Task Card_is_unreachable_when_the_edit_no_longer_applies_or_the_file_is_not_the_callers()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("a.txt", "alpha");
        Project theirs = h.SeedProject(Guid.NewGuid(), "Theirs");
        ProjectFile foreign = h.Seed("x.txt", "alpha", projectId: theirs.Id);
        AssistantApprovalCardBuilder builder = CardBuilder(h);

        ApprovalVerdict gone = await builder.BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("missing text", "x", false)))), h.Context, null, CancellationToken.None);
        ApprovalVerdict other = await builder.BuildAsync("ApplyEditProjectFile",
            Args(("fileId", foreign.Id), ("edits", Edits(("alpha", "x", false)))), h.Context, null, CancellationToken.None);
        h.Access.DenyEdit = true;
        ApprovalVerdict viewer = await builder.BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("alpha", "x", false)))), h.Context, null, CancellationToken.None);

        gone.Card.Should().BeNull();
        gone.Refusal.Should().NotBeNull();
        other.Refusal.Should().NotBeNull();
        viewer.Refusal.Should().NotBeNull();
    }

    [Fact]
    public void Both_apply_tools_are_gated_and_in_the_catalog_with_the_file_kind()
    {
        foreach (string tool in new[] { "ApplyEditProjectFile", "ApplyInsertProjectFileLines" })
        {
            AssistantApprovalCatalog.IsGated(tool).Should().BeTrue();
            AssistantApprovalCatalog.Describe(tool).Kind.Should().Be(AssistantActionKind.File);
        }
    }

    [Fact]
    public async Task Card_for_an_edit_of_a_file_that_is_not_served_shows_no_lines_and_is_refused()
    {
        var h = new Harness();
        ProjectFile file = h.Seed("kept.txt", "secret line\nsecond\n");
        file.OutputProducedAt = DateTime.UtcNow.AddDays(-3);
        h.Project.CsamOutputHoldAt = DateTime.UtcNow.AddDays(-1);
        await h.Db.SaveChangesAsync();

        ApprovalVerdict verdict = await CardBuilder(h).BuildAsync("ApplyEditProjectFile",
            Args(("fileId", file.Id), ("edits", Edits(("secret", "other", false))), ("confirmationToken", "t")), h.Context, null, CancellationToken.None);

        verdict.Card.Should().BeNull();
        verdict.Refusal.Should().NotBeNull();
        System.Text.Json.JsonSerializer.Serialize(verdict).Should().NotContain("secret line");
    }
}
