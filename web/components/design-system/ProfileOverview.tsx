'use client';

import { useState } from 'react';
import { Badge, Group, SimpleGrid, Stack, Text } from '@mantine/core';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import {
  listTokens,
  readColorHex,
  readDesignAssets,
  readFontFiles,
  readFontFamily,
  readNumber,
  readProductMap,
  readStringList,
  readText,
  tokenConf,
  type DesignAsset,
  type DesignAssetRole,
  type DesignDocument,
  type DesignSystemSummary,
} from '@/lib/types/design-system';
import { ColorSwatch } from './ColorSwatch';
import { PanelError } from './PanelNotes';
import { DraftReviewBanner } from './DraftReviewBanner';
import { ProfileExportMenu } from './ProfileExportMenu';
import { TypeSpecimen } from './TypeSpecimen';
import {
  componentsCount,
  OVERVIEW_ASSET_ICON_LABEL,
  OVERVIEW_ASSET_LOGO_LABEL,
  OVERVIEW_ASSET_MISSING,
  OVERVIEW_ASSET_PREVIEW_LABEL,
  OVERVIEW_ASSETS_EMPTY,
  OVERVIEW_ASSETS_TITLE,
  OVERVIEW_COLORS_EMPTY,
  OVERVIEW_COLORS_TITLE,
  OVERVIEW_DESIGN_EMPTY,
  OVERVIEW_DESIGN_TITLE,
  OVERVIEW_FONTS_TITLE,
  OVERVIEW_FRAMEWORK_LABEL,
  OVERVIEW_PREVIEW_CARDS_EMPTY,
  OVERVIEW_PREVIEW_CARDS_TITLE,
  OVERVIEW_PRODUCT_EMPTY,
  OVERVIEW_PRODUCT_TITLE,
  OVERVIEW_TOP_COMPONENTS_LABEL,
  OVERVIEW_TYPE_TITLE,
  OVERVIEW_VOICE_DO_LABEL,
  OVERVIEW_VOICE_DONT_LABEL,
  OVERVIEW_VOICE_SUMMARY_LABEL,
  OVERVIEW_VOICE_TITLE,
  OVERVIEW_VOICE_TONE_LABEL,
  previewCardsCount,
  routesCount,
} from './copy';
import mobile from './mobile.module.css';
import { useT, type T } from '@/i18n';

function leafName(path: string): string {
  return path.split('.').pop() ?? path;
}

/** The `family` segment of a `typography.family.<role>` path swapped for `weight`. */
function weightPathFor(path: string): string {
  return path.split('.').map((segment) => (segment === 'family' ? 'weight' : segment)).join('.');
}

function listBlock(label: string, values: string[]) {
  if (values.length === 0) return null;
  return (
    <Group gap="xs" align="flex-start" wrap="nowrap">
      <Text size="xs" fw={600} c="dimmed" w={64}>{label}</Text>
      <Text size="sm">{values.join(', ')}</Text>
    </Group>
  );
}

const PROJECT_FILE_PREFIX = 'project-file:';
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const FONT_FILE_PATTERN = /\.(woff2?|ttf|otf|ttc)$/i;

/**
 * The same-origin hardened route that serves one stored brand asset, or null when the reference
 * does not name a project file. No URL from an imported document is ever fetched.
 */
function assetUrl(projectId: string, fileRef: string): string | null {
  if (!fileRef.startsWith(PROJECT_FILE_PREFIX)) return null;
  const fileId = fileRef.slice(PROJECT_FILE_PREFIX.length);
  if (!GUID_PATTERN.test(fileId)) return null;
  return `/api/v1/projects/${encodeURIComponent(projectId)}/design-system/assets/${fileId}`;
}

function isFontFileName(name: string): boolean {
  return FONT_FILE_PATTERN.test(name);
}

