/**
 * Every customer-facing string of the brand-and-product profile tab lives here (SPEC §17), in plain
 * language: "brand", "product profile", "design files". The prose deliberately avoids implementation
 * names (the media stack, the execution engine, the renderer) and any internal step vocabulary.
 */
import { msg, type Msg } from '@/i18n/msg';
import type {
  ConfidenceBand,
  DesignProposalTrigger,
  DesignSourceStatus,
  DesignVersionOrigin,
  DesignVersionState,
  ImportCapabilitySupport,
  ImportDiagnosticSeverity,
} from '@/lib/types/design-system';

// --- Empty state -------------------------------------------------------------------------------
export const EMPTY_TITLE = msg('brandLibraries.profile.emptyState.emptyTitle');
export const EMPTY_DESCRIPTION = msg('brandLibraries.profile.emptyState.emptyDescription');
export const EMPTY_IMPORT_LABEL = msg('brandLibraries.profile.emptyState.emptyImportLabel');
export const EMPTY_BUILD_LABEL = msg('brandLibraries.profile.emptyState.emptyBuildLabel');
export const EMPTY_EDIT_TOOLTIP = msg('brandLibraries.profile.emptyState.emptyEditTooltip');

// --- Error state -------------------------------------------------------------------------------
export const ERROR_TITLE = msg('brandLibraries.profile.errorState.errorTitle');
export const ERROR_404 = msg('brandLibraries.profile.errorState.error404');
export const ERROR_403 = msg('brandLibraries.profile.errorState.error403');
export const ERROR_FALLBACK = msg('brandLibraries.profile.errorState.errorFallback');

// --- Sub-tabs ----------------------------------------------------------------------------------
export const SUB_TAB_OVERVIEW = msg('brandLibraries.profile.subTabs.subTabOverview');
export const SUB_TAB_TOKENS = msg('brandLibraries.profile.subTabs.subTabTokens');
export const SUB_TAB_SOURCES = msg('brandLibraries.profile.subTabs.subTabSources');
export const SUB_TAB_HISTORY = msg('brandLibraries.profile.subTabs.subTabHistory');

// --- Overview ----------------------------------------------------------------------------------
export const OVERVIEW_COLORS_TITLE = msg('brandLibraries.profile.overview.overviewColorsTitle');
export const OVERVIEW_COLORS_EMPTY = msg('brandLibraries.profile.overview.overviewColorsEmpty');
export const OVERVIEW_TYPE_TITLE = msg('brandLibraries.profile.overview.overviewTypeTitle');
export const OVERVIEW_TYPE_EMPTY = msg('brandLibraries.profile.overview.overviewTypeEmpty');
export const TYPE_SPECIMEN_SAMPLE = msg('brandLibraries.profile.overview.typeSpecimenSample');
export const TYPE_SPECIMEN_WEIGHT_LABEL = msg('brandLibraries.profile.overview.typeSpecimenWeightLabel');
export const OVERVIEW_VOICE_TITLE = msg('brandLibraries.profile.overview.overviewVoiceTitle');
export const OVERVIEW_VOICE_EMPTY = msg('brandLibraries.profile.overview.overviewVoiceEmpty');
export const OVERVIEW_VOICE_SUMMARY_LABEL = msg('brandLibraries.profile.overview.overviewVoiceSummaryLabel');
export const OVERVIEW_VOICE_TONE_LABEL = msg('brandLibraries.profile.overview.overviewVoiceToneLabel');
export const OVERVIEW_VOICE_DO_LABEL = msg('brandLibraries.profile.overview.overviewVoiceDoLabel');
export const OVERVIEW_VOICE_DONT_LABEL = msg('brandLibraries.profile.overview.overviewVoiceDontLabel');
export const OVERVIEW_PRODUCT_TITLE = msg('brandLibraries.profile.overview.overviewProductTitle');
export const OVERVIEW_PRODUCT_EMPTY = msg('brandLibraries.profile.overview.overviewProductEmpty');
export const OVERVIEW_FRAMEWORK_LABEL = msg('brandLibraries.profile.overview.overviewFrameworkLabel');
export const OVERVIEW_TOP_COMPONENTS_LABEL = msg('brandLibraries.profile.overview.overviewTopComponentsLabel');
export const OVERVIEW_PREVIEW_CARDS_TITLE = msg('brandLibraries.profile.overview.overviewPreviewCardsTitle');
export const OVERVIEW_PREVIEW_CARDS_EMPTY = msg('brandLibraries.profile.overview.overviewPreviewCardsEmpty');
export const OVERVIEW_ASSETS_TITLE = msg('brandLibraries.profile.overview.overviewAssetsTitle');
export const OVERVIEW_ASSETS_EMPTY = msg('brandLibraries.profile.overview.overviewAssetsEmpty');
export const OVERVIEW_ASSET_LOGO_LABEL = msg('brandLibraries.profile.overview.overviewAssetLogoLabel');
export const OVERVIEW_ASSET_ICON_LABEL = msg('brandLibraries.profile.overview.overviewAssetIconLabel');
export const OVERVIEW_ASSET_PREVIEW_LABEL = msg('brandLibraries.profile.overview.overviewAssetPreviewLabel');
export const OVERVIEW_ASSET_MISSING = msg('brandLibraries.profile.overview.overviewAssetMissing');
export const OVERVIEW_FONTS_TITLE = msg('brandLibraries.profile.overview.overviewFontsTitle');
export const OVERVIEW_DESIGN_TITLE = msg('brandLibraries.profile.overview.overviewDesignTitle');
export const OVERVIEW_DESIGN_EMPTY = msg('brandLibraries.profile.overview.overviewDesignEmpty');

