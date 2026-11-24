namespace ReelBolt.Inference.Api.Controllers.Dto;

/// <summary>
/// <c>POST api/v1/projects/{id}/files/import-url</c>: fetch one file from a public https URL into the
/// project's files. <see cref="FileName"/> defaults to the URL path's last segment; <see cref="DirectoryPath"/>
/// to the project root.
/// </summary>
public record ImportProjectFileFromUrlRequest(
    string Url,
    string? FileName = null,
    string? DirectoryPath = null);

/// <summary>Body of <c>POST files/sample</c>: which bundled sample to copy into the project.</summary>
public record AddSampleFileRequest(string? SampleKey);
