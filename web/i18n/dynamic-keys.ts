/**
 * Catalogue key prefixes that are looked up with a VALUE that is only known at run time
 * (`t(`status.${status}`)`, an error code, an enum). The unused-key test cannot see those call
 * sites, so every key under a declared prefix counts as used. Add the prefix here in the same
 * commit that adds the dynamic lookup; keep prefixes as narrow as the lookup.
 *
 * Format: the full dotted key path, `<namespace file>.<path>`; a trailing `.` is implied.
 */
export const DYNAMIC_KEY_PREFIXES: readonly string[] = [
  // Server failure/error codes map to errors.<CODE> and failures.<CODE> (docs/i18n.md).
  'errors',
  // Plan card included-features lines are chosen by plan-features.ts from the plan's entitlements.
  'plans.card.includes',
  'failures',
  // Pre-run linter sentences are looked up from the wire `messageKey` (apps.lint.WL0xx[.variant]).
  'apps.lint',
  // The permission to send media: why a step sends (the server's reason code) and who allowed a run (its grantor).
  'apps.permission.mediaEgress.reason',
  'apps.permission.mediaEgress.allowedBy',
  // Files tab: the reason a zip could not be unpacked is looked up by the server's failure code.
  'files.unpack.failure',
  // The run gate's start refusals (START_REFUSALS in AppRunPage): the code picks the key pair.
  'apps.run.page.videoNotChosen',
  'apps.run.page.videoNotChosenNoStep',
  'apps.run.page.avatarNotChosen',
  'apps.run.page.avatarNotChosenNoStep',
  'apps.run.page.modelUnsupported',
  'apps.run.page.modelUnsupportedNoStep',
  'apps.run.page.lipSyncModelNotSet',
  'apps.run.page.lipSyncModelNotSetNoStep',
  // App run page: why a model card cannot be chosen is looked up by the options' `unavailableCode`.
  'apps.run.inputs.unavailable',
  // App run page: a number input's unit and a shape input's chips are looked up by value.
  'apps.run.inputs.unitSuffix',
  'apps.run.inputs.unit',
  'apps.run.inputs.shape',
  // Built-in recipes' app inputs: label, help, example and option keys come from the server's manifest (labelKey...).
  'apps.recipes.inputs',
  // App page builder: a field's type, a new field's first name, an input's kind and a number's unit are looked up by value.
  'apps.builder.types',
  'apps.builder.newLabel',
  'apps.builder.kinds',
  'apps.builder.edit.units',
  // Marketing contacts: status filter, name source and Brevo state labels are looked up by enum value.
  // Avatars tab: status, reason, kind, role, reference state, delete outcome, statement language and refusal code are looked up by value.
  'avatars.facts.fields',
  'avatars.facts.placeholders',
  'avatars.facts.errors',
  'stepResults.fidelity',
  'avatars.status',
  'avatars.reasons',
  'avatars.kinds',
  'avatars.roles',
  'avatars.refState',
  'avatars.delete.outcomes',
  'avatars.form.languages',
  'avatars.form.problems',
  'avatars.errors',
  // Objects: a status, reason, problem or refusal whose person sentence does not fit an object reads its own, by the same value.
  'avatars.objectStatus',
  'avatars.objectReasons',
  'avatars.form.objectProblems',
  'avatars.objectErrors',
  // Workspace avatar library: an item's status and reason, an import's reason and a refusal code are looked up by value.
  'avatars.library.status',
  'avatars.library.reasons',
  'avatars.library.linkedReasons',
  'avatars.library.errors',
  // Avatar variants: a text problem, a refusal code, the plan feature a refusal names and why a price is missing are looked up by value.
  'avatars.variants.problems',
  'avatars.variants.errors',
  'avatars.variants.planErrors',
  'avatars.variants.ai.estimate.unavailable',
  // Avatar builds ("Being made"): the stage and the failure code are looked up by value.
  'avatars.builds.stage',
  'avatars.builds.failures',
  // VideoGenerate avatar section: the block reason and the speech hint are looked up by value.
  'wfSteps.videoGenerate.avatar.blocked',
  'wfSteps.videoGenerate.avatar.speech.hint',
    'marketingContacts.filters',
  'marketingContacts.source',
  'marketingContacts.brevoState',
  // Timeline adjustment badges: names are looked up by the badge's label key, menu words through a passed-in translator.
  'editor-timeline.badges',
  // Background tool names are looked up by the job kind (`tool.${kind}`).
  'editor-background.tool',
  // editor-a: lookups keyed by an enum or id
  'editor.shell.workspaceBar.names',
  'editor.phone.phoneDock.dock',
  'editor.viewer.transformOverlay.corners',
  'editor.hub.timelinesView.sort',
  'editor.deliver.renderSettings.formats',
  'editor.deliver.renderSettings.qualitySteps',
  'editor.deliver.renderSettings.codecs',
  'editor.deliver.renderSettings.speeds',
  'editor.deliver.renderSettings.platforms',
  'editor.deliver.renderSettings.rateModes',
  'editor.deliver.renderSettings.rateHints',
  'editor.deliver.renderSettings.renderLabel',
  'editor.deliver.presetList.use',
  'editor.deliver.deliverWorkspace.captionsSummary',
  'editor.panels.inspectorPanel.transformChannels',
  'editor.panels.timelineSettingsPanel.aspectOptions',
  'editor.panels.editIndexPanel.columns',
  'editor.panels.captionLookPicker.positions',
  'editor.deliver.captionDelivery.description',
  'editor.phone.phoneEditor.sheetTitles',
  'editor.phone.phoneActionBar.sheetTitles',
  'editor.review.shareForReview.status',
  'editor.phone.mediaTile.kind',
  // editor-helpers-1: Msg keys built from an enum
  'editor-helpers-1.deliver.statusWord',
  'editor-helpers-1.deliver.notes.kind',
  'editor-helpers-1.panels.versions.cause',
  'editor-helpers-1.phone.readiness',
  // editor-helpers-2: English logic-module text mapped to a catalogue id at display time (phone/helper-text.ts)
  'editor-helpers-2.text',
  // Step-panel enum labels (value sent to the API is unchanged; only the label is looked up).
  'wfSteps.pinInsert.contentKind',
  'wfSteps.extractFrame.sourceKind',
  'wfSteps.extractFrame.at',
  'wfSteps.extractFrame.format',
  'wfSteps.audioGenerate.kind',
  'wfSteps.imageGenerate.mode',
  'wfSteps.imageGenerate.sourceKind',
  'wfSteps.audioCleanup.target',
  'wfSteps.audioCleanup.sourceKind',
  'wfSteps.audioCleanup.outputKind',
  'wfSteps.videoEnhance.op',
  'wfSteps.videoEnhance.sourceKind',
  'wfSteps.videoEnhance.upscale.size',
  'wfSteps.videoEnhance.interpolate.multiplier',
  'wfSteps.videoEnhance.denoise.strengthValue',
  'wfSteps.videoEnhance.extend.aspect',
  'wfSteps.videoEnhance.extend.mode',
  'wfSteps.videoEnhance.extend.side',
  'editor-enhance.extend.sides',
  'wfSteps.videoCompile.engine',
  'wfSteps.videoCompile.keepOrderMode',
  'wfSteps.videoCompile.outputFormatMode',
  'wfSteps.videoCompile.outputFitMode',
  'wfSteps.videoCompile.loudnessMode',
  'wfSteps.videoCompile.insertFitMode',
  'wfSteps.videoCompile.insertDipMode',
  'wfSteps.videoCompile.trackedEffect',
  'wfSteps.videoCompile.regionEffect',
  'wfSteps.videoCompile.beatSyncMode',
  'wfSteps.videoCompile.voiceoverModeOption',
  'wfSteps.videoCompile.transitionPolicyMode',
  'wfSteps.videoCompile.videoCodecMode',
  'wfSteps.videoCompile.musicDuckingMode',
  'wfSteps.videoCompile.musicFitMode',
  'wfSteps.videoCompile.encodingMode',
  'wfSteps.videoCompile.insertSurface',
  'wfSteps.videoCompile.insertAudioProblem',
  'wfSteps.videoAnalyze.sourceKind',
  'wfSteps.videoAnalyze.subShotMode',
  'wfSteps.videoAnalyze.insertRegionColor',
  'wfSteps.videoAnalyze.trackMethod',
  'wfSteps.videoAnalyze.matchScope',
  'wfSteps.room',
  'wfSteps.videoGenerate.planSource',
  'wfSteps.reframe.fits.option',
  'wfSteps.reframe.insertRef.from',
  'wfSteps.captionStyle.style',
  'wfSteps.captionStyle.styleName',
  'wfSteps.captionStyle.font',
  'wfSteps.captionStyle.position',
  'wfSteps.captionStyle.textColor',
  'wfSteps.captionStyle.boxColor',
  'wfSteps.thumbnailGenerate.fontColor',
  'wfSteps.thumbnailGenerate.boxColor',
  'wfSteps.thumbnailGenerate.selection',
  'wfSteps.thumbnailGenerate.fit',
  'wfSteps.thumbnailGenerate.position',
  'wfSteps.timelineRender.preset',
  'wfSteps.timelineRender.codec',
  'wfSteps.timelineRender.speed',
  'wfSteps.timelineRender.encoding.rateMode',
  'wfSteps.videoCompile.rateControl.options',
  'wfSteps.kit.provider.capability',
  'wfSteps.kit.noAudio',
  'wfSteps.extract.inputSource',
  'wfSteps.extract.operation',
  'wfSteps.designSystem.op',
  'workflows.stepType',
  'stepResults.status',
  'execution.stepState',
  'execution.page.runStatus',
  'workflows.flow',
  'workflows.node.extract.operation',
  'workflows.agentPicker.group',
  // Assistant welcome copy is keyed by page kind (components/assistant/pageInfo.ts).
  'assistant.welcome',
  // Label tables keyed by an enum value (lib/utils, lib/types): libLabels.<table>.<Key>.
  'libLabels',
  // The shared vocabulary (save, cancel, ...) is used by every area as it migrates; tighten once the migration is done.
  'common',
  // T-007: the command palette's unavailable reason is looked up by an enum from lib/editing/commands.ts.
  'editor.prefs.commandPalette.reasons',
  // The Simple shell: task ids, helper ids, place ids and the kinds of full-editor work are looked up by id.
  'editor-simple.tasks',
  'editor-simple.ask.helpers',
  'editor-simple.ask.placeholders',
  'editor-simple.selection.work',
  'editor-simple.shell.place',
  // web-shell: keys whose translator is bound to a name the unused-key scan does not follow (tn, tm) or is passed
  // into a helper as a parameter (revisions/revision-errors.ts).
  'dashboard.nudge',
  'misc',
  'revisions.errors',
  // components/change-requests/film-helpers.ts resolves these through the translator FilmChangeSection passes in.
  'revisions.panel.keptUnless',
  'revisions.panel.versionOption',
  'revisions.panel.original',
  // changeRequests copy is resolved through a translator passed to non-component helpers (change-request-copy/form/errors).
  'changeRequests',
];