/** "1 preview card" / "2 preview cards" — pluralised in one place. */
export function previewCardsCount(count: number): Msg {
  return msg('brandLibraries.profile.overview.previewCardsCount', { count });
}

/** "1 route" / "2 routes" — pluralised in one place. */
export function routesCount(count: number): Msg {
  return msg('brandLibraries.profile.overview.routesCount', { count });
}

/** "1 component" / "2 components" — pluralised in one place. */
export function componentsCount(count: number): Msg {
  return msg('brandLibraries.profile.overview.componentsCount', { count });
}

// --- Tokens ------------------------------------------------------------------------------------
export const TOKENS_TITLE = msg('brandLibraries.profile.tokens.tokensTitle');
export const TOKENS_EMPTY = msg('brandLibraries.profile.tokens.tokensEmpty');
export const DRAFT_TOKENS_HINT = msg('brandLibraries.profile.tokens.draftTokensHint');

/** Heading over the draft's token tree, where the lock switches live. */
export function draftTokensTitle(versionNumber: number): Msg {
  return msg('brandLibraries.profile.tokens.draftTitle', { n: versionNumber });
}

/** Heading over the approved version's (read-only) token tree when a draft is shown above it. */
export function approvedTokensTitle(versionNumber: number): Msg {
  return msg('brandLibraries.profile.tokens.approvedTitle', { n: versionNumber });
}
export const TOKEN_PROVENANCE_LABEL = msg('brandLibraries.profile.tokens.tokenProvenanceLabel');
export const TOKEN_PROVENANCE_METHOD = msg('brandLibraries.profile.tokens.tokenProvenanceMethod');
export const TOKEN_PROVENANCE_IMPORTER = msg('brandLibraries.profile.tokens.tokenProvenanceImporter');
export const TOKEN_PROVENANCE_LOCATION = msg('brandLibraries.profile.tokens.tokenProvenanceLocation');
export const TOKEN_PROVENANCE_CONFIDENCE = msg('brandLibraries.profile.tokens.tokenProvenanceConfidence');
export const TOKEN_PROVENANCE_NONE = msg('brandLibraries.profile.tokens.tokenProvenanceNone');
export const TOKEN_PROVENANCE_TRIGGER = msg('brandLibraries.profile.tokens.tokenProvenanceTrigger');

/** Top-level token groups, in the order the tree renders them. */
export const TOKEN_GROUP_ORDER = [
  'color',
  'typography',
  'spacing',
  'radius',
  'shadow',
  'motion',
  'component',
  'z',
] as const;

export const TOKEN_GROUP_LABELS: Record<string, Msg> = {
  color: msg('brandLibraries.profile.tokens.groups.color'),
  typography: msg('brandLibraries.profile.tokens.groups.typography'),
  spacing: msg('brandLibraries.profile.tokens.groups.spacing'),
  radius: msg('brandLibraries.profile.tokens.groups.radius'),
  shadow: msg('brandLibraries.profile.tokens.groups.shadow'),
  motion: msg('brandLibraries.profile.tokens.groups.motion'),
  component: msg('brandLibraries.profile.tokens.groups.component'),
  z: msg('brandLibraries.profile.tokens.groups.z'),
};

// --- Confidence --------------------------------------------------------------------------------
export const BAND_LABELS: Record<ConfidenceBand, Msg> = {
  high: msg('brandLibraries.profile.confidence.bands.high'),
  medium: msg('brandLibraries.profile.confidence.bands.medium'),
  low: msg('brandLibraries.profile.confidence.bands.low'),
};

// --- Sources -----------------------------------------------------------------------------------
export const SOURCES_TITLE = msg('brandLibraries.profile.sources.sourcesTitle');
export const SOURCES_EMPTY = msg('brandLibraries.profile.sources.sourcesEmpty');
export const SOURCES_COLUMN_PATH = msg('brandLibraries.profile.sources.sourcesColumnPath');
export const SOURCES_COLUMN_IMPORTER = msg('brandLibraries.profile.sources.sourcesColumnImporter');
export const SOURCES_COLUMN_SHA = msg('brandLibraries.profile.sources.sourcesColumnSha');
export const SOURCES_COLUMN_STATUS = msg('brandLibraries.profile.sources.sourcesColumnStatus');

export const SOURCE_STATUS_LABELS: Record<DesignSourceStatus, Msg> = {
  Current: msg('brandLibraries.profile.sources.statuses.Current'),
  Changed: msg('brandLibraries.profile.sources.statuses.Changed'),
  Missing: msg('brandLibraries.profile.sources.statuses.Missing'),
};

// --- History -----------------------------------------------------------------------------------
export const HISTORY_TITLE = msg('brandLibraries.profile.history.historyTitle');
export const HISTORY_EMPTY = msg('brandLibraries.profile.history.historyEmpty');

export function versionLabel(versionNumber: number): Msg {
  return msg('brandLibraries.profile.history.versionLabel', { n: versionNumber });
}

