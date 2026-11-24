'use client';

import React, { useState } from 'react';
import useSWR from 'swr';
import { Alert, Button, Group, List, Select, Stack, Switch, Text } from '@mantine/core';
import { IconDownload } from '@tabler/icons-react';
import { EXPORT_FORMATS, editTimelineExportScopeKey, editTimelineExportUrl, getExportScope, type EditTimelineExportFormat, type ExportScope } from '@/lib/editing/sync/api';
import { FORMAT_LABELS, FORMAT_SUMMARY, handOffCounts, type HandOffSource } from '@/lib/editing/interchange/hand-off';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { HandOffCard } from './HandOffCard';
import { useT } from '@/i18n';
import { mediaSentenceMsg } from '@/lib/editing/interchange/labels-msg';
import { handOffLinesMsg } from '@/lib/editing/interchange/labels-msg';

export interface ExportPanelProps {
  projectId: string;
  timelineId: string;
  doc: HandOffSource | null;
  /** SRT/WebVTT of a timeline without captions would be empty, so those two are not offered then. */
  captionCount: number | null;
  dirty: boolean;
  /**
   * `page` (the Deliver page, wave 5): the file type is chosen first, what it carries and leaves out is read next,
   * and its download comes after that account; the hand-off package is its own section beside this one.
   * `compact` (the phone sheet): the five downloads first, as before, with the hand-off note under them.
   */
  layout?: 'compact' | 'page';
}

/**
 * R12.1: "Export for other apps": one download per format, with an honest account of the chosen format: what the
 * file carries, what it leaves out, which media it points at, and a "Hand off" preview of what will be in it.
 * The lists come from the server, which writes the file.
 */
export function ExportPanel({ projectId, timelineId, doc, captionCount, dirty, layout = 'compact' }: ExportPanelProps) {
  const t = useT('editor.deliver.exportPanel');
  const [format, setFormat] = useState<EditTimelineExportFormat>('otio');
  const [includeAudio, setIncludeAudio] = useState(true);
  const [roles, setRoles] = useState(true);
  const { data: scope, error, isLoading } = useSWR<ExportScope>(editTimelineExportScopeKey(projectId, timelineId, format), () => getExportScope(projectId, timelineId, format));
  const lines = doc ? handOffLinesMsg(format, handOffCounts(doc), { includeAudio }).map((m) => t.msg(m)) : [];
  const options = { includeAudio, roles };
  const page = layout === 'page';

  /** One format's download: a plain link the browser saves, or a sentence when the file would be empty. */
  const download = (f: EditTimelineExportFormat, primary: boolean) => {
    const label = FORMAT_LABELS[f];
    if ((f === 'srt' || f === 'vtt') && captionCount === 0) {
      return (
        <Text key={f} size="xs" c="dimmed" aria-disabled="true" title={t('thisTimelineHasNo')} data-testid={`deliver-export-${f}`} data-disabled="true">
          {t('noCaptionsYet', { label })}
        </Text>
      );
    }
    return (
      <Button
        key={f}
        component="a"
        size="xs"
        fz="xs"
        variant={primary ? 'filled' : 'light'}
        mih={44}
        href={editTimelineExportUrl(projectId, timelineId, f, options)}
        download
        leftSection={primary ? <IconDownload size={14} /> : undefined}
        data-testid={`deliver-export-${f}`}
        data-primary={primary ? 'true' : undefined}
      >
        {primary ? t('downloadFormat', { label }) : label}
      </Button>
    );
  };

  const formatSelect = (
    <Select
      size="xs"
      label={page ? t('fileType') : t('whatIsInThe')}
      data={EXPORT_FORMATS.map((f) => ({ value: f, label: FORMAT_LABELS[f] }))}
      value={format}
      onChange={(v) => v && setFormat(v as EditTimelineExportFormat)}
      allowDeselect={false}
      w={260}
      maw="100%"
      comboboxProps={{ withinPortal: true }}
      data-testid="export-scope-format"
    />
  );

  const account = (
    <>
      <Text size="xs" data-testid="export-scope-summary">
        {FORMAT_SUMMARY[format]}
      </Text>
      {page && isLoading && !scope && (
        <Text size="xs" c="dimmed" role="status" data-testid="export-scope-loading">
          {t('loadingScope')}
        </Text>
      )}
      {error && (
        <Alert color="orange" variant="light" data-testid="export-scope-error">
          {humanizeErrorT(t, error, t('theDetailsOfThis'))}
        </Alert>
      )}
      {scope && (
        <Group align="flex-start" gap="lg" data-testid="export-scope">
          <Stack gap={2} maw={320}>
            <Text size="xs" fw={600}>{t('itCarries')}</Text>
            <List size="xs" spacing={2} data-testid="export-scope-carries">
              {scope.carries.map((c) => <List.Item key={c}>{c}</List.Item>)}
            </List>
          </Stack>
          <Stack gap={2} maw={320}>
            <Text size="xs" fw={600}>{t('itLeavesOut')}</Text>
            {scope.dropped.length === 0 ? (
              <Text size="xs" c="dimmed">{t('nothingFromTheTimeline')}</Text>
            ) : (
              <List size="xs" spacing={2} data-testid="export-scope-dropped">
                {scope.dropped.map((d) => <List.Item key={d}>{d}</List.Item>)}
              </List>
            )}
          </Stack>
        </Group>
      )}
      {scope && (
        <Text size="xs" c="dimmed" data-testid="export-scope-media">
          {t.msg(mediaSentenceMsg(scope.mediaMode))}
        </Text>
      )}
      {format === 'fcpxml' && (
        <Group gap="md">
          <Switch size="xs" label={t('includeAudio')} checked={includeAudio} onChange={(e) => setIncludeAudio(e.currentTarget.checked)} data-testid="export-fcpxml-audio" />
          <Switch size="xs" label={t('nameAudioRoles')} checked={roles} onChange={(e) => setRoles(e.currentTarget.checked)} data-testid="export-fcpxml-roles" />
        </Group>
      )}
      <Text size="xs" data-testid="export-handoff">
        <b>{t('handOff')}</b> {lines.length > 0 ? lines.join(', ') : t('nothingYetThisTimeline')}
      </Text>
    </>
  );

  const savedNote = dirty ? (
    <Text size="xs" c="dimmed">
      {t('theseFilesContainWhat')}
    </Text>
  ) : null;

  if (page) {
    return (
      <Stack gap="xs" data-testid="export-panel">
        {formatSelect}
        {account}
        <Group gap="xs" data-testid="deliver-export-links" align="center">
          {download(format, true)}
        </Group>
        {savedNote}
        <Group gap="xs" align="center" data-testid="deliver-export-others">
          <Text size="xs" c="dimmed">
            {t('otherFileTypes')}
          </Text>
          {EXPORT_FORMATS.filter((f) => f !== format).map((f) => download(f, false))}
        </Group>
        <Text size="xs" c="dimmed">
          {t('chooseToSeeScope')}
        </Text>
      </Stack>
    );
  }

  return (
    <Stack gap="xs" data-testid="export-panel">
      <Group gap="xs" data-testid="deliver-export-links" align="center">
        <Text size="xs" c="dimmed">
          {t('exportForOtherApps')}
        </Text>
        {EXPORT_FORMATS.map((f) => download(f, false))}
      </Group>
      {formatSelect}
      {account}
      <HandOffCard projectId={projectId} timelineId={timelineId} doc={doc} dirty={dirty} />
      {savedNote}
    </Stack>
  );
}
