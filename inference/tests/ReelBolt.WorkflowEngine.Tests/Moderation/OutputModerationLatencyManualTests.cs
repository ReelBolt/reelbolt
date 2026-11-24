using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// Manual latency probe for the background lane: synthetic 1080p media, the real ffmpeg sampler and the
/// real processor, with fixed fake delays standing in for the vendors. Does nothing unless
/// REELBOLT_MANUAL_LATENCY is set, so the normal suite never runs it. The numbers exclude the network
/// download from object storage and the real vendor's behaviour.
/// <para>
/// REELBOLT_MANUAL_LATENCY=1 runs one 30 s file once (the ticket's 20-60 s shape, still under a couple
/// of minutes), which fits the gate's <c>--blame-hang-timeout 150s</c>. REELBOLT_MANUAL_LATENCY=full runs the 30, 120 and 300 s files three
/// times each and the five-file lane, which takes many minutes: run it on its own with a longer hang
/// timeout, for example
/// <c>REELBOLT_MANUAL_LATENCY=full dotnet test tests/ReelBolt.WorkflowEngine.Tests --filter Category=Manual --blame-hang-timeout 30m</c>.
/// </para>
/// </summary>
[Trait("Category", "Manual")]
public sealed class OutputModerationLatencyManualTests(ITestOutputHelper output)
{
    private static readonly TimeSpan VendorCall = TimeSpan.FromMilliseconds(300);

    private static bool Enabled => Environment.GetEnvironmentVariable("REELBOLT_MANUAL_LATENCY") is "1" or "full";

    private static bool Full => Environment.GetEnvironmentVariable("REELBOLT_MANUAL_LATENCY") == "full";

    private sealed class DelayGate : IContentModerationGate
    {
        public int Calls;

        public async Task<ModerationDecision> ModerateAsync(ModerationInput input, ModerationAttribution a, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(VendorCall, ct);
            return new ModerationDecision(ModerationAction.Allow, true, false, null, string.Empty, "h");
        }

        public Task<bool> ScreenGenerationPromptAsync(string t, IReadOnlyList<ModerationImageInput> i, ModerationAttribution a, CancellationToken c) => throw new NotSupportedException();

        public Task<bool> ScreenGeneratedTextAsync(string t, string s, ModerationAttribution a, CancellationToken c) => throw new NotSupportedException();

        public Task<bool> ScreenPublishAsync(ModerationInput i, ModerationAttribution a, CancellationToken c) => throw new NotSupportedException();
    }

    private sealed class DelayTranscriber(Func<double> audioSeconds) : ITranscriptionClient
    {
        public async Task<TranscriptResult> TranscribeAsync(Stream wav, string fileName, string? language, bool wordTimestamps, CancellationToken ct)
        {
            // Assumption: transcription takes 10% of the audio duration.
            await Task.Delay(TimeSpan.FromSeconds(audioSeconds() * 0.1), ct);
            return new TranscriptResult("hello", [], [], null);
        }
    }

    private sealed class TimedSampler(IModerationMediaSampler inner) : IModerationMediaSampler
    {
        public TimeSpan Total;

        public async Task<ModerationMediaSample> SampleAsync(Stream media, string mimeType, CancellationToken ct)
        {
            Stopwatch sw = Stopwatch.StartNew();
            ModerationMediaSample sample = await inner.SampleAsync(media, mimeType, ct);
            Total += sw.Elapsed;
            return sample;
        }
    }

