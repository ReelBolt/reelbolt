using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class ProjectFileWorkspaceDeleteScopeTests
{
    private static readonly Guid P = Guid.NewGuid();
    private static string Ok => $"projects/{P}/agentFiles/voiceover/lines/abc.wav";

    [Fact]
    public void A_voiceover_key_of_the_project_is_allowed()
        => FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(P, Ok)).Should().NotThrow();

    [Fact]
    public void An_audio_cleanup_temporary_upload_of_the_project_is_allowed_and_another_projects_is_not()
    {
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(P, $"projects/{P}/agentFiles/audio-cleanup/{Guid.NewGuid()}/source.wav")).Should().NotThrow();
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(P, $"projects/{Guid.NewGuid()}/agentFiles/audio-cleanup/x/source.wav")).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(P, $"projects/{P}/agentFiles/audio-cleanup/../voiceover/x.wav")).Should().Throw<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("other-project")]
    [InlineData("same-project-elsewhere")]
    [InlineData("traversal")]
    [InlineData("backslash")]
    [InlineData("encoded")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("bare-prefix")]
    [InlineData("double-slash")]
    public void Everything_else_is_refused(string kind)
    {
        string key = kind switch
        {
            "other-project" => $"projects/{Guid.NewGuid()}/agentFiles/voiceover/lines/abc.wav",
            "same-project-elsewhere" => $"projects/{P}/agentFiles/other/abc.wav",
            "traversal" => $"projects/{P}/agentFiles/voiceover/../../secret.wav",
            "backslash" => $"projects/{P}/agentFiles/voiceover/lines\\abc.wav",
            "encoded" => $"projects/{P}/agentFiles/voiceover/%2e%2e/abc.wav",
            "empty" => "",
            "whitespace" => "  ",
            "bare-prefix" => $"projects/{P}/agentFiles/voiceover/",
            _ => $"projects/{P}/agentFiles/voiceover//abc.wav"
        };
        FluentActions.Invoking(() => ProjectFileWorkspace.ValidateDeleteScope(P, key)).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task The_s3_delete_is_issued_for_an_allowed_key_and_never_for_a_refused_one()
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());
        var ws = new ProjectFileWorkspace(s3.Object, new Mock<IServiceScopeFactory>().Object,
            new ConfigurationBuilder().Build(), NullLogger<ProjectFileWorkspace>.Instance);

        await ws.DeleteArtifactAsync(P, Ok, CancellationToken.None);
        s3.Verify(c => c.DeleteObjectAsync(It.Is<DeleteObjectRequest>(r => r.Key == Ok), It.IsAny<CancellationToken>()), Times.Once);

        await FluentActions.Awaiting(() => ws.DeleteArtifactAsync(P, $"projects/{P}/agentFiles/other/x.wav", CancellationToken.None))
            .Should().ThrowAsync<UnauthorizedAccessException>();
        s3.Verify(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
