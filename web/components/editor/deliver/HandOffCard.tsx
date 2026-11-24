'use client';
import React, { useState } from 'react';
import useSWR from 'swr';
import { Alert, Button, Collapse, Group, List, Radio, Select, Stack, Switch, Text, UnstyledButton } from '@mantine/core';
import { IconChevronDown, IconChevronRight, IconDownload } from '@tabler/icons-react';
import { ApiError } from '@/lib/api/client';
import { editTimelineExportScopeKey, editTimelineHandOffUrl, getExportScope, type ExportScope } from '@/lib/editing/sync/api';
import {
  EDITOR_APPS,
  HAND_OFF_FORMAT_LABELS,
  HAND_OFF_MEDIA_OPTIONS,
  HOW_TO_OPEN,
  defaultFormatForApp,
  formatNeedsTrack,
  formatsForApp,
  handOffMediaState,
  initialMediaChoice,
  type EditorApp,
  type HandOffFormat,
  type HandOffMediaChoice,
} from '@/lib/editing/interchange/handoff-ui';
import type { HandOffSource } from '@/lib/editing/interchange/hand-off';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { EarlyPreviewLabel } from '@/components/editor/shell/EarlyPreviewLabel';
import { useDeviceClass } from '@/lib/editing/use-device-class';
import { msg, useT, type Msg } from '@/i18n';
import { handOffFailureMsg } from '@/lib/editing/interchange/labels-msg';


export interface HandOffCardProps {
  projectId: string;
  timelineId: string;
  doc: HandOffSource | null;
  dirty: boolean;
  /** False when a panel header above the card already names it (the Deliver page); the card then draws no title of its own. */
  heading?: boolean;
}

/** Asks the server once for the response headers, so a refusal can be shown in words; the body is never read. */
async function preflight(url: string): Promise<Msg | null> {
  const controller = new AbortController();
  try {
    const res = await fetch(url, { credentials: 'same-origin', signal: controller.signal });
    if (res.ok) return null;
    let message: string | null = null;
    let code: string | null = null;
    try {
      const body = (await res.json()) as { message?: unknown; code?: unknown };
      message = typeof body.message === 'string' ? body.message : null;
      code = typeof body.code === 'string' ? body.code : null;
    } catch {
      /* not JSON: the status alone speaks */
    }
    return handOffFailureMsg(res.status, code, message);
  } catch (e) {
    return e instanceof ApiError ? handOffFailureMsg(e.status, null, null) : msg('editor.deliver.handOffCard.packageFailed');
  } finally {
    controller.abort();
  }
}

/**
 * R12.2: "For another editor". One obvious action: pick the app, pick whether the video files travel
 * with the cut, see what the package holds and how big it is, download. The download is a plain link
 * the browser streams to disk; before it starts we ask once for the headers so a refusal reads as a sentence.
 */
