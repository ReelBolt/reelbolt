using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>ListProjectFiles filters, keyset paging, cursor validation and size budget (AssistantFileListing).</summary>
public sealed class AssistantFileListingTests : IDisposable
{
    private static readonly DateTime Epoch = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Guid _projectId = Guid.NewGuid();

    public AssistantFileListingTests()
    {
        // ProjectFile has a required Project; the project-scoped query filters join through it.
        _db.Projects.Add(new Project { Id = _projectId, Name = "Proj", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private ProjectFile Add(string name, string dir = "/", string mime = "text/plain", int minutes = 0, string? summary = null, Guid? project = null)
    {
        ProjectFile f = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project ?? _projectId,
            OriginalFileName = name,
            DirectoryPath = dir,
            OriginalPath = dir.Trim('/').Length == 0 ? name : dir.Trim('/') + "/" + name,
            MimeType = mime,
            SizeBytes = 10,
            AgentSummary = summary,
            StorageKey = "k/" + name,
            StorageBucket = "b",
            UploadedAt = Epoch.AddMinutes(minutes)
        };
        if (project is { } other && !_db.Projects.Any(p => p.Id == other))
            _db.Projects.Add(new Project { Id = other, Name = "Other", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() });
        _db.ProjectFiles.Add(f);
        _db.SaveChanges();
        return f;
    }

    private JsonElement List(string? pathPrefix = null, string? mimePrefix = null, DateTime? since = null,
        string? nameContains = null, int? limit = null, string? cursor = null) =>
        JsonDocument.Parse(AssistantFileListing.List(
            _projectId, _db.ProjectFiles.Where(f => f.ProjectId == _projectId), "Proj",
            pathPrefix, mimePrefix, since, nameContains, limit, cursor)).RootElement.Clone();

    private static string[] Names(JsonElement r) =>
        r.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("originalFileName").GetString()!).ToArray();

    [Fact]
    public void No_arguments_returns_the_envelope_newest_first()
    {
        Add("old.txt", minutes: 1);
        Add("new.txt", minutes: 5);

        JsonElement r = List();

        r.GetProperty("projectName").GetString().Should().Be("Proj");
        r.GetProperty("total").GetInt32().Should().Be(2);
        r.GetProperty("truncatedSummaries").GetBoolean().Should().BeFalse();
        r.TryGetProperty("nextCursor", out _).Should().BeFalse();
        Names(r).Should().Equal("new.txt", "old.txt");
    }

    [Fact]
    public void Row_shape_is_unchanged()
    {
        Add("a.txt");
        JsonElement row = List().GetProperty("files")[0];
        row.EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "originalFileName", "directoryPath", "category", "mimeType", "sizeBytes",
            "agentSummary", "summaryStatus", "indexingStatus", "uploadedAt");
    }

    [Fact]
    public void Path_prefix_filters_on_directory_with_or_without_leading_slash()
    {
        Add("a.md", dir: "docs/specs");
        Add("b.md", dir: "/docs/specs/v2");
        Add("c.md", dir: "src");

        Names(List(pathPrefix: "docs/")).Should().BeEquivalentTo("a.md", "b.md");
        Names(List(pathPrefix: "/docs/specs/v2")).Should().Equal("b.md");
    }

    [Fact]
    public void Mime_prefix_filters()
    {
        Add("a.png", mime: "image/png");
        Add("b.mp4", mime: "video/mp4");
        Names(List(mimePrefix: "image/")).Should().Equal("a.png");
    }

    [Fact]
    public void Since_keeps_only_files_uploaded_at_or_after()
    {
        Add("old.txt", minutes: 0);
        Add("new.txt", minutes: 10);
        Names(List(since: Epoch.AddMinutes(5))).Should().Equal("new.txt");
    }

    [Fact]
    public void Name_contains_is_case_insensitive()
    {
        Add("Storyboard-01.png");
        Add("notes.md");
        Names(List(nameContains: "STORYBOARD")).Should().Equal("Storyboard-01.png");
    }

    [Fact]
    public void Other_projects_files_are_never_listed()
    {
        Add("mine.txt");
        Add("theirs.txt", project: Guid.NewGuid());
        Names(List()).Should().Equal("mine.txt");
    }

    [Fact]
    public void Limit_defaults_to_100_and_is_capped_at_500_and_floored_at_1()
    {
        for (int i = 0; i < 520; i++)
            _db.ProjectFiles.Add(new ProjectFile { Id = Guid.NewGuid(), ProjectId = _projectId, OriginalFileName = $"f{i}", UploadedAt = Epoch.AddSeconds(i) });
        _db.SaveChanges();

        JsonElement d = List();
        Names(d).Length.Should().Be(100);
        d.GetProperty("total").GetInt32().Should().Be(520);
        d.GetProperty("nextCursor").GetString().Should().NotBeNullOrEmpty();
        // The 500 cap is a ceiling; the page byte budget may end a page sooner, always with a cursor.
        JsonElement capped = List(limit: 100000);
        Names(capped).Length.Should().BeInRange(100, 500);
        capped.TryGetProperty("nextCursor", out _).Should().BeTrue();
        Names(List(limit: 0)).Length.Should().Be(1);
    }

