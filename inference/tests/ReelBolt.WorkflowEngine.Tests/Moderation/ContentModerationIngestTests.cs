using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Ingest moderation's decision table: a Block hides and moves the object, a Clean keeps it, and
/// everything that could not be classified becomes Error — never Clean. The whole processor is
/// exercised without a background host, over a real (in-memory) <see cref="InferenceApiDbContext"/>.
/// </summary>
public class ContentModerationIngestTests
{
    private static readonly byte[] OnePixelJpeg = [0xFF, 0xD8, 0xFF, 0xD9];

    // -------------------------------------------------------------------------------------------
    // Fakes
    // -------------------------------------------------------------------------------------------

    private sealed class FakeStorage : IFileStorageService
    {
        public byte[] DownloadBytes { get; set; } = [1, 2, 3];
        public string? ETag { get; set; }
        public Exception? DownloadFailure { get; set; }
        public Exception? MoveFailure { get; set; }
        /// <summary>Runs at the start of a download, to simulate a quarantine landing mid-scan.</summary>
        public Func<Task>? BeforeDownload { get; set; }
        public List<(string From, string To)> Moves { get; } = [];
        public int Downloads { get; private set; }

        public string BucketName => "moderation-test-bucket";

        public async Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct)
        {
            Downloads++;
            if (BeforeDownload is not null) await BeforeDownload();
            if (DownloadFailure is not null) throw DownloadFailure;
            return new MemoryStream(DownloadBytes, writable: false);
        }

        public Task MoveAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
        {
            if (MoveFailure is not null) throw MoveFailure;
            Moves.Add((sourceStorageKey, destinationStorageKey));
            return Task.CompletedTask;
        }

        public Task<StoredFileObject> UploadAsync(
            Guid projectId, Guid fileId, Stream content, string fileName, string contentType,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct,
            string category = "userFiles", string? originalPath = null) =>
            throw new NotSupportedException("Ingest moderation never uploads.");

