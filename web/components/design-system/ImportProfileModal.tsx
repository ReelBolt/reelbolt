'use client';

import { describeError } from '@/lib/api/describe-error';
import { useCallback, useMemo, useState } from 'react';
import {
  Badge,
  Button,
  Checkbox,
  Divider,
  FileInput,
  Group,
  Modal,
  MultiSelect,
  PasswordInput,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useSWRConfig } from 'swr';
import { QuietText } from '@/components/dashboard/Panel';
import { ApiError, extractErrorMessage } from '@/lib/api/client';
import { importBrandPdf, importDesign, importFigmaVariables, importZip } from '@/lib/api/design-system';
import { FIGMA_IMPORT_KEY, useFigmaImportEnabled } from '@/lib/hooks/use-figma-import-enabled';
import { useProjectFiles } from '@/lib/hooks/use-files';
import type {
  DesignSystemKind,
  DesignSystemsSummary,
  DesignSystemSummary,
  DesignVersionSummary,
  ImportDiagnostic,
  ImportDiagnosticSeverity,
  ImportReport,
  SkippedFilesGroup,
} from '@/lib/types/design-system';
import { DraftReviewBanner } from './DraftReviewBanner';
import { ImportCapabilityMatrix } from './ImportCapabilityMatrix';
import { ImportReportPanel } from './ImportReportPanel';
import {
  CAPABILITIES_COLLAPSE_LABEL,
  CAPABILITIES_EXPAND_LABEL,
  CAPABILITIES_HINT,
  DIAGNOSTICS_EMPTY,
  DIAGNOSTICS_TITLE,
  DIAGNOSTIC_SEVERITY_LABELS,
  IMPORT_BUTTON_LABEL,
  IMPORT_DONE_LABEL,
  IMPORT_ERROR,
  IMPORT_FIGMA_BUTTON_LABEL,
  IMPORT_FIGMA_ERROR,
  IMPORT_FIGMA_FILE_KEY_LABEL,
  IMPORT_FIGMA_FILE_KEY_PLACEHOLDER,
  IMPORT_FIGMA_TITLE,
  IMPORT_FIGMA_TOKEN_HINT,
  IMPORT_FIGMA_TOKEN_LABEL,
  IMPORT_FILES_LABEL,
  IMPORT_HINT,
  IMPORT_KIND_DESIGN_LABEL,
  IMPORT_KIND_PRODUCT_LABEL,
  IMPORT_MODAL_TITLE,
  IMPORT_PDF_BUTTON_LABEL,
  IMPORT_PDF_ERROR,
  IMPORT_PDF_HINT,
  IMPORT_PDF_LABEL,
  IMPORT_PDF_NOT_A_PDF,
  IMPORT_PDF_TOO_LARGE,
  IMPORT_PDF_TITLE,
  IMPORT_PDF_UNAVAILABLE,
  IMPORT_PDF_UNREADABLE,
  IMPORT_PRODUCT_MAP_LINK,
  IMPORT_SUCCESS,
  IMPORT_WARNING_TITLE,
  IMPORT_ZIP_BUTTON_LABEL,
  IMPORT_ZIP_ERROR,
  IMPORT_ZIP_HINT,
  IMPORT_ZIP_LABEL,
  IMPORT_ZIP_NEXT_STEPS,
  IMPORT_ZIP_NOTHING_RECOGNIZED,
  IMPORT_ZIP_NOT_A_ZIP,
  IMPORT_ZIP_REJECTED,
  IMPORT_ZIP_TITLE,
  IMPORT_ZIP_TOO_LARGE,
  IMPORT_ZIP_UNAVAILABLE,
  importAssetsText,
  importReferencesText,
  zipIgnoredText,
  zipSkippedExamplesText,
  zipSkippedText,
  ZIP_SKIPPED_TITLE,
  zipRecognizedText,
  zipVendorText,
} from './copy';
import mobile from './mobile.module.css';
import { useT, type T } from '@/i18n';

