#nullable enable
namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The step configs of prod run 04ef3b0a (read-only SELECT of workflow_steps), verbatim except the project
/// file id, which is the placeholder <c>__PROJECT_FILE_ID__</c> (swap for a fresh test id). Chain:
/// 1 VideoAnalyze (segmentation track "digital speedometer") -> 2 VideoCompile (Landscape, Crop, keepWholeSources,
/// regionEffects Blur + censorLabels on the same track) -> 3 VideoAnalyze of step 2's output -> 4 VideoCompile
/// (Vertical, Pad + canvasFill Blur, keepWholeSources, "vertical-filled.mp4").
/// </summary>
internal static class ProdRun04ef3b0aConfigs
{
    public const string PlaceholderProjectFileId = "__PROJECT_FILE_ID__";

    public const string Step1Analyze = """{"expect": null, "source": {"kind": "ProjectFile", "projectFileId": "__PROJECT_FILE_ID__"}, "version": 1, "language": null, "detectShots": true, "subShotMode": "Content", "minSilenceMs": 350, "trackObjects": [{"label": "digital speedometer", "method": "Segmentation"}], "visualDetail": "Compact", "detectSilence": true, "maxInputBytes": 2000000000, "maxSubShotSec": 4, "minSubShotSec": 1.2, "transcription": "Off", "analyzeVisuals": true, "maxOutputChars": 24000, "sceneThreshold": 0.3, "wordTimestamps": true, "detectLetterbox": true, "detectSharpness": false, "maxViewSegments": 400, "visualGridWidth": 32, "visualSampleFps": 2, "detectLookGroups": true, "keyframesPerShot": 1, "maxAsrChunkBytes": 20000000, "minStillWindowMs": 400, "visualGridHeight": 18, "maxSharpnessShots": 24, "maxViewLookGroups": 12, "analyzeAudioLevels": true, "maxDurationSeconds": 1800, "maxSubShotsPerShot": 8, "objectTrackMaxSide": 1280, "silenceThresholdDb": -34, "analyzeColorGrading": true, "maxSegmentTextChars": 160, "detectNearDuplicates": true, "duplicateWindowShots": 20, "objectDetectEverySec": 0.5, "stillMotionThreshold": 0.02, "maxObjectDetectFrames": 24, "maxVisualSampleFrames": 4000, "allowSourceMediaEgress": true, "maxSegmentationObjects": 1, "maxStillWindowsPerShot": 3, "maxViewDuplicateGroups": 20, "segmentationProviderId": "bfcdeae5-b80d-48e5-87f8-cd350f594e29", "lookSimilarityThreshold": 0.88, "transcriptionProviderId": null, "duplicateSimilarityThreshold": 0.9, "segmentationDetectionThreshold": 0.35}""";

    public const string Step2Compile = """{"crf": 20, "mode": "Reencode", "expect": null, "preset": "veryfast", "version": 1, "decision": {"from": "Previous"}, "dipCutMs": 220, "outputFit": "Crop", "softCutMs": 200, "audioCodec": "aac", "dissolveMs": 500, "videoCodec": "libx264", "censorStyle": "Blur", "maxOverlays": 20, "maxSegments": 200, "censorLabels": ["digital speedometer"], "dipToBlackMs": 600, "graphicsPlan": null, "minSegmentMs": 250, "outputFormat": "Landscape", "prePaddingMs": 80, "censorHoldSec": 0.4, "censorPadding": 0, "overlayFadeMs": 300, "overlayHoldMs": 6000, "postPaddingMs": 120, "regionEffects": [{"effect": "Blur", "target": "digital speedometer", "opacity": 1, "strength": 1, "featherPx": 8}], "takeSelection": null, "enableGraphics": false, "generatedClips": null, "outputFileName": "censored-no-bars.mp4", "overlayShortMs": 1500, "audioSeamRampMs": 24, "maxTransitionMs": 1200, "overlayBoxColor": "black@0.45", "overlayMediumMs": 3000, "programFadeInMs": 0, "keepWholeSources": true, "overlayFontColor": "white", "programFadeColor": "black", "programFadeOutMs": 0, "transitionPolicy": "Off", "analysisStepOrder": 1, "maxGeneratedClips": 8, "sectionBreakGapMs": 8000, "overlayFontSizePct": 5, "maxOverlayTextChars": 80, "registerProjectFile": true, "analysisStepResultId": null, "enableGeneratedClips": false, "programAudioFadeInMs": 0, "allowKeyframeSnapping": false, "maxTransitionRatioPct": 35, "maxTransitionSegments": 80, "programAudioFadeOutMs": 0, "maxOverlaySubtextChars": 60}""";

    public const string Step3Analyze = """{"source": {"kind": "StepOutput", "stepOrder": 2}, "version": 1, "subShotMode": "Off", "transcription": "Off"}""";

    public const string Step4Compile = """{"mode": "Reencode", "version": 1, "decision": {"from": "Previous"}, "outputFit": "Pad", "canvasFill": "Blur", "minSegmentMs": 250, "outputFormat": "Vertical", "outputFileName": "vertical-filled.mp4", "keepWholeSources": true, "transitionPolicy": "Off", "analysisStepOrder": 3, "registerProjectFile": true}""";
}
