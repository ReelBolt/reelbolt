using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Csam;

/// <summary>
/// The matcher's whole policy: PDQ-first routing, quarantine on a match, fail-open on every
/// provider/configuration failure, and an audit row on every attempt. Offline throughout.
/// </summary>
public class CsamHashMatcherTests
{
    // ------------------------------------------------------------------ fail-open (report contract)

    [Fact]
    public async Task Scan_Timeout_FailsOpen_RecordsNotScanned()
    {
        CsamFakeServer server = new()
        {
            Override = (_, _) => throw new TaskCanceledException("the provider did not answer in time")
        };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        h.Outcome.ErrorCode.Should().Be("TIMEOUT");
        h.File.CsamStatus.Should().Be(CsamScanStatus.NotScanned);
        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("not-scanned");
        audit.ErrorCode.Should().Be("TIMEOUT");
        audit.Quarantined.Should().BeFalse();
    }

    [Fact]
    public async Task Scan_ServerError_FailsOpen_RecordsNotScanned()
    {
        CsamFakeServer server = new()
        {
            Override = (_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        h.Outcome.ErrorCode.Should().Be("HTTP_503");
        h.File.CsamStatus.Should().Be(CsamScanStatus.NotScanned);
        h.Db.CsamScanAudits.Single().ErrorCode.Should().Be("HTTP_503");
    }

    [Fact]
    public async Task Scan_MissingConfiguration_FailsOpen_RecordsNotScanned()
    {
        CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Password = null);

        using Harness h = await RunAsync(options: options);

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        h.Outcome.ErrorCode.Should().Be("CONFIG_MISSING");
        h.File.CsamStatus.Should().Be(CsamScanStatus.NotScanned);
        h.Handler.Requests.Should().BeEmpty("nothing may leave the host without configuration");
        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("not-scanned");
        audit.ErrorCode.Should().Be("CONFIG_MISSING");
        audit.Source.Should().Be("none");
    }

    [Fact]
    public async Task Scan_TransportError_FailsOpen_RecordsNotScanned()
    {
        CsamFakeServer server = new()
        {
            Override = (_, _) => throw new HttpRequestException("connection refused")
        };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        h.Outcome.ErrorCode.Should().Be("TRANSPORT");
        h.Db.CsamScanAudits.Single().ErrorCode.Should().Be("TRANSPORT");
    }

    [Fact]
    public async Task Scan_MalformedResponse_FailsOpen_RecordsNotScanned()
    {
        CsamFakeServer server = new()
        {
            Override = (_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", Encoding.UTF8, "application/json")
            }
        };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        h.Outcome.ErrorCode.Should().Be("INVALID_RESPONSE");
        h.Db.CsamScanAudits.Single().ErrorCode.Should().Be("INVALID_RESPONSE");
    }

    // ------------------------------------------------------------------ verdicts

    [Fact]
    public async Task Scan_NoKnownMatch_MarksClean_AndAudits()
    {
        using Harness h = await RunAsync();

        h.Outcome.Status.Should().Be(CsamScanStatus.Clean);
        h.Outcome.Quarantined.Should().BeFalse();
        h.File.CsamStatus.Should().Be(CsamScanStatus.Clean);
        h.File.CsamClassification.Should().Be("no-known-match");
        h.Storage.Moves.Should().BeEmpty();

        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("clean");
        audit.Classification.Should().Be("no-known-match");
        audit.Source.Should().Be("pdq");
        audit.Quarantined.Should().BeFalse();
        audit.ProviderVersion.Should().Be("2026-10-01T00:00:00Z");
    }

    [Fact]
    public async Task Scan_ExactMatch_StampsTheProjectMarker_AndACleanScanDoesNot()
    {
        void AddProject(InferenceApiDbContext db, ProjectFile file)
        {
            db.Projects.Add(new Project { Id = file.ProjectId, Name = "p" });
            db.SaveChanges();
        }

        using (Harness match = await RunAsync(
            server: new CsamFakeServer { PdqClassification = "csam", PdqMatchType = "exact" }, beforeMatcher: AddProject))
        {
            match.Db.Projects.IgnoreQueryFilters().Single().ContentQuarantinedAt.Should().NotBeNull();
            match.Db.Projects.IgnoreQueryFilters().Single().CsamOutputHoldAt.Should().NotBeNull();
        }

        using Harness clean = await RunAsync(beforeMatcher: AddProject);
        clean.Db.Projects.IgnoreQueryFilters().Single().ContentQuarantinedAt.Should().BeNull();
        clean.Db.Projects.IgnoreQueryFilters().Single().CsamOutputHoldAt.Should().BeNull();
    }

    [Fact]
    public async Task Scan_ExactMatch_Quarantines_AndMovesObject()
    {
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.Quarantined);
        h.Outcome.Quarantined.Should().BeTrue();
        h.File.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        h.File.CsamClassification.Should().Be("csam");
        h.File.CsamMatchType.Should().Be("exact");
        h.File.CsamQuarantineKey.Should().NotBeNull();
        h.File.StorageKey.Should().Be(h.File.CsamQuarantineKey);
        h.File.StorageKey.Should().StartWith($"projects/{h.File.ProjectId}/quarantine/{h.File.Id}/");
        h.File.CsamQuarantinedAt.Should().NotBeNull();

        h.Storage.Moves.Should().ContainSingle();
        h.Storage.Objects.Keys.Should().Contain(h.File.CsamQuarantineKey);

        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("match");
        audit.Classification.Should().Be("csam");
        audit.MatchType.Should().Be("exact");
        audit.Quarantined.Should().BeTrue();
        audit.QuarantineSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_NearMatch_Quarantines_AndQueues()
    {
        CsamFakeServer server = new()
        {
            PdqClassification = "harmful-abusive-material",
            PdqMatchType = "near",
            PdqNearMatches = 2
        };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.Quarantined);
        h.File.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("match");
        audit.MatchType.Should().Be("near");
        audit.NearMatchCount.Should().Be(2);
        audit.Quarantined.Should().BeTrue();
    }