interface ImportProfileModalProps {
  projectId: string;
  opened: boolean;
  onClose: () => void;
  /** Called after a successful import so the tab can refresh the summaries. */
  onImported?: () => void;
  /** False only disables the draft banner's approve/reject pair; the API is the real guard. */
  canEdit?: boolean;
  /** The project's current design summaries, so the draft is compared with the approved version. */
  current?: DesignSystemsSummary | null;
}

/** The largest import the route accepts (SPEC §9). */
const MAX_IMPORT_FILES = 50;

const SEVERITY_COLORS: Record<ImportDiagnosticSeverity, string> = {
  Info: 'blue',
  Warn: 'yellow',
  Error: 'red',
};

/** The versions and notes one successful import produced, shown under the forms. */
interface ImportOutcome {
  versions: DesignVersionSummary[];
  diagnostics: ImportDiagnostic[];
  /** The axis-tagged summary, when the API sent one (T-102). */
  report?: ImportReport | null;
  /** The zip route's kept binary assets and reference documents; absent for the other routes. */
  assetCount?: number;
  referenceCount?: number;
  /** True when the zip route created a product-map Draft with preview cards. */
  productMapCreated?: boolean;
  /** Only an archive import carries these: the vendor label and the read/skipped entry counts. */
  bundle?: {
    vendor: string | null;
    recognized: number;
    ignored: number;
    skipped: SkippedFilesGroup[];
  };
}

/** The diagnostic codes that mean an archive entry was seen but not used (P4-17). */
const IGNORED_ENTRY_CODES = new Set(['BUNDLE_FILE_DROPPED', 'FILE_TOO_LARGE']);

/** How many archive entries the reader recorded as skipped. */
function ignoredEntryCount(diagnostics: ImportDiagnostic[]): number {
  return diagnostics.filter((diagnostic) => IGNORED_ENTRY_CODES.has(diagnostic.code)).length;
}

/**
 * The draft banner reads a summary. `current` is the project's real summaries, so the banner can
 * diff the new draft against the approved version and ask before replacing it; without it (the
 * tab has not loaded) the draft is shown as a first version.
 */
function draftSummary(version: DesignVersionSummary, current: DesignSystemsSummary | null): DesignSystemSummary {
  const existing = [current?.design, current?.productMap].find((s) => s?.id === version.designSystemId) ?? null;
  return {
    id: version.designSystemId,
    kind: existing?.kind ?? 'design',
    name: existing?.name ?? '',
    currentVersion: existing?.currentVersion ?? null,
    latestDraft: version,
    pendingProposals: existing?.pendingProposals ?? 0,
    parentLibrary: existing?.parentLibrary ?? null,
  };
}

/** The diagnostic codes that say the import found nothing, or less than the approved design has. */
const IMPORT_WARNING_CODES = new Set(['IMPORT_ZERO_TOKENS', 'IMPORT_LOSES_TOKENS']);

/** The API's sentences for those codes, joined, or null when the import raised none. */
function importWarning(diagnostics: ImportDiagnostic[]): string | null {
  const messages = diagnostics.filter((d) => IMPORT_WARNING_CODES.has(d.code)).map((d) => d.message);
  return messages.length > 0 ? messages.join(' ') : null;
}

/** The API's sentence when it carried one, else the fallback — for every non-PDF import. */
function importErrorMessage(error: unknown, tr: T, fallback: string): string {
  if (error instanceof ApiError) {
    return extractErrorMessage(error.body) ?? describeError(tr, error, fallback);
  }
  return describeError(tr, error, fallback);
}

/** Plain-language sentences for the PDF route's refusals (415/413/503/422), then the API's own. */
function pdfErrorMessage(error: unknown, tr: T): string {
  if (error instanceof ApiError) {
    if (error.status === 415) return tr.msg(IMPORT_PDF_NOT_A_PDF);
    if (error.status === 413) return tr.msg(IMPORT_PDF_TOO_LARGE);
    if (error.status === 503) return tr.msg(IMPORT_PDF_UNAVAILABLE);
    if (error.status === 422) return tr.msg(IMPORT_PDF_UNREADABLE);
    return extractErrorMessage(error.body) ?? describeError(tr, error, tr.msg(IMPORT_PDF_ERROR));
  }
  return describeError(tr, error, tr.msg(IMPORT_PDF_ERROR));
}

