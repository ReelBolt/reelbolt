using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Tests.Agents;

namespace ReelBolt.WorkflowEngine.Tests.AssistantEval;

/// <summary>
/// One goal's world: an in-memory database holding a project built from the goal's <c>project</c>
/// block, the REAL assistant tool provider over it (the registration the product ships), the real
/// per-turn session block and a lexical stand-in for documentation retrieval.
/// </summary>
internal sealed class EvalStubWorld : IDisposable
{
    public required InferenceApiDbContext Db { get; init; }
    public required AssistantToolProvider Provider { get; init; }
    public required AssistantToolContext Context { get; init; }
    public required Guid UserId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string SessionBlock { get; init; }
    public required IReadOnlyDictionary<Guid, string> AgentTypeById { get; init; }
    public required PlatformDocsContextBuilder DocsContext { get; init; }
    public Guid? VoiceId { get; init; }
    public required EvalProject Project { get; init; }

    public string? AgentTypeOf(Guid id) => AgentTypeById.TryGetValue(id, out string? t) ? t : null;

    public void Dispose() => Db.Dispose();

    public static EvalStubWorld Build(EvalGoal goal, IPlatformDocsSearch docs)
    {
        var db = new InferenceApiDbContext(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid userId = Guid.NewGuid();
        Guid orgId = TestTenancy.OrgOf(userId);
        var project = new Project { Id = Guid.NewGuid(), Name = "Launch video", OwnerId = userId, OrganizationId = orgId };
        db.Projects.Add(project);

        // One built-in agent per type, exactly as the seeder gives the template provisioning service.
        var agentTypes = new Dictionary<Guid, string>();
        foreach (AgentType type in Enum.GetValues<AgentType>())
        {
            var agent = new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = type, IsBuiltIn = true };
            db.AgentDefinitions.Add(agent);
            agentTypes[agent.Id] = type.ToString();
        }

        SeedFiles(db, project.Id, goal.Project);
        SeedDesignSystem(db, project.Id, userId, goal.Project.DesignSystem);
        Guid? voiceId = goal.Project.ConsentedVoice ? SeedVoice(db, project.Id, userId) : null;
        db.SaveChanges();

        var user = new FakeCurrentUser(userId);
        var balance = new FixedPlanBalance(goal.Project.Plan);
        AssistantToolProvider provider = BuildProvider(db, user, docs, balance);
        var context = new AssistantToolContext(userId, project.Id);

        string session = AssistantSessionContext.Build(
            new AssistantSessionContext.Caller(userId, "marketer@example.com", "Eval User", false, "Eval workspace", ReelBolt.Shared.Tenancy.OrganizationRole.Member),
            new ReelBolt.Inference.Api.Controllers.Dto.AssistantPageContextDto
            {
                Path = $"/projects/{project.Id}",
                Kind = "project",
                Title = project.Name,
                EntityIds = new Dictionary<string, string?> { ["projectId"] = project.Id.ToString() }
            },
            project.Id, null);

        return new EvalStubWorld
        {
            Db = db,
            Provider = provider,
            Project = goal.Project,
            VoiceId = voiceId,
            Context = context,
            UserId = userId,
            ProjectId = project.Id,
            SessionBlock = session,
            AgentTypeById = agentTypes,
            DocsContext = new PlatformDocsContextBuilder(
                docs, Options.Create(new PlatformDocsOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDocsContextBuilder>.Instance)
        };
    }

    /// <summary>The same registration <c>AssistantToolTestFactory.CreateProvider</c> builds, with a plan-aware credit reader.</summary>
    private static AssistantToolProvider BuildProvider(
        InferenceApiDbContext db, FakeCurrentUser user, IPlatformDocsSearch docs, ICreditBalanceReader balance)
    {
        var tokens = ConfirmationTokenService.CreateEphemeral();
        var storage = new FakeFileStorage();
        var access = TestTenancy.Access(db, user);
        var p0 = AssistantToolTestFactory.P0Tools(db, user, tokens);
        var p2 = AssistantToolTestFactory.P2Tools(db, user, tokens, storage);
        var runs = new AssistantRunTools(db, access, user, balance);
        var executionService = new Mock<IWorkflowExecutionService>();
        return new AssistantToolProvider(
            db,
            new WorkflowEditDiffService(db),
            executionService.Object,
            AllowingGuard(),
            user,
            new AssistantProjectTools(db, tokens, AssistantToolTestFactory.TemplateProvisioning(db), user, access),
            new AssistantFileTools(db, tokens, storage, Mock.Of<IPublishEndpoint>(), new RecordingSummarizationQueue(), access),
            new AssistantWorkflowTools(db, tokens, access, user),
            new AssistantAdminTools(db, tokens, user, new FakeSecretProtector()),
            new AssistantKnowledgeTools(db, docs, AssistantToolTestFactory.TemplateProvisioning(db)),
            new AssistantStyleTools(db, tokens, access),
            AssistantToolTestFactory.DesignSystemTools(db, user, tokens, storage),
            AssistantToolTestFactory.PictureTools(db, user, tokens, storage),
            p0.Outputs, p0.Uploads, runs, p0.Revisions, p0.ChangeRequests,
            p2.Patch, p2.PictureDocuments, p2.GreenScreen,
            access,
            null,
            null,
            new WorkflowExecutionPreflight(db, null),
            designBuildGate: null,
            providerResolver: null,
            briefFiles: null);
    }

    private static IAssistantExecutionGuard AllowingGuard()
    {
        var guard = new Mock<IAssistantExecutionGuard>();
        guard.Setup(g => g.TryAcquireAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Func<Task<RunRateBucket>>>(), It.IsAny<string>(), It.IsAny<Func<Task<Guid?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RunRateDecision(true, RunRateBucket.Paid, 10, 0, 3600, 720));
        return guard.Object;
    }

    private static void SeedFiles(InferenceApiDbContext db, Guid projectId, EvalProject p)
    {
        void Add(string name, string mime, long size, string summary)
        {
            db.ProjectFiles.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                OriginalFileName = name,
                OriginalPath = name,
                StorageKey = $"projects/{projectId}/userFiles/{name}",
                StorageBucket = "eval",
                MimeType = mime,
                SizeBytes = size,
                UploadedAt = DateTime.UtcNow,
                AgentSummary = summary,
                SummaryStatus = SummaryStatus.Done
            });
        }