export const VERSION_STATE_LABELS: Record<DesignVersionState, Msg> = {
  Draft: msg('brandLibraries.versionState.Draft'),
  Approved: msg('brandLibraries.versionState.Approved'),
  Superseded: msg('brandLibraries.versionState.Superseded'),
  Rejected: msg('brandLibraries.versionState.Rejected'),
};

export const VERSION_ORIGIN_LABELS: Record<DesignVersionOrigin, Msg> = {
  Import: msg('brandLibraries.profile.history.origins.Import'),
  StaticExtract: msg('brandLibraries.profile.history.origins.StaticExtract'),
  LlmExtract: msg('brandLibraries.profile.history.origins.LlmExtract'),
  Manual: msg('brandLibraries.profile.history.origins.Manual'),
  Merge: msg('brandLibraries.profile.history.origins.Merge'),
  Restore: msg('brandLibraries.profile.history.origins.Restore'),
};

// --- Build, import, review and the execution pin (P2-22; SPEC §17) -----------------------------

/** The build modal's opening sentence (SPEC §17/P2-22). */
export const BUILD_MODAL_TITLE = msg('brandLibraries.profile.build.buildModalTitle');
export const BUILD_MODAL_BODY = msg('brandLibraries.profile.build.buildModalBody');
export const BUILD_COST_NOTICE = msg('brandLibraries.profile.build.buildCostNotice');
export const BUILD_BUTTON_LABEL = msg('brandLibraries.profile.build.buildButtonLabel');
export const CONFIRM_BUTTON_LABEL = msg('brandLibraries.profile.build.confirmButtonLabel');
export const VIEW_RUN_LABEL = msg('brandLibraries.profile.build.viewRunLabel');
export const BUILD_ERROR = msg('brandLibraries.profile.build.buildError');

/** The estimate the confirm step shows, in plain language (SPEC §17). */
export function upToCredits(credits: number): Msg {
  return msg('brandLibraries.profile.build.upToCredits', { credits });
}

/** The running state: the current run status, as a sentence. */
export function buildRunningStatus(status: string): Msg {
  return msg('brandLibraries.profile.build.runningStatus', { status });
}

/** "Between 12 and 60 credits", with a typical figure when the server gives one. */
export function buildEstimateRange(low: number, high: number, typical: number | null): Msg {
  return msg('brandLibraries.profile.build.estimateRange', { low, high, hasTypical: typical === null ? 'no' : 'yes', typical: typical ?? '' });
}

/** The over-the-limit sentence: this build's estimate against the workspace's limit. */
export function buildOverCapText(low: number | null, high: number | null, cap: number | null): Msg {
  return msg('brandLibraries.profile.build.overCap', {
    estimate: low !== null && high !== null ? 'range' : high !== null ? 'upTo' : 'more',
    low: low ?? '',
    high: high ?? '',
    limit: cap !== null ? 'cap' : 'none',
    cap: cap ?? '',
  });
}

export const BUILD_FREE_PATH = msg('brandLibraries.profile.build.buildFreePath');
export const BUILD_IMPORT_INSTEAD_LABEL = msg('brandLibraries.profile.build.buildImportInsteadLabel');

// The accept-the-estimate step on a generic run or retry of the profile-build workflow.
export const ESTIMATE_GATE_TITLE = msg('brandLibraries.profile.build.estimateGateTitle');
export const ESTIMATE_GATE_BODY = msg('brandLibraries.profile.build.estimateGateBody');
export function estimateGateRange(low: number, high: number): Msg {
  return msg('brandLibraries.profile.estimateGate.range', { low, high });
}
export function estimateGateUpTo(high: number): Msg {
  return msg('brandLibraries.profile.estimateGate.upTo', { high });
}
export const ESTIMATE_GATE_CANCEL_LABEL = msg('brandLibraries.profile.build.estimateGateCancelLabel');
export const ESTIMATE_GATE_CLOSE_LABEL = msg('brandLibraries.profile.build.estimateGateCloseLabel');
export const ESTIMATE_GATE_OVER_CAP_TITLE = msg('brandLibraries.profile.build.estimateGateOverCapTitle');
export function estimateGateOverCapText(low: number | null, high: number | null, cap: number | null): Msg {
  return msg('brandLibraries.profile.estimateGate.overCap', {
    estimate: low !== null && high !== null ? 'range' : high !== null ? 'upTo' : 'more',
    low: low ?? '',
    high: high ?? '',
    limit: cap !== null ? 'cap' : 'none',
    cap: cap ?? '',
  });
}
export const ESTIMATE_GATE_FREE_PATH = msg('brandLibraries.profile.build.estimateGateFreePath');
export const ESTIMATE_GATE_PROFILE_LINK_LABEL = msg('brandLibraries.profile.build.estimateGateProfileLinkLabel');
export const ESTIMATE_GATE_UNAVAILABLE_TITLE = msg('brandLibraries.profile.build.estimateGateUnavailableTitle');
export const ESTIMATE_GATE_UNAVAILABLE = msg('brandLibraries.profile.build.estimateGateUnavailable');