/** The API's `code` when a refusal carried one, so a specific state can pick its own sentence. */
function errorCode(error: unknown): string | null {
  if (!(error instanceof ApiError)) return null;
  if (typeof error.body !== 'object' || error.body === null) return null;
  const code = (error.body as { code?: unknown }).code;
  return typeof code === 'string' ? code : null;
}

/**
 * Plain-language sentences for the archive route's refusals (415/413/503/400), then the API's own.
 * `IMPORT_NOTHING_RECOGNIZED` is the one that also carries next steps (P4-17, SPEC §16.4).
 */
function zipErrorMessage(error: unknown, tr: T): ZipErrorState {
  const skipped = skippedFromBody(error);
  const base = zipErrorBase(error, tr);
  return skipped.length > 0 ? { ...base, skipped } : base;
}

type ZipErrorState = { message: string; nextSteps?: string; skipped?: SkippedFilesGroup[] };

/** The `skipped` groups a refusal body carried (400 `IMPORT_NOTHING_RECOGNIZED`), validated loosely. */
function skippedFromBody(error: unknown): SkippedFilesGroup[] {
  if (!(error instanceof ApiError)) return [];
  if (typeof error.body !== 'object' || error.body === null) return [];
  return normalizeSkipped((error.body as { skipped?: unknown }).skipped);
}

function normalizeSkipped(raw: unknown): SkippedFilesGroup[] {
  if (!Array.isArray(raw)) return [];
  const groups: SkippedFilesGroup[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const { reason, count, examples } = item as Record<string, unknown>;
    if (typeof reason !== 'string' || typeof count !== 'number') continue;
    groups.push({
      reason,
      count,
      examples: Array.isArray(examples)
        ? examples.filter((e): e is string => typeof e === 'string').slice(0, 3)
        : [],
    });
  }
  return groups;
}

/** One line per skipped reason: friendly sentence, then up to 3 example names as plain text. */
function SkippedFilesList({ skipped }: { skipped: SkippedFilesGroup[] }) {
  const tr = useT();
  if (skipped.length === 0) return null;
  return (
    <Stack gap={2} data-testid="zip-skipped">
      <Text size="sm" fw={600}>
        {tr.msg(ZIP_SKIPPED_TITLE)}
      </Text>
      {skipped.map((group) => (
        <Stack key={group.reason} gap={0} data-testid="zip-skipped-row">
          <Text size="sm">{tr.msg(zipSkippedText(group.reason, group.count))}</Text>
          {group.examples.length > 0 && (
            <Text size="xs" c="dimmed">
              {tr.msg(zipSkippedExamplesText(group.examples))}
            </Text>
          )}
        </Stack>
      ))}
    </Stack>
  );
}

function zipErrorBase(error: unknown, tr: T): { message: string; nextSteps?: string } {
  if (error instanceof ApiError) {
    if (errorCode(error) === 'IMPORT_NOTHING_RECOGNIZED')
      return { message: tr.msg(IMPORT_ZIP_NOTHING_RECOGNIZED), nextSteps: tr.msg(IMPORT_ZIP_NEXT_STEPS) };
    if (error.status === 415) return { message: tr.msg(IMPORT_ZIP_NOT_A_ZIP) };
    if (error.status === 413) return { message: tr.msg(IMPORT_ZIP_TOO_LARGE) };
    if (error.status === 503) return { message: tr.msg(IMPORT_ZIP_UNAVAILABLE) };
    if (error.status === 400) return { message: tr.msg(IMPORT_ZIP_REJECTED) };
    return { message: extractErrorMessage(error.body) ?? describeError(tr, error, tr.msg(IMPORT_ZIP_ERROR)) };
  }
  return { message: describeError(tr, error, tr.msg(IMPORT_ZIP_ERROR)) };
}

