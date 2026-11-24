'use client';

import React from 'react';
import { Button, Menu, Text } from '@mantine/core';
import { IconCheck, IconChevronDown } from '@tabler/icons-react';
import { cameraProfileGroups } from '@/lib/editing/color/camera-profile-ops';
import type { ViewingLutChoice } from '@/lib/editing/prefs/viewing-lut';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useT } from '@/i18n';
import { lutReasonMsg } from '@/lib/editing/color/lut-reason';
import type { ViewingLutProblem } from './useViewingLutSpec';

export interface ViewingLutMenuProps {
  projectId: string;
  choice: ViewingLutChoice;
  onChoose: (next: ViewingLutChoice) => void;
  problem: ViewingLutProblem | null;
}

/** The viewer toolbar's "Viewing LUT" menu: Off, each project `.cube`, and a camera profile used as a view. Desktop only. */
export function ViewingLutMenu({ projectId, choice, onChoose, problem }: ViewingLutMenuProps) {
  const tx = useT('editor.viewingLut');
  const { data: files } = useProjectFiles(projectId);
  const lutFiles = (files ?? []).filter((f) => /\.cube$/i.test(f.originalFileName));
  const tr = useT('editor-color.lutReason');
  const problemText = (reason: ViewingLutProblem): string => `${tr.msg(lutReasonMsg(typeof reason === 'string' ? { code: reason } : reason))} ${tx('problemOff')}`;
  const isFile = (id: string) => choice?.kind === 'file' && choice.fileId === id;
  const isProfile = (id: string) => choice?.kind === 'profile' && choice.id === id;
  const tick = (on: boolean) => (on ? <IconCheck size={14} aria-hidden /> : undefined);
  return (
    <>
      <Menu withinPortal position="bottom-start" shadow="md">
        <Menu.Target>
          <Button size="xs" fz="xs" variant={choice ? 'light' : 'default'} rightSection={<IconChevronDown size={12} aria-hidden />} data-testid="viewing-lut-button">
            {tx('label')}
          </Button>
        </Menu.Target>
        <Menu.Dropdown data-testid="viewing-lut-menu">
          <Menu.Item leftSection={tick(choice === null)} onClick={() => onChoose(null)} data-testid="viewing-lut-off">
            {tx('off')}
          </Menu.Item>
          {lutFiles.length > 0 && <Menu.Label>{tx('lutFiles')}</Menu.Label>}
          {lutFiles.map((f) => (
            <Menu.Item key={f.id} leftSection={tick(isFile(f.id))} onClick={() => onChoose({ kind: 'file', fileId: f.id })} data-testid={`viewing-lut-file-${f.id}`}>
              {f.originalFileName}
            </Menu.Item>
          ))}
          <Menu.Label>{tx('cameraProfileAsView')}</Menu.Label>
          {cameraProfileGroups().flatMap((g) =>
            g.items.map((p) => (
              <Menu.Item key={p.id} leftSection={tick(isProfile(p.id))} onClick={() => onChoose({ kind: 'profile', id: p.id })} data-testid={`viewing-lut-profile-${p.id}`}>
                {p.displayName}
              </Menu.Item>
            )),
          )}
        </Menu.Dropdown>
      </Menu>
      {problem && (
        <Text size="xs" c="red" role="alert" data-testid="viewing-lut-problem">
          {problemText(problem)}
        </Text>
      )}
    </>
  );
}

/** The permanent badge on the picture while a viewing LUT is on: it is never in the export. */
export function ViewingLutBadge() {
  const tx = useT('editor.viewingLut');
  return (
    <div
      role="status"
      data-testid="viewing-lut-badge"
      style={{ position: 'absolute', right: 6, bottom: 6, zIndex: 10, padding: '4px 8px', borderRadius: 6, background: 'rgba(15,17,21,0.9)', color: '#fff', fontSize: 12, lineHeight: 1.3, pointerEvents: 'none' }}
    >
      {tx('badge')}
    </div>
  );
}