    [Fact]
    public async Task Scan_QuarantineMoveFailure_StillQuarantinesRow()
    {
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };

        using Harness h = await RunAsync(server: server, configureStorage: s => s.MoveFailure = new IOException("storage down"));

        h.File.CsamStatus.Should().Be(CsamScanStatus.Quarantined, "the row must never be served even when the move failed");
        h.File.CsamQuarantineKey.Should().BeNull();
        h.Storage.Moves.Should().BeEmpty();

        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("match");
        audit.Quarantined.Should().BeTrue();
        audit.QuarantineSucceeded.Should().BeFalse();
    }

    // ------------------------------------------------------------------ content never leaves the audit/log

    [Fact]
    public async Task Scan_AuditAndLogs_NeverContainFileContent()
    {
        const string marker = "USER-SECRET-CONTENT-DO-NOT-STORE-9f2c";
        // A JPEG header keeps the sniffer on the image path, so this test exercises the PDQ branch
        // while still carrying a secret marker the audit and logs must never contain.
        byte[] content = [0xFF, 0xD8, 0xFF, .. Encoding.UTF8.GetBytes(marker)];
        string base64Content = Convert.ToBase64String(content);
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };

        using Harness h = await RunAsync(content: content, server: server);

        h.Logger.Messages.Should().NotBeEmpty("a match logs one warning");
        h.Logger.Messages.Should().NotContain(message => message.Contains(marker, StringComparison.Ordinal));
        h.Logger.Messages.Should().NotContain(message => message.Contains(CsamTestSupport.Password, StringComparison.Ordinal));
        h.Logger.Messages.Should().NotContain(message => message.Contains(base64Content, StringComparison.Ordinal));

        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        string serialized = JsonSerializer.Serialize(audit);
        serialized.Should().NotContain(marker);
        serialized.Should().NotContain(base64Content);
        serialized.Should().NotContain(CsamTestSupport.Password);
        audit.PdqHashBase64.Should().Be(FixedPdqHasher.Hash);
        audit.ContentSha256Hex.Should().Be(
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant());
    }

    // ------------------------------------------------------------------ routing and host

    [Fact]
    public async Task Scan_OnlyCallsConfiguredArachnidHost()
    {
        CsamFakeServer server = new();
        CsamRecordingHandler handler = new(server);
        CsamOptions options = CsamTestSupport.EnabledOptions();

        using (Harness image = await RunAsync(server: server, handler: handler, options: options)) { }
        using (Harness video = await RunAsync(mime: "video/mp4", fileName: "clip.mp4", server: server, handler: handler, options: options)) { }

        handler.Requests.Should().NotBeEmpty();
        handler.Requests.Select(r => r.RequestUri!.Host).Distinct().Should().Equal("shield.test");
        handler.Requests.Select(r => r.RequestUri!.Scheme).Distinct().Should().Equal("https");
    }

    [Fact]
    public async Task Scan_UsesPdqForImage_AndMediaForVideo()
    {
        CsamFakeServer server = new();
        CsamRecordingHandler imageHandler = new(server);
        using (Harness image = await RunAsync(server: server, handler: imageHandler))
        {
            imageHandler.Paths.Should().Contain("/v1/pdq");
            imageHandler.Paths.Should().NotContain("/v1/media");
            image.Db.CsamScanAudits.Single().Source.Should().Be("pdq");
        }

        CsamRecordingHandler videoHandler = new(server);
        using (Harness video = await RunAsync(mime: "video/mp4", fileName: "clip.mp4", server: server, handler: videoHandler))
        {
            videoHandler.Paths.Should().Contain("/v1/media");
            videoHandler.Paths.Should().NotContain("/v1/pdq");
            video.Db.CsamScanAudits.Single().Source.Should().Be("media");
        }
    }

    [Fact]
    public async Task Scan_UndecodableImage_FallsBackToMedia()
    {
        CsamRecordingHandler handler = new();

        using Harness h = await RunAsync(hasher: new UndecodablePdqHasher(), handler: handler);

        handler.Paths.Should().Contain("/v1/media");
        handler.Paths.Should().NotContain("/v1/pdq");
        h.Db.CsamScanAudits.Single().Source.Should().Be("media");
    }

    [Fact]
    public async Task Scan_UnsupportedMediaType_FailsOpenWithoutRequest()
    {
        CsamRecordingHandler handler = new();

        // A real PDF header is what the sniffer must see; a lying document label with binary bytes
        // is media and goes to the provider (Scan_DeclaredTextMime_WithMediaBytes_IsSniffedAndSentToMedia).
        using Harness h = await RunAsync(
            mime: "application/pdf", fileName: "doc.pdf", handler: handler,
            content: Encoding.UTF8.GetBytes("%PDF-1.7\n1 0 obj\n<<>>\nendobj\n"));

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        h.Outcome.ErrorCode.Should().Be("UNSUPPORTED_MEDIA_TYPE");
        handler.Requests.Should().BeEmpty();
        h.Db.CsamScanAudits.Single().Source.Should().Be("none");
    }

    [Fact]
    public async Task Scan_Disabled_SendsNothing_WritesDisabledAudit_AndLeavesTheRowNotScanned()
    {
        CsamOptions options = CsamTestSupport.EnabledOptions(o => o.Enabled = false);
        CsamRecordingHandler handler = new();

        using Harness h = await RunAsync(options: options, handler: handler);

        h.Outcome.Status.Should().Be(CsamScanStatus.NotScanned);
        handler.Requests.Should().BeEmpty();
        h.File.CsamStatus.Should().Be(CsamScanStatus.NotScanned);
        h.File.CsamScannedAt.Should().BeNull("nothing was scanned, so it must not look like one");

        CsamScanAudit audit = h.Db.CsamScanAudits.Single();
        audit.Status.Should().Be("disabled");
        audit.Source.Should().Be("none");
        audit.Quarantined.Should().BeFalse();
        audit.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Scan_FailOpen_NeverDowngradesAnAlreadyQuarantinedRow()
    {
        CsamFakeServer server = new()
        {
            Override = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };

        using Harness h = await RunAsync(server: server, beforeScan: file =>
        {
            file.CsamStatus = CsamScanStatus.Quarantined;
            file.CsamQuarantineKey = "projects/p/quarantine/x";
        });

        h.File.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        h.Db.CsamScanAudits.Single().Status.Should().Be("not-scanned");
    }

    // ------------------------------------------------------------------ concurrency and content bypass

    [Fact]
    public async Task Scan_NoMatch_NeverDowngradesAConcurrentQuarantine()
    {
        using Harness h = await RunAsync(simulateConcurrentQuarantine: true);

        h.File.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        h.File.CsamClassification.Should().Be("csam");
        h.File.CsamMatchType.Should().Be("exact");

        ProjectFile stored = await h.Db.ProjectFiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == h.File.Id);
        stored.CsamStatus.Should().Be(CsamScanStatus.Quarantined);
        stored.CsamClassification.Should().Be("csam");
        stored.CsamMatchType.Should().Be("exact");

        // The attempt is still audited as a clean scan; only the row's verdict is protected.
        h.Db.CsamScanAudits.Single().Status.Should().Be("clean");
    }

    [Fact]
    public async Task Scan_LyingMimeType_IsSniffedAndSentToMedia()
    {
        // An MP4 container ("ftyp" at bytes 4..8) behind an application/octet-stream label.
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

        using Harness h = await RunAsync(mime: "application/octet-stream", fileName: "blob.bin", content: mp4);

        h.Handler.Paths.Should().Contain("/v1/media");
        h.Handler.Paths.Should().NotContain("/v1/pdq");
        h.Db.CsamScanAudits.Single().Source.Should().Be("media");
        h.Handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/v1/media")
            .Content!.Headers.ContentType!.MediaType.Should().Be("video/mp4",
                "the sniffed type, not the caller's application/octet-stream, goes to the provider");
    }

    [Fact]
    public async Task Scan_TextFile_IsNotSent()
    {
        byte[] text = Encoding.UTF8.GetBytes("hello, this is a plain text file with an octet-stream label");

        using Harness h = await RunAsync(mime: "application/octet-stream", fileName: "blob.bin", content: text);

        h.Handler.Requests.Should().BeEmpty("text is known non-media and must not leave the host");
        h.Outcome.ErrorCode.Should().Be("UNSUPPORTED_MEDIA_TYPE");
        h.Db.CsamScanAudits.Single().Source.Should().Be("none");
    }

    [Fact]
    public async Task Scan_DeclaredTextMime_WithMediaBytes_IsSniffedAndSentToMedia()
    {
        // A text/plain label must not protect an MP4 container from the scan.
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

        using Harness h = await RunAsync(mime: "text/plain", fileName: "lie.txt", content: mp4);

        h.Handler.Paths.Should().Contain("/v1/media");
        h.Outcome.ErrorCode.Should().NotBe("UNSUPPORTED_MEDIA_TYPE");
        h.Db.CsamScanAudits.Single().Source.Should().Be("media");
    }

    [Fact]
    public async Task Quarantine_PublishesIndexDelete()
    {
        Moq.Mock<MassTransit.IPublishEndpoint> publisher = new();
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };

        using Harness h = await RunAsync(server: server, publisher: publisher.Object);

        publisher.Verify(p => p.Publish(
            It.Is<ReelBolt.Shared.IntegrationEvents.ProjectFileIndexingRequested>(
                m => m.FileId == h.File.Id && m.Operation == "Delete"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Scan_Match_PurgesMediaDerivatives()
    {
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };
        Guid derivativeId = Guid.NewGuid();
        string derivativeKey = string.Empty;

        using Harness h = await RunAsync(server: server,
            beforeMatcher: (db, file) =>
            {
                derivativeKey = $"projects/{file.ProjectId}/agentFiles/media-derivatives/{derivativeId}/proxy.mp4";
                db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = derivativeId,
                    ProjectId = file.ProjectId,
                    SourceKind = MediaSourceKind.ProjectFile,
                    SourceProjectFileId = file.Id,
                    SourceStorageKey = file.StorageKey,
                    StorageKey = derivativeKey,
                    MimeType = "video/mp4",
                    Kind = MediaDerivativeKind.Proxy
                });
                db.SaveChanges();
            },
            configureStorage: storage => storage.Put(derivativeKey, [9, 9, 9]));

        h.Storage.Objects.Should().NotContainKey(derivativeKey);
        h.Db.MediaDerivatives.Should().BeEmpty();
    }

    [Fact]
    public async Task Scan_Match_PurgesStepResultDerivativesOfTheSameRender()
    {
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };
        string derivativeKey = string.Empty;
        string indexKey = string.Empty;

        using Harness h = await RunAsync(server: server,
            beforeMatcher: (db, file) =>
            {
                Guid workflowId = Guid.NewGuid();
                Guid executionId = Guid.NewGuid();
                Guid stepId = Guid.NewGuid();
                WorkflowStepResult result = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Completed,
                    OutputStorageKey = file.StorageKey,
                    ExecutedAt = DateTime.UtcNow
                };
                db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = file.ProjectId, Name = "wf" });
                db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = workflowId, StepOrder = 1, StepType = StepType.VideoCompile });
                db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = executionId,
                    WorkflowDefinitionId = workflowId,
                    ProjectId = file.ProjectId,
                    Status = ExecutionStatus.Passed,
                    StartedAt = DateTime.UtcNow
                });
                db.WorkflowStepResults.Add(result);

                // The same render addressed as a STEP RESULT: SourceProjectFileId is null, but the
                // bytes are named by SourceStorageKey, exactly what the old purge missed.
                derivativeKey = $"projects/{file.ProjectId}/agentFiles/media-derivatives/{Guid.NewGuid():N}/proxy.mp4";
                indexKey = $"projects/{file.ProjectId}/agentFiles/media-derivatives/{Guid.NewGuid():N}/index.json";
                db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = Guid.NewGuid(),
                    ProjectId = file.ProjectId,
                    SourceKind = MediaSourceKind.StepResult,
                    SourceStepResultId = result.Id,
                    SourceStorageKey = file.StorageKey,
                    StorageKey = derivativeKey,
                    IndexStorageKey = indexKey,
                    MimeType = "video/mp4",
                    Kind = MediaDerivativeKind.Proxy
                });
                db.SaveChanges();
            },
            configureStorage: storage =>
            {
                storage.Put(derivativeKey, [9, 9, 9]);
                storage.Put(indexKey, [8, 8, 8]);
            });

        h.Db.MediaDerivatives.Should().BeEmpty("a step-result derivative of a quarantined render is evidence too");
        h.Storage.Objects.Should().NotContainKey(derivativeKey);
        h.Storage.Objects.Should().NotContainKey(indexKey);
    }

    [Fact]
    public async Task Scan_DeclaredImageWithVideoBytes_IsSniffedAndSentToMedia()
    {
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

        using Harness h = await RunAsync(mime: "image/jpeg", fileName: "lie.jpg", content: mp4);

        h.Handler.Paths.Should().Contain("/v1/media");
        h.Handler.Requests.Single(r => r.RequestUri!.AbsolutePath == "/v1/media")
            .Content!.Headers.ContentType!.MediaType.Should().Be("video/mp4");
        h.Outcome.ErrorCode.Should().NotBe("UNSUPPORTED_MEDIA_TYPE");
    }

    [Fact]
    public async Task Scan_DeclaredImageWithAnEightKibTextPrefix_IsStillScanned()
    {
        // A crafted 8 KiB printable prefix is what the head read sees; a declared image must still
        // be scanned (PDQ over the whole object, then /v1/media), never UNSUPPORTED and downloadable.
        byte[] prefix = Enumerable.Repeat((byte)'A', CsamMimeSniffer.HeadBytes).ToArray();
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F'];
        byte[] content = [.. prefix, .. jpeg];

        using Harness h = await RunAsync(mime: "image/jpeg", fileName: "crafted.jpg", content: content);

        h.Outcome.ErrorCode.Should().NotBe("UNSUPPORTED_MEDIA_TYPE",
            "a declared image/video whose head looks like text is still a scan candidate");
        (h.Handler.Paths.Contains("/v1/pdq") || h.Handler.Paths.Contains("/v1/media")).Should().BeTrue(
            "the crafted file must reach the provider, not stop at UNSUPPORTED");
    }

    [Fact]
    public async Task Scan_OverlongProviderStrings_AreClampedNotThrown()
    {
        CsamFakeServer server = new()
        {
            PdqClassification = "csam",
            PdqMatchType = new string('x', 200)
        };

        using Harness h = await RunAsync(server: server);

        h.Outcome.Status.Should().Be(CsamScanStatus.Quarantined);
        h.Db.CsamScanAudits.Single().MatchType!.Length.Should().Be(50,
            "the audit column is 50 characters; a long provider field must not make the verdict write throw");
    }

    [Fact]
    public async Task Scan_Match_RemovesDerivativeRowsEvenWhenTheirObjectsCannotBeDeleted()
    {
        CsamFakeServer server = new() { PdqClassification = "csam", PdqMatchType = "exact" };
        string derivativeKey = string.Empty;

        using Harness h = await RunAsync(server: server,
            beforeMatcher: (db, file) =>
            {
                Guid workflowId = Guid.NewGuid();
                Guid executionId = Guid.NewGuid();
                Guid stepId = Guid.NewGuid();
                WorkflowStepResult result = new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Completed,
                    OutputStorageKey = file.StorageKey,
                    ExecutedAt = DateTime.UtcNow
                };
                db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = workflowId, ProjectId = file.ProjectId, Name = "wf" });
                db.WorkflowSteps.Add(new WorkflowStep { Id = stepId, WorkflowDefinitionId = workflowId, StepOrder = 1, StepType = StepType.VideoCompile });
                db.WorkflowExecutions.Add(new WorkflowExecution
                {
                    Id = executionId,
                    WorkflowDefinitionId = workflowId,
                    ProjectId = file.ProjectId,
                    Status = ExecutionStatus.Passed,
                    StartedAt = DateTime.UtcNow
                });
                db.WorkflowStepResults.Add(result);
                derivativeKey = $"projects/{file.ProjectId}/agentFiles/media-derivatives/{Guid.NewGuid():N}/proxy.mp4";
                db.MediaDerivatives.Add(new MediaDerivative
                {
                    Id = Guid.NewGuid(),
                    ProjectId = file.ProjectId,
                    SourceKind = MediaSourceKind.StepResult,
                    SourceStepResultId = result.Id,
                    SourceStorageKey = file.StorageKey,
                    StorageKey = derivativeKey,
                    MimeType = "video/mp4",
                    Kind = MediaDerivativeKind.Proxy
                });
                db.SaveChanges();
            },
            configureStorage: storage =>
            {
                storage.Put(derivativeKey, [9, 9, 9]);
                // The verdict save removes the row; the object delete then fails. The route must
                // still 404 because there is no row left to address.
                storage.DeleteFailure = new IOException("simulated object-store failure");
            });

        h.Outcome.Status.Should().Be(CsamScanStatus.Quarantined);
        h.Db.MediaDerivatives.Should().BeEmpty("the rows are removed in the verdict save, before the object delete");
        h.Storage.Objects.Should().ContainKey(derivativeKey, "the failed object delete leaves the bytes, unreachable");
    }

    [Fact]
    public async Task Scan_CleanClassificationLongerThan100Chars_IsClamped()
    {
        CsamFakeServer server = new() { MediaClassification = new string('x', 200) };
        byte[] mp4 = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

        using Harness h = await RunAsync(server: server, mime: "video/mp4", fileName: "clip.mp4", content: mp4);

        h.Outcome.Status.Should().Be(CsamScanStatus.Clean);
        h.Db.CsamScanAudits.Single().Classification!.Length.Should().Be(100,
            "the clean path goes through the same central clamp as a match");
    }

    [Fact]
    public async Task Move_WhenQuarantineKeyDiverges_MovesTheActualObject()
    {
        InferenceApiDbContext db = CsamTestSupport.NewDb();
        ProjectFile file = CsamTestSupport.AddFile(db, bytes: [1, 2, 3]);
        string originalKey = file.StorageKey;
        CsamTestStorage storage = new();
        storage.Put(originalKey, [1, 2, 3]);

        file.CsamStatus = CsamScanStatus.Quarantined;
        file.CsamQuarantineKey = $"projects/{file.ProjectId}/quarantine/{file.Id}/photo.jpg";

        string movedTo = await new CsamQuarantineService(storage).MoveAsync(file, CancellationToken.None);

        movedTo.Should().Be(file.CsamQuarantineKey);
        file.StorageKey.Should().Be(file.CsamQuarantineKey);
        storage.Objects.Keys.Should().Contain(movedTo);
        storage.Objects.Keys.Should().NotContain(originalKey);
    }

    // ------------------------------------------------------------------ harness

    private static async Task<Harness> RunAsync(
        CsamOptions? options = null,
        CsamFakeServer? server = null,
        CsamRecordingHandler? handler = null,
        IPdqHasher? hasher = null,
        string mime = "image/jpeg",
        string fileName = "photo.jpg",
        byte[]? content = null,
        Action<CsamTestStorage>? configureStorage = null,
        Action<ProjectFile>? beforeScan = null,
        bool simulateConcurrentQuarantine = false,
        Action<InferenceApiDbContext, ProjectFile>? beforeMatcher = null,
        MassTransit.IPublishEndpoint? publisher = null)
    {
        options ??= CsamTestSupport.EnabledOptions();
        byte[] bytes = content ?? [1, 2, 3, 4];
        string databaseName = $"csam-{Guid.NewGuid():N}";
        InferenceApiDbContext db = CsamTestSupport.NewDb(databaseName);
        ProjectFile file = CsamTestSupport.AddFile(db, mime, fileName, bytes);
        beforeScan?.Invoke(file);
        db.SaveChanges();

        if (simulateConcurrentQuarantine)
        {
            // A second context on the same InMemory store: another scan quarantined the row after
            // this matcher loaded it but before it persisted its (no-match) verdict.
            using InferenceApiDbContext concurrent = CsamTestSupport.NewDb(databaseName);
            ProjectFile other = concurrent.ProjectFiles.IgnoreQueryFilters().Single(f => f.Id == file.Id);
            other.CsamStatus = CsamScanStatus.Quarantined;
            other.CsamClassification = "csam";
            other.CsamMatchType = "exact";
            other.CsamQuarantineKey = $"projects/{file.ProjectId}/quarantine/{file.Id}/x.jpg";
            await concurrent.SaveChangesAsync();
        }

        CsamTestStorage storage = new();
        storage.Put(file.StorageKey, bytes);
        beforeMatcher?.Invoke(db, file);
        configureStorage?.Invoke(storage);

        handler ??= new CsamRecordingHandler(server ?? new CsamFakeServer());
        using HttpClient http = CsamTestSupport.NewHttpClient(options, handler);
        ArachnidShieldClient client = new(http, Options.Create(options));
        CsamListLogger<CsamHashMatcher> logger = new();
        CsamHashMatcher matcher = new(
            db, storage, hasher ?? new FixedPdqHasher(), client,
            new CsamQuarantineService(storage), Options.Create(options), logger, clock: null, publisher);

        CsamScanOutcome outcome = await matcher.ScanAsync(file, Guid.NewGuid(), CancellationToken.None);

        return new Harness(file, outcome, storage, handler, logger, db);
    }

    private sealed class Harness(
        ProjectFile file,
        CsamScanOutcome outcome,
        CsamTestStorage storage,
        CsamRecordingHandler handler,
        CsamListLogger<CsamHashMatcher> logger,
        InferenceApiDbContext db) : IDisposable
    {
        public ProjectFile File { get; } = file;
        public CsamScanOutcome Outcome { get; } = outcome;
        public CsamTestStorage Storage { get; } = storage;
        public CsamRecordingHandler Handler { get; } = handler;
        public CsamListLogger<CsamHashMatcher> Logger { get; } = logger;
        public InferenceApiDbContext Db { get; } = db;

        public void Dispose() => Db.Dispose();
    }
}