function assetRoleLabel(role: DesignAssetRole, tr: T): string {
  switch (role) {
    case 'logo':
      return tr.msg(OVERVIEW_ASSET_LOGO_LABEL);
    case 'icon':
      return tr.msg(OVERVIEW_ASSET_ICON_LABEL);
    case 'preview':
      return tr.msg(OVERVIEW_ASSET_PREVIEW_LABEL);
  }
}

/** The readable part of a `fonts.files` value: the stored file name, or its project-file id. */
function fontFileLabel(fileRef: string): string {
  return fileRef.startsWith(PROJECT_FILE_PREFIX) ? fileRef.slice(PROJECT_FILE_PREFIX.length) : fileRef;
}

/**
 * One stored brand asset: its mark (SVG or raster) through the hardened route, or its name as plain
 * text when the file is missing, foreign or a font — never a broken-image icon.
 */
function AssetMark({
  projectId,
  role,
  asset,
}: {
  projectId: string;
  role: DesignAssetRole;
  asset: DesignAsset;
}) {
  const tr = useT();
  const [failed, setFailed] = useState(false);
  const name = asset.name;
  const url = assetUrl(projectId, asset.fileRef);
  const showImage = !failed && url !== null && typeof name === 'string' && !isFontFileName(name);

  return (
    <Group gap="xs" align="center" wrap="nowrap">
      <Text size="xs" fw={600} c="dimmed" w={64}>{assetRoleLabel(role, tr)}</Text>
      {showImage ? (
        <>
          {/* eslint-disable-next-line @next/next/no-img-element -- an authenticated same-origin route, not an optimizable static asset */}
          <img
            src={url}
            alt={name}
            loading="lazy"
            onError={() => setFailed(true)}
            style={{ height: 32, maxWidth: 140, objectFit: 'contain' }}
          />
          <Text size="sm">{name}</Text>
        </>
      ) : (
        <Text size="sm">{name ?? tr.msg(OVERVIEW_ASSET_MISSING)}</Text>
      )}
    </Group>
  );
}

/**
 * The read-only Overview: the current design profile's colours, type and voice, then the product
 * profile's framework, size and core components. A draft is deliberately NOT shown here — the
 * review banner is P2-22.
 */
