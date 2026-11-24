using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Tests.Agents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// T-202: the brief a run starts from may be a text project file of the SAME project, resolved to the run's
/// userRequest by one small service shared by the REST execute, the assistant twin and the ExecuteWorkflow tool.
/// </summary>
public class BriefFileResolverTests : IDisposable
{
    private readonly InferenceApiDbContext _db = new(new DbContextOptionsBuilder<InferenceApiDbContext>()
        .UseInMemoryDatabase("brief-" + Guid.NewGuid().ToString("N")).Options);

    private readonly FakeFileStorage _storage = new();
    private readonly Guid _project = Guid.NewGuid();
    private readonly Guid _otherProject = Guid.NewGuid();

    private BriefFileResolver Resolver() => new(_db, _storage);

    private Guid Seed(Guid projectId, string name, string mime, byte[] content, long? declaredSize = null)
    {
        if (!_db.Projects.Any(p => p.Id == projectId))
        {
            Guid owner = Guid.NewGuid();
            _db.Projects.Add(new Project { Id = projectId, Name = "P", OwnerId = owner, OrganizationId = TestTenancy.OrgOf(owner), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        }

        Guid id = Guid.NewGuid();
        string key = $"projects/{projectId}/userFiles/{id}";
        _storage.Objects[key] = content;
        _db.ProjectFiles.Add(new ProjectFile
        {
            Id = id,
            ProjectId = projectId,
            OriginalFileName = name,
            MimeType = mime,
            StorageKey = key,
            SizeBytes = declaredSize ?? content.Length,
        });
        _db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task A_text_file_of_the_same_project_becomes_the_brief()
    {
        Guid file = Seed(_project, "brief.md", "text/markdown", Encoding.UTF8.GetBytes("Launch film for Acme."));

        BriefResolution result = await Resolver().ResolveAsync(_project, file, null, CancellationToken.None);

        result.Error.Should().BeNull();
        result.Text.Should().Be("Launch film for Acme.");
        result.FileName.Should().Be("brief.md");
    }

    [Fact]
    public async Task No_file_id_passes_the_request_through_untouched()
    {
        BriefResolution result = await Resolver().ResolveAsync(_project, null, "just this", CancellationToken.None);

        result.Error.Should().BeNull();
        result.Text.Should().Be("just this");
    }

    [Fact]
    public async Task A_file_of_another_project_and_a_missing_file_get_the_same_single_refusal()
    {
        Guid foreign = Seed(_otherProject, "secret.txt", "text/plain", Encoding.UTF8.GetBytes("not yours"));

        BriefResolution foreignResult = await Resolver().ResolveAsync(_project, foreign, null, CancellationToken.None);
        BriefResolution missingResult = await Resolver().ResolveAsync(_project, Guid.NewGuid(), null, CancellationToken.None);

        foreignResult.Text.Should().BeNull();
        foreignResult.Error.Should().NotBeNullOrWhiteSpace();
        missingResult.Error.Should().Be(foreignResult.Error);
    }

    [Fact]
    public async Task A_file_over_the_cap_is_refused_naming_the_cap()
    {
        Guid file = Seed(_project, "big.txt", "text/plain", new byte[BriefFileResolver.MaxBytes + 1]);

        BriefResolution result = await Resolver().ResolveAsync(_project, file, null, CancellationToken.None);

        result.Text.Should().BeNull();
        result.Error.Should().Contain("64 KiB");
    }

    [Fact]
    public async Task A_file_whose_stored_body_is_over_the_cap_is_refused_even_if_its_recorded_size_is_small()
    {
        Guid file = Seed(_project, "liar.txt", "text/plain", new byte[BriefFileResolver.MaxBytes + 10], declaredSize: 10);

        BriefResolution result = await Resolver().ResolveAsync(_project, file, null, CancellationToken.None);

        result.Error.Should().Contain("64 KiB");
    }

    [Fact]
    public async Task A_binary_file_is_refused()
    {
        Guid file = Seed(_project, "logo.png", "image/png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        BriefResolution result = await Resolver().ResolveAsync(_project, file, null, CancellationToken.None);

        result.Text.Should().BeNull();
        result.Error.Should().Contain("text");
    }

    [Fact]
    public async Task A_request_next_to_a_file_is_appended_under_a_notes_line()
    {
        Guid file = Seed(_project, "brief.txt", "text/plain", Encoding.UTF8.GetBytes("The brief."));

        BriefResolution result = await Resolver().ResolveAsync(_project, file, "Make it punchy.", CancellationToken.None);

        result.Text.Should().Be("The brief.\n\nNotes for this run:\nMake it punchy.");
    }

    public void Dispose() => _db.Dispose();
}