// editor-b: loudness target names/hints are looked up by target id.
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-audio.loudness.targets');

// editor-thumb-a: crop handle and field names are looked up by handle id / field key.
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-thumbnail-a.crop.handles', 'editor-thumbnail-a.crop.fields', 'editor-thumbnail-a.crop.fieldAria');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-thumbnail-a.layers.menu', 'editor-thumbnail-a.layers.maskMenu', 'editor-thumbnail-a.layers.kind', 'editor-thumbnail-a.layers.blendMode', 'editor-thumbnail-a.layers.roleName');
// editor-media: colour labels are looked up by the label id (red, orange, ...).
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-media.color', 'editor-media.colorLower');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-timeline.trim.modes');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-timeline.trackMenu.swatches', 'editor-timeline.trackMenu.heights', 'editor-timeline.trackMenu.view');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-timeline.canvas.trimModes', 'editor-timeline.canvas.trimReadout');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-timeline.canvas.snapLabel');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-pictures.defaultName', 'editor-pictures.shapes');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-color.grade.scalars', 'editor-color.grade.wheels', 'editor-color.grade.looks', 'editor-color.grade.strengths', 'editor-color.grade.shadowTones', 'editor-color.grade.highlightTones', 'editor-color.look.looks', 'editor-color.look.fixes', 'editor-color.scopes.kinds');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-audio.vo.fileErrors');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-misc.place.choices', 'editor-misc.place.positions', 'editor-misc.place.colors', 'editor-misc.graphics.templates');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-misc.cut.kinds');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-misc.compose.effectTypes', 'editor-misc.compose.corners');
// editor-lib: label tables looked up by enum member (keymap action, transition type, motion preset, look, graphic choice).
(DYNAMIC_KEY_PREFIXES as string[]).push(
  'editor-lib.keymap.actions',
  'editor-lib.keymap.groups',
  'editor-lib.transitions',
  'editor-lib.motion',
  'editor-lib.looks',
  'editor-lib.graphics.template',
  'editor-lib.graphics.position',
  'editor-lib.graphics.color',
);

