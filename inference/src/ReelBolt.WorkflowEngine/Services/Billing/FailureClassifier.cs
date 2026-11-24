using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.WorkflowEngine.Services.Billing;

/// <summary>
/// Decides WHO bears the cost of the work a failed step consumed (decision D8; the mapping table is
/// docs/design/saas-metering.md "Failure classes"). B6a's <see cref="ICreditGate.ReleaseAsync"/>
/// takes the answer as its <see cref="FailureClass"/> argument: the platform-caused classes
/// (<c>PlatformProviderError</c>, <c>PlatformInternal</c>) and anything we cannot attribute
/// (<c>None</c>, or an undefined member) refund the step; every other class keeps the charge for
/// what the step actually consumed.
///
/// <para>
/// Classify at the point of production wherever a class is known (a coded executor failure, a
/// thrown exception type, the cancellation registry's recorded provenance) — this type is the one
/// place that mapping lives, and it is deliberately a pure, static function so it can be unit
/// tested without a database. A code or exception we do not recognise is
/// <see cref="FailureClass.PlatformInternal"/>: refunding a failure we cannot explain is the
/// deliberate default (docs/design/saas-metering.md, "Founder decisions"), because charging a user
/// for our own unexplained failure is the worse error.
/// </para>
///
/// <para>
/// Provider-owner codes (the <c>TRANSCRIPTION_*</c>/<c>VISION_*</c>/<c>PROVIDER_*</c> family)
/// classify as platform-caused here: the B1 rule is "follow the resolved provider's owner", and a
/// BYO row would make them <see cref="FailureClass.ByoProviderError"/> instead — a distinction the
/// executor does not have at classification time (B3b records the billing mode on the usage row,
/// not on the step result). Both readings refund for the platform case and only diverge for BYO,
/// where the LLM kinds were free anyway; the drift is the conservative direction (refund). A caller
/// that DOES know the owner can set <c>StepExecutionResult.FailureClass</c> and win over the table.
/// </para>
/// </summary>
public static class FailureClassifier
{
    /// <summary>
    /// <c>SCREAMING_SNAKE</c>, at least one underscore — the shape every failure code in this
    /// codebase already has (asserted by <c>FailureClassifierDriftGuardTests</c> against the source
    /// tree, so a new code cannot ship unmapped).
    /// </summary>
    public static readonly System.Text.RegularExpressions.Regex CodePattern =
        new("^[A-Z][A-Z0-9]*(_[A-Z0-9]+)+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Every failure code the engine can produce, mapped to the class that decides who pays.
    /// Straight from docs/design/saas-metering.md "Mapping: coded failures from deterministic
    /// executors"; a code whose grep evidence is an executor's own <c>Failure(...)</c> helper.
    /// </summary>
    private static readonly Dictionary<string, FailureClass> CodeTable = new(StringComparer.Ordinal)
    {
        // --- Config and wiring: the user's own workflow definition ---------------------------
        ["COLOR_NOT_ALLOWED"] = FailureClass.UserInput,
        ["CONFIG_INVALID"] = FailureClass.UserInput,
        // Voiceover spend bound: the step's declared words exceed Voiceover:MaxAdmittedNarrationWords.
        // Nothing was synthesized (the check is pre-flight), and the script is the author's.
        ["NARRATION_TOO_MANY_WORDS"] = FailureClass.UserInput,
        ["TOO_MANY_ITEMS"] = FailureClass.UserInput,
        // Content moderation (content-moderation T-005): a generation prompt the moderation gate
        // refused at high confidence. Nothing was reserved or submitted, and the prompt is the author's.
        ["CONTENT_BLOCKED"] = FailureClass.UserInput,
        ["INPUT_MISSING"] = FailureClass.UserInput,
        ["PATH_NOT_FOUND"] = FailureClass.UserInput,
        ["SHAPE_MISMATCH"] = FailureClass.UserInput,
        ["UNKNOWN_ID"] = FailureClass.UserInput,
        ["EXPECT_FAILED"] = FailureClass.UserInput,
        ["VIEW_UNRESOLVED"] = FailureClass.UserInput,
        ["SOURCE_UNRESOLVED"] = FailureClass.UserInput,
        ["SOURCE_NOT_FOUND"] = FailureClass.UserInput,
        // ThumbnailGenerate: a brand font or explicit source the caller named, and a time past the source window.
        ["FONT_NOT_FOUND"] = FailureClass.UserInput,
        ["FONT_INVALID"] = FailureClass.UserInput,
        ["SOURCE_NOT_ANALYSED"] = FailureClass.UserInput,
        ["AT_TIME_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["SOURCE_NOT_PLAYABLE"] = FailureClass.UserInput, // T-023: same-project StepResult that failed, is unfinished or is not a video
        ["COMPOSITION_SOURCE_NOT_FOUND"] = FailureClass.UserInput, // continue-composition source outside the run's project
        ["CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT"] = FailureClass.UserInput,
        ["SOURCE_INVALID"] = FailureClass.UserInput,
        ["SOURCE_TRIM_INVALID"] = FailureClass.UserInput,
        ["INPUT_TOO_LARGE"] = FailureClass.UserInput,
        // The shared source limit (VideoSourceLimits): refused before anything was spent. The old codes stay
        // mapped for rows already stored.
        ["SOURCE_TOO_LARGE"] = FailureClass.UserInput,
        ["SOURCE_TOO_LONG"] = FailureClass.UserInput,
        // The scratch disk could not take the source in time, or a stage ran out of space: ours, refunded.
        ["SCRATCH_BUSY"] = FailureClass.PlatformInternal,
        // A big clip whose analysis outran its deadline: our limit, not the author's mistake, so the step is refunded.
        ["SOURCE_TOO_DEMANDING"] = FailureClass.PlatformInternal,
        ["DURATION_EXCEEDED"] = FailureClass.UserInput,
        ["BYTE_BUDGET_EXCEEDED"] = FailureClass.UserInput,
        ["MAX_CLIPS_EXCEEDED"] = FailureClass.UserInput,
        ["CODEC_NOT_ALLOWED"] = FailureClass.UserInput,
        ["BUDGET_EXCEEDED"] = FailureClass.UserInput,
        ["EMPTY_PLAN"] = FailureClass.UserInput,
        ["INVALID_PROMPT"] = FailureClass.UserInput,
        ["EGRESS_REFUSED"] = FailureClass.UserInput,
        ["SEEDED_TARGET_NOT_ALLOWED"] = FailureClass.UserInput,
        // Prompted region effects (initiative "prompted region effects"): a VideoAnalyze step's
        // text-prompted segmentation target. The provider row's OWNER decides the provider-error class and
        // is not knowable here, so the producer records the real class via
        // StepExecutionContext.RecordFailure and wins over this table; the entries below are the
        // conservative platform reading (refund) for any path that reaches classification without it.
        ["SEGMENTATION_NO_PROVIDER"] = FailureClass.UserInput,
        ["SEGMENTATION_EGRESS_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["SEGMENTATION_PROVIDER_ERROR"] = FailureClass.PlatformProviderError,
        ["SEGMENTATION_BAD_RESPONSE"] = FailureClass.PlatformInternal,
        // A tap that found no object: the customer is not charged (capped per user and project, see ObjectAnalysisService).
        ["OBJECT_NOT_FOUND_AT_POINT"] = FailureClass.PlatformInternal,
        // Cancellations and the generic fallback (FailureMessages): a user Stop keeps consumption, the rest refund.
        ["USER_CANCELLED"] = FailureClass.UserCancelled,
        ["RUN_STOPPED"] = FailureClass.PlatformInternal,
        ["STEP_INTERNAL_ERROR"] = FailureClass.PlatformInternal,
        ["RUN_INTERRUPTED"] = FailureClass.PlatformInternal,
        // docs/design/app-inputs.md 6.3: the workflow changed between Run and the claim; ends before step 1, nothing held.
        ["APP_INPUTS_STALE"] = FailureClass.PlatformInternal,
        ["NO_RENDER_PRODUCED"] = FailureClass.PlatformInternal,
        ["KEYFRAME_NOT_SUPPORTED"] = FailureClass.UserInput,
        // The engine could not open the media session the frames are cut in (no runner online, a
        // session lease busy): our infrastructure, and nothing was bought.
        ["KEYFRAME_MEDIA_UNAVAILABLE"] = FailureClass.PlatformInternal,
        // The narration our own script/voice step produced does not fit its scene even at the maximum
        // speed-up: platform-caused, so refunded. DUAL MAPPING: this table default is the platform case; the
        // Voiceover step sets an explicit UserInput class (which wins over this table) when the narration is
        // the user's own Inline text, so that spend is kept.
        ["NARRATION_DOES_NOT_FIT"] = FailureClass.PlatformInternal,
        ["NARRATION_ANCHOR_LOST"] = FailureClass.PlatformInternal,
        ["VOICE_NOT_FOUND"] = FailureClass.UserInput,
        ["VOICE_CONSENT_MISSING"] = FailureClass.UserInput,
        // Avatars (docs/avatars.md "The engine avatar resolver"): an unusable avatar is the customer's input to fix and
        // fails the step, never falls back to no avatar. Raised by IAvatarResolver.ResolveRequiredAsync.
        ["AVATAR_NOT_FOUND"] = FailureClass.UserInput,
        ["AVATAR_CONSENT_REVOKED"] = FailureClass.UserInput,
        ["AVATAR_CONSENT_MISSING"] = FailureClass.UserInput,
        // Avatars phase 2 (VideoGenerate): the resolved model cannot carry the avatar (no reference mode, a frame
        // alongside, a driving audio track) is the step's own setting; the switch being off denies the feature;
        // a reference that could not be read from OUR storage is ours (refunded, nothing was submitted).
        ["AVATAR_NOT_SUPPORTED_BY_MODEL"] = FailureClass.UserInput,
        ["AVATAR_GENERATION_DISABLED"] = FailureClass.EntitlementDenied,
        ["AVATAR_REFERENCE_UNAVAILABLE"] = FailureClass.PlatformInternal,
        // Avatars phase 3 (the still-then-animate path): a missing or wrong-length narration, a missing still model and
        // an avatar with no voice to speak in are the customer's to fix; a narration or still we could not read, make or
        // store is ours (refunded; no video was submitted).
        ["AVATAR_AUDIO_UNAVAILABLE"] = FailureClass.UserInput,
        ["AVATAR_AUDIO_LENGTH"] = FailureClass.UserInput,
        // A stored reference recording that is not WAV or MP3 by its bytes: refused by our client before any request, the
        // customer re-uploads it (nothing submitted, nothing charged).
        ["AVATAR_REFERENCE_AUDIO_FORMAT"] = FailureClass.UserInput,
        ["AVATAR_AUDIO_UNREADABLE"] = FailureClass.PlatformInternal,
        ["AVATAR_STILL_PROVIDER_MISSING"] = FailureClass.UserInput,
        ["AVATAR_STILL_MODEL_UNSUPPORTED"] = FailureClass.UserInput,
        ["AVATAR_STILL_FAILED"] = FailureClass.PlatformInternal,
        ["AVATAR_VOICE_MISSING"] = FailureClass.UserInput,
        ["VOICE_CHOICE_REQUIRED"] = FailureClass.UserInput,
        ["PROVIDER_NOT_FOUND"] = FailureClass.UserInput,
        ["PROVIDER_NOT_RESOLVED"] = FailureClass.UserInput,
        ["NO_LINES"] = FailureClass.UserInput,
        ["SCRIPT_SOURCE_INVALID"] = FailureClass.UserInput,
        ["SCRIPTWRITER_NOT_FOUND"] = FailureClass.UserInput,
        ["NARRATION_SOURCE_INVALID"] = FailureClass.UserInput,
        ["NARRATION_STEP_NOT_FOUND"] = FailureClass.UserInput,
        ["PICKUP_SOURCE_INVALID"] = FailureClass.UserInput,
        ["PICKUP_STEP_NOT_FOUND"] = FailureClass.UserInput,
        ["DECISION_UNRESOLVED"] = FailureClass.UserInput,
        ["ANALYSIS_NOT_FOUND"] = FailureClass.UserInput,
        // TimelineRenderStepExecutor (editor v2, P4). The step's config and the editor document are the
        // author's input: a config that fails Validate(), a timeline id that is not in the project, a
        // revision with no stored document, a document the validator rejects, and an asset reference that
        // does not resolve to (or lies outside) this project are all theirs to fix.
        ["INVALID_CONFIG"] = FailureClass.UserInput,
        ["TIMELINE_NOT_FOUND"] = FailureClass.UserInput,
        ["REVISION_NOT_RESTORABLE"] = FailureClass.UserInput,
        ["DOCUMENT_INVALID"] = FailureClass.UserInput,
        ["LUT_INVALID"] = FailureClass.UserInput,
        ["LUT_UNSUPPORTED"] = FailureClass.UserInput,
        ["ASSET_UNRESOLVED"] = FailureClass.UserInput,
        ["ASSET_OUTSIDE_PROJECT"] = FailureClass.UserInput,
        // DesignSystemStepExecutor (product design system, P2): a missing design profile, a config
        // that fails Validate(), and a project with no design sources at all are the author's.
        ["PROFILE_MISSING"] = FailureClass.UserInput,
        ["DESIGN_CONFIG_INVALID"] = FailureClass.UserInput,
        ["DESIGN_NO_SOURCES"] = FailureClass.UserInput,

        // Incompatible option combinations: pure config, all rejected by the compile's own checks.
        ["CAPTIONS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        // Subtitles from the footage's own speech when the analysis found none: the clip has nothing to caption.
        ["CAPTIONS_NO_TRANSCRIPT"] = FailureClass.UserInput,
        ["CENSOR_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["COLOR_GRADE_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["GENERATED_CLIPS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["GRAPHICS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["INSERTS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        // A screen insert asked for with nothing to put on the screen, or content that found no screen to go on.
        ["INSERT_CONTENT_MISSING"] = FailureClass.UserInput,
        ["INSERT_NOT_APPLIED"] = FailureClass.UserInput,
        ["INSERT_CONTENT_UNUSABLE"] = FailureClass.UserInput,
        // Our video tools lack a filter the insert needs: not the person's doing.
        ["INSERT_FILTER_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["INSERT_AUDIO_REQUIRES_INSERTS"] = FailureClass.UserInput,
        ["INSERT_AUDIO_REQUIRES_AUDIO_REENCODE"] = FailureClass.UserInput,
        ["INSERT_AUDIO_GAIN_OUT_OF_RANGE"] = FailureClass.UserInput,
        // Pictures and videos over the film (docs/design/picture-overlay.md 6.5): a configured overlay that cannot
        // resolve or is not usable fails the compile; every one is the person's own configuration.
        ["MEDIA_OVERLAY_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["MEDIA_OVERLAY_REF_INVALID"] = FailureClass.UserInput,
        ["MEDIA_OVERLAY_CONTENT_NOT_FOUND"] = FailureClass.UserInput,
        ["MEDIA_OVERLAY_CONTENT_UNUSABLE"] = FailureClass.UserInput,
        ["MEDIA_OVERLAY_SPAN_INVALID"] = FailureClass.UserInput,
        ["MEDIA_OVERLAY_OPACITY_INVALID"] = FailureClass.UserInput,
        ["MEDIA_OVERLAY_LIMIT_EXCEEDED"] = FailureClass.UserInput,
        ["MULTI_SOURCE_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["MUSIC_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["MUSIC_REQUIRES_AUDIO_REENCODE"] = FailureClass.UserInput,
        ["OUTPUT_FORMAT_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["PICKUPS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["PROGRAM_AUDIO_FADE_REQUIRES_AUDIO_REENCODE"] = FailureClass.UserInput,
        ["REORDER_REQUIRES_REENCODE"] = FailureClass.UserInput,
        // Keyframed reframing, per-source fits and the subject safe area (VideoReframeCodes): config
        // the customer wrote, refused at save time and again by the compile before it builds a graph.
        ["REFRAME_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["REFRAME_UNKNOWN_SOURCE"] = FailureClass.UserInput,
        ["REFRAME_RANGE_INVALID"] = FailureClass.UserInput,
        ["REFRAME_NO_KEYFRAMES"] = FailureClass.UserInput,
        ["REFRAME_TOO_MANY_KEYFRAMES"] = FailureClass.UserInput,
        ["REFRAME_TOO_MANY_REFRAMES"] = FailureClass.UserInput,
        ["REFRAME_KEYFRAMES_UNSORTED"] = FailureClass.UserInput,
        ["REFRAME_KEYFRAME_OUTSIDE_RANGE"] = FailureClass.UserInput,
        ["REFRAME_ZOOM_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["REFRAME_CENTER_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["REFRAME_EASING_UNKNOWN"] = FailureClass.UserInput,
        ["REFRAME_OVERLAP"] = FailureClass.UserInput,
        ["SOURCE_FIT_UNKNOWN_SOURCE"] = FailureClass.UserInput,
        // followSubject (FollowSubjectCodes): config errors, plus a track the analysis does not hold or holds empty.
        ["FOLLOW_TOO_MANY"] = FailureClass.UserInput,
        ["FOLLOW_TRACK_ID_INVALID"] = FailureClass.UserInput,
        ["FOLLOW_RANGE_INVALID"] = FailureClass.UserInput,
        ["FOLLOW_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["FOLLOW_CONFLICTS_REFRAME"] = FailureClass.UserInput,
        ["FOLLOW_TRACK_NOT_FOUND"] = FailureClass.UserInput,
        ["FOLLOW_TRACK_EMPTY"] = FailureClass.UserInput,
        ["EVENT_MOVES_TOO_MANY"] = FailureClass.UserInput,
        ["EVENT_MOVES_EVENT_ID_INVALID"] = FailureClass.UserInput,
        ["EVENT_MOVES_WORD_UNKNOWN"] = FailureClass.UserInput,
        ["EVENT_MOVES_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["FLASH_CUES_TOO_MANY"] = FailureClass.UserInput,
        ["FLASH_CUES_EVENT_ID_INVALID"] = FailureClass.UserInput,
        ["FLASH_CUES_WORD_UNKNOWN"] = FailureClass.UserInput,
        ["FLASH_CUES_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["EFFECT_WINDOW_EVENT_INVALID"] = FailureClass.UserInput,
        ["SPEED_RAMPS_TOO_MANY"] = FailureClass.UserInput,
        ["SPEED_RAMPS_EVENT_ID_INVALID"] = FailureClass.UserInput,
        ["SPEED_RAMPS_WORD_UNKNOWN"] = FailureClass.UserInput,
        ["SPEED_RAMPS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["SPEED_RAMPS_ENCODE_FAILED"] = FailureClass.PlatformInternal,
        ["EVENT_CUT_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["EVENT_CUT_NOT_IN_SOURCE"] = FailureClass.UserInput,
        ["SUBJECT_SAFE_AREA_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["REFRAME_ANCHOR_UNKNOWN"] = FailureClass.UserInput,
        ["FOLLOW_OVERLAP"] = FailureClass.UserInput,
        ["SFX_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["SFX_REQUIRES_AUDIO_REENCODE"] = FailureClass.UserInput,
        ["NO_CANDIDATE_SHOT"] = FailureClass.UserInput,
        ["STREAMCOPY_REQUIRES_KEYFRAME_SNAPPING"] = FailureClass.UserInput,
        ["TRANSITIONS_REQUIRE_REENCODE"] = FailureClass.UserInput,
        ["VOICEOVER_MODE_REQUIRES_REENCODE"] = FailureClass.UserInput,

        // Prompted region effects: the CONFIG refusals. RegionEffectRules.Validate is pure arithmetic
        // over the author's own effect list, so every one of these is a config typo to fix rather than a
        // failure to refund; EFFECT_REQUIRES_REENCODE is the same shape as the REENCODE block above.
        ["EFFECT_INVALID"] = FailureClass.UserInput,
        ["EFFECT_REQUIRES_REENCODE"] = FailureClass.UserInput,
        ["EFFECT_TARGET_INVALID"] = FailureClass.UserInput,
        ["EFFECT_STRENGTH_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["EFFECT_FEATHER_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["EFFECT_OPACITY_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["EFFECT_LIMIT_EXCEEDED"] = FailureClass.UserInput,
        // ...and the TWO analyze refusals in the family, both refused before any provider call.
        // SEGMENTATION_SOURCE_TOO_LONG: the author picked a clip longer than the MaxSegmentationSeconds cap
        // they (or the default) set. SEGMENTATION_FRAME_BUDGET_EXCEEDED: the clip's own probed frame count
        // is more than the credit hold priced (the hold assumes 30 fps), so running it would spend past the
        // reservation. Nothing was bought either way, and the fix (shorten the clip, lower its frame rate,
        // raise the cap) is theirs. Deliberately the ONLY SEGMENTATION_* codes here: the provider, HTTP and
        // decode failures below stay with the platform, and the two unverified-mask/target codes stay
        // explicitly mapped to PlatformInternal further down so a blanket "EFFECT_*/SEGMENTATION_* is user
        // input" reading cannot creep in.
        ["SEGMENTATION_SOURCE_TOO_LONG"] = FailureClass.UserInput,
        ["SEGMENTATION_FRAME_BUDGET_EXCEEDED"] = FailureClass.UserInput,

        // --- Our own code and our own platform providers --------------------------------------
        ["ANALYSIS_INVALID"] = FailureClass.PlatformInternal,
        ["ANALYSIS_ARTIFACT_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["ANALYSIS_SOURCE_UNRESOLVED"] = FailureClass.PlatformInternal,
        ["COMPOSITION_RESTORE_FAILED"] = FailureClass.PlatformInternal,
        ["CENSOR_UNVERIFIED"] = FailureClass.PlatformInternal,
        ["INSERT_TRACKING_FAILED"] = FailureClass.PlatformInternal,
        // Prompted region effects (T-005). "The analyze step never looked for this prompt" and
        // "two tracks share this prompt" are the same shape as CENSOR_UNVERIFIED — a config pairing
        // between two steps — so they are classified with it rather than as fresh user-input codes.
        ["EFFECT_TARGET_UNVERIFIED"] = FailureClass.PlatformInternal,
        ["EFFECT_TARGET_AMBIGUOUS"] = FailureClass.PlatformInternal,
        // T-017: an all-empty mask is the analyze/compile pairing producing nothing to render — the same
        // shape as its two siblings above, not the caller's input. The mask the analyze step stored says
        // the object is on no frame, so the compile refuses rather than publishing a no-op as applied.
        ["EFFECT_TARGET_EMPTY"] = FailureClass.PlatformInternal,
        // T-022/D-23: a prompt that matches more tracks than one effect may paint through. The pairing is
        // ours to refuse, like its siblings, and nothing was downloaded before it fired.
        ["EFFECT_TARGET_TOO_BROAD"] = FailureClass.PlatformInternal,
        // A mask whose bytes/size/frames disagree with the artifact is our own storage or decode
        // pipeline disagreeing with itself; never the caller's input.
        ["SEGMENTATION_MASK_UNVERIFIED"] = FailureClass.PlatformInternal,
        ["DELIVERY_FAILED"] = FailureClass.PlatformInternal,
        ["DOWNLOAD_FAILED"] = FailureClass.PlatformInternal,
        ["ENCODE_FAILED"] = FailureClass.PlatformInternal,
        // ThumbnailGenerateStepExecutor. FRAME_EXTRACTION_FAILED/FRAME_EMPTY/FRAME_TOO_LARGE are
        // ffmpeg and encoder outcomes on our own compute, so the refund lands on us; the two that
        // describe the CALLER's inputs (a colour outside the allowlist, footage with no shot long
        // enough to be a cover) are the author's to fix, badly chosen footage being a user-input
        // problem rather than a platform one.
        ["FRAME_EMPTY"] = FailureClass.PlatformInternal,
        ["FRAME_EXTRACTION_FAILED"] = FailureClass.PlatformInternal,
        ["FRAME_TOO_LARGE"] = FailureClass.PlatformInternal,
        ["THUMBNAIL_FAILED"] = FailureClass.PlatformInternal,
        // The second thumbnail stage (burning the text band onto the extracted frame).
        ["THUMBNAIL_OVERLAY_FAILED"] = FailureClass.PlatformInternal,
        // ExtractFrameStepExecutor: the frame-grab itself reuses FRAME_EXTRACTION_FAILED/FRAME_EMPTY/FRAME_TOO_LARGE;
        // EXTRACT_FRAME_FAILED is its unexpected-exception catch-all. FRAME_INPUT_* are VideoGenerate's FirstFrame/
        // LastFrame refs that name nothing usable (the author's reference, so UserInput).
        ["EXTRACT_FRAME_FAILED"] = FailureClass.PlatformInternal,
        // PinInsertStepExecutor (T-206): a pin naming a region the analysis never offered or content that does not
        // resolve, and an analysis that offered no screens at all, are the author's; PIN_INSERT_FAILED is the
        // executor's unexpected-exception catch-all.
        ["PIN_INSERT_UNRESOLVED"] = FailureClass.UserInput,
        ["PIN_INSERT_NO_REGIONS"] = FailureClass.UserInput,
        ["PIN_INSERT_FAILED"] = FailureClass.PlatformInternal,
        // AudioGenerateStepExecutor (T-108). The vendor codes are AudioGenerationException.Code; the executor sets
        // ByoProviderError itself when the resolved row is the organization's own. A bad prompt / plan is the
        // author's (the vendor's suggestion is in ErrorDetails); a rejected key or an exhausted vendor account on a
        // platform row is ours; a 429/5xx or an accepted-then-failed job is the platform vendor's.
        ["NO_AUDIO_PROVIDER"] = FailureClass.UserInput,
        ["NO_READABLE_SOURCE"] = FailureClass.UserInput,
        ["CODE_ARCHIVE_UNREADABLE"] = FailureClass.UserInput,
        ["AUDIO_BAD_REQUEST"] = FailureClass.UserInput,
        ["AUDIO_REQUEST_INVALID"] = FailureClass.UserInput,
        ["AUDIO_PROVIDER_CONFIG"] = FailureClass.PlatformInternal,
        ["AUDIO_VENDOR_QUOTA"] = FailureClass.PlatformInternal,
        ["AUDIO_VENDOR_RETRYABLE"] = FailureClass.PlatformProviderError,
        ["AUDIO_VENDOR_FAILED"] = FailureClass.PlatformProviderError,
        ["AUDIO_GENERATE_FAILED"] = FailureClass.PlatformInternal,
        // ImageGenerateStepExecutor. A missing provider, a bad request, a source that is not a picture and a vendor that
        // refused the prompt or picture are the author's; a rejected key or exhausted account on a platform row, a
        // presigner the deployment lacks and the catch-all are ours; a vendor that was busy or answered nothing is the
        // platform vendor's. The executor sets ByoProviderError itself when the resolved row is the organization's own.
        ["NO_IMAGE_PROVIDER"] = FailureClass.UserInput,
        ["IMAGE_REQUEST_INVALID"] = FailureClass.UserInput,
        ["IMAGE_VENDOR_REJECTED"] = FailureClass.UserInput,
        ["SOURCE_NOT_IMAGE"] = FailureClass.UserInput,
        ["IMAGE_PROVIDER_CONFIG"] = FailureClass.PlatformInternal,
        ["IMAGE_EGRESS_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["IMAGE_VENDOR_RETRYABLE"] = FailureClass.PlatformProviderError,
        ["IMAGE_EMPTY"] = FailureClass.PlatformProviderError,
        ["IMAGE_GENERATE_FAILED"] = FailureClass.PlatformInternal,
        // Audio cleanup (local filters, audio-cleanup initiative): every failure of the first-party filter chain is
        // ours (a missing or altered model file, a probe or ffmpeg that could not run), and a cleaned file whose
        // length drifted past 50 ms is a processing fault, so all of them are refunded.
        ["AUDIO_CLEANUP_PROBE_FAILED"] = FailureClass.PlatformInternal,
        ["AUDIO_CLEANUP_FFMPEG_FAILED"] = FailureClass.PlatformInternal,
        ["RNNOISE_MODEL_MISSING"] = FailureClass.PlatformInternal,
        ["RNNOISE_MODEL_CHECKSUM_MISMATCH"] = FailureClass.PlatformInternal,
        ["AUDIO_DURATION_MISMATCH"] = FailureClass.PlatformProviderError,
        // AudioCleanupStepExecutor (hosted targets and the step's own refusals). Not having the customer's consent, a
        // provider, a short enough source, a source with sound or an output the source can carry are the author's to
        // fix; a presigner the deployment lacks, a failed upload and the catch-all are ours. Vendor failures reuse the
        // AUDIO_VENDOR_* / AUDIO_PROVIDER_CONFIG / AUDIO_REQUEST_INVALID codes above.
        ["NO_AUDIO_ISOLATION_PROVIDER"] = FailureClass.UserInput,
        ["AUDIO_SOURCE_TOO_LONG"] = FailureClass.UserInput,
        ["AUDIO_SOURCE_NO_AUDIO"] = FailureClass.UserInput,
        ["AUDIO_OUTPUT_KIND_UNSUPPORTED"] = FailureClass.UserInput,
        ["AUDIO_EGRESS_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["AUDIO_CLEANUP_UPLOAD_FAILED"] = FailureClass.PlatformInternal,
        ["AUDIO_CLEANUP_FAILED"] = FailureClass.PlatformInternal,
        ["FRAME_OUT_OF_RANGE"] = FailureClass.UserInput,
        ["FRAME_INPUT_NOT_FOUND"] = FailureClass.UserInput,
        ["FRAME_INPUT_INVALID"] = FailureClass.UserInput,
        // VideoEnhanceStepExecutor. A missing consent, provider or usable source, a source over the cap, an output over
        // the plan or the caps and a request the filters refuse are the author's to fix; the local ffmpeg path failing,
        // the deployment lacking a presigner, a failed upload and the catch-all are ours, so they refund. The vendor's
        // own codes (ENHANCE_PROVIDER_CONFIG, ENHANCE_VENDOR_QUOTA, ENHANCE_VENDOR_RETRYABLE, ENHANCE_VENDOR_FAILED,
        // ENHANCE_VENDOR_REFUSED) are classified in the hosted range below.
        ["NO_VIDEO_ENHANCE_PROVIDER"] = FailureClass.UserInput,
        ["ENHANCE_PROVIDER_CONFIG"] = FailureClass.PlatformInternal,
        ["ENHANCE_VENDOR_QUOTA"] = FailureClass.PlatformProviderError,
        ["ENHANCE_VENDOR_RETRYABLE"] = FailureClass.PlatformProviderError,
        ["ENHANCE_VENDOR_FAILED"] = FailureClass.PlatformProviderError,
        ["ENHANCE_VENDOR_REFUSED"] = FailureClass.PlatformProviderError,
        ["ENHANCE_SOURCE_TOO_LONG"] = FailureClass.UserInput,
        ["ENHANCE_SOURCE_TOO_SHORT"] = FailureClass.UserInput,
        ["ENHANCE_SOURCE_NO_VIDEO"] = FailureClass.UserInput,
        ["ENHANCE_OUTPUT_TOO_LARGE"] = FailureClass.UserInput,
        ["ENHANCE_FPS_TOO_HIGH"] = FailureClass.UserInput,
        ["ENHANCE_NOT_LARGER"] = FailureClass.UserInput,
        ["ENHANCE_INVALID_REQUEST"] = FailureClass.UserInput,
        ["ENHANCE_REQUEST_INVALID"] = FailureClass.UserInput,
        ["ENHANCE_PROBE_FAILED"] = FailureClass.PlatformInternal,
        ["ENHANCE_FFMPEG_FAILED"] = FailureClass.PlatformInternal,
        ["ENHANCE_PICTURE_INVALID"] = FailureClass.PlatformInternal,
        ["ENHANCE_DURATION_MISMATCH"] = FailureClass.PlatformProviderError,
        ["ENHANCE_EGRESS_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["ENHANCE_UPLOAD_FAILED"] = FailureClass.PlatformInternal,
        ["ENHANCE_FAILED"] = FailureClass.PlatformInternal,
        // The source measured at run time is bigger or longer than the route measured when it priced the hold: nothing the user typed
        // is wrong and nothing was uploaded, so the hold is returned and the person asks again.
        ["ENHANCE_SOURCE_CHANGED"] = FailureClass.PlatformInternal,
        ["ENHANCE_STILL_RUNNING"] = FailureClass.PlatformProviderError,
        // S-67: a FirstFrame/LastFrame object the run's own history names could not be read back from OUR storage
        // (auth, transport, a lost object). The author's reference was fine, so the step is refunded, and the step
        // answers one fixed sentence — the exception's own text never reaches the output.
        ["FRAME_INPUT_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["NO_TRANSCRIPTION_PROVIDER"] = FailureClass.PlatformInternal,
        ["NO_WORDS"] = FailureClass.PlatformInternal,
        ["NOT_SUBMITTED"] = FailureClass.PlatformInternal,
        // The video provider's price lookup (Higgsfield's oracle) could not answer right now: not the person's doing, retryable.
        ["VIDEO_PRICE_UNAVAILABLE"] = FailureClass.PlatformProviderError,
        ["PROVIDER_AUTH_FAILED"] = FailureClass.PlatformInternal,
        ["PROVIDER_INSUFFICIENT_BALANCE"] = FailureClass.PlatformInternal,
        ["RESERVATION_ORPHANED"] = FailureClass.PlatformInternal,
        ["SOURCE_TRIM_FAILED"] = FailureClass.PlatformInternal,
        ["SOURCE_TRIM_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["STILL_IMAGE_FAILED"] = FailureClass.PlatformInternal,
        ["UNEXPECTED_ERROR"] = FailureClass.PlatformInternal,
        // TimelineRenderStepExecutor: a stored document we wrote but cannot read back, an ffmpeg stage of
        // the render failing on our own compute, and the catch-all are ours.
        ["DOCUMENT_UNREADABLE"] = FailureClass.PlatformInternal,
        ["RENDER_FAILED"] = FailureClass.PlatformInternal,
        ["UNEXPECTED"] = FailureClass.PlatformInternal,
        // DesignSystemStepExecutor (product design system, P2): parsing our own sources, composing
        // the artifact and writing it are our work.
        ["DESIGN_PARSE_FAILED"] = FailureClass.PlatformInternal,
        ["DESIGN_COMPOSE_INVALID"] = FailureClass.PlatformInternal,
        ["DESIGN_ARTIFACT_WRITE_FAILED"] = FailureClass.PlatformInternal,
        ["VOICE_RESOLVER_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["GLOSSARY_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["VOICEOVER_PAYLOAD_NOT_DECODABLE"] = FailureClass.PlatformInternal,
        ["VOICEOVER_PAYLOAD_NOT_WAV"] = FailureClass.PlatformInternal,
        // ModelUnpricedException (ReelBolt.Shared.Inference): the resolver refused a Platform model with no effective
        // price. A gap in our catalog, never the user's doing, so the step is refunded; mapped so its code persists.
        ["MODEL_UNPRICED"] = FailureClass.PlatformInternal,
        // LocalRunnerUnavailableException (ReelBolt.Shared.Inference): the customer's own model, reached through their own
        // desktop runner, could not be served (offline, switched off, busy). Their machine, not our provider, so it is
        // a bring-your-own provider error: nothing is refunded for the work the step already consumed.
        ["LOCAL_RUNNER_UNAVAILABLE"] = FailureClass.ByoProviderError,
        // ContextTooLargeException (Execution/Context): an Anthropic tool loop whose frozen prefix outgrew
        // WorkflowEngine:ToolLoopBudget:HardMaxChars, stopped before the provider call. Our limit, refunded.
        ["CONTEXT_TOO_LARGE"] = FailureClass.PlatformInternal,

        // Platform-managed provider failures: the vendor's side, not the user's (provider-owner
        // rule — see this class's summary for the BYO caveat).
        // AgentStepExecutor: a structured agent answer still not matching its OutputSchemaType after one repair turn.
        // Same class as SCRIPTWRITER_OUTPUT_INVALID (the model's output, not the user's input).
        ["AGENT_OUTPUT_SCHEMA_INVALID"] = FailureClass.PlatformProviderError,
        ["ALL_TAKES_FAILED"] = FailureClass.PlatformProviderError,
        ["ALL_IMAGES_FAILED"] = FailureClass.PlatformProviderError,
        ["DECISION_INVALID"] = FailureClass.PlatformProviderError,
        ["EMPTY_KEEP"] = FailureClass.PlatformProviderError,
        ["INVALID_SPAN"] = FailureClass.PlatformProviderError,
        ["MIXED_SOURCE_SPAN"] = FailureClass.PlatformProviderError,
        ["NARRATION_OUTPUT_INVALID"] = FailureClass.PlatformProviderError,
        ["NO_DOWNLOAD_URL"] = FailureClass.PlatformProviderError,
        ["NO_SEGMENTS_REMAINING"] = FailureClass.PlatformProviderError,
        ["PICKUP_OUTPUT_INVALID"] = FailureClass.PlatformProviderError,
        ["POLL_ERROR"] = FailureClass.PlatformProviderError,
        ["POLL_TIMEOUT"] = FailureClass.PlatformProviderError,
        ["PROVIDER_CANCELLED"] = FailureClass.PlatformProviderError,
        ["PROVIDER_FAILED"] = FailureClass.PlatformProviderError,
        ["PROVIDER_REJECTED"] = FailureClass.PlatformProviderError,
        ["SCRIPTWRITER_OUTPUT_INVALID"] = FailureClass.PlatformProviderError,
        ["SUBMIT_OUTCOME_UNKNOWN"] = FailureClass.PlatformProviderError,
        ["TRANSCRIPTION_FAILED"] = FailureClass.PlatformProviderError,
        ["TRANSCRIPTION_UNAVAILABLE"] = FailureClass.PlatformProviderError,
        ["VISION_FAILED"] = FailureClass.PlatformProviderError,
        ["VISION_UNAVAILABLE"] = FailureClass.PlatformProviderError,

        // --- Produced by the gates themselves (B6a/B8a), before any work ran -------------------
        // T-012: a run admitted under the free run-rate bucket whose loaded steps are paid (the workflow was edited
        // after admission). Failed before the first step, so nothing was charged; the user's edit, theirs to restart.
        ["RUN_CLASS_CHANGED"] = FailureClass.UserInput,
        ["INSUFFICIENT_CREDITS"] = FailureClass.InsufficientCredits,
        ["API_KEY_BUDGET_EXCEEDED"] = FailureClass.InsufficientCredits,
        ["API_KEY_INACTIVE"] = FailureClass.InsufficientCredits,
        // A workspace Owner/Admin revoked the API key that started the run: a deliberate stop on the customer's
        // side (not the person who started it), so the work done so far is kept like a user stop.
        ["API_KEY_REVOKED"] = FailureClass.UserCancelled,
        ["PLAN_NOT_ENTITLED"] = FailureClass.EntitlementDenied,

        // --- D12: the organization's own runner, or the output it returned ----------------------
        // Every code OutputVerifier can produce. Mapped explicitly rather than left to the
        // unmapped-by-default PlatformInternal, which would REFUND them: a runner that published
        // nothing, overshot the size cap, returned something unreadable or the wrong shape, or
        // returned bytes that do not match the digest it reported burned the CUSTOMER's machine and
        // the customer's electricity. The verifier's result carries the class directly too
        // (OutputVerificationResult.FailureClass); this table is the path that survives a caller
        // that forwards only the code.
        ["RUNNER_OUTPUT_MISSING"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_TOO_LARGE"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_UNREADABLE"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_INVALID_DOCUMENT"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_DURATION_MISMATCH"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_FRAME_SIZE_MISMATCH"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_CODEC_MISMATCH"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_FRAME_RATE_MISMATCH"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_CHECKSUM_MISMATCH"] = FailureClass.Runner,
        ["RUNNER_OUTPUT_BLANK"] = FailureClass.Runner,
        // ...and the one code in this family that is OUR fault, not the runner's: the output passed
        // every check and the platform then failed to move it. Refunded, like every other
        // unattributable-to-the-user failure.
        ["RUNNER_OUTPUT_PROMOTE_FAILED"] = FailureClass.PlatformInternal,
        // D11: the verifier refused a runner-produced object without naming which check failed. The
        // same reading as every other RUNNER_OUTPUT_* code - the customer's machine produced something
        // the platform would not accept - and deliberately NOT refunded.
        ["RUNNER_OUTPUT_UNVERIFIED"] = FailureClass.Runner,

        // --- D6: no compute target, and the session that could not start -------------------------
        // NO_RUNNER_ONLINE is the user's own choice coming back at them: a workspace set to
        // "runner only" (ComputePreference.RunnerOnly) failed its step because no paired desktop
        // runner was online. Nothing ran, so nothing was consumed; the code is the user's
        // configuration, exactly like VOICE_NOT_FOUND, and it refunds the step's unused reservation.
        ["NO_RUNNER_ONLINE"] = FailureClass.UserInput,
        // The other side of that coin: the organization is entitled to no compute target at all -
        // no runner use and no pool. A plan question, so it classifies like PLAN_NOT_ENTITLED.
        ["COMPUTE_NOT_AVAILABLE"] = FailureClass.EntitlementDenied,
        // runner_sessions allows ONE open session per execution and the engine serialises them. A
        // wait that expires is OUR scheduling failing to make progress, not the user's mistake, so it
        // is refunded like every other failure we caused.
        ["RUNNER_SESSION_BUSY"] = FailureClass.PlatformInternal,
        // D11: a media job was created and no runner took it before the assignment window closed. The
        // engine scheduled work it could not deliver, which is our fault, not the user's - refunded
        // like every other failure we caused. (The user's own "no runner is paired" case is
        // NO_RUNNER_ONLINE, which the SELECTOR reports before any job exists.)
        ["RUNNER_JOB_UNCLAIMED"] = FailureClass.PlatformInternal,

        // --- Composition revisions (ReviseStepExecutor / CompositionRevisionRunner) ----------------
        // Every one of these but REVISION_NO_CHANGES is refunded. The workspace codes are storage and sandbox plumbing on our
        // side (an archive we failed to keep, a sandbox that refused a restore, a run whose project the
        // platform no longer holds); REVISION_REQUEST_MISSING is an execution of the hidden revision
        // workflow with no request behind it, which only the platform can create. The two agent codes
        // follow the provider-owner rule like DECISION_INVALID: the platform model did not deliver.
        ["WORKSPACE_ARCHIVE_TOO_LARGE"] = FailureClass.PlatformInternal,
        ["WORKSPACE_ARCHIVE_CORRUPT"] = FailureClass.PlatformInternal,
        ["WORKSPACE_ARCHIVE_MISSING"] = FailureClass.PlatformInternal,
        ["WORKSPACE_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["WORKSPACE_RESTORE_FAILED"] = FailureClass.PlatformInternal,
        ["WORKSPACE_TRANSFER_NOT_SUPPORTED"] = FailureClass.PlatformInternal,
        ["REVISION_REQUEST_MISSING"] = FailureClass.PlatformInternal,
        ["REVISION_AGENT_UNAVAILABLE"] = FailureClass.PlatformInternal,
        // A revision's replay of the steps that depend on the film (docs/design/composition-revisions.md):
        // an execution of a per-revision replay workflow whose plan is missing or unreadable, or whose
        // source run no longer holds a result the plan reuses. Both are platform plumbing — refunded.
        ["REPLAY_PLAN_MISSING"] = FailureClass.PlatformInternal,
        ["REPLAY_SOURCE_MISSING"] = FailureClass.PlatformInternal,
        ["REVISION_AGENT_FAILED"] = FailureClass.PlatformProviderError,
        // No longer emitted (T-020: the platform renders an unrendered change itself); kept so a
        // failure recorded before that change still classifies the same.
        ["REVISION_NOT_RENDERED"] = FailureClass.PlatformProviderError,
        // The agent changed the film without rendering and the platform's own fallback render failed:
        // the render is our side of the revision - refunded.
        ["REVISION_RENDER_FAILED"] = FailureClass.PlatformInternal,
        // The agent neither changed nor rendered anything: the instruction did not say what to change
        // in a way that could be acted on. The owner is asked for a more specific one - not refunded.
        ["REVISION_NO_CHANGES"] = FailureClass.UserInput,
        // A step change request (docs/design/change-requests.md) whose item scope and overrides matched
        // nothing in the step: the request named lines the step does not have. Nothing was synthesised;
        // the owner asks again with a different item - not refunded (nothing was consumed).
        ["CHANGE_NOT_APPLIED"] = FailureClass.UserInput,
        // The model's answer to a script rewrite was not a usable script (not JSON, scenes moved or untouched
        // scenes altered): the platform model failed to deliver - refunded, never retried.
        ["SCRIPT_REWRITE_INVALID"] = FailureClass.PlatformProviderError,
        // A take carried over from the run being changed that had itself failed there (no vendor reason recorded):
        // nothing is bought again for it, so nothing is consumed here either - classed as the platform's.
        ["CARRIED_TAKE_FAILED"] = FailureClass.PlatformInternal,

        // --- Designed graphics (GraphicDesignStepExecutor / GraphicDesignRunner) -------------------
        // Every one of these is refunded, on the revision codes' reasoning: the request row, the agent
        // registration, the base version and the upload are platform plumbing; the rest are the
        // platform model not delivering a graphic the runner could render, verify and crop (the
        // user's description is never the reason a composition is missing, unrenderable or empty).
        ["GRAPHIC_REQUEST_MISSING"] = FailureClass.PlatformInternal,
        ["GRAPHIC_AGENT_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["GRAPHIC_BASE_UNAVAILABLE"] = FailureClass.PlatformInternal,
        ["GRAPHIC_UPLOAD_FAILED"] = FailureClass.PlatformInternal,
        ["GRAPHIC_AGENT_FAILED"] = FailureClass.PlatformProviderError,
        ["GRAPHIC_COMPOSITION_MISSING"] = FailureClass.PlatformProviderError,
        ["GRAPHIC_NOT_RENDERED"] = FailureClass.PlatformProviderError,
        ["GRAPHIC_INVALID_OUTPUT"] = FailureClass.PlatformProviderError,
        ["GRAPHIC_EMPTY"] = FailureClass.PlatformProviderError,
    };

    /// <summary>
    /// <c>SCREAMING_SNAKE</c> literals in the engine that are deliberately NOT failure codes: env
    /// var names, room-director chat sentinels, the room executors' code <em>prefixes</em> (the
    /// composed code, e.g. <c>EDIT_ROOM_FAILED</c>, is what gets classified), and the credit gate's
    /// reservation <em>reasons</em> (which never reach a step result). Kept here rather than in the
    /// test so the exemption is a reviewed part of this mapping, not test-local noise.
    /// </summary>
    public static readonly IReadOnlySet<string> NonCodeLiterals = new HashSet<string>(StringComparer.Ordinal)
    {
        "POD_NAME",          // Environment.GetEnvironmentVariable("POD_NAME") in ExecutionLeaseService
        "ROOM_DECIDED",      // the room directors' end-of-discussion sentinel
        "EDIT_ROOM",         // RoomStepExecutorBase.FailureCodePrefix
        "GRAPHICS_ROOM",     // ditto
        "COLOR_GRADE_ROOM",  // ditto
        "DAILIES_ROOM",      // ditto (issue #93 phase 4)
        "BILLING_DISABLED",  // CreditGate reservation reason
        "NO_PLATFORM_COST",  // CreditGate reservation reason
        "GATE_UNAVAILABLE",  // CreditGate reservation reason
        "RUNNER_INTERNAL_TOKEN", // the gateway's shared service token: an env var name, never a failure code
        "DOMAIN_MAX",        // .cube LUT file keywords parsed/written by CubeLut (P7), never failure codes
        "DOMAIN_MIN",        // ditto
        "LUT_3D_SIZE",       // ditto
        "LUT_1D_SIZE",       // ditto
        "LUT_1D_INPUT_RANGE", // ditto
        "LUT_3D_INPUT_RANGE", // ditto
        "FILE_READ_FAILED",  // DesignSystemStepExecutor Parse: Warn ImportDiagnostics for one unreadable
        "FILE_TOO_LARGE",    // source file / one over the per-file cap. Parse continues past both, so
                             // neither is ever a step failure code.
        "NETWORK_FONT",      // NetworkFontPreflight's tool-result tag the model reads; a refused render fails under its own code
        "MISSING_STATIC_FILE", // StaticFilePreflight's tool-result tag, likewise never a step failure code
        "HTML5_VIDEO_IN_RENDER", // Html5VideoPreflight's tool-result tag, likewise never a step failure code
        "WEB_SEARCH_CAP_REACHED",   // WebSearchAgentTools: tool-result error codes the model reads. A search that
        "WEB_SEARCH_UNAVAILABLE",   // fails or is capped is never a step failure, so none is a failure code.
        "WEB_SEARCH_INVALID_QUERY", // ditto
        "WEB_SEARCH_FAILED",        // ditto
        "WEB_SEARCH_RESTRICTED_PROVIDER", // ditto
    };

    /// <summary>The full code → class table, for the drift guard and for diagnostics.</summary>
    public static IReadOnlyDictionary<string, FailureClass> Codes => CodeTable;

    /// <summary>Stable code for a gate refusal that could not even be evaluated (our fault).</summary>
    public const string GateUnavailableCode = "GATE_UNAVAILABLE";

    /// <summary>
    /// The class of <paramref name="code"/>, or <see cref="FailureClass.PlatformInternal"/> for an
    /// unmapped one (refund by default — see this class's summary).
    /// </summary>
    public static FailureClass ForCode(string? code)
        => code is not null && CodeTable.TryGetValue(code, out FailureClass mapped) ? mapped : FailureClass.PlatformInternal;

    /// <summary>Whether <paramref name="code"/> has an explicit entry in the table.</summary>
    public static bool TryGetCode(string? code, out FailureClass failureClass)
    {
        if (code is not null && CodeTable.TryGetValue(code, out failureClass))
            return true;

        failureClass = FailureClass.None;
        return false;
    }

    /// <summary>
    /// The class implied by the exception itself. <see cref="OperationCanceledException"/> is decided
    /// by <paramref name="cancellationReason"/> — the cancellation registry records WHY an execution
    /// was cancelled, and a transport kill or a restart must not be charged to the user
    /// (docs/design/saas-metering.md "Mapping: exceptions and non-coded paths").
    /// </summary>
    public static FailureClass ForException(Exception? exception, string? cancellationReason = null) => exception switch
    {
        null => FailureClass.PlatformInternal,
        CreditRefusalException refusal => refusal.FailureClass,
        Agents.Tools.AgentWorkflowException => FailureClass.UserInput,
        ReelBolt.Shared.Inference.LocalRunnerUnavailableException => FailureClass.ByoProviderError,
        Execution.RunClassChangedException => FailureClass.UserInput,
        OperationCanceledException => IsUserStop(cancellationReason)
            ? FailureClass.UserCancelled
            : FailureClass.PlatformInternal,
        TimeoutException => FailureClass.PlatformInternal,
        _ => FailureClass.PlatformInternal,
    };

    /// <summary>
    /// The one entry point the executor uses. Precedence: a class assigned where the failure was
    /// produced, then an explicit code (the failed result's own code, or one named in the message),
    /// then the exception itself. Returns <see cref="FailureClass.PlatformInternal"/> when nothing
    /// identifies the failure.
    /// </summary>
    public static FailureClass Classify(
        FailureClass carried, string? code, Exception? exception, string? cancellationReason = null)
    {
        // A cancellation is decided by the registry's RECORDED provenance, before anything else: no
        // code or class an earlier attempt recorded may overrule who actually asked for the stop.
        if (exception is OperationCanceledException)
            return ForException(exception, cancellationReason);

        if (carried != FailureClass.None)
            return carried;

        if (code is not null)
            return ForCode(code);

        if (exception is not null)
            return ForException(exception, cancellationReason);

        return FailureClass.PlatformInternal;
    }

    /// <summary>
    /// Best-effort code for a failed step's output JSON: <c>{"error":{"code":"…"}}</c> (every
    /// deterministic executor's <c>Failure(...)</c> helper) or <c>{"meta":{"failed":"…"}}</c> (the
    /// VideoGenerate executor's <c>CreateErrorOutput</c>). Null for anything that is not a
    /// recognised code — an agent step's prose output, or a JSON document without one.
    /// </summary>
    public static string? CodeFromOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || output[0] is not ('{' or '['))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement root = document.RootElement;
            return Recognised(ReadString(root, "error", "code")) ?? Recognised(ReadString(root, "meta", "failed"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a RECOGNISED code named in a message — the retry wrapper's
    /// "… Last error: …" text, or an exception message that quotes a code. Only codes that are in
    /// the table qualify, so this can never invent one out of prose (docs/design/saas-metering.md
    /// allows message-substring classification strictly as a fallback to a code we already know).
    /// </summary>
    public static string? CodeFromMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        string? found = null;
        if (message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase))
            found = "SCRATCH_BUSY";
        foreach (string token in message.Split(
            [' ', '\t', '\n', '\r', ':', ',', ';', '(', ')', '[', ']', '{', '}', '"', '\'', '.', '/', '|', '=', '-'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (CodeTable.ContainsKey(token))
                found = token;
        }

        return found;
    }

    /// <summary>The code an exception carries: a <see cref="CreditRefusalException"/>'s own, else one recognised in its message.</summary>
    public static string? CodeFromException(Exception? exception) => exception switch
    {
        null => null,
        CreditRefusalException refusal => refusal.Code,
        ReelBolt.WorkflowEngine.Services.Video.MediaStageLimitExceededException => ReelBolt.Shared.Workflows.VideoSourceLimits.TooLargeCode,
        IOException io when ReelBolt.WorkflowEngine.Services.Video.ScratchDiskGuard.IsDiskFull(io) => "SCRATCH_BUSY",
        _ => CodeFromMessage(exception.Message),
    };

    /// <summary>
    /// Whether a cancellation reason names a deliberate stop by the user. The only producer of the
    /// <c>"Stopped by user {id}"</c> wording is <c>WorkflowExecutorService.CancelExecutionAsync</c>;
    /// every other cancellation (transport, shutdown, link) carries the registry's "no stop request
    /// was recorded" text and is ours.
    /// </summary>
    public static bool IsUserStop(string? cancellationReason)
        => cancellationReason is not null
            && cancellationReason.Contains("Stopped by user", StringComparison.Ordinal);

    private static string? ReadString(JsonElement root, string parent, string child)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(parent, out JsonElement section)
            && section.ValueKind == JsonValueKind.Object
            && section.TryGetProperty(child, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? Recognised(string? candidate)
        => candidate is not null && CodeTable.ContainsKey(candidate) ? candidate : null;
}
