using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.S3;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.History;

/// <summary>
/// Project versioning keeps old bytes in ONE place: <see cref="ProjectHistoryProtectingStorage"/>, a
/// decorator over <see cref="IFileStorageService"/>. A class that takes <see cref="IAmazonS3"/> directly can
/// overwrite, delete or move a live <c>userFiles</c>/<c>agentFiles</c> object without that protection, so
/// this test fails when a new one appears and forces a decision: route it through the storage service, or
/// add it to the allowlist below with the reason it cannot touch a live project file key.
/// <para>
/// Verified to write live project file keys through <see cref="IFileStorageService"/> (so protected, no
/// raw S3 dependency): ProjectFilesController (UpdateContent, Move, RenameFolder, Delete, DeleteFolder),
/// DirectUploadService, UrlImportService, AssistantFileTools, LocalFolderSyncService, CloudSyncService,
/// GitHubRepositoryPullService, DesignSystemsController. The engine never overwrites or deletes a live
/// project file key (it writes new rows and keys; VideoCompileStepExecutor removes only output rows).
/// </para>
/// </summary>
public class ProjectHistoryWritePathInventoryTests
{
    /// <summary>Type name (without namespace) to the reason it may hold a raw S3 client.</summary>
    private static readonly Dictionary<string, string> Allowlist = new(StringComparer.Ordinal)
    {
        ["S3FileStorageService"] = "the underlying store the decorator wraps",
        ["CompositionRevisionService"] = "read-only GetObject of a render output sidecar; never writes or deletes",
        ["MediaController"] = "read-only streaming",
        ["SegmentationMaskPreviewController"] = "read-only streaming of a mask preview",
        ["ProjectFileMediaController"] = "read-only streaming",
        ["ReviewLinksPublicController"] = "read-only streaming of review outputs",
        ["MarketplaceAdminController"] = "read-only ranged streaming of a marketplace preview or poster under review; never writes or deletes",
        ["MarketplacePublicController"] = "read-only ranged streaming of a published marketplace preview or poster; never writes or deletes",
        ["S3MarketplaceObjectStore"] = "platform-owned marketplace/{recipeId}/ preview and poster objects, never a project file key",
        ["S3MarketplacePreviewObjects"] = "every key is checked to sit under the platform marketplace root before any read, copy, move or delete; never a project file key",
        ["OutputLinksController"] = "read-only presign of outputs",
        ["OutputsController"] = "deletes only addressable output keys (ProjectOutputKeys.IsAddressableOutput), never userFiles/agentFiles",
        ["StepResultArtifactsController"] = "writes step-result artifacts under outputs, never a live project file key",
        ["S3StorageObjectCopier"] = "server-side copy to a new key; the source is never modified",
        ["S3MultipartObjectStore"] = "direct uploads always target a NEW server-generated key; the replaced object is deleted afterwards through IFileStorageService",
        ["ImagePreviewPrewarmConsumer"] = "writes and deletes only derived WebP previews at ImagePreviewStore.Key (agentFiles/previews/{hash}-w{width}.webp), never a live project file key; reads the output source and deletes the preview if the source vanished",
        ["ImageDocumentCommands"] = "image-editor scratch objects, outside userFiles/agentFiles",
        ["AccountPurgeService"] = "account purge deletes the whole projects/{p}/ prefix, history included",
        ["AccountExportService"] = "writes and removes its own export bundle, not project file keys",
        ["S3LibraryAssetStorage"] = "design-libraries/ prefix, not a project file key",
        ["S3SfxLibraryObjectCopier"] = "server-side copy of a built-in library object to a NEW, deterministic project key built from the library row; the source is never modified and the key is never a request value",
        ["S3AvatarLibraryStore"] = "the workspace avatar library's own objects under organizations/{org}/avatar-library/ (every library key is checked against that layout before any read, copy, list or delete); the one project key it writes is the NEW, server-built key of an import's copy, and a project object it copies from is never modified",
        ["S3SfxLibraryStorage"] = "every key is checked to sit under the built-in sound library's own prefix (SfxLibraryKeys.IsLibraryKey) before any read or write; never a project file key",
    };

    [Fact]
    public void Only_allowlisted_types_take_a_direct_s3_client()
    {
        System.Reflection.Assembly api = typeof(IFileStorageService).Assembly;

        List<string> offenders = api.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAmazonS3))))
            .Select(t => t.Name)
            .Where(n => !Allowlist.ContainsKey(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "a class with a raw IAmazonS3 can bypass the project-history decorator; route it through IFileStorageService or allowlist it with a reason");
    }
}