        public Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct) => Task.CompletedTask;

        public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult<StoredObjectMetadata?>(null);

        public Task<int> DeleteManyAsync(Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct) =>
            Task.FromResult(0);

        public Task CopyAsync(Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct) =>
            throw new NotSupportedException("Ingest moderation never copies.");

        public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
            Task.FromResult(ETag);
    }

    private sealed class FakeSampler : IModerationMediaSampler
    {
        public Func<Stream, string, CancellationToken, Task<ModerationMediaSample>> Handler { get; set; } =
            (_, _, _) => Task.FromResult(new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Image, null));

        public int Calls { get; private set; }
        public string? LastMimeType { get; private set; }

        public Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct)
        {
            Calls++;
            LastMimeType = mimeType;
            return Handler(media, mimeType, ct);
        }
    }

    private sealed class FakeGate : IContentModerationGate
    {
        public Func<ModerationInput, ModerationDecision> Handler { get; set; } =
            _ => new ModerationDecision(ModerationAction.Allow, Available: true, ReviewQueued: false, null, string.Empty, "hash");

        public int Calls { get; private set; }
        public ModerationInput? LastInput { get; private set; }
        public ModerationAttribution? LastAttribution { get; private set; }

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Calls++;
            LastInput = input;
            LastAttribution = attribution;
            return Task.FromResult(Handler(input));
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeQueue : IBackgroundTaskQueue<ContentModerationTask>
    {
        public List<ContentModerationTask> Queued { get; } = [];

        public bool TryQueue(ContentModerationTask task)
        {
            Queued.Add(task);
            return true;
        }

        public ValueTask<ContentModerationTask> DequeueAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public void Complete(ContentModerationTask task)
        {
        }

        public bool IsTracked(ContentModerationTask task) => false;
        public int FreeCapacity => 100;
        public int TrackedCount => Queued.Count;
    }

    private sealed class FakeEnqueuer : IContentModerationEnqueuer
    {
        public List<Guid> Calls { get; } = [];

        public bool TryQueue(Guid fileId)
        {
            Calls.Add(fileId);
            return true;
        }
    }

    /// <summary>Records, for each save that writes a moderation verdict, whether the project's marker is in the same save.</summary>
    private sealed class SaveWatcher : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public List<bool> VerdictSavesWithMarker { get; } = [];

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            DbContext context = eventData.Context!;
            bool verdict = context.ChangeTracker.Entries<ProjectFile>().Any(e =>
                e.State == EntityState.Modified && e.Property(f => f.ModerationStatus).IsModified
                && e.Entity.ModerationStatus == FileModerationStatus.Quarantined);
            if (verdict)
            {
                VerdictSavesWithMarker.Add(context.ChangeTracker.Entries<Project>().Any(e =>
                    e.State == EntityState.Modified && e.Entity.ContentQuarantinedAt is not null));
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string mimeType = "image/png", string fileName = "photo.png")
        {
            DatabaseName = $"content-moderation-ingest-{Guid.NewGuid():N}";
            Db = new InferenceApiDbContext(OptionsFor(DatabaseName, Watcher));

            Project = new Project
            {
                Id = Guid.NewGuid(),
                Name = "Moderated",
                OwnerId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid()
            };
            File = new ProjectFile
            {
                Id = Guid.NewGuid(),
                ProjectId = Project.Id,
                OriginalFileName = fileName,
                OriginalPath = fileName,
                StorageKey = $"projects/{Project.Id}/userFiles/{fileName}",
                StorageFileName = fileName,
                StorageBucket = "moderation-test-bucket",
                StoragePrefix = $"projects/{Project.Id}/userFiles/",
                MimeType = mimeType,
                SizeBytes = 3,
                SummaryStatus = SummaryStatus.Pending,
                // These tests exercise the general moderation control; its CSAM gate is settled
                // unless a test sets Pending/Scanning/Quarantined explicitly.
                CsamStatus = CsamScanStatus.NotScanned
            };

            Db.Projects.Add(Project);
            Db.ProjectFiles.Add(File);
            Db.SaveChanges();

            Publisher
                .Setup(p => p.Publish(It.IsAny<ProjectFileIndexingRequested>(), It.IsAny<CancellationToken>()))
                .Callback<ProjectFileIndexingRequested, CancellationToken>((message, _) => Published.Add(message))
                .Returns(Task.CompletedTask);

            // No transcription provider by default; tests that classify audio override this setup
            // (a later Moq setup wins).
            Resolver
                .Setup(r => r.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((ResolvedTranscriptionProvider?)null);
        }

        public SaveWatcher Watcher { get; } = new();
        public string DatabaseName { get; }
        public InferenceApiDbContext Db { get; }
        public Project Project { get; }
        public ProjectFile File { get; }
        public FakeStorage Storage { get; } = new();
        public FakeSampler Sampler { get; } = new();
        public FakeGate Gate { get; } = new();
        public FakeEnqueuer Enqueuer { get; } = new();
        public Mock<IPublishEndpoint> Publisher { get; } = new();
        public List<ProjectFileIndexingRequested> Published { get; } = [];
        public Mock<IInferenceProviderResolver> Resolver { get; } = new();
        public Mock<ITranscriptionClientFactory> TranscriptionFactory { get; } = new();

        public Harness()
            : this("image/png", "photo.png")
        {
        }

        public ContentModerationProcessor Processor(
            ContentModerationOptions? options = null, IOptions<CsamOptions>? csamOptions = null)
        {
            return new ContentModerationProcessor(
                Db,
                Storage,
                Sampler,
                Gate,
                Resolver.Object,
                TranscriptionFactory.Object,
                Publisher.Object,
                Options.Create(options ?? new ContentModerationOptions { Enabled = true }),
                NullLogger<ContentModerationProcessor>.Instance,
                tenant: null,
                enqueuer: Enqueuer,
                csamOptions: csamOptions);
        }

        public InferenceApiDbContext NewContext() => new(OptionsFor(DatabaseName));

        public void Dispose() => Db.Dispose();
    }

    private static DbContextOptions<InferenceApiDbContext> OptionsFor(
        string databaseName, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null) =>
        new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(databaseName)
            .AddInterceptors(interceptor is null ? [] : [interceptor])
            .Options;

    private static void SetupAudioTranscription(
        Mock<IInferenceProviderResolver> resolver,
        Mock<ITranscriptionClientFactory> factory,
        string transcript)
    {
        ResolvedTranscriptionProvider provider = new(
            Guid.NewGuid(), "whisper", InferenceProviderKind.OpenAICompatible,
            "http://asr", "whisper-1", "key", 30);
        resolver
            .Setup(r => r.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);

        Mock<ITranscriptionClient> client = new();
        client
            .Setup(c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TranscriptResult(transcript, [], [], "en"));
        factory.Setup(f => f.Get(provider)).Returns(client.Object);
    }

    // -------------------------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Processor_SkipsACsamHeldFile_BeforeAnyDownload()
    {
        using Harness h = new();
        h.File.CsamStatus = CsamScanStatus.Quarantined;
        h.File.CsamQuarantineKey = $"projects/{h.Project.Id}/quarantine/{h.File.Id}/photo.png";
        await h.Db.SaveChangesAsync();

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Storage.Downloads.Should().Be(0, "a CSAM-quarantined file must never be read by general moderation");
        h.Sampler.Calls.Should().Be(0);
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Processor_WaitsForAPendingCsamVerdict()
    {
        using Harness h = new();
        h.File.CsamStatus = CsamScanStatus.Pending;
        await h.Db.SaveChangesAsync();

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Storage.Downloads.Should().Be(0, "the CSAM verdict must settle before the bytes leave the host");
        h.Gate.Calls.Should().Be(0);
        using InferenceApiDbContext check = h.NewContext();
        check.ProjectFiles.Single().ModerationStatus.Should().Be(FileModerationStatus.Unmoderated,
            "the moderation sweep retries the row after the CSAM verdict");
    }

    [Fact]
    public async Task Processor_DoesNotApplyAGeneralBlockAfterCsamBecomesHeld()
    {
        using Harness h = new();
        h.Gate.Handler = _ =>
        {
            // A CSAM match lands while the general moderation gate is running.
            h.File.CsamStatus = CsamScanStatus.Quarantined;
            h.File.CsamQuarantineKey = $"projects/{h.Project.Id}/quarantine/{h.File.Id}/photo.png";
            h.Db.SaveChanges();
            return new ModerationDecision(
                ModerationAction.Block, Available: true, ReviewQueued: false, "violence", "blocked", "hash");
        };

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Storage.Moves.Should().BeEmpty("a general Block must not move the CSAM evidence object");
        using InferenceApiDbContext check = h.NewContext();
        ProjectFile saved = check.ProjectFiles.Single();
        saved.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        saved.StorageKey.Should().Be($"projects/{h.Project.Id}/userFiles/photo.png",
            "the CSAM quarantine key is untouched by the general control");
    }

    [Fact]
    public async Task Block_moves_the_object_to_quarantine_and_hides_the_file()
    {
        using Harness h = new();
        h.Gate.Handler = _ => new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, "violence", "blocked", "hash");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        string expectedDestination = $"projects/{h.Project.Id}/quarantine/photo.png";

        using (InferenceApiDbContext check = h.NewContext())
        {
            ProjectFile saved = check.ProjectFiles.Single();
            saved.ModerationStatus.Should().Be(FileModerationStatus.Quarantined);
            saved.StorageKey.Should().Be(expectedDestination);
            saved.StoragePrefix.Should().Be($"projects/{h.Project.Id}/quarantine/");
            saved.SummaryStatus.Should().Be(SummaryStatus.Done, "a quarantined file is never summarized");
            saved.IndexingStatus.Should().Be(FileIndexingStatus.NotIndexed);
            saved.IndexingError.Should().Be("Quarantined");
        }

        h.Storage.Moves.Should().ContainSingle()
            .Which.Should().Be(($"projects/{h.Project.Id}/userFiles/photo.png", expectedDestination));
        h.Published.Should().ContainSingle()
            .Which.Operation.Should().Be("Delete");
        h.Gate.LastAttribution!.Surface.Should().Be(ModerationSurface.Ingest);
        h.Gate.LastAttribution.ProjectId.Should().Be(h.Project.Id);
    }

    [Fact]
    public async Task Block_stamps_the_project_marker_in_the_same_save_as_the_verdict()
    {
        using Harness h = new();
        h.Gate.Handler = _ => new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, "violence", "blocked", "hash");
        DateTime before = DateTime.UtcNow.AddSeconds(-1);

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Watcher.VerdictSavesWithMarker.Should().NotBeEmpty().And.OnlyContain(withMarker => withMarker);
        using InferenceApiDbContext check = h.NewContext();
        check.Projects.IgnoreQueryFilters().Single().ContentQuarantinedAt.Should().NotBeNull().And.BeOnOrAfter(before);
        check.Projects.IgnoreQueryFilters().Single().CsamOutputHoldAt.Should().BeNull("a moderation verdict never writes the output marker");
    }

    [Fact]
    public async Task A_clean_file_leaves_the_project_marker_alone()
    {
        using Harness h = new();
        h.Gate.Handler = _ => new ModerationDecision(
            ModerationAction.Allow, Available: true, ReviewQueued: false, null, string.Empty, "hash");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using InferenceApiDbContext check = h.NewContext();
        check.Projects.IgnoreQueryFilters().Single().ContentQuarantinedAt.Should().BeNull();
    }

    [Fact]
    public async Task A_move_failure_keeps_the_file_quarantined()
    {
        using Harness h = new();
        h.Storage.MoveFailure = new IOException("storage unavailable");
        h.Gate.Handler = _ => new ModerationDecision(
            ModerationAction.Block, Available: true, ReviewQueued: false, null, string.Empty, "hash");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using InferenceApiDbContext check = h.NewContext();
        ProjectFile saved = check.ProjectFiles.Single();
        saved.ModerationStatus.Should().Be(FileModerationStatus.Quarantined,
            "the bytes are hidden by the status even when the move failed");
        saved.StorageKey.Should().Be($"projects/{h.Project.Id}/userFiles/photo.png");
    }

    [Fact]
    public async Task Allow_keeps_the_file_clean()
    {
        using Harness h = new();

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Clean);
        h.Storage.Moves.Should().BeEmpty();
        h.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task Review_keeps_the_file_clean_because_the_gate_already_queued_it()
    {
        using Harness h = new();
        h.Gate.Handler = _ => new ModerationDecision(
            ModerationAction.Review, Available: true, ReviewQueued: true, "harassment", "review", "hash");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using InferenceApiDbContext check = h.NewContext();
        check.ProjectFiles.Single().ModerationStatus.Should().Be(FileModerationStatus.Clean);
        h.Storage.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unavailable_gate_is_Error_and_never_Clean()
    {
        using Harness h = new();
        h.Gate.Handler = _ => new ModerationDecision(
            ModerationAction.Allow, Available: false, ReviewQueued: false, null, string.Empty, "hash");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error);
        h.Storage.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_sample_is_Error_and_never_reaches_the_gate()
    {
        using Harness h = new();
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([], null, ModerationContentKind.Image, "image downscale failed"));

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error);
        h.Gate.Calls.Should().Be(0, "there was nothing to classify");
        h.Storage.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task An_audio_only_file_with_no_transcript_is_Error()
    {
        using Harness h = new("audio/mpeg", "clip.mp3");
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([], [1, 2, 3], ModerationContentKind.Audio, null));

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error,
            "with no transcription provider the audio could not be classified");
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_audio_file_is_classified_from_its_transcript()
    {
        using Harness h = new("audio/mpeg", "clip.mp3");
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([], [1, 2, 3], ModerationContentKind.Audio, null));
        SetupAudioTranscription(h.Resolver, h.TranscriptionFactory, "hello there");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Clean);
        h.Gate.LastInput!.Text.Should().Be("hello there");
        h.Gate.LastInput.Kind.Should().Be(ModerationContentKind.Audio);
    }

    [Fact]
    public async Task Text_beyond_the_bounded_number_of_calls_is_judged_as_far_as_it_fits_and_is_never_Clean()
    {
        using Harness h = new("audio/mpeg", "clip.mp3");
        string text = string.Concat(Enumerable.Repeat("word ", 130_000)); // 650 000 characters, over 64 calls of 8 000
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([], null, ModerationContentKind.Audio, null, SubtitleText: text));

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Gate.Calls.Should().Be(ModerationTextChunker.DefaultMaxChunks, "what fits is judged");
        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error, "the rest was not judged, so it is not Clean");
    }

    [Fact]
    public async Task A_video_with_no_frames_and_no_transcript_is_Error()
    {
        using Harness h = new("video/mp4", "clip.mp4");
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([], null, ModerationContentKind.Video, "video frame sampling failed"));

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error);
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_real_jpeg_upload_is_sampled_judged_and_Clean_not_Error()
    {
        if (!(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(directory => File.Exists(Path.Combine(directory, "ffmpeg"))))
            return;

        string jpg = Path.Combine(Path.GetTempPath(), "ingest-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                       "ffmpeg", $"-y -loglevel error -f lavfi -i testsrc=size=320x240 -frames:v 1 \"{jpg}\"")
            { RedirectStandardError = true })!)
            {
                p.StandardError.ReadToEnd();
                p.WaitForExit();
            }

            using Harness h = new("image/jpeg", "photo.jpg");
            FfmpegModerationMediaSampler real = new(new SystemProcessRunner(), new ContentModerationOptions { Enabled = true });
            h.Sampler.Handler = (_, mime, ct) => real.SampleAsync(File.OpenRead(jpg), mime, ct);

            await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

            h.File.ModerationStatus.Should().Be(FileModerationStatus.Clean, "ingest used to end Error for every JPEG");
            h.Gate.Calls.Should().Be(1);
        }
        finally
        {
            File.Delete(jpg);
        }
    }

    [Fact]
    public async Task A_video_that_declared_a_false_length_is_judged_on_the_frames_it_gave_and_is_not_called_Clean()
    {
        using Harness h = new("video/mp4", "clip.mp4");
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Video,
                "the declared duration does not match the video", Deterministic: true));

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Gate.Calls.Should().Be(1, "ingest judges what it got");
        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error, "a check that failed part-way is never called Clean; the file stays usable");
    }

    [Fact]
    public async Task Disabled_moderation_is_Clean_and_never_samples()
    {
        using Harness h = new();

        await h.Processor(new ContentModerationOptions { Enabled = false })
            .ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Clean);
        h.Sampler.Calls.Should().Be(0);
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_non_media_mime_is_Clean_without_sampling()
    {
        using Harness h = new("text/plain", "notes.txt");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Clean);
        h.Sampler.Calls.Should().Be(0);
        h.Gate.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_exception_marks_Error_and_leaves_the_file_usable()
    {
        using Harness h = new();
        h.Storage.DownloadFailure = new IOException("storage unavailable");

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(FileModerationStatus.Error);
        h.Storage.Moves.Should().BeEmpty();
    }

    [Fact]
    public async Task A_replacement_during_the_scan_is_not_marked_clean_by_the_stale_scan()
    {
        using Harness h = new("image/png", "photo.png");
        string replacementKey = $"projects/{h.Project.Id}/userFiles/replacement.png";

        h.Sampler.Handler = async (_, _, _) =>
        {
            // A re-upload replaces the bytes at a new key while this scan is in flight.
            using InferenceApiDbContext other = h.NewContext();
            ProjectFile row = await other.ProjectFiles.SingleAsync(f => f.Id == h.File.Id);
            row.StorageKey = replacementKey;
            row.ModerationStatus = FileModerationStatus.Unmoderated;
            await other.SaveChangesAsync();
            return new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Image, null);
        };

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using (InferenceApiDbContext check = h.NewContext())
        {
            ProjectFile saved = await check.ProjectFiles.SingleAsync(f => f.Id == h.File.Id);
            saved.ModerationStatus.Should().Be(FileModerationStatus.Unmoderated,
                "the stale scan classified the OLD bytes and must not mark the replacement Clean");
            saved.StorageKey.Should().Be(replacementKey);
        }

        // The enqueued second scan (no in-flight dedupe drops it) classifies the replacement.
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Image, null));
        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using InferenceApiDbContext after = h.NewContext();
        (await after.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
            .Should().Be(FileModerationStatus.Clean);
    }

    [Fact]
    public async Task A_same_key_overwrite_during_the_scan_is_not_marked_clean_by_the_stale_scan()
    {
        using Harness h = new("image/png", "photo.png");
        h.Storage.ETag = "v1";

        h.Sampler.Handler = async (_, _, _) =>
        {
            // A presigned PUT replays the SAME key with different bytes while the scan runs.
            using InferenceApiDbContext other = h.NewContext();
            ProjectFile row = await other.ProjectFiles.SingleAsync(f => f.Id == h.File.Id);
            row.ModerationStatus = FileModerationStatus.Unmoderated;
            await other.SaveChangesAsync();
            h.Storage.ETag = "v2";
            return new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Image, null);
        };

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using (InferenceApiDbContext check = h.NewContext())
        {
            (await check.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
                .Should().Be(FileModerationStatus.Unmoderated,
                    "the ETag changed, so the stale scan classified different bytes");
        }

        h.Enqueuer.Calls.Should().Contain(h.File.Id,
            "the abandoned scan must offer a scan for the replacement");

        // The re-offered scan sees the new ETag and classifies the new bytes.
        h.Sampler.Handler = (_, _, _) => Task.FromResult(
            new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Image, null));
        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using InferenceApiDbContext after = h.NewContext();
        (await after.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
            .Should().Be(FileModerationStatus.Clean);
    }

    [Fact]
    public async Task A_file_quarantined_between_the_check_and_the_download_is_never_sent_to_the_provider()
    {
        using Harness h = new("image/png", "photo.png");
        h.Storage.BeforeDownload = async () =>
        {
            // A concurrent writer quarantines the row as the scan starts reading the bytes.
            using InferenceApiDbContext other = h.NewContext();
            ProjectFile row = await other.ProjectFiles.SingleAsync(f => f.Id == h.File.Id);
            row.ModerationStatus = FileModerationStatus.Quarantined;
            await other.SaveChangesAsync();
        };

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Gate.Calls.Should().Be(0,
            "a quarantine that lands after the initial check must stop the provider call");
        using InferenceApiDbContext check = h.NewContext();
        (await check.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
            .Should().Be(FileModerationStatus.Quarantined);
    }

    [Fact]
    public async Task A_file_quarantined_during_sampling_is_never_sent_to_the_transcription_provider()
    {
        using Harness h = new("audio/mpeg", "clip.mp3");
        ResolvedTranscriptionProvider provider = new(
            Guid.NewGuid(), "whisper", InferenceProviderKind.OpenAICompatible,
            "http://asr", "whisper-1", "key", 30);
        h.Resolver
            .Setup(r => r.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(provider);
        Mock<ITranscriptionClient> transcriptionClient = new();
        h.TranscriptionFactory.Setup(f => f.Get(provider)).Returns(transcriptionClient.Object);

        h.Sampler.Handler = async (_, _, _) =>
        {
            // A concurrent writer quarantines the row while the audio is being sampled.
            using InferenceApiDbContext other = h.NewContext();
            ProjectFile row = await other.ProjectFiles.SingleAsync(f => f.Id == h.File.Id);
            row.ModerationStatus = FileModerationStatus.Quarantined;
            await other.SaveChangesAsync();
            return new ModerationMediaSample([], [1, 2, 3], ModerationContentKind.Audio, null);
        };

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        transcriptionClient.Verify(
            c => c.TranscribeAsync(
                It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "a quarantine that lands during sampling must stop the ASR egress");
        h.Gate.Calls.Should().Be(0);
        using InferenceApiDbContext check = h.NewContext();
        (await check.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
            .Should().Be(FileModerationStatus.Quarantined);
    }

    [Fact]
    public async Task A_same_key_overwrite_while_scanning_returns_the_row_to_unmoderated_and_requeues()
    {
        using Harness h = new("image/png", "photo.png");
        h.Storage.ETag = "v1";

        h.Sampler.Handler = async (_, _, _) =>
        {
            // The bytes change in place; the row stays Scanning (no upload reset).
            h.Storage.ETag = "v2";
            await Task.CompletedTask;
            return new ModerationMediaSample([OnePixelJpeg], null, ModerationContentKind.Image, null);
        };

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        using InferenceApiDbContext check = h.NewContext();
        (await check.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
            .Should().Be(FileModerationStatus.Unmoderated,
                "the abandoned scan must not leave the row Scanning with no queued scan");
        h.Enqueuer.Calls.Should().Contain(h.File.Id);
    }

    [Fact]
    public async Task The_sweeper_reoffers_a_stale_scanning_row()
    {
        using Harness h = new();
        h.File.ModerationStatus = FileModerationStatus.Scanning;
        h.File.UploadedAt = DateTime.UtcNow.AddMinutes(-31);
        await h.Db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddScoped(_ => h.NewContext());
        using ServiceProvider provider = services.BuildServiceProvider();
        var queue = new FakeQueue();
        var worker = new ContentModerationWorker(
            queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContentModerationWorker>.Instance);

        int queued = await worker.SweepOnceAsync(CancellationToken.None);

        queued.Should().Be(1);
        queue.Queued.Should().ContainSingle().Which.FileId.Should().Be(h.File.Id);
        using InferenceApiDbContext check = h.NewContext();
        (await check.ProjectFiles.SingleAsync(f => f.Id == h.File.Id)).ModerationStatus
            .Should().Be(FileModerationStatus.Unmoderated,
                "a stale Scanning row is returned to the queue, never left without a scan");
    }

    [Fact]
    public async Task Processor_ModeratesAPendingRow_WhenTheCsamControlIsDisabled()
    {
        using Harness h = new();
        h.File.CsamStatus = CsamScanStatus.Pending;
        await h.Db.SaveChangesAsync();
        IOptions<CsamOptions> disabled = Options.Create(new CsamOptions
        {
            Arachnid = new ArachnidOptions { Enabled = false }
        });

        await h.Processor(csamOptions: disabled).ProcessAsync(h.File.Id, CancellationToken.None);

        h.Gate.Calls.Should().Be(1, "a disabled CSAM control can never settle Pending, so it must not block moderation");
        h.Storage.Downloads.Should().Be(1);
    }

    [Fact]
    public async Task Sweeper_QueuesAPendingRow_WhenTheCsamControlIsDisabled()
    {
        using Harness h = new();
        h.File.CsamStatus = CsamScanStatus.Pending;
        h.File.UploadedAt = DateTime.UtcNow.AddMinutes(-31);
        await h.Db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddScoped(_ => h.NewContext());
        using ServiceProvider provider = services.BuildServiceProvider();
        var queue = new FakeQueue();
        IOptions<CsamOptions> disabled = Options.Create(new CsamOptions
        {
            Arachnid = new ArachnidOptions { Enabled = false }
        });
        var worker = new ContentModerationWorker(
            queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContentModerationWorker>.Instance, disabled);

        int queued = await worker.SweepOnceAsync(CancellationToken.None);

        queued.Should().Be(1);
        queue.Queued.Should().ContainSingle().Which.FileId.Should().Be(h.File.Id);
    }

    [Fact]
    public async Task A_stopped_sweeper_does_not_bypass_the_csam_gate()
    {
        using Harness h = new();
        h.File.CsamStatus = CsamScanStatus.Pending;
        h.File.UploadedAt = DateTime.UtcNow.AddMinutes(-31);
        await h.Db.SaveChangesAsync();
        IOptions<CsamOptions> stopped = Options.Create(new CsamOptions
        {
            Arachnid = new ArachnidOptions { Enabled = true },
            Sweeper = new CsamSweeperOptions { IntervalMinutes = 0 }
        });

        await h.Processor(csamOptions: stopped).ProcessAsync(h.File.Id, CancellationToken.None);
        h.Storage.Downloads.Should().Be(0, "an enabled CSAM control with a stopped sweeper is a fault, not a bypass");
        h.Gate.Calls.Should().Be(0);

        ServiceCollection services = new();
        services.AddScoped(_ => h.NewContext());
        using ServiceProvider provider = services.BuildServiceProvider();
        var queue = new FakeQueue();
        var worker = new ContentModerationWorker(
            queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContentModerationWorker>.Instance, stopped);

        (await worker.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        queue.Queued.Should().BeEmpty();
    }

    [Theory]
    [InlineData(CsamScanStatus.Pending, false)]
    [InlineData(CsamScanStatus.Scanning, false)]
    [InlineData(CsamScanStatus.Quarantined, false)]
    [InlineData(CsamScanStatus.NotScanned, true)]
    public async Task The_sweeper_never_offers_a_csam_held_or_unresolved_row(
        CsamScanStatus csamStatus, bool hasQuarantineKey)
    {
        using Harness h = new();
        h.File.ModerationStatus = FileModerationStatus.Unmoderated;
        h.File.UploadedAt = DateTime.UtcNow.AddMinutes(-31);
        h.File.CsamStatus = csamStatus;
        h.File.CsamQuarantineKey = hasQuarantineKey
            ? $"projects/{h.Project.Id}/quarantine/{h.File.Id}/photo.png"
            : null;
        await h.Db.SaveChangesAsync();

        ServiceCollection services = new();
        services.AddScoped(_ => h.NewContext());
        using ServiceProvider provider = services.BuildServiceProvider();
        var queue = new FakeQueue();
        var worker = new ContentModerationWorker(
            queue, provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ContentModerationWorker>.Instance);

        int queued = await worker.SweepOnceAsync(CancellationToken.None);

        queued.Should().Be(0, "CSAM-held and unresolved rows never reach general moderation");
        queue.Queued.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FileModerationStatus.Clean)]
    [InlineData(FileModerationStatus.Quarantined)]
    [InlineData(FileModerationStatus.Error)]
    public async Task Already_classified_files_are_skipped(FileModerationStatus status)
    {
        using Harness h = new();
        h.File.ModerationStatus = status;
        h.Db.SaveChanges();

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.File.ModerationStatus.Should().Be(status);
        h.Sampler.Calls.Should().Be(0);
        h.Gate.Calls.Should().Be(0);
        h.Storage.Downloads.Should().Be(0);
    }

    [Fact]
    public async Task An_image_is_sent_as_the_sampled_jpeg_not_the_original()
    {
        using Harness h = new();
        h.Storage.DownloadBytes = [9, 9, 9, 9];

        await h.Processor().ProcessAsync(h.File.Id, CancellationToken.None);

        h.Gate.LastInput!.Images.Should().ContainSingle()
            .Which.Bytes.Should().Equal(OnePixelJpeg);
        h.Gate.LastInput.Text.Should().BeNull();
        h.Sampler.LastMimeType.Should().Be("image/png");
    }
}
