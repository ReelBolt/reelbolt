// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { avatarUsesByFile } from '@/lib/avatars/model';
import type { ProjectAvatar } from '@/lib/types/avatars';
import type { ProjectFile } from '@/lib/types/project';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/components/assistant/AssistantShellProvider', () => ({ useAssistantShell: () => ({ openWithPrompt: vi.fn() }) }));
const queue = { entries: [], enqueue: vi.fn(), retry: vi.fn(), clearFinished: vi.fn() };
vi.mock('@/lib/hooks/use-upload-queue', () => ({ useUploadQueue: () => queue }));
vi.mock('@/lib/hooks/use-file-drop', () => ({ useWindowFileDrop: () => false }));

import { FileManager } from './FileManager';

const file = (id: string, name: string): ProjectFile => ({
  id, projectId: 'p', originalFileName: name, category: 'userFiles', mimeType: 'image/png',
  sizeBytes: 1, storageKey: 'k', agentSummary: null, summaryStatus: 'Done', indexingStatus: 'NotIndexed', uploadedAt: '2026-10-01T00:00:00Z',
});

const avatar = (name: string, fileIds: string[], subjectKind?: 'Person' | 'Object') => ({
  id: name, name, subjectKind, references: fileIds.map((projectFileId) => ({ projectFileId })), consent: { documentFileId: null },
} as unknown as ProjectAvatar);

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: true, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('FileManager avatar badges', () => {
  it('badges a file a person uses "Avatar" and a file an object uses "Object", and leaves other files alone', () => {
    const uses = avatarUsesByFile([
      avatar('Anna', ['f1'], 'Person'),
      avatar('Red shoe', ['f2', 'f1'], 'Object'),
      // A server from before objects sends no kind: a person.
      avatar('Marco', ['f3']),
    ]);
    render(
      <MantineProvider>
        <FileManager projectId="p" files={[file('f1', 'both.png'), file('f2', 'shoe.png'), file('f3', 'marco.png'), file('f4', 'plain.png')]} onChanged={vi.fn()} avatarUsesByFile={uses} />
      </MantineProvider>,
    );
    const badges = (id: string) => [...document.querySelectorAll(`[data-file-id="${id}"] [data-testid="file-avatar-badge"]`)].map((b) => b.textContent);
    expect(badges('f1')).toEqual(['Avatar: Anna', 'Object: Red shoe']);
    expect(badges('f2')).toEqual(['Object: Red shoe']);
    expect(badges('f3')).toEqual(['Avatar: Marco']);
    expect(badges('f4')).toEqual([]);
    expect(screen.getAllByTestId('file-avatar-badge')).toHaveLength(4);
  });
});