    private static void Ffmpeg(string args)
    {
        using Process p = Process.Start(new ProcessStartInfo("ffmpeg", "-y -loglevel error " + args) { RedirectStandardError = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException(p.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task Measure_background_verdict_latency()
    {
        if (!Enabled) return;

        string dir = Path.Combine(Path.GetTempPath(), "reelbolt-latency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Guid projectId = Guid.NewGuid();
            double audioSeconds = 0;
            DelayGate gate = new();
            TimedSampler sampler = new(new FfmpegModerationMediaSampler(new SystemProcessRunner(), new ContentModerationOptions { Enabled = true }));

            Mock<IFileStorageService> storage = new();
            storage.Setup(s => s.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string k, CancellationToken _) =>
                {
                    FileInfo f = new(Path.Combine(dir, Path.GetFileName(k)));
                    return new StoredObjectMetadata(f.Length, "video/mp4", "\"" + f.Length + "\"");
                });
            storage.Setup(s => s.DownloadAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, string k, CancellationToken _) => (Stream)File.OpenRead(Path.Combine(dir, Path.GetFileName(k))));

            Mock<IInferenceProviderResolver> providers = new();
            providers.Setup(p => p.ResolvePlatformTranscriptionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedTranscriptionProvider(null, "fake", InferenceProviderKind.OpenAICompatible, "http://x", "m", "k", 30));
            Mock<ITranscriptionClientFactory> factory = new();
            factory.Setup(f => f.Get(It.IsAny<ResolvedTranscriptionProvider>())).Returns(new DelayTranscriber(() => audioSeconds));

            string dbName = Guid.NewGuid().ToString();
            ServiceCollection services = new();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseInMemoryDatabase(dbName));
            services.AddSingleton<IContentModerationGate>(gate);
            services.AddSingleton<IModerationMediaSampler>(sampler);
            services.AddSingleton(storage.Object);
            services.AddSingleton(providers.Object);
            services.AddSingleton(factory.Object);
            services.AddSingleton(Options.Create(new ContentModerationOptions { Enabled = true }));
            services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
            ChannelBackgroundTaskQueue<OutputModerationTask> queue = new(100);
            services.AddSingleton<IBackgroundTaskQueue<OutputModerationTask>>(queue);
            services.AddScoped<PublishModeration>();
            services.AddScoped<OutputModerationProcessor>();
            ServiceProvider sp = services.BuildServiceProvider();
            using (IServiceScope s = sp.CreateScope())
            {
                InferenceApiDbContext db = s.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                db.Projects.Add(new Project { Id = projectId, Name = "p", OrganizationId = Guid.NewGuid() });
                db.SaveChanges();
            }

            OutputModerationWorker worker = new(
                queue, sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new ContentModerationOptions { Enabled = true }),
                NullLogger<OutputModerationWorker>.Instance);

            async Task<(TimeSpan Total, TimeSpan Sampling, int Calls)> RunAsync(string name)
            {
                sampler.Total = TimeSpan.Zero;
                int before = gate.Calls;
                Stopwatch sw = Stopwatch.StartNew();
                OutputModerationTask t = new(projectId, $"projects/{projectId}/outputFiles/{name}");
                queue.TryQueue(t);
                await worker.RunOneAsync(await queue.DequeueAsync(CancellationToken.None), CancellationToken.None);
                return (sw.Elapsed, sampler.Total, gate.Calls - before);
            }

            foreach (int seconds in Full ? new[] { 30, 120, 300 } : new[] { 30 })
            {
                string name = $"synthetic-{seconds}.mp4";
                Ffmpeg($"-f lavfi -i testsrc=size=1920x1080:rate=30 -f lavfi -i sine=frequency=440 -t {seconds} -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac {Path.Combine(dir, name)}");
                audioSeconds = seconds;
                for (int run = 1; run <= (Full ? 3 : 1); run++)
                {
                    string unique = $"{run}-{name}";
                    File.Copy(Path.Combine(dir, name), Path.Combine(dir, unique), true);
                    var r = await RunAsync(unique);
                    TimeSpan assumed = VendorCall * r.Calls + TimeSpan.FromSeconds(seconds * 0.1);
                    output.WriteLine($"{seconds}s run{run}: total {r.Total.TotalSeconds:F1}s, sampling {r.Sampling.TotalSeconds:F1}s, assumed vendor+transcription {assumed.TotalSeconds:F1}s, gate calls {r.Calls}");
                }
            }

            if (!Full) return;

            audioSeconds = 30;
            for (int i = 0; i < 5; i++)
                File.Copy(Path.Combine(dir, "synthetic-30.mp4"), Path.Combine(dir, $"lane-{i}.mp4"), true);
            Stopwatch lane = Stopwatch.StartNew();
            for (int i = 0; i < 5; i++) queue.TryQueue(new OutputModerationTask(projectId, $"projects/{projectId}/outputFiles/lane-{i}.mp4"));
            for (int i = 0; i < 5; i++)
                await worker.RunOneAsync(await queue.DequeueAsync(CancellationToken.None), CancellationToken.None);
            output.WriteLine($"5 x 30s queued together, one at a time: last verdict after {lane.Elapsed.TotalSeconds:F1}s");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