// The finished-run states (replace "Building… (Passed)").
export const BUILD_DONE = msg('brandLibraries.profile.build.buildDone');
export const BUILD_DRAFT_READY = msg('brandLibraries.profile.build.buildDraftReady');
export const BUILD_NOT_SAVED = msg('brandLibraries.profile.build.buildNotSaved');
export const BUILD_SAVE_DRAFTS_LABEL = msg('brandLibraries.profile.build.buildSaveDraftsLabel');
export const BUILD_SAVED = msg('brandLibraries.profile.build.buildSaved');
export const BUILD_SAVE_ERROR = msg('brandLibraries.profile.build.buildSaveError');
export const BUILD_FAILED = msg('brandLibraries.profile.build.buildFailed');
export const BUILD_STOPPED = msg('brandLibraries.profile.build.buildStopped');
export const BUILD_CHECKING_DRAFTS = msg('brandLibraries.profile.build.buildCheckingDrafts');

/**
 * The step order of the Compose step in the \`product-profile-from-code\` template, where a build
 * leaves its profile artifact. "Save as drafts" reads that step's artifact (\`POST …/ingest\`).
 */
export const BUILD_COMPOSE_STEP_ORDER = 9;

// --- Import ------------------------------------------------------------------------------------
export const IMPORT_MODAL_TITLE = msg('brandLibraries.profile.import.importModalTitle');
export const IMPORT_FILES_LABEL = msg('brandLibraries.profile.import.importFilesLabel');
export const IMPORT_KIND_DESIGN_LABEL = msg('brandLibraries.profile.import.importKindDesignLabel');
export const IMPORT_KIND_PRODUCT_LABEL = msg('brandLibraries.profile.import.importKindProductLabel');
export const IMPORT_BUTTON_LABEL = msg('brandLibraries.profile.import.importButtonLabel');
export const IMPORT_HINT = msg('brandLibraries.profile.import.importHint');
export const IMPORT_SUCCESS = msg('brandLibraries.profile.import.importSuccess');
export const IMPORT_ERROR = msg('brandLibraries.profile.import.importError');

// --- Draft review and restore ------------------------------------------------------------------
export function draftReviewTitle(versionNumber: number): Msg {
  return msg('brandLibraries.profile.draftReview.title', { n: versionNumber });
}
export const APPROVE_BUTTON_LABEL = msg('brandLibraries.profile.draftReview.approveButtonLabel');
export const REJECT_BUTTON_LABEL = msg('brandLibraries.profile.draftReview.rejectButtonLabel');
export const RESTORE_BUTTON_LABEL = msg('brandLibraries.profile.draftReview.restoreButtonLabel');
export const EDIT_ONLY_TOOLTIP = msg('brandLibraries.profile.draftReview.editOnlyTooltip');
export const DRAFT_SUMMARY_ERROR = msg('brandLibraries.profile.draftReview.draftSummaryError');
export const DRAFT_SUMMARY_NO_CHANGES = msg('brandLibraries.profile.draftReview.draftSummaryNoChanges');
export const DRAFT_KEEP_REVIEWING_LABEL = msg('brandLibraries.profile.draftReview.draftKeepReviewingLabel');

/** "3 added · 2 changed · 1 removed", the headline of a draft's summary. */
export function draftSummaryCounts(added: number, changed: number, removed: number): Msg {
  return msg('brandLibraries.profile.draftReview.summaryCounts', { added, changed, removed });
}

/** "Routes: 3 added · 2 changed · 1 removed", one line per product-map collection. */
export function draftSectionCounts(label: Msg, added: number, changed: number, removed: number): Msg {
  return msg('brandLibraries.profile.draftReview.sectionCounts', { label, added, changed, removed });
}

/** "Also changed: directories, architecture", the product-map sections beyond the three main lists. */
export function draftAlsoChanged(sections: string[]): Msg {
  return msg('brandLibraries.profile.draftReview.alsoChanged', { sections: sections.join(', ') });
}

/** "Compared with version 1:" — the line that introduces the counts. */
export function draftComparedWith(currentVersionNumber: number): Msg {
  return msg('brandLibraries.profile.draftReview.comparedWith', { n: currentVersionNumber });
}

/** The first few token names, then "and 3 more" when there are more (one message, so the plural and the join are the catalogue's). */
export function draftNamesList(names: string, moreCount: number): Msg {
  return msg('brandLibraries.profile.draftReview.namesList', { names, count: moreCount });
}

/** The sentence on the confirm step: approving replaces what every run uses from now on. */
export function draftConfirmText(draftNumber: number, currentNumber: number): Msg {
  return msg('brandLibraries.profile.draftReview.confirmText', { draft: draftNumber, current: currentNumber });
}

/** The confirm text when there is no current version to compare with but the import raised a warning. */
export function draftConfirmFirstText(draftNumber: number): Msg {
  return msg('brandLibraries.profile.draftReview.confirmFirstText', { draft: draftNumber });
}

/** The import result's own heading for the warnings that mean the draft may be worse than what you have. */
export const IMPORT_WARNING_TITLE = msg('brandLibraries.profile.draftReview.importWarningTitle');

/** The confirm button, naming the version so it cannot be clicked by reflex. */
export function draftConfirmButton(draftNumber: number): Msg {
  return msg('brandLibraries.profile.draftReview.confirmButton', { draft: draftNumber });
}