/** Severity badge + code + path + message, one row each (SPEC §17). */
function DiagnosticsList({ diagnostics }: { diagnostics: ImportDiagnostic[] }) {
  const tr = useT();
  if (diagnostics.length === 0) {
    return <QuietText data-testid="import-diagnostics-empty">{tr.msg(DIAGNOSTICS_EMPTY)}</QuietText>;
  }

  return (
    <Stack gap="xs" data-testid="import-diagnostics">
      <Text fw={600}>{tr.msg(DIAGNOSTICS_TITLE)}</Text>
      {diagnostics.map((diagnostic, index) => (
        <Group
          key={`${diagnostic.code}-${diagnostic.path}-${index}`}
          gap="xs"
          align="flex-start"
          wrap="nowrap"
          className={mobile.wrapRow}
          data-testid={`import-diagnostic-${diagnostic.severity}`}
        >
          <Badge color={SEVERITY_COLORS[diagnostic.severity] ?? 'gray'} variant="light">
            {(DIAGNOSTIC_SEVERITY_LABELS[diagnostic.severity] ? tr.msg(DIAGNOSTIC_SEVERITY_LABELS[diagnostic.severity]) : diagnostic.severity)}
          </Badge>
          <Stack gap={0}>
            <Text size="sm">{diagnostic.message}</Text>
            <QuietText>
              {diagnostic.path ? `${diagnostic.code} · ${diagnostic.path}` : diagnostic.code}
            </QuietText>
          </Stack>
        </Group>
      ))}
    </Stack>
  );
}

/**
 * The Import flow of the brand-and-product tab (P2-22, extended by P3-14; SPEC §9/§16.4/§17): pick
 * project files and the kinds of profile to read from them (all text importers are auto-detected),
 * import Figma variables with a one-shot access token, or upload a Brand PDF. Every import shows
 * its diagnostics and the Draft review banner; nothing is approved here.
 */