    [Fact]
    public void Paging_walks_every_file_once_and_stays_stable_when_newer_files_are_inserted()
    {
        for (int i = 0; i < 25; i++)
            Add($"f{i:00}.txt", minutes: i);

        List<string> seen = [];
        JsonElement page = List(limit: 10);
        seen.AddRange(Names(page));
        // A newer file lands between pages: it must not shift or duplicate the remaining pages.
        Add("inserted.txt", minutes: 999);
        while (page.TryGetProperty("nextCursor", out JsonElement c))
        {
            page = List(limit: 10, cursor: c.GetString());
            seen.AddRange(Names(page));
        }

        seen.Should().HaveCount(25).And.OnlyHaveUniqueItems();
        seen.Should().NotContain("inserted.txt");
        seen.First().Should().Be("f24.txt");
        seen.Last().Should().Be("f00.txt");
    }

    [Fact]
    public void Files_with_identical_timestamps_page_without_loss_or_duplication()
    {
        for (int i = 0; i < 12; i++)
            Add($"same{i:00}.txt", minutes: 0);

        List<string> seen = [];
        JsonElement page = List(limit: 5);
        seen.AddRange(Names(page));
        while (page.TryGetProperty("nextCursor", out JsonElement c))
        {
            page = List(limit: 5, cursor: c.GetString());
            seen.AddRange(Names(page));
        }

        seen.Should().HaveCount(12).And.OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("not-base64url!!")]
    [InlineData("")]
    public void Garbage_cursor_is_refused(string cursor)
    {
        Add("a.txt");
        if (cursor.Length == 0)
        {
            // empty is "no cursor": first page
            Names(List(cursor: cursor)).Should().Equal("a.txt");
            return;
        }
        List(cursor: cursor).GetProperty("error").GetString().Should().Contain("cursor");
    }

    [Fact]
    public void Tampered_cursor_is_refused_and_returns_no_files()
    {
        for (int i = 0; i < 5; i++)
            Add($"f{i}.txt", minutes: i);
        string cursor = List(limit: 2).GetProperty("nextCursor").GetString()!;

        // Flip a character in the payload.
        char[] chars = cursor.ToCharArray();
        chars[2] = chars[2] == 'A' ? 'B' : 'A';
        JsonElement r = List(limit: 2, cursor: new string(chars));

        r.GetProperty("error").GetString().Should().Contain("cursor");
        r.TryGetProperty("files", out _).Should().BeFalse();
    }

    [Fact]
    public void Cursor_replayed_under_different_filters_is_refused()
    {
        for (int i = 0; i < 5; i++)
            Add($"f{i}.txt", minutes: i);
        string cursor = List(limit: 2).GetProperty("nextCursor").GetString()!;

        List(limit: 2, nameContains: "f", cursor: cursor).GetProperty("error").GetString().Should().Contain("cursor");
    }

    [Fact]
    public void Summaries_are_capped_at_300_chars_and_flagged()
    {
        Add("long.txt", summary: new string('x', 1000));
        Add("short.txt", minutes: -1, summary: "tiny");

        JsonElement r = List();

        r.GetProperty("truncatedSummaries").GetBoolean().Should().BeTrue();
        JsonElement[] rows = r.GetProperty("files").EnumerateArray().ToArray();
        rows[0].GetProperty("agentSummary").GetString()!.Length.Should().BeLessThanOrEqualTo(300);
        rows[1].GetProperty("agentSummary").GetString().Should().Be("tiny");
    }

    [Fact]
    public void Default_response_for_600_files_stays_under_60KB()
    {
        for (int i = 0; i < 600; i++)
        {
            _db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = _projectId,
                OriginalFileName = $"scene-{i:000}-establishing-shot-v{i % 7}.png",
                DirectoryPath = "storyboards/act-two/sequence-" + (i % 12),
                OriginalPath = "storyboards/act-two/sequence-" + (i % 12) + "/x.png",
                MimeType = "image/png",
                SizeBytes = 1_234_567,
                AgentSummary = new string('s', 900),
                UploadedAt = Epoch.AddSeconds(i)
            });
        }
        _db.SaveChanges();

        string json = AssistantFileListing.List(_projectId, _db.ProjectFiles.Where(f => f.ProjectId == _projectId), "Proj", null, null, null, null, null, null);

        Encoding.UTF8.GetByteCount(json).Should().BeLessThan(60 * 1024);
    }
}
