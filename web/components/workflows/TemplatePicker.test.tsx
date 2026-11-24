// @vitest-environment jsdom
/**
 * Templates that need an approved brand and product profile (`requiresProfile`, set by
 * WorkflowsController for the `-ds` templates) are unavailable until the project has one. The
 * project's profile state is the REAL summary shape: `{ design, productMap }`, each either null or
 * a system row whose `currentVersion` is null until a version is approved.
 */
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import { describe, it, expect, vi, beforeAll, afterEach } from 'vitest';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/hooks/use-design-system', () => ({ useDesignSystem: vi.fn() }));
vi.mock('@/lib/hooks/use-workflow-apps', () => ({ useWorkflowApps: vi.fn(() => ({ data: undefined, error: undefined })) }));
vi.mock('next/link', () => ({
  default: ({ children, href }: { children: React.ReactNode; href: string }) => <a href={href}>{children}</a>,
}));

import { useDesignSystem } from '@/lib/hooks/use-design-system';
import { useWorkflowApps } from '@/lib/hooks/use-workflow-apps';
import type { DesignSystemSummary } from '@/lib/types/design-system';
import type { WorkflowAppCard, WorkflowTemplateSummary } from '@/lib/types/workflow';
import { TemplatePicker, pickableTemplates } from './TemplatePicker';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({ matches: false, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {} }),
  });
  global.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as never;
});
afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const TEMPLATES: WorkflowTemplateSummary[] = [
  { key: 'reel', name: '15-second vertical reel', description: 'A punchy reel.', version: 1, autoCreateOnProject: false, requiresUserInput: true, stepCount: 6, requiresProfile: false },
  { key: 'promo-ds', name: 'Promo video from your profile', description: 'Uses your profile.', version: 1, autoCreateOnProject: false, requiresUserInput: true, stepCount: 8, requiresProfile: true },
];

function system(kind: 'design' | 'product_map', approved: boolean): DesignSystemSummary {
  return {
    id: kind,
    kind,
    name: kind,
    currentVersion: approved ? { id: `${kind}-v1`, versionNumber: 1, digest: 'd', approvedAt: '2026-01-01T00:00:00Z' } : null,
    latestDraft: null,
    pendingProposals: 0,
    parentLibrary: null,
  };
}

function swr(data: unknown, isLoading = false, error: unknown = undefined): never {
  return { data, isLoading, error } as never;
}

function renderPicker(onSelect = vi.fn()) {
  render(
    <MantineProvider>
      <TemplatePicker
        projectId="p1"
        templates={TEMPLATES}
        selectedKey={null}
        onSelect={onSelect}
        onApply={vi.fn()}
      />
    </MantineProvider>,
  );
  return onSelect;
}

const radio = (name: RegExp) => screen.getByRole('radio', { name }) as HTMLButtonElement;

describe('TemplatePicker requiresProfile gating', () => {
  it('disables the profile template for a project with no profile, and explains with a link', () => {
    vi.mocked(useDesignSystem).mockReturnValue(swr({ design: null, productMap: null }));
    const onSelect = renderPicker();

    expect(radio(/Promo video from your profile/).disabled).toBe(true);
    fireEvent.click(radio(/Promo video from your profile/));
    expect(onSelect).not.toHaveBeenCalled();

    expect(screen.getByText(/needs an approved brand and product profile/i)).toBeTruthy();
    const link = screen.getByRole('link', { name: /set up your profile/i });
    expect(link.getAttribute('href')).toBe('/projects/p1?tab=profile');

    // The ordinary template is untouched.
    expect(radio(/15-second vertical reel/).disabled).toBe(false);
  });

  it('still disables it when only a draft exists (no approved version)', () => {
    vi.mocked(useDesignSystem).mockReturnValue(
      swr({ design: system('design', false), productMap: system('product_map', false) }),
    );
    renderPicker();
    expect(radio(/Promo video from your profile/).disabled).toBe(true);
  });

  it('still disables it when only the brand half is approved (the template requires both)', () => {
    vi.mocked(useDesignSystem).mockReturnValue(
      swr({ design: system('design', true), productMap: null }),
    );
    renderPicker();
    expect(radio(/Promo video from your profile/).disabled).toBe(true);
  });

  it('enables it once both profiles are approved', () => {
    vi.mocked(useDesignSystem).mockReturnValue(
      swr({ design: system('design', true), productMap: system('product_map', true) }),
    );
    const onSelect = renderPicker();
    expect(radio(/Promo video from your profile/).disabled).toBe(false);
    fireEvent.click(radio(/Promo video from your profile/));
    expect(onSelect).toHaveBeenCalledWith('promo-ds');
    expect(screen.queryByRole('link', { name: /set up your profile/i })).toBeNull();
  });

  it('does not gate while the profile is still loading or if it cannot be read', () => {
    vi.mocked(useDesignSystem).mockReturnValue(swr(undefined, true));
    renderPicker();
    expect(radio(/Promo video from your profile/).disabled).toBe(false);

    cleanup();
    vi.mocked(useDesignSystem).mockReturnValue(swr(undefined, false, new Error('x')));
    renderPicker();
    expect(radio(/Promo video from your profile/).disabled).toBe(false);
  });

  describe('library-inherited design (Q4-2)', () => {
    const linked = (approved: boolean): DesignSystemSummary => ({
      ...system('design', approved),
      parentLibrary: { id: 'lib-1', name: 'Org brand' },
    });

    it('enables it for a library-only design plus the project own approved product map', () => {
      vi.mocked(useDesignSystem).mockReturnValue(
        swr({ design: linked(false), productMap: system('product_map', true) }),
      );
      renderPicker();
      expect(radio(/Promo video from your profile/).disabled).toBe(false);
    });

    it('enables it for own design + own product map (unchanged)', () => {
      vi.mocked(useDesignSystem).mockReturnValue(
        swr({ design: linked(true), productMap: system('product_map', true) }),
      );
      renderPicker();
      expect(radio(/Promo video from your profile/).disabled).toBe(false);
    });

    it('still blocks a library design when the product map is not approved (libraries carry none)', () => {
      vi.mocked(useDesignSystem).mockReturnValue(
        swr({ design: linked(false), productMap: system('product_map', false) }),
      );
      renderPicker();
      expect(radio(/Promo video from your profile/).disabled).toBe(true);

      cleanup();
      vi.mocked(useDesignSystem).mockReturnValue(swr({ design: linked(false), productMap: null }));
      renderPicker();
      expect(radio(/Promo video from your profile/).disabled).toBe(true);
    });

    it('blocks it when there is neither an own nor a library design', () => {
      vi.mocked(useDesignSystem).mockReturnValue(
        swr({ design: system('design', false), productMap: system('product_map', true) }),
      );
      renderPicker();
      expect(radio(/Promo video from your profile/).disabled).toBe(true);
    });
  });
});