        switch (p.Code)
        {
            case "blazor":
                Add("src/Pages/Expenses.razor", "text/plain", 4200, "Blazor page listing expense reports with filters and an approve button.");
                Add("src/Pages/NewExpense.razor", "text/plain", 3100, "Blazor form to submit a new expense with receipt upload.");
                Add("README.md", "text/markdown", 900, "Readme of the expenses portal.");
                break;
            case "react":
                Add("src/App.tsx", "text/plain", 3800, "React app shell with routing for the dashboard.");
                Add("src/components/Dashboard.tsx", "text/plain", 5200, "React dashboard with charts and filters.");
                Add("README.md", "text/markdown", 900, "Readme of the app.");
                break;
            case "other":
                Add("main.py", "text/x-python", 2800, "Python service entry point.");
                Add("README.md", "text/markdown", 900, "Readme of the project.");
                break;
        }

        for (int i = 1; i <= p.VideoClips; i++)
            Add($"clip-{i:00}.mp4", "video/mp4", 25_000_000, $"Screen or camera footage, clip {i}, about 40 seconds.");
        for (int i = 1; i <= p.Images; i++)
            Add($"photo-{i:00}.jpg", "image/jpeg", 2_400_000, $"A photo, number {i}.");
        if (p.MusicTrack) Add("music-track.mp3", "audio/mpeg", 4_000_000, "A calm instrumental music track, about 2 minutes.");
        if (p.SfxFiles) Add("sfx-whoosh.wav", "audio/wav", 300_000, "A short whoosh sound effect.");
    }

    private static void SeedDesignSystem(InferenceApiDbContext db, Guid projectId, Guid userId, string status)
    {
        if (status is not ("draft" or "approved")) return;
        Guid versionId = Guid.NewGuid();
        var system = new DesignSystem
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Kind = DesignSystemKinds.Design,
            Name = "Brand",
            CurrentVersionId = status == "approved" ? versionId : null,
            CreatedByUserId = userId
        };
        db.DesignSystems.Add(system);
        db.DesignSystemVersions.Add(new DesignSystemVersion
        {
            Id = versionId,
            DesignSystemId = system.Id,
            VersionNumber = 1,
            State = status == "approved" ? DesignVersionState.Approved : DesignVersionState.Draft,
            ApprovedAt = status == "approved" ? DateTime.UtcNow : null,
            DocumentJson = """{"colors":{"primary":"#1F4FD8"},"fonts":{"heading":"Inter"}}""",
            DocumentDigest = new string('a', 64),
            Origin = DesignVersionOrigin.Manual,
            CreatedByUserId = userId
        });
    }

    private static Guid SeedVoice(InferenceApiDbContext db, Guid projectId, Guid userId)
    {
        Guid voiceId = Guid.NewGuid();
        db.ProjectVoices.Add(new ProjectVoice
        {
            Id = voiceId,
            ProjectId = projectId,
            Name = "Presenter voice",
            Source = ProjectVoiceSource.Cloned,
            RemoteVoiceId = "remote-eval",
            ConsentAttestedByUserId = userId,
            ConsentAttestedAt = DateTime.UtcNow,
            ConsentStatement = "I consent.",
            ConsentSubjectName = "Eval User",
            CreatedByUserId = userId
        });
        return voiceId;
    }

    private sealed class FixedPlanBalance(string plan) : ICreditBalanceReader
    {
        public Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct) =>
            Task.FromResult<CreditBalanceSnapshot?>(new CreditBalanceSnapshot(500_000, 0, plan, DateTime.UtcNow.AddDays(20)));
    }
}