export function ProfileOverview({
  projectId,
  design,
  productMap,
  designDocument,
  productDocument,
  canEdit,
  onChanged,
  designDocumentFailed = false,
  onRetryDesignDocument,
}: {
  projectId: string;
  design: DesignSystemSummary | null;
  productMap: DesignSystemSummary | null;
  designDocument: DesignDocument | null;
  productDocument: DesignDocument | null;
  canEdit: boolean;
  onChanged: () => void;
  /** The current design version's document could not be fetched (not the same as "no profile"). */
  designDocumentFailed?: boolean;
  onRetryDesignDocument?: () => void;
}) {
  const tr = useT();
  const colors = designDocument
    ? listTokens(designDocument)
        .filter(({ token }) => token.$type === 'color')
        .map(({ path }) => ({ path, hex: readColorHex(designDocument, path), conf: tokenConf(designDocument, path) }))
        .filter((color): color is { path: string; hex: string; conf: number | null } => color.hex !== null)
    : [];

  const specimens = designDocument
    ? listTokens(designDocument)
        .filter(({ token }) => token.$type === 'fontFamily')
        .map(({ path }) => ({
          path,
          family: readFontFamily(designDocument, path),
          weight: readNumber(designDocument, weightPathFor(path)),
        }))
        .filter((specimen): specimen is { path: string; family: string; weight: number | null } => specimen.family !== null)
    : [];

  const voiceSummary = designDocument ? readText(designDocument, 'voice.summary') : null;
  const voiceTone = designDocument ? readStringList(designDocument, 'voice.tone') : [];
  const voiceDo = designDocument ? readStringList(designDocument, 'voice.do') : [];
  const voiceDont = designDocument ? readStringList(designDocument, 'voice.dont') : [];
  const hasVoice = Boolean(voiceSummary) || voiceTone.length > 0 || voiceDo.length > 0 || voiceDont.length > 0;

  const assets = readDesignAssets(designDocument);
  const fontFiles = readFontFiles(designDocument);
  const assetEntries = (['logo', 'icon', 'preview'] as const).flatMap((role) => {
    const asset = assets[role];
    return asset ? [{ role, asset }] : [];
  });
  const fontFamilies = Object.keys(fontFiles).sort((a, b) => a.localeCompare(b));

  const product = readProductMap(productDocument);
  const frameworkBits = product
    ? [product.framework?.name, product.framework?.language, ...(product.framework?.styling ?? [])].filter(
        (bit): bit is string => Boolean(bit),
      )
    : [];
  const routeTotal = product?.routes?.length ?? 0;
  const componentTotal = product?.components?.length ?? 0;
  const topComponents = product
    ? [...(product.components ?? [])].sort((a, b) => Number(b.core) - Number(a.core)).slice(0, 3)
    : [];
  const previewCards = product?.previewCards ?? [];

  const hasDesign =
    Boolean(design?.currentVersion) &&
    colors.length +
      specimens.length +
      (hasVoice ? 1 : 0) +
      assetEntries.length +
      fontFamilies.length >
      0;

  return (
    <Stack gap="md">
      {(design?.currentVersion || productMap?.currentVersion) && (
        <Group justify="flex-end" gap="xs">
          {design?.currentVersion && <ProfileExportMenu projectId={projectId} kind="design" />}
          {productMap?.currentVersion && <ProfileExportMenu projectId={projectId} kind="product_map" />}
        </Group>
      )}
      {design && (
        <DraftReviewBanner
          projectId={projectId}
          summary={design}
          kind="design"
          canEdit={canEdit}
          onChanged={onChanged}
        />
      )}
      {productMap && (
        <DraftReviewBanner
          projectId={projectId}
          summary={productMap}
          kind="product_map"
          canEdit={canEdit}
          onChanged={onChanged}
        />
      )}

      <Panel title={tr.msg(OVERVIEW_DESIGN_TITLE)}>
        {designDocumentFailed && design?.currentVersion && !designDocument ? (
          <PanelError onRetry={() => onRetryDesignDocument?.()} />
        ) : !hasDesign ? (
          <QuietText>{tr.msg(OVERVIEW_DESIGN_EMPTY)}</QuietText>
        ) : (
          <Stack gap="md">
            {colors.length > 0 && (
              <Stack gap="xs">
                <Text size="sm" fw={650}>{tr.msg(OVERVIEW_COLORS_TITLE)}</Text>
                <SimpleGrid cols={{ base: 1, xs: 2, sm: 3 }} spacing="sm">
                  {colors.map((color) => (
                    <ColorSwatch key={color.path} name={leafName(color.path)} hex={color.hex} conf={color.conf} />
                  ))}
                </SimpleGrid>
              </Stack>
            )}
            {specimens.length > 0 && (
              <Stack gap="xs">
                <Text size="sm" fw={650}>{tr.msg(OVERVIEW_TYPE_TITLE)}</Text>
                {specimens.map((specimen) => (
                  <TypeSpecimen key={specimen.path} family={specimen.family} weight={specimen.weight} />
                ))}
              </Stack>
            )}
            {hasVoice ? (
              <Stack gap={4}>
                <Text size="sm" fw={650}>{tr.msg(OVERVIEW_VOICE_TITLE)}</Text>
                {voiceSummary && (
                  <Group gap="xs" align="flex-start" wrap="nowrap" className={mobile.wrapRow}>
                    <Text size="xs" fw={600} c="dimmed" w={64}>{tr.msg(OVERVIEW_VOICE_SUMMARY_LABEL)}</Text>
                    <Text size="sm">{voiceSummary}</Text>
                  </Group>
                )}
                {listBlock(tr.msg(OVERVIEW_VOICE_TONE_LABEL), voiceTone)}
                {listBlock(tr.msg(OVERVIEW_VOICE_DO_LABEL), voiceDo)}
                {listBlock(tr.msg(OVERVIEW_VOICE_DONT_LABEL), voiceDont)}
              </Stack>
            ) : null}
            <Stack gap="xs">
              <Text size="sm" fw={650}>{tr.msg(OVERVIEW_ASSETS_TITLE)}</Text>
              {assetEntries.length > 0 ? (
                assetEntries.map(({ role, asset }) => (
                  <AssetMark key={role} projectId={projectId} role={role} asset={asset} />
                ))
              ) : (
                <QuietText>{tr.msg(OVERVIEW_ASSETS_EMPTY)}</QuietText>
              )}
            </Stack>
            {fontFamilies.length > 0 && (
              <Stack gap={2}>
                <Text size="sm" fw={650}>{tr.msg(OVERVIEW_FONTS_TITLE)}</Text>
                {fontFamilies.map((family) => (
                  <Group key={family} gap="xs" align="baseline" wrap="nowrap" className={mobile.wrapRow}>
                    <Text size="sm" fw={600}>{family}</Text>
                    <Text size="xs" c="dimmed">{fontFileLabel(fontFiles[family])}</Text>
                  </Group>
                ))}
              </Stack>
            )}
            {colors.length === 0 && specimens.length === 0 && (
              <QuietText>{tr.msg(OVERVIEW_COLORS_EMPTY)}</QuietText>
            )}
          </Stack>
        )}
      </Panel>

      {productMap && (
        <Panel title={tr.msg(OVERVIEW_PRODUCT_TITLE)}>
          {!product ? (
            <QuietText>{tr.msg(OVERVIEW_PRODUCT_EMPTY)}</QuietText>
          ) : (
            <Stack gap="xs">
              {frameworkBits.length > 0 && (
                <Group gap="xs" align="center">
                  <Text size="xs" fw={600} c="dimmed">{tr.msg(OVERVIEW_FRAMEWORK_LABEL)}</Text>
                  {frameworkBits.map((bit) => (
                    <Badge key={bit} size="xs" variant="light" color="gray">{bit}</Badge>
                  ))}
                </Group>
              )}
              <Group gap="xs">
                <Badge size="sm" variant="light" color="blue">{tr.msg(routesCount(routeTotal))}</Badge>
                <Badge size="sm" variant="light" color="blue">{tr.msg(componentsCount(componentTotal))}</Badge>
              </Group>
              {topComponents.length > 0 && (
                <Stack gap={2}>
                  <Text size="xs" fw={600} c="dimmed">{tr.msg(OVERVIEW_TOP_COMPONENTS_LABEL)}</Text>
                  {topComponents.map((component) => (
                    <Text key={component.path} size="sm">
                      <Text span fw={600}>{component.name}</Text>{' — '}{component.responsibility}
                    </Text>
                  ))}
                </Stack>
              )}
              <Stack gap="xs">
                <Group gap="xs" align="center">
                  <Text size="xs" fw={600} c="dimmed">{tr.msg(OVERVIEW_PREVIEW_CARDS_TITLE)}</Text>
                  <Badge size="xs" variant="light" color="gray">{tr.msg(previewCardsCount(previewCards.length))}</Badge>
                </Group>
                {previewCards.length > 0 ? (
                  previewCards.map((card, index) => (
                    <Stack key={`${card.path ?? card.name}-${index}`} gap={2}>
                      <Group gap="xs" align="baseline">
                        {card.group && <Text size="xs" c="dimmed">{card.group}</Text>}
                        <Text size="sm" fw={600}>{card.name}</Text>
                        {card.subtitle && <Text size="xs" c="dimmed">{card.subtitle}</Text>}
                      </Group>
                      {card.text && <Text size="sm">{card.text}</Text>}
                    </Stack>
                  ))
                ) : (
                  <QuietText>{tr.msg(OVERVIEW_PREVIEW_CARDS_EMPTY)}</QuietText>
                )}
              </Stack>
            </Stack>
          )}
        </Panel>
      )}
    </Stack>
  );
}