// --- Execution pin badge (SPEC §3.5/§17) -------------------------------------------------------
export const PIN_USED_BRAND = msg('brandLibraries.profile.pin.pinUsedBrand');
export const PIN_USED_PRODUCT = msg('brandLibraries.profile.pin.pinUsedProduct');

/** "Used brand v7 / product v3", rendering only the members the execution actually pinned. */
export function executionPinLabel(designVersionNumber: number | null, productVersionNumber: number | null): Msg | null {
  const parts: Msg[] = [];
  if (designVersionNumber !== null) parts.push(msg('brandLibraries.profile.pin.usedBrand', { n: designVersionNumber }));
  if (productVersionNumber !== null) parts.push(msg('brandLibraries.profile.pin.usedProduct', { n: productVersionNumber }));
  if (parts.length === 0) return null;
  if (parts.length === 1) return parts[0];
  return msg('brandLibraries.profile.pin.both', { brand: parts[0], product: parts[1] });
}

// --- Proposals, versions and locks (P3-04; SPEC §9/§13.4/§17) ----------------------------------

export const SUB_TAB_PROPOSALS = msg('brandLibraries.profile.proposals.subTabProposals');
export const PROPOSALS_EMPTY = msg('brandLibraries.profile.proposals.proposalsEmpty');
export const PROPOSALS_TITLE = msg('brandLibraries.profile.proposals.proposalsTitle');
// A locked token the source now contradicts: the proposal refuses the change and keeps the current value.
export const PROPOSAL_CONFLICT_BADGE = msg('brandLibraries.profile.proposals.proposalConflictBadge');
export const LOCKED_LABEL = msg('brandLibraries.profile.proposals.lockedLabel');
export const LOCK_LABEL = msg('brandLibraries.profile.proposals.lockLabel');
export const UNLOCK_LABEL = msg('brandLibraries.profile.proposals.unlockLabel');
export const PROPOSAL_CHANGE_SCORE_LABEL = msg('brandLibraries.profile.proposals.proposalChangeScoreLabel');
export const REJECT_NOTE_LABEL = msg('brandLibraries.profile.proposals.rejectNoteLabel');
export const REJECT_NOTE_PLACEHOLDER = msg('brandLibraries.profile.proposals.rejectNotePlaceholder');
export const PROPOSAL_ERROR = msg('brandLibraries.profile.proposals.proposalError');

/** Why a proposal was raised, in plain language (no internal trigger names). */
export const PROPOSAL_TRIGGER_LABELS: Record<DesignProposalTrigger, Msg> = {
  SourceChanged: msg('brandLibraries.profile.proposals.triggers.SourceChanged'),
  Manual: msg('brandLibraries.profile.proposals.triggers.Manual'),
  Scheduled: msg('brandLibraries.profile.proposals.triggers.Scheduled'),
  Import: msg('brandLibraries.profile.proposals.triggers.Import'),
};

/** The proposal header's score, shown as a number so colour is never the only signal. */
export function proposalScore(score: number): string {
  return score.toFixed(2);
}

// --- Comparing two versions ---------------------------------------------------------------------

export const COMPARE_LABEL = msg('brandLibraries.profile.compare.compareLabel');
export const COMPARE_HINT = msg('brandLibraries.profile.compare.compareHint');
export const COMPARE_CLEAR_LABEL = msg('brandLibraries.profile.compare.compareClearLabel');
export const DIFF_ADDED_LABEL = msg('brandLibraries.profile.compare.diffAddedLabel');
export const DIFF_REMOVED_LABEL = msg('brandLibraries.profile.compare.diffRemovedLabel');
export const DIFF_CHANGED_LABEL = msg('brandLibraries.profile.compare.diffChangedLabel');
export const DIFF_BEFORE_LABEL = msg('brandLibraries.profile.compare.diffBeforeLabel');
export const DIFF_AFTER_LABEL = msg('brandLibraries.profile.compare.diffAfterLabel');
export const DIFF_PATH_LABEL = msg('brandLibraries.profile.compare.diffPathLabel');
export const DIFF_EMPTY = msg('brandLibraries.profile.compare.diffEmpty');
export const DIFF_ERROR = msg('brandLibraries.profile.compare.diffError');

/** "Changes from v1 to v2" — newer version first. */
export function changesFromTo(olderVersionNumber: number, newerVersionNumber: number): Msg {
  return msg('brandLibraries.profile.compare.changesFromTo', { older: olderVersionNumber, newer: newerVersionNumber });
}

/** The checkbox label that picks a version for the two-version compare. */
export function compareSelectionLabel(versionNumber: number): Msg {
  return msg('brandLibraries.profile.compare.selectionLabel', { version: versionLabel(versionNumber) });
}

// --- Staying current ---------------------------------------------------------------------------

export const CHECK_NOW_LABEL = msg('brandLibraries.profile.stayingCurrent.checkNowLabel');
export const CHECKING_LABEL = msg('brandLibraries.profile.stayingCurrent.checkingLabel');
export const CHECK_DONE = msg('brandLibraries.profile.stayingCurrent.checkDone');
export const CHECK_ERROR = msg('brandLibraries.profile.stayingCurrent.checkError');
export const REBUILD_WITH_AI_LABEL = msg('brandLibraries.profile.stayingCurrent.rebuildWithAiLabel');
export const LOCK_ERROR = msg('brandLibraries.profile.stayingCurrent.lockError');

