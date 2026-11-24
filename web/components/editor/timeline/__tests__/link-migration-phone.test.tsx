// @vitest-environment jsdom
/** The sound-tracks offer on a phone: one short row that still applies and dismisses; the desktop keeps its full sentence. */
import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, screen } from '@testing-library/react';
import { renderWithI18n } from '@/test/i18n';
import { setTestViewport, resetTestViewport } from '@/lib/editing/__tests__/viewport';
import type { EditTimeline } from '@/lib/editing/types';
import { LinkMigrationBanner } from '../LinkMigrationBanner';

const ops = [{ type: 'x' }];
vi.mock('@/lib/editing/link', () => ({ migrateToLinkedAudio: () => ({ ops, linked: 1, added: 2 }) }));
vi.mock('@mantine/notifications', () => ({ notifications: { show: vi.fn() } }));

afterEach(() => {
  cleanup();
  resetTestViewport();
  window.localStorage.clear();
});

const show = (locale: string, onApply = vi.fn()) => {
  renderWithI18n(<LinkMigrationBanner timelineId="t1" doc={{} as EditTimeline} pool={[]} readOnly={false} onApply={onApply} />, { locale });
  return onApply;
};

describe('LinkMigrationBanner', () => {
  it('on a phone it says the count in one short sentence, in each language', () => {
    setTestViewport('phone');
    show('en');
    expect(screen.getByTestId('link-migration-banner').textContent).toContain('Sound tracks are ready to add for 3 clips.');
    cleanup();
    show('it');
    expect(screen.getByTestId('link-migration-banner').textContent).toContain('Tracce audio pronte da aggiungere per 3 clip.');
  });

  it('on a phone Apply applies the plan and keeps its full name for assistive tech; Not now hides it for good', () => {
    setTestViewport('phone');
    const onApply = show('en');
    const apply = screen.getByTestId('link-migration-apply');
    expect(apply.getAttribute('aria-label')).toBe('Add sound tracks to my clips');
    expect(apply.getAttribute('aria-label')).toContain(apply.textContent);
    fireEvent.click(apply);
    expect(onApply).toHaveBeenCalledWith(ops, 'Add sound tracks to my clips');
    fireEvent.click(screen.getByTestId('link-migration-dismiss'));
    expect(screen.queryByTestId('link-migration-banner')).toBeNull();
    expect(window.localStorage.getItem('reelforge.editor.linkMigrationDismissed.t1')).toBe('1');
  });

  it('on a desktop it keeps the full sentence', () => {
    setTestViewport('desktop');
    show('en');
    expect(screen.getByTestId('link-migration-banner').textContent).toContain('Some clips in this timeline have sound');
  });
});
