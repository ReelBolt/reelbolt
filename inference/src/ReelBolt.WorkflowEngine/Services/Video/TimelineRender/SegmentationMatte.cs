namespace ReelBolt.WorkflowEngine.Services.Video.TimelineRender;

/// <summary>One track of one video analysis: the key a segmentation mask clip names its masks by.</summary>
public sealed record SegmentationMaskRef(Guid AnalysisStepResultId, string TrackId);

/// <summary>A mask video already downloaded and hash-verified (<c>ContentHash</c> is the verified SHA-256).</summary>
public sealed record LocalSegmentationMask(string LocalPath, string ContentHash);