// --- Failed loads ------------------------------------------------------------------------------
export const PANEL_ERROR = msg('brandLibraries.profile.failedLoads.panelError');
export const RETRY_LABEL = msg('brandLibraries.profile.failedLoads.retryLabel');

// --- Extended import: Figma variables, Brand PDF and diagnostics (P3-14; SPEC §17) --------------

export const IMPORT_FIGMA_TITLE = msg('brandLibraries.profile.extendedImport.importFigmaTitle');
export const IMPORT_FIGMA_FILE_KEY_LABEL = msg('brandLibraries.profile.extendedImport.importFigmaFileKeyLabel');
export const IMPORT_FIGMA_FILE_KEY_PLACEHOLDER = msg('brandLibraries.profile.extendedImport.importFigmaFileKeyPlaceholder');
export const IMPORT_FIGMA_TOKEN_LABEL = msg('brandLibraries.profile.extendedImport.importFigmaTokenLabel');
export const IMPORT_FIGMA_TOKEN_HINT = msg('brandLibraries.profile.extendedImport.importFigmaTokenHint');
export const IMPORT_FIGMA_BUTTON_LABEL = msg('brandLibraries.profile.extendedImport.importFigmaButtonLabel');
export const IMPORT_FIGMA_ERROR = msg('brandLibraries.profile.extendedImport.importFigmaError');

export const IMPORT_PDF_TITLE = msg('brandLibraries.profile.extendedImport.importPdfTitle');
export const IMPORT_PDF_LABEL = msg('brandLibraries.profile.extendedImport.importPdfLabel');
export const IMPORT_PDF_HINT = msg('brandLibraries.profile.extendedImport.importPdfHint');
export const IMPORT_PDF_BUTTON_LABEL = msg('brandLibraries.profile.extendedImport.importPdfButtonLabel');
export const IMPORT_PDF_ERROR = msg('brandLibraries.profile.extendedImport.importPdfError');
export const IMPORT_PDF_NOT_A_PDF = msg('brandLibraries.profile.extendedImport.importPdfNotAPdf');
export const IMPORT_PDF_TOO_LARGE = msg('brandLibraries.profile.extendedImport.importPdfTooLarge');
export const IMPORT_PDF_UNAVAILABLE = msg('brandLibraries.profile.extendedImport.importPdfUnavailable');
export const IMPORT_PDF_UNREADABLE = msg('brandLibraries.profile.extendedImport.importPdfUnreadable');

export const DIAGNOSTICS_TITLE = msg('brandLibraries.profile.extendedImport.diagnosticsTitle');
export const DIAGNOSTICS_EMPTY = msg('brandLibraries.profile.extendedImport.diagnosticsEmpty');
export const DIAGNOSTIC_SEVERITY_LABELS: Record<ImportDiagnosticSeverity, Msg> = {
  Info: msg('brandLibraries.profile.extendedImport.severities.Info'),
  Warn: msg('brandLibraries.profile.extendedImport.severities.Warn'),
  Error: msg('brandLibraries.profile.extendedImport.severities.Error'),
};
export const IMPORT_DONE_LABEL = msg('brandLibraries.profile.extendedImport.importDoneLabel');

// --- Archive / design-export import (P4-17; SPEC §16.4/§17) ------------------------------------

/** The upload label the contract names verbatim: the neutral word, never a vendor name. */
export const IMPORT_ZIP_TITLE = msg('brandLibraries.profile.archiveImport.importZipTitle');
export const IMPORT_ZIP_LABEL = msg('brandLibraries.profile.archiveImport.importZipLabel');
export const IMPORT_ZIP_HINT = msg('brandLibraries.profile.archiveImport.importZipHint');
export const IMPORT_ZIP_BUTTON_LABEL = msg('brandLibraries.profile.archiveImport.importZipButtonLabel');
export const IMPORT_ZIP_ERROR = msg('brandLibraries.profile.archiveImport.importZipError');
export const IMPORT_ZIP_NOT_A_ZIP = msg('brandLibraries.profile.archiveImport.importZipNotAZip');
export const IMPORT_ZIP_TOO_LARGE = msg('brandLibraries.profile.archiveImport.importZipTooLarge');
export const IMPORT_ZIP_UNAVAILABLE = msg('brandLibraries.profile.archiveImport.importZipUnavailable');
export const IMPORT_ZIP_REJECTED = msg('brandLibraries.profile.archiveImport.importZipRejected');

/** The blocked/unrecognized sentence from the contract, with the next steps that follow it. */
export const IMPORT_ZIP_NOTHING_RECOGNIZED = msg('brandLibraries.profile.archiveImport.importZipNothingRecognized');
export const IMPORT_ZIP_NEXT_STEPS = msg('brandLibraries.profile.archiveImport.importZipNextSteps');

export const ZIP_VENDOR_LABEL = msg('brandLibraries.profile.archiveImport.zipVendorLabel');
export const ZIP_VENDOR_UNKNOWN = msg('brandLibraries.profile.archiveImport.zipVendorUnknown');
export const ZIP_RECOGNIZED_LABEL = msg('brandLibraries.profile.archiveImport.zipRecognizedLabel');
export const ZIP_IGNORED_LABEL = msg('brandLibraries.profile.archiveImport.zipIgnoredLabel');