// editor-lib (lib/editor): tables keyed by operation, tool, format, shape, status, font and credit-line variant.
(DYNAMIC_KEY_PREFIXES as string[]).push(
  'editor-lib.aiOps',
  'editor-lib.aiTools',
  'editor-lib.exportFormats',
  'editor-lib.canvasSize.hints',
  'editor-lib.graphicPanel',
  'editor-lib.newPicture',
  'editor-lib.credits',
  'editor-lib.text.fonts',
  'editor-lib.text.problem',
);
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-lib.handoff');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-lib.sync.state', 'editor-lib.time.formats', 'editor-lib.place');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-lib.device.quality');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-lib.honesty.noun');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-lib.open.unplaced', 'editor-lib.diff.counts', 'editor-lib.pool', 'editor-lib.refusal.fields', 'editor-lib.subtitles.looks', 'editor-lib.dsp.stages', 'editor-lib.mediaAnalysis', 'editor-lib.assist', 'editor-lib.preset', 'editor-lib.frameHandles', 'editor-lib.fader.pan', 'editor-lib.link', 'editor-lib.guest', 'editor-lib.diff', 'editor-lib.vo', 'editor-lib.review', 'editor-lib.history');
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-lib.names');
// editor integration: keys built inside Msg helpers (template prefixes) or chosen by a ternary at the call site
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-helpers-1.deliver', 'editor-helpers-1.panels.versions', 'editor-lib.resultBar', 'editor-lib.maskSave', 'editor-lib.text', 'editor-color.grade', 'editor-audio.cleanVoice', 'editor-audio.vo', 'editor-thumbnail-a.editor.panelTitle', 'editor-thumbnail-a.common.noProvider', 'editor-media.viewer');