export function ImportProfileModal({
  projectId,
  opened,
  onClose,
  onImported,
  canEdit = true,
  current = null,
}: ImportProfileModalProps) {
  const tr = useT();
  const { data } = useProjectFiles(projectId);
  const files = data ?? [];
  const { mutate: mutateGlobal } = useSWRConfig();
  const figmaAvailable = useFigmaImportEnabled(projectId, opened);

  const [fileIds, setFileIds] = useState<string[]>([]);
  const [design, setDesign] = useState(true);
  const [productMap, setProductMap] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  const [figmaFileKey, setFigmaFileKey] = useState('');
  const [figmaToken, setFigmaToken] = useState('');
  const [figmaSubmitting, setFigmaSubmitting] = useState(false);

  const [pdfFile, setPdfFile] = useState<File | null>(null);
  const [pdfSubmitting, setPdfSubmitting] = useState(false);

  const [zipFile, setZipFile] = useState<File | null>(null);
  const [zipSubmitting, setZipSubmitting] = useState(false);
  const [zipError, setZipError] = useState<ZipErrorState | null>(null);

  const [error, setError] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<ImportOutcome | null>(null);
  /** Closed by default: the matrix is fetched only when the customer asks to see it. */
  const [capabilitiesOpen, setCapabilitiesOpen] = useState(false);

  /** Every close path resets, so reopening never shows the previous selection or result. */
  const handleClose = useCallback(() => {
    setFileIds([]);
    setDesign(true);
    setProductMap(false);
    setSubmitting(false);
    setFigmaFileKey('');
    setFigmaToken('');
    setFigmaSubmitting(false);
    setPdfFile(null);
    setPdfSubmitting(false);
    setZipFile(null);
    setZipSubmitting(false);
    setZipError(null);
    setError(null);
    setOutcome(null);
    setCapabilitiesOpen(false);
    onClose();
  }, [onClose]);

  const kinds = useMemo<DesignSystemKind[]>(() => {
    const out: DesignSystemKind[] = [];
    if (design) out.push('design');
    if (productMap) out.push('product_map');
    return out;
  }, [design, productMap]);

  const canSubmit = fileIds.length > 0 && kinds.length > 0 && !submitting;
  const canSubmitFigma = figmaFileKey.trim().length > 0 && figmaToken.length > 0 && !figmaSubmitting;
  const canSubmitPdf = pdfFile !== null && !pdfSubmitting;
  const canSubmitZip = zipFile !== null && !zipSubmitting;

  /** One landing point for a success: the notes, the report, the banner and the tab's refresh. */
  const finish = useCallback(
    (
      versions: DesignVersionSummary[],
      diagnostics: ImportDiagnostic[],
      extra: Pick<
        ImportOutcome,
        'report' | 'assetCount' | 'referenceCount' | 'productMapCreated' | 'bundle'
      > = {},
    ) => {
      setOutcome({ versions, diagnostics, ...extra });
      notifications.show({ color: 'green', message: tr.msg(IMPORT_SUCCESS) });
      onImported?.();
    },
    [onImported, tr],
  );

  const submit = useCallback(async () => {
    if (fileIds.length === 0 || kinds.length === 0) return;
    setSubmitting(true);
    setError(null);
    try {
      const response = await importDesign(projectId, fileIds, kinds);
      finish(response?.versions ?? [], response?.diagnostics ?? [], {
        report: response?.report,
      });
    } catch (e) {
      setError(importErrorMessage(e, tr, tr.msg(IMPORT_ERROR)));
    } finally {
      setSubmitting(false);
    }
  }, [projectId, fileIds, kinds, finish, tr]);

  const submitFigma = useCallback(async () => {
    const token = figmaToken;
    const fileKey = figmaFileKey.trim();
    if (!fileKey || !token) return;
    setFigmaSubmitting(true);
    setError(null);
    // The token is cleared the moment it is handed to the request: it lives in state for no longer.
    setFigmaToken('');
    try {
      const response = await importFigmaVariables(projectId, fileKey, token);
      finish([response.version], response.diagnostics ?? [], { report: response.report });
      setFigmaFileKey('');
    } catch (e) {
      if (e instanceof ApiError && e.status === 404 && e.code === 'feature_disabled') {
        // The server just told us it is off: remember it so the section stays hidden.
        void mutateGlobal(FIGMA_IMPORT_KEY, false, { revalidate: false });
      } else {
        setError(importErrorMessage(e, tr, tr.msg(IMPORT_FIGMA_ERROR)));
      }
    } finally {
      setFigmaSubmitting(false);
    }
  }, [projectId, figmaFileKey, figmaToken, finish, mutateGlobal, tr]);

  const submitPdf = useCallback(async () => {
    if (!pdfFile) return;
    setPdfSubmitting(true);
    setError(null);
    try {
      const response = await importBrandPdf(projectId, pdfFile);
      finish([response.version], response.diagnostics ?? [], { report: response.report });
      setPdfFile(null);
    } catch (e) {
      setError(pdfErrorMessage(e, tr));
    } finally {
      setPdfSubmitting(false);
    }
  }, [projectId, pdfFile, finish, tr]);

  const submitZip = useCallback(async () => {
    if (!zipFile) return;
    setZipSubmitting(true);
    setZipError(null);
    try {
      const response = await importZip(projectId, zipFile);
      const diagnostics = response.diagnostics ?? [];
      finish([response.version], diagnostics, {
        report: response.report,
        assetCount: response.assets?.length ?? 0,
        referenceCount: response.references?.length ?? 0,
        productMapCreated: Boolean(response.productMapVersion),
        bundle: {
          vendor: response.vendor ?? null,
          recognized: response.sources?.length ?? 0,
          ignored: ignoredEntryCount(diagnostics),
          skipped: normalizeSkipped(response.skipped),
        },
      });
      setZipFile(null);
    } catch (e) {
      setZipError(zipErrorMessage(e, tr));
    } finally {
      setZipSubmitting(false);
    }
  }, [projectId, zipFile, finish, tr]);

  return (
    <Modal opened={opened} onClose={handleClose} title={tr.msg(IMPORT_MODAL_TITLE)}>
      <Stack gap="md">
        <QuietText>{tr.msg(IMPORT_HINT)}</QuietText>

        <Stack gap="xs" data-testid="import-capabilities-section">
          <Button
            variant="subtle"
            size="compact-sm"
            onClick={() => setCapabilitiesOpen((open) => !open)}
            aria-expanded={capabilitiesOpen}
            aria-controls="import-capabilities"
            data-testid="capabilities-toggle"
          >
            {capabilitiesOpen ? tr.msg(CAPABILITIES_COLLAPSE_LABEL) : tr.msg(CAPABILITIES_EXPAND_LABEL)}
          </Button>
          {capabilitiesOpen ? (
            <Stack gap={4} id="import-capabilities">
              <Text size="sm" c="dimmed">
                {tr.msg(CAPABILITIES_HINT)}
              </Text>
              <ImportCapabilityMatrix />
            </Stack>
          ) : null}
        </Stack>

        <MultiSelect
          label={tr.msg(IMPORT_FILES_LABEL)}
          data={files.map((file) => ({ value: file.id, label: file.originalFileName }))}
          value={fileIds}
          onChange={setFileIds}
          searchable
          maxValues={MAX_IMPORT_FILES}
          nothingFoundMessage="No matching files"
          data-testid="import-files"
        />

        <Stack gap="xs">
          <Checkbox
            label={tr.msg(IMPORT_KIND_DESIGN_LABEL)}
            checked={design}
            onChange={(event) => setDesign(event.currentTarget.checked)}
          />
          <Checkbox
            label={tr.msg(IMPORT_KIND_PRODUCT_LABEL)}
            checked={productMap}
            onChange={(event) => setProductMap(event.currentTarget.checked)}
          />
        </Stack>

        <Button
          onClick={() => void submit()}
          disabled={!canSubmit}
          loading={submitting}
          data-testid="import-submit"
        >
          {tr.msg(IMPORT_BUTTON_LABEL)}
        </Button>

        {figmaAvailable && (
          <>
          <Divider label={tr.msg(IMPORT_FIGMA_TITLE)} labelPosition="left" />
          <TextInput
            label={tr.msg(IMPORT_FIGMA_FILE_KEY_LABEL)}
            placeholder={tr.msg(IMPORT_FIGMA_FILE_KEY_PLACEHOLDER)}
            value={figmaFileKey}
            onChange={(event) => setFigmaFileKey(event.currentTarget.value)}
            data-testid="figma-file-key"
          />
          <PasswordInput
            label={tr.msg(IMPORT_FIGMA_TOKEN_LABEL)}
            description={tr.msg(IMPORT_FIGMA_TOKEN_HINT)}
            value={figmaToken}
            onChange={(event) => setFigmaToken(event.currentTarget.value)}
            data-testid="figma-token"
          />
          <Button
            onClick={() => void submitFigma()}
            disabled={!canSubmitFigma}
            loading={figmaSubmitting}
            data-testid="figma-submit"
          >
            {tr.msg(IMPORT_FIGMA_BUTTON_LABEL)}
          </Button>
          </>
        )}

        <Divider label={tr.msg(IMPORT_PDF_TITLE)} labelPosition="left" />
        <FileInput
          label={tr.msg(IMPORT_PDF_LABEL)}
          description={tr.msg(IMPORT_PDF_HINT)}
          accept="application/pdf,.pdf"
          value={pdfFile}
          onChange={setPdfFile}
          clearable
          data-testid="pdf-file"
        />
        <Button
          onClick={() => void submitPdf()}
          disabled={!canSubmitPdf}
          loading={pdfSubmitting}
          data-testid="pdf-submit"
        >
          {tr.msg(IMPORT_PDF_BUTTON_LABEL)}
        </Button>

        <Divider label={tr.msg(IMPORT_ZIP_TITLE)} labelPosition="left" />
        <FileInput
          label={tr.msg(IMPORT_ZIP_LABEL)}
          description={tr.msg(IMPORT_ZIP_HINT)}
          accept=".zip,.sketch,.penpot,application/zip"
          value={zipFile}
          onChange={setZipFile}
          clearable
          data-testid="zip-file"
        />
        <Button
          onClick={() => void submitZip()}
          disabled={!canSubmitZip}
          loading={zipSubmitting}
          data-testid="zip-submit"
        >
          {tr.msg(IMPORT_ZIP_BUTTON_LABEL)}
        </Button>

        {zipError && (
          <Stack gap={2} data-testid="zip-error">
            <Text size="sm" c="red">
              {zipError.message}
            </Text>
            {zipError.nextSteps && (
              <Text size="sm" c="dimmed" data-testid="zip-error-next-steps">
                {zipError.nextSteps}
              </Text>
            )}
            {zipError.skipped && <SkippedFilesList skipped={zipError.skipped} />}
          </Stack>
        )}

        {error && (
          <Text size="sm" c="red" data-testid="import-error">
            {error}
          </Text>
        )}

        {outcome && (
          <Stack gap="sm" data-testid="import-result">
            {outcome.bundle && (
              <Stack gap={2} data-testid="zip-result">
                <Text size="sm" fw={600} data-testid="zip-vendor">
                  {tr.msg(zipVendorText(outcome.bundle.vendor))}
                </Text>
                <Group gap="lg" className={mobile.wrapRow}>
                  <Text size="sm" data-testid="zip-recognized">
                    {tr.msg(zipRecognizedText(outcome.bundle.recognized))}
                  </Text>
                  <Text size="sm" data-testid="zip-ignored">
                    {tr.msg(zipIgnoredText(outcome.bundle.ignored))}
                  </Text>
                </Group>
                <SkippedFilesList skipped={outcome.bundle.skipped} />
              </Stack>
            )}
            {(outcome.assetCount !== undefined || outcome.referenceCount !== undefined) && (
              <Group gap="lg" className={mobile.wrapRow}>
                {outcome.assetCount !== undefined && (
                  <Text size="sm" data-testid="import-assets-count">
                    {tr.msg(importAssetsText(outcome.assetCount))}
                  </Text>
                )}
                {outcome.referenceCount !== undefined && (
                  <Text size="sm" data-testid="import-references-count">
                    {tr.msg(importReferencesText(outcome.referenceCount))}
                  </Text>
                )}
              </Group>
            )}
            {outcome.productMapCreated && (
              <Text size="sm" data-testid="import-product-map-link">
                {tr.msg(IMPORT_PRODUCT_MAP_LINK)}
              </Text>
            )}
            {importWarning(outcome.diagnostics) && (
              <Text size="sm" fw={600} c="red" data-testid="import-warning-title">
                {tr.msg(IMPORT_WARNING_TITLE)}
              </Text>
            )}
            <ImportReportPanel report={outcome.report} />
            <DiagnosticsList diagnostics={outcome.diagnostics} />
            {outcome.versions.map((version) => (
              <DraftReviewBanner
                key={version.id}
                projectId={projectId}
                summary={draftSummary(version, current)}
                kind={draftSummary(version, current).kind}
                canEdit={canEdit}
                warning={version.id === outcome.versions[0]?.id ? importWarning(outcome.diagnostics) : null}
                onChanged={() => onImported?.()}
              />
            ))}
            <Button variant="light" onClick={handleClose} data-testid="import-done">
              {tr.msg(IMPORT_DONE_LABEL)}
            </Button>
          </Stack>
        )}
      </Stack>
    </Modal>
  );
}
