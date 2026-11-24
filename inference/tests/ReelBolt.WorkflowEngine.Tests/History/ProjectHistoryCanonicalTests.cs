using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// The pure half of project versioning: canonical document builders, content identities and the
/// manifest digest. These must be deterministic — the same logical input has to produce the same
/// bytes, so dedupe and "is it unchanged" are decided by content, not by query order.
/// </summary>
public class ProjectHistoryCanonicalTests
{
    [Fact]
    public void Manifest_digest_ignores_the_output_hold_provenance()
    {
        ProjectHistoryManifestItem item = new(
            ProjectVersionItemKind.File,
            Guid.NewGuid().ToString("D"),
            Name: "clips/a.mp4",
            Category: "agentFiles",
            SizeBytes: 10,
            ContentId: "sha256:" + new string('a', 64),
            OutputProducedAt: DateTime.UtcNow,
            PlatformMade: true);

        ProjectHistoryManifestItem withoutStamp = item with { OutputProducedAt = null, PlatformMade = false };

        ProjectHistoryManifestDigest.Compute([item])
            .Should().Be(ProjectHistoryManifestDigest.Compute([withoutStamp]),
                "the digest is the canonical manifest identity, not capture-time provenance");
    }

    [Fact]
    public void Workflow_document_is_deterministic_across_config_key_and_step_order()
    {
        Guid agentId = Guid.NewGuid();
        Guid firstStep = Guid.NewGuid();
        Guid secondStep = Guid.NewGuid();

        WorkflowDefinition forward = Workflow(
            agentId,
            (firstStep, 1, """{"b":2,"a":1}"""),
            (secondStep, 2, """{"z":true,"y":"text"}"""));

        // Same logical document: steps listed in the other order and JSON keys in the other order.
        WorkflowDefinition reversed = Workflow(
            agentId,
            (secondStep, 2, """{"y":"text","z":true}"""),
            (firstStep, 1, """{"a":1,"b":2}"""));

        string a = ProjectHistoryDocuments.Workflow(forward);
        string b = ProjectHistoryDocuments.Workflow(reversed);

        b.Should().Be(a);
        ProjectHistoryDocuments.Sha256(b).Should().Be(ProjectHistoryDocuments.Sha256(a));
    }

    [Fact]
    public void Workflow_document_carries_agent_identity_and_never_result_ids()
    {
        Guid agentId = Guid.NewGuid();
        WorkflowDefinition workflow = Workflow(agentId, (Guid.NewGuid(), 1, """{"a":1}"""));

        string canonical = ProjectHistoryDocuments.Workflow(workflow);

        canonical.Should().Contain(agentId.ToString("D"));
        canonical.Should().Contain("AuthorAgent");
        canonical.Should().Contain("Writer");
        canonical.Should().NotContain("result", "result/execution ids are never captured");
    }

    [Fact]
    public void Timeline_document_canonicalizes_the_embedded_document()
    {
        Guid timelineId = Guid.NewGuid();

        EditTimelineEntity first = Timeline(timelineId, """{"tracks":[],"name":"T","b":2,"a":1}""");
        EditTimelineEntity second = Timeline(timelineId, """{"a":1,"b":2,"name":"T","tracks":[]}""");

        ProjectHistoryDocuments.Timeline(second).Should().Be(ProjectHistoryDocuments.Timeline(first));
    }

    [Fact]
    public void Pronunciation_document_is_sorted_by_term_whatever_the_row_order()
    {
        ProjectPronunciationEntry[] forward =
        [
            new() { Term = "alpha", Spelling = "alfa", CaseSensitive = false },
            new() { Term = "beta", Spelling = "beta", CaseSensitive = true }
        ];

        ProjectPronunciationEntry[] reversed = [forward[1], forward[0]];

        ProjectHistoryDocuments.Pronunciation(reversed)
            .Should().Be(ProjectHistoryDocuments.Pronunciation(forward));
    }

    [Fact]
    public void Project_document_contains_only_name_and_description()
    {
        string canonical = ProjectHistoryDocuments.Project("My project", "A description");

        canonical.Should().Be("""{"description":"A description","name":"My project"}""");
    }

    [Fact]
    public void Manifest_digest_ignores_item_order_but_not_content()
    {
        ProjectHistoryManifestItem[] items =
        [
            new(ProjectVersionItemKind.File, "file-1", Name: "a.txt", ContentId: "sha256:" + new string('a', 64)),
            new(ProjectVersionItemKind.Project, "project", Name: "P", DocumentSha256: new string('b', 64)),
            new(ProjectVersionItemKind.Pronunciation, "pronunciation", DocumentSha256: new string('c', 64))
        ];

        ProjectHistoryManifestItem[] shuffled = [items[2], items[0], items[1]];

        ProjectHistoryManifestDigest.Compute(shuffled)
            .Should().Be(ProjectHistoryManifestDigest.Compute(items));

        ProjectHistoryManifestItem[] changed =
        [
            items[0] with { ContentId = "sha256:" + new string('d', 64) },
            items[1],
            items[2]
        ];

        ProjectHistoryManifestDigest.Compute(changed)
            .Should().NotBe(ProjectHistoryManifestDigest.Compute(items));
    }

    [Theory]
    [InlineData("sha256:" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("etag:" + "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210")]
    [InlineData("unverified:5f0e3b0a-6cbd-4c8a-9d1e-2f3a4b5c6d7e")]
    public void Content_ids_fit_the_column(string contentId)
    {
        contentId.Length.Should().BeLessThanOrEqualTo(ContentId.MaxLength);
    }

    [Fact]
    public void Content_id_uses_a_known_hash_only_with_its_etag()
    {
        string hash = new('a', 64);

        ContentId.FromKnownHash(hash, "\"etag\"").Should().Be("sha256:" + hash);
        ContentId.FromKnownHash(hash, null).Should().BeNull("a hash without its ETag cannot vouch for the bytes");
        ContentId.FromKnownHash("not-a-hash", "\"etag\"").Should().BeNull();
    }

    [Fact]
    public void Content_id_from_etag_is_stable_and_size_sensitive()
    {
        string a = ContentId.FromEtag("\"abc\"", 10);
        string b = ContentId.FromEtag("\"abc\"", 10);
        string c = ContentId.FromEtag("\"abc\"", 11);

        a.Should().Be(b);
        c.Should().NotBe(a);
        ContentId.IsEtag(a).Should().BeTrue();
    }

    private static WorkflowDefinition Workflow(
        Guid agentId,
        params (Guid StepId, int Order, string ConfigJson)[] steps)
    {
        AgentDefinition agent = new()
        {
            Id = agentId,
            Name = "Writer",
            AgentType = AgentType.AuthorAgent
        };

        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "My workflow",
            RequiresUserInput = true,
            Kind = WorkflowDefinitionKind.Standard,
            AppManifestJson = """{"title":"App","v":1}"""
        };

        foreach ((Guid stepId, int order, string config) in steps)
        {
            workflow.Steps.Add(new WorkflowStep
            {
                Id = stepId,
                WorkflowDefinitionId = workflow.Id,
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent,
                StepOrder = order,
                StepType = StepType.Agent,
                VideoGenerateConfigJson = config
            });
        }

        return workflow;
    }

    private static EditTimelineEntity Timeline(Guid id, string documentJson) => new()
    {
        Id = id,
        Name = "Timeline",
        Revision = 3,
        DocumentJson = documentJson
    };
}