/** "Export format: lovable", or the neutral fallback when the archive matched no layout. */
export function zipVendorText(vendor: string | null): Msg {
  return msg('brandLibraries.profile.archiveImport.vendorText', { vendor: vendor ?? ZIP_VENDOR_UNKNOWN });
}

/** "Files read: 3" — one number, labelled, so colour is never the only signal. */
export function zipRecognizedText(count: number): Msg {
  return msg('brandLibraries.profile.archiveImport.recognizedText', { count });
}

/** "Entries skipped: 2" — entries the reader saw but could not use. */
export function zipIgnoredText(count: number): Msg {
  return msg('brandLibraries.profile.archiveImport.ignoredText', { count });
}

// --- Skipped files after an archive import (T-016) ---------------------------------------------
export const ZIP_SKIPPED_TITLE = msg('brandLibraries.profile.skipped.zipSkippedTitle');
export const ZIP_SKIPPED_EXAMPLES_LABEL = msg('brandLibraries.profile.skipped.zipSkippedExamplesLabel');


/** One plain-language sentence per worker reason code; an unknown code falls back to the count. */
export function zipSkippedText(reason: string, count: number): Msg {
  switch (reason) {
    case 'DROPPED_SCRIPT':
      return msg('brandLibraries.profile.skipped.droppedScript', { count });
    case 'DROPPED_HTML':
      return msg('brandLibraries.profile.skipped.droppedHtml', { count });
    case 'DOCUMENT_TOO_LARGE':
      return msg('brandLibraries.profile.skipped.documentTooLarge', { count });
    default:
      return msg('brandLibraries.profile.skipped.default', { count });
  }
}

/** "For example: a1.js, a2.js" — names are rendered as text only. */
export function zipSkippedExamplesText(examples: string[]): Msg {
  return msg('brandLibraries.profile.skipped.examplesText', { examples: examples.join(', ') });
}

// --- Brand font previews -------------------------------------------------------------------------
export const TYPE_SPECIMEN_UNAVAILABLE = msg('brandLibraries.profile.fontPreviews.typeSpecimenUnavailable');
export const TYPE_SPECIMEN_GENERIC = msg('brandLibraries.profile.fontPreviews.typeSpecimenGeneric');

// --- Importer capability matrix (T-101; SPEC §17) ------------------------------------------------

export const CAPABILITIES_TITLE = msg('brandLibraries.profile.capabilities.capabilitiesTitle');
export const CAPABILITIES_EXPAND_LABEL = msg('brandLibraries.profile.capabilities.capabilitiesExpandLabel');
export const CAPABILITIES_COLLAPSE_LABEL = msg('brandLibraries.profile.capabilities.capabilitiesCollapseLabel');
export const CAPABILITIES_HINT = msg('brandLibraries.profile.capabilities.capabilitiesHint');
export const CAPABILITIES_LOADING = msg('brandLibraries.profile.capabilities.capabilitiesLoading');
export const CAPABILITIES_UNAVAILABLE = msg('brandLibraries.profile.capabilities.capabilitiesUnavailable');
export const CAPABILITIES_EMPTY = msg('brandLibraries.profile.capabilities.capabilitiesEmpty');
export const CAPABILITY_IMPORTER_COLUMN = msg('brandLibraries.profile.capabilities.capabilityImporterColumn');
export const CAPABILITY_DETAILS_COLUMN = msg('brandLibraries.profile.capabilities.capabilityDetailsColumn');
export const CAPABILITY_OPEN_DETAILS_LABEL = msg('brandLibraries.profile.capabilities.capabilityOpenDetailsLabel');
export const CAPABILITY_HIDE_DETAILS_LABEL = msg('brandLibraries.profile.capabilities.capabilityHideDetailsLabel');

export const CAPABILITY_SUPPORT_LABELS: Record<ImportCapabilitySupport, Msg> = {
  Full: msg('brandLibraries.profile.capabilities.supports.Full'),
  Partial: msg('brandLibraries.profile.capabilities.supports.Partial'),
  None: msg('brandLibraries.profile.capabilities.supports.None'),
};

/**
 * Plain-language headings for every axis the server sends. An unknown axis falls back to its own
 * name, so a server that appends one still renders.
 */
export const CAPABILITY_AXIS_LABELS: Record<string, Msg> = {
  Tokens: msg('brandLibraries.profile.capabilities.axes.Tokens'),
  Modes: msg('brandLibraries.profile.capabilities.axes.Modes'),
  Assets: msg('brandLibraries.profile.capabilities.axes.Assets'),
  Fonts: msg('brandLibraries.profile.capabilities.axes.Fonts'),
  Components: msg('brandLibraries.profile.capabilities.axes.Components'),
  PreviewCards: msg('brandLibraries.profile.capabilities.axes.PreviewCards'),
  Prose: msg('brandLibraries.profile.capabilities.axes.Prose'),
  Voice: msg('brandLibraries.profile.capabilities.axes.Voice'),
  DerivedTokens: msg('brandLibraries.profile.capabilities.axes.DerivedTokens'),
  ReferenceDocuments: msg('brandLibraries.profile.capabilities.axes.ReferenceDocuments'),
};

export function capabilityAxisLabel(axis: string): Msg {
  return CAPABILITY_AXIS_LABELS[axis] ?? msg('brandLibraries.profile.capabilities.axisUnknown', { axis });
}