// background-activity: the job type label and the state label are looked up by the type / state string.
(DYNAMIC_KEY_PREFIXES as string[]).push('activity.types', 'activity.states', 'activity.failure', 'activity.cost', 'activity.editor', 'activity.stages');

// editor-ai-tools: tool names and descriptions are looked up by the registered tool's key (lib/editing/ai-tools.ts).
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-ai-tools.tools', 'editor-ai-tools.groups');
// audio cleanup: the noise picker looks its labels up by choice
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-audio.cleanup.noise');
// Render sound checks: the sentence key comes from the check's kind (render-checks.ts).
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-audio.checks');
// video enhance: labels looked up by choice, option, refusal kind or failure code
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-enhance.choice', 'editor-enhance.choiceHint', 'editor-enhance.engine', 'editor-enhance.upscale', 'editor-enhance.multiplier', 'editor-enhance.strength', 'editor-enhance.shutter', 'editor-enhance.extend.aspect', 'editor-enhance.extend.mode', 'editor-enhance.refusal', 'editor-enhance.failure');
// editor-timeline: events lane type words, kind words, action labels and refusal reasons are looked up by id.
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-timeline.eventsLane.types', 'editor-timeline.eventsLane.kinds', 'editor-timeline.eventsLane.actions', 'editor-timeline.eventsLane.reasons');
// Avatars in the editors: a clip job's state, why the "Generate clip" form cannot be sent and the missing plan feature are looked up by value.
(DYNAMIC_KEY_PREFIXES as string[]).push('editor-media.subjects.jobs.state', 'editor-media.subjects.dialog.blocker', 'editor-media.subjects.dialog.refusal.plan', 'editor-thumbnail-a.editor.subjects.plan');
// API key spend guard: mapApiKeyError (daily-cap.ts) receives the translator as an argument, so the scan cannot attribute these two keys.
(DYNAMIC_KEY_PREFIXES as string[]).push('org.apiKeys.capGuard.confirmRequired', 'org.apiKeys.capGuard.readOnlyPlan');