/// <summary>
/// Lexical (BM25) retrieval over the shipped documentation corpus: the offline stand-in for the
/// production hybrid index (dense vectors + BM25 + rerank), which needs an embedding provider and a
/// vector store. It feeds both the per-turn documentation block and the SearchPlatformDocs tool.
/// </summary>
internal sealed class Bm25DocsSearch : IPlatformDocsSearch
{
    private readonly IReadOnlyList<PlatformDocsChunk> _chunks;
    private readonly List<Dictionary<string, int>> _tf;
    private readonly Dictionary<string, int> _df = new();
    private readonly double _avgLen;
    private readonly int[] _len;

    public Bm25DocsSearch(string docsRoot)
    {
        _chunks = PlatformDocsCorpus.Load(docsRoot, new PlatformDocsOptions().MaxChunkChars).Chunks;
        _tf = _chunks.Select(c => Count(Tokens(c.EmbeddingText))).ToList();
        _len = _tf.Select(d => d.Values.Sum()).ToArray();
        _avgLen = _len.Length == 0 ? 1 : _len.Average();
        foreach (Dictionary<string, int> d in _tf)
            foreach (string term in d.Keys) _df[term] = _df.GetValueOrDefault(term) + 1;
    }

    public int ChunkCount => _chunks.Count;

    public Task<PlatformDocsSearchResult> SearchAsync(string query, int limit, CancellationToken ct)
    {
        string[] q = Tokens(query).Distinct().ToArray();
        var scored = new List<(int Index, double Score)>();
        for (int i = 0; i < _chunks.Count; i++)
        {
            double s = 0;
            foreach (string term in q)
            {
                if (!_tf[i].TryGetValue(term, out int f)) continue;
                double idf = Math.Log(1 + (_chunks.Count - _df[term] + 0.5) / (_df[term] + 0.5));
                s += idf * (f * 2.2) / (f + 1.2 * (0.25 + 0.75 * _len[i] / _avgLen));
            }
            if (s > 0) scored.Add((i, s));
        }

        double top = scored.Count == 0 ? 1 : scored.Max(x => x.Score);
        var hits = scored.OrderByDescending(x => x.Score).Take(Math.Max(1, limit)).Select(x =>
        {
            PlatformDocsChunk c = _chunks[x.Index];
            float score = (float)(x.Score / top);
            return new PlatformDocsHit(c.Title, c.Section, c.Url, c.Audience, c.Text, score)
            {
                ChunkId = c.Id,
                DocPath = c.DocPath,
                Meta = c.Meta,
                FusedScore = score
            };
        }).ToList();
        return Task.FromResult(new PlatformDocsSearchResult(PlatformDocsSearchStatus.Ok, hits));
    }

    private static IEnumerable<string> Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value);

    private static Dictionary<string, int> Count(IEnumerable<string> tokens)
    {
        var d = new Dictionary<string, int>();
        foreach (string t in tokens) d[t] = d.GetValueOrDefault(t) + 1;
        return d;
    }

    /// <summary>The docs folder of the repository that holds the test output.</summary>
    public static string LocateDocsRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "docs", "user-guide");
            if (Directory.Exists(candidate)) return Path.Combine(d.FullName, "docs");
        }
        throw new DirectoryNotFoundException("docs/ not found above " + AppContext.BaseDirectory);
    }
}
