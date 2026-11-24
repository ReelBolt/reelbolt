#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Agents;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Metering;

/// <summary>
/// The background file summarizer has no caller: its model call is attributed to the file's project and
/// organization under <see cref="UsageSource.FileSummary"/>, with no user.
/// </summary>
public class FileSummaryAttributionTests
{
    [Fact]
    public async Task A_file_summary_is_metered_as_FileSummary_for_the_files_org_and_project_with_no_user()
    {
        string dbName = Guid.NewGuid().ToString();
        DbContextOptions<InferenceApiDbContext> dbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        Guid orgId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = orgId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "notes.txt",
            StorageKey = "k",
            StorageBucket = "b",
            Category = "userFiles",
            MimeType = "text/plain",
            SizeBytes = 2,
            SummaryStatus = SummaryStatus.Pending,
            UploadedAt = DateTime.UtcNow
        };
        using (InferenceApiDbContext seed = new(dbOptions))
        {
            seed.Projects.Add(project);
            seed.ProjectFiles.Add(file);
            seed.SaveChanges();
        }

        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance,
            metering: Options.Create(new MeteringOptions { FailOnUnattributed = true }));
        IChatClient metered = new MeteringChatClient(
            new UsageReportingChatClient(300, 40),
            new ResolvedInferenceProvider(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30),
            recorder);

        Mock<IReelBoltAgent> summarizer = new();
        summarizer
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns<string, Guid?, CancellationToken>(async (prompt, _, ct) =>
            {
                ChatResponse response = await metered.GetResponseAsync(prompt, cancellationToken: ct);
                return new AgentRunResult { Output = response.Text };
            });
        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(AgentType.FileSummarizerAgent)).Returns(summarizer.Object);
        Mock<IFileStorageService> storage = new();
        storage
            .Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Encoding.UTF8.GetBytes("hello")));

        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(dbOptions));
        services.AddSingleton(registry.Object);
        services.AddSingleton(storage.Object);
        FileSummarizationService service = new(
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FileSummarizationService>.Instance);

        await service.ProcessAsync(new FileSummarizationTask(file.Id), CancellationToken.None);

        using (InferenceApiDbContext check = new(dbOptions))
            check.ProjectFiles.Single().SummaryStatus.Should().Be(SummaryStatus.Done);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().OnlyContain(e =>
            e.Source == UsageSource.FileSummary
            && e.OrganizationId == orgId
            && e.ProjectId == project.Id
            && e.UserId == null
            && e.ExecutionId == null
            && e.StepResultId == null);
    }

    [Fact]
    public async Task An_image_description_is_metered_as_FileSummary_for_the_files_org_and_project_with_no_user()
    {
        // A picture's summary is its vision description (PR #299): that model call is part of the file summary
        // too, so it must be attributed exactly like the text summarizer's, never Unattributed.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01];
        string dbName = Guid.NewGuid().ToString();
        DbContextOptions<InferenceApiDbContext> dbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        Guid orgId = Guid.NewGuid();
        Project project = new() { Id = Guid.NewGuid(), Name = "P", OwnerId = Guid.NewGuid(), OrganizationId = orgId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OriginalFileName = "logo.png",
            StorageKey = "k",
            StorageBucket = "b",
            Category = "userFiles",
            MimeType = "image/png",
            SizeBytes = png.Length,
            SummaryStatus = SummaryStatus.Pending,
            UploadedAt = DateTime.UtcNow
        };
        using (InferenceApiDbContext seed = new(dbOptions))
        {
            seed.Projects.Add(project);
            seed.ProjectFiles.Add(file);
            seed.SaveChanges();
        }

        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance,
            metering: Options.Create(new MeteringOptions { FailOnUnattributed = true }));
        ResolvedInferenceProvider vision =
            new(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-vision", "", 30);
        IChatClient metered = new MeteringChatClient(new UsageReportingChatClient(500, 60), vision, recorder);

        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vision);
        Mock<IChatClientFactory> chatClients = new();
        chatClients.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(metered);
        Mock<IFileStorageService> storage = new();
        storage
            .Setup(s => s.DownloadAsync(project.Id, "k", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(png));

        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(dbOptions));
        services.AddSingleton(Mock.Of<IAgentRegistry>());
        services.AddSingleton(storage.Object);
        services.AddSingleton(resolver.Object);
        services.AddSingleton(chatClients.Object);
        FileSummarizationService service = new(
            Mock.Of<IBackgroundTaskQueue<FileSummarizationTask>>(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FileSummarizationService>.Instance);

        await service.ProcessAsync(new FileSummarizationTask(file.Id), CancellationToken.None);

        using (InferenceApiDbContext check = new(dbOptions))
            check.ProjectFiles.Single().SummaryStatus.Should().Be(SummaryStatus.Done);
        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        sink.Events.Should().NotBeEmpty();
        sink.Events.Should().OnlyContain(e =>
            e.Source == UsageSource.FileSummary
            && e.OrganizationId == orgId
            && e.ProjectId == project.Id
            && e.UserId == null
            && e.ExecutionId == null
            && e.StepResultId == null);
    }

    [Fact]
    public async Task The_background_worker_meters_every_summary_and_description_to_each_files_own_org_and_project()
    {
        // Staging regression (pre-#296): an 85-file upload produced 85 Unattributed usage events with no org or
        // project. This drives the real hosted entry point (ExecuteAsync -> queue/sweep -> worker loop), across two
        // organizations and both model paths (text summarizer, image vision), so a scope set by the wrong file, or
        // missing on the worker thread, shows up per event.
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01];
        DbContextOptions<InferenceApiDbContext> dbOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        Project[] projects =
        [
            new() { Id = Guid.NewGuid(), Name = "A", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Id = Guid.NewGuid(), Name = "B", OwnerId = Guid.NewGuid(), OrganizationId = Guid.NewGuid(), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        ];
        var files = new System.Collections.Generic.List<ProjectFile>();
        for (int i = 0; i < 12; i++)
        {
            bool image = i % 3 == 0;
            files.Add(new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = projects[i % 2].Id,
                OriginalFileName = image ? $"pic{i}.png" : $"notes{i}.txt",
                StorageKey = $"k{i}",
                StorageBucket = "b",
                Category = "userFiles",
                MimeType = image ? "image/png" : "text/plain",
                SizeBytes = image ? png.Length : 5,
                SummaryStatus = SummaryStatus.Pending,
                CsamStatus = CsamScanStatus.Clean,
                UploadedAt = DateTime.UtcNow.AddSeconds(-i)
            });
        }
        using (InferenceApiDbContext seed = new(dbOptions))
        {
            seed.Projects.AddRange(projects);
            seed.ProjectFiles.AddRange(files);
            seed.SaveChanges();
        }

        CapturingSink sink = new();
        UsageRecorder recorder = new(
            new StaticPriceService(), sink, NullLogger<UsageRecorder>.Instance,
            metering: Options.Create(new MeteringOptions { FailOnUnattributed = true }));
        IChatClient text = new MeteringChatClient(
            new UsageReportingChatClient(300, 40),
            new ResolvedInferenceProvider(Guid.NewGuid(), "platform", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-test", "", 30),
            recorder);
        ResolvedInferenceProvider vision =
            new(Guid.NewGuid(), "vision", InferenceProviderKind.OpenAICompatible, "http://x", "gpt-vision", "", 30);
        IChatClient visionClient = new MeteringChatClient(new UsageReportingChatClient(500, 60), vision, recorder);

        Mock<IReelBoltAgent> summarizer = new();
        summarizer
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns<string, Guid?, CancellationToken>(async (prompt, _, ct) =>
            {
                ChatResponse response = await text.GetResponseAsync(prompt, cancellationToken: ct);
                return new AgentRunResult { Output = response.Text };
            });
        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(AgentType.FileSummarizerAgent)).Returns(summarizer.Object);
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vision);
        Mock<IChatClientFactory> chatClients = new();
        chatClients.Setup(f => f.Get(It.IsAny<ResolvedInferenceProvider>())).Returns(visionClient);
        Mock<IFileStorageService> storage = new();
        foreach (ProjectFile f in files)
        {
            byte[] body = f.MimeType == "image/png" ? png : Encoding.UTF8.GetBytes("hello");
            storage
                .Setup(s => s.DownloadAsync(f.ProjectId, f.StorageKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(body));
        }

        ServiceCollection services = new();
        services.AddScoped(_ => new InferenceApiDbContext(dbOptions));
        services.AddSingleton(registry.Object);
        services.AddSingleton(storage.Object);
        services.AddSingleton(resolver.Object);
        services.AddSingleton(chatClients.Object);
        ChannelBackgroundTaskQueue<FileSummarizationTask> queue = new();
        // Half arrive on the upload fast path; the sweeper picks up the rest.
        foreach (ProjectFile f in files.Take(files.Count / 2))
            queue.TryQueue(new FileSummarizationTask(f.Id)).Should().BeTrue();
        FileSummarizationService service = new(
            queue,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<FileSummarizationService>.Instance,
            Options.Create(new FileSummarizationOptions { MaxConcurrency = 3, InitialSweepDelaySeconds = 0, InteractiveYieldSeconds = 0 }));

        await service.StartAsync(CancellationToken.None);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                using InferenceApiDbContext poll = new(dbOptions);
                if (poll.ProjectFiles.All(f => f.SummaryStatus == SummaryStatus.Done || f.SummaryStatus == SummaryStatus.Failed))
                    break;
                DateTime.UtcNow.Should().BeBefore(deadline, "the worker should finish every file");
                await Task.Delay(50);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        // An unattributed model call throws under FailOnUnattributed, which fails that file's summary.
        using (InferenceApiDbContext check = new(dbOptions))
            check.ProjectFiles.Select(f => f.SummaryStatus).ToList().Should().OnlyContain(s => s == SummaryStatus.Done);

        await recorder.StartAsync(CancellationToken.None);
        await recorder.StopAsync(CancellationToken.None);
        // One model call per file; each call records one input-token row (plus its output-token row).
        var calls = sink.Events.Where(e => e.Kind == UsageKind.LlmInputTokens).ToList();
        calls.Should().HaveCount(files.Count);
        foreach (Project p in projects)
        {
            int expected = files.Count(f => f.ProjectId == p.Id);
            calls.Count(e => e.ProjectId == p.Id && e.OrganizationId == p.OrganizationId).Should().Be(expected);
        }
        sink.Events.Should().OnlyContain(e =>
            e.Source == UsageSource.FileSummary
            && e.UserId == null
            && e.ExecutionId == null
            && e.StepResultId == null);
    }
}