/**
 * The promo from code is the workflow a new project is given, unless the project was started from
 * clips: then it gets the footage edit and has no promo workflow. The picker leaves the promo out
 * only for a project that really has a workflow made from it (the `source` of the project's apps).
 */
describe('TemplatePicker and the template a new project starts with', () => {
  const PROMO: WorkflowTemplateSummary = {
    key: 'quick-win-promo', name: 'Promo from your code', description: 'A promo video of the app you built.',
    version: 1, autoCreateOnProject: true, requiresUserInput: false, stepCount: 11, requiresProfile: false,
  };
  const ALL = [PROMO, ...TEMPLATES];
  const app = (source: string): WorkflowAppCard => ({
    workflowId: `wf-${source}`, title: source, tagline: null, category: 'footage', costClass: 'Uses credits',
    inputsCount: 0, lastRun: null, source,
  });
  const gallery = (...sources: string[]) => swr({ apps: sources.map(app), recipes: [] });

  function renderAll() {
    vi.mocked(useDesignSystem).mockReturnValue(swr({ design: null, productMap: null }));
    render(
      <MantineProvider>
        <TemplatePicker projectId="p1" templates={ALL} selectedKey={null} onSelect={vi.fn()} onApply={vi.fn()} />
      </MantineProvider>,
    );
  }
  const offered = () => screen.getAllByRole('radio').map((el) => el.textContent ?? '');
  const offersPromo = () => offered().some((text) => text.includes('Promo from your code'));

  it('offers the promo to a project started from clips, which has no workflow made from it', () => {
    vi.mocked(useWorkflowApps).mockReturnValue(gallery('video-derush-edit'));
    renderAll();
    expect(vi.mocked(useWorkflowApps)).toHaveBeenCalledWith('p1');
    expect(offersPromo()).toBe(true);
    expect(offered()).toHaveLength(3);
  });

  it('offers it to a project with no workflow at all, or only ones built by hand', () => {
    vi.mocked(useWorkflowApps).mockReturnValue(gallery());
    renderAll();
    expect(offersPromo()).toBe(true);
    cleanup();
    vi.mocked(useWorkflowApps).mockReturnValue(gallery('custom', 'custom'));
    renderAll();
    expect(offersPromo()).toBe(true);
  });

  it('leaves it out for a project that has a workflow made from it', () => {
    vi.mocked(useWorkflowApps).mockReturnValue(gallery('quick-win-promo', 'video-derush-edit'));
    renderAll();
    expect(offersPromo()).toBe(false);
    expect(offered()).toHaveLength(2);
  });

  it('holds it back while the project is still being read, and offers it when that fails', () => {
    vi.mocked(useWorkflowApps).mockReturnValue(swr(undefined, true));
    renderAll();
    expect(offersPromo()).toBe(false);
    cleanup();
    vi.mocked(useWorkflowApps).mockReturnValue(swr(undefined, false, new Error('offline')));
    renderAll();
    expect(offersPromo()).toBe(true);
  });

  it('pickableTemplates: an auto-created template is left out only when a workflow was made from it', () => {
    const keys = (madeFrom?: ReadonlySet<string>) => pickableTemplates(ALL, madeFrom).map((t) => t.key);
    expect(keys(new Set(['video-derush-edit']))).toEqual(['quick-win-promo', 'reel', 'promo-ds']);
    expect(keys(new Set())).toEqual(['quick-win-promo', 'reel', 'promo-ds']);
    expect(keys(new Set(['quick-win-promo']))).toEqual(['reel', 'promo-ds']);
    // Not known yet: held back, never guessed.
    expect(keys(undefined)).toEqual(['reel', 'promo-ds']);
    // Templates that are not auto-created are offered whatever the project already has.
    expect(keys(new Set(['reel', 'quick-win-promo']))).toEqual(['reel', 'promo-ds']);
    expect(pickableTemplates(undefined, new Set())).toEqual([]);
  });
});