export function HandOffCard({ projectId, timelineId, doc, dirty, heading = true }: HandOffCardProps) {
  const tx = useT('editor.deliver.handOffCard');
  const phone = useDeviceClass() === 'phone';
  const [app, setApp] = useState<EditorApp>('premiere');
  const [format, setFormat] = useState<HandOffFormat>(defaultFormatForApp('premiere'));
  const [media, setMedia] = useState<HandOffMediaChoice | null>(null);
  const [track, setTrack] = useState<string | null>(null);
  const [includeAudio, setIncludeAudio] = useState(true);
  const [roles, setRoles] = useState(true);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [showHow, setShowHow] = useState(false);
  const { data: scope, error: scopeError } = useSWR<ExportScope>(phone ? null : editTimelineExportScopeKey(projectId, timelineId, format), () => getExportScope(projectId, timelineId, format));

  if (phone) {
    return (
      <Stack gap="xs">
        <EarlyPreviewLabel />
        <Text size="xs" c="dimmed" data-testid="handoff-early-line">{tx('earlyLine')}</Text>
        <Text size="xs" c="dimmed" data-testid="handoff-phone-note">
          {tx('handingTheCutTo')}
        </Text>
      </Stack>
    );
  }

  const state = handOffMediaState(scope?.handOff);
  const chosenMedia: HandOffMediaChoice = media ?? initialMediaChoice(state);
  const overCap = state.kind === 'overCap';
  const mediaValue: HandOffMediaChoice = overCap ? 'manifest' : chosenMedia;
  const videoTracks = (doc?.tracks ?? []).filter((t) => t.kind === 'video' && typeof t.id === 'string');
  const url = editTimelineHandOffUrl(projectId, timelineId, format, { media: mediaValue, track: formatNeedsTrack(format) ? track : null, includeAudio, roles });

  const chooseApp = (next: EditorApp) => {
    setApp(next);
    setFormat(defaultFormatForApp(next));
    setProblem(null);
  };

  const download = async (e: React.MouseEvent<HTMLAnchorElement>) => {
    e.preventDefault();
    if (busy) return;
    setBusy(true);
    setProblem(null);
    const refused = await preflight(url);
    setBusy(false);
    if (refused) {
      setProblem(tx.msg(refused));
      return;
    }
    window.location.assign(url);
  };

  const formats = formatsForApp(app);

  return (
    <Stack gap="xs" data-testid="handoff-card">
      {heading && <Text size="sm" fw={600}>{tx('forAnotherEditor')}</Text>}
      <EarlyPreviewLabel />
      <Text size="xs" c="dimmed" data-testid="handoff-early-line">{tx('earlyLine')}</Text>
      <Text size="xs" c="dimmed">
        {tx('aPackageWithThe')} {dirty ? tx('itContainsWhatIs') : ''}
      </Text>
      <Select
        size="xs"
        label={tx('whichEditor')}
        data={EDITOR_APPS.map((a) => ({ value: a.value, label: a.label }))}
        value={app}
        onChange={(v) => v && chooseApp(v as EditorApp)}
        allowDeselect={false}
        w={260}
        comboboxProps={{ withinPortal: true }}
        data-testid="handoff-app"
      />
      {formats.length > 1 && (
        <Select
          size="xs"
          label={tx('fileType')}
          data={formats.map((f) => ({ value: f, label: HAND_OFF_FORMAT_LABELS[f] }))}
          value={format}
          onChange={(v) => v && setFormat(v as HandOffFormat)}
          allowDeselect={false}
          w={260}
          comboboxProps={{ withinPortal: true }}
          data-testid="handoff-format"
        />
      )}
      {formatNeedsTrack(format) && videoTracks.length > 1 && (
        <Select
          size="xs"
          label={tx('videoTrack')}
          description={tx('aCutListHolds')}
          data={videoTracks.map((t, i) => ({ value: t.id as string, label: t.name || `Video ${i + 1}` }))}
          value={track ?? (videoTracks[0].id as string)}
          onChange={(v) => setTrack(v)}
          allowDeselect={false}
          w={260}
          comboboxProps={{ withinPortal: true }}
          data-testid="handoff-track"
        />
      )}
      {format === 'fcpxml' && (
        <Group gap="md">
          <Switch size="xs" label={tx('includeAudio')} checked={includeAudio} onChange={(e) => setIncludeAudio(e.currentTarget.checked)} data-testid="handoff-audio" />
          <Switch size="xs" label={tx('nameAudioRoles')} checked={roles} onChange={(e) => setRoles(e.currentTarget.checked)} data-testid="handoff-roles" />
        </Group>
      )}
      <Radio.Group value={mediaValue} onChange={(v) => setMedia(v as HandOffMediaChoice)} label={tx('videoFiles')} data-testid="handoff-media">
        <Stack gap={6} mt={4}>
          {HAND_OFF_MEDIA_OPTIONS.map((o) => (
            <Radio key={o.value} size="xs" value={o.value} label={o.label} disabled={o.value === 'zip' && overCap} data-testid={`handoff-media-${o.value}`} />
          ))}
        </Stack>
      </Radio.Group>
      {scopeError && (
        <Alert color="orange" variant="light" data-testid="handoff-scope-error">
          {humanizeErrorT(tx, scopeError, tx('theSizeOfThe'))}
        </Alert>
      )}
      {state.kind === 'ok' || state.kind === 'overCap' ? (
        <Text size="xs" data-testid="handoff-size">
          <b>{tx('thePackageHolds')}</b> {state.line}
        </Text>
      ) : null}
      {overCap && (
        <Text size="xs" c="orange" data-testid="handoff-over-cap">
          {state.reason}
        </Text>
      )}
      {scope && (
        <Group align="flex-start" gap="lg" data-testid="handoff-scope">
          <Stack gap={2} maw={320}>
            <Text size="xs" fw={600}>{tx('itCarries')}</Text>
            <List size="xs" spacing={2}>{scope.carries.map((c) => <List.Item key={c}>{c}</List.Item>)}</List>
          </Stack>
          <Stack gap={2} maw={320}>
            <Text size="xs" fw={600}>{tx('itLeavesOut')}</Text>
            {scope.dropped.length === 0 ? (
              <Text size="xs" c="dimmed">{tx('nothingFromTheTimeline')}</Text>
            ) : (
              <List size="xs" spacing={2}>{scope.dropped.map((d) => <List.Item key={d}>{d}</List.Item>)}</List>
            )}
          </Stack>
        </Group>
      )}
      {problem && (
        <Alert color="red" variant="light" role="alert" data-testid="handoff-problem">
          {problem}
        </Alert>
      )}
      <Group>
        <Button component="a" href={url} download onClick={(e: React.MouseEvent<HTMLAnchorElement>) => void download(e)} loading={busy} mih={44} leftSection={<IconDownload size={16} />} data-testid="handoff-download" aria-busy={busy}>
          {busy ? tx('preparingThePackage') : tx('downloadThePackage')}
        </Button>
      </Group>
      <UnstyledButton onClick={() => setShowHow((v) => !v)} aria-expanded={showHow} mih={44} data-testid="handoff-how-toggle">
        <Group gap={4}>
          {showHow ? <IconChevronDown size={14} /> : <IconChevronRight size={14} />}
          <Text size="xs" fw={600}>{tx('howToOpenIt')}</Text>
        </Group>
      </UnstyledButton>
      <Collapse expanded={showHow}>
        <List size="xs" spacing={4} data-testid="handoff-how">
          {HOW_TO_OPEN[app].map((line) => <List.Item key={line}>{line}</List.Item>)}
        </List>
      </Collapse>
    </Stack>
  );
}