/** The accessible name of one cell: what it reads, how much, and why — never colour alone. */
export function capabilityCellLabel(importerLabel: string, axis: string, support: ImportCapabilitySupport, note: string): Msg {
  return msg('brandLibraries.profile.capabilities.cellLabel', {
    importer: importerLabel,
    axis: capabilityAxisLabel(axis),
    support: CAPABILITY_SUPPORT_LABELS[support],
    hasNote: note ? 'yes' : 'no',
    note,
  });
}

/** The expand/collapse button's accessible name, naming the row it belongs to. */
export function capabilityDetailsLabel(importerLabel: string, expanded: boolean): Msg {
  return expanded
    ? msg('brandLibraries.profile.capabilities.hideDetailsFor', { importer: importerLabel })
    : msg('brandLibraries.profile.capabilities.showDetailsFor', { importer: importerLabel });
}

// --- What one import carried and dropped (T-102; SPEC §17) ---------------------------------------

export const IMPORT_REPORT_TITLE = msg('brandLibraries.profile.importReport.importReportTitle');
export const IMPORT_REPORT_AXIS_COLUMN = msg('brandLibraries.profile.importReport.importReportAxisColumn');
export const IMPORT_REPORT_SUPPORT_COLUMN = msg('brandLibraries.profile.importReport.importReportSupportColumn');
export const IMPORT_REPORT_IMPORTED_COLUMN = msg('brandLibraries.profile.importReport.importReportImportedColumn');
export const IMPORT_REPORT_DROPPED_COLUMN = msg('brandLibraries.profile.importReport.importReportDroppedColumn');
export const IMPORT_REPORT_DROPPED_BECAUSE_COLUMN = msg('brandLibraries.profile.importReport.importReportDroppedBecauseColumn');
export const IMPORT_REPORT_NOTHING_DROPPED = msg('brandLibraries.profile.importReport.importReportNothingDropped');
export const IMPORT_ASSETS_LABEL = msg('brandLibraries.profile.importReport.importAssetsLabel');
export const IMPORT_REFERENCES_LABEL = msg('brandLibraries.profile.importReport.importReferencesLabel');
export const IMPORT_PRODUCT_MAP_LINK = msg('brandLibraries.profile.importReport.importProductMapLink');

/** The report names the same axes and support levels as the capability matrix, in the same words. */
export const IMPORT_REPORT_AXIS_LABELS = CAPABILITY_AXIS_LABELS;
export const IMPORT_REPORT_SUPPORT_LABELS = CAPABILITY_SUPPORT_LABELS;

export function importAxisLabel(axis: string): Msg {
  return IMPORT_REPORT_AXIS_LABELS[axis] ?? msg('brandLibraries.profile.capabilities.axisUnknown', { axis });
}

/** "Files stored: 3" — the binary assets the route kept as project files. */
export function importAssetsText(count: number): Msg {
  return msg('brandLibraries.profile.importReport.assetsText', { count });
}

/** "Reference documents stored: 2" — the prose documents the route kept as project files. */
export function importReferencesText(count: number): Msg {
  return msg('brandLibraries.profile.importReport.referencesText', { count });
}

/**
 * One plain-language sentence per dropped reason code. An unknown code falls back to the sanitized
 * API message, never to a raw dump of the worker's vocabulary.
 */
export function importDropSentence(code: string, message: string): Msg {
  switch (code) {
    case 'UNSUPPORTED_VALUE':
      return msg('brandLibraries.profile.importReport.drop.unsupportedValue');
    case 'MIX_UNRESOLVED':
      return msg('brandLibraries.profile.importReport.drop.mixUnresolved');
    case 'GRADIENT_NOT_REPRESENTABLE':
      return msg('brandLibraries.profile.importReport.drop.gradientNotRepresentable');
    case 'CSS_TOKEN_UNMAPPED':
      return msg('brandLibraries.profile.importReport.drop.cssTokenUnmapped');
    case 'CLAUDE_DESIGN_FILE_SKIPPED':
      return msg('brandLibraries.profile.importReport.drop.designFileSkipped');
    case 'DROPPED_SCRIPT':
      return msg('brandLibraries.profile.importReport.drop.droppedScript');
    case 'DROPPED_BINARY':
      return msg('brandLibraries.profile.importReport.drop.droppedBinary');
    case 'BUNDLE_MARKUP_NOT_READ':
      return msg('brandLibraries.profile.importReport.drop.bundleMarkupNotRead');
    case 'SVG_REFUSED':
      return msg('brandLibraries.profile.importReport.drop.svgRefused');
    case 'FONT_SOURCE_NOT_FETCHED':
      return msg('brandLibraries.profile.importReport.drop.fontSourceNotFetched');
    case 'FONT_FILE_REJECTED':
      return msg('brandLibraries.profile.importReport.drop.fontFileRejected');
    case 'REFERENCE_CAP':
      return msg('brandLibraries.profile.importReport.drop.referenceCap');
    case 'REFERENCE_TOO_LARGE':
      return msg('brandLibraries.profile.importReport.drop.referenceTooLarge');
    case 'ASSET_QUARANTINED':
      return msg('brandLibraries.profile.importReport.drop.assetQuarantined');
    default:
      return msg('brandLibraries.profile.importReport.drop.fallback', { message });
  }
}
