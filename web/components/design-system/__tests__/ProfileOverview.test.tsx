// @vitest-environment jsdom
/**
 * T-103: the profile overview surfaces the structured product-map preview cards, the stored brand
 * assets and the font files. Everything from an archive stays data: card text renders as text (a
 * `<script>` in it is escaped, never markup) and an asset is only ever fetched from the same-origin
 * hardened route, with a plain-text fallback when the file cannot be shown.
 */
import type { ComponentProps } from 'react';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

import type {
  DesignDocument,
  DesignSystemKind,
  DesignSystemSummary,
} from '@/lib/types/design-system';
import { ProfileOverview } from '../ProfileOverview';

const LOGO_ID = '11111111-2222-3333-4444-555555555555';
const ICON_ID = '66666666-7777-8888-9999-aaaaaaaaaaaa';
const PREVIEW_ID = 'bbbbbbbb-cccc-dddd-eeee-ffffffffffff';
const FONT_ID = '12121212-3434-5656-7878-909090909090';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: () => ({
      matches: false,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
    }),
  });
  global.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as never;
  Element.prototype.scrollIntoView = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

function summary(kind: DesignSystemKind, withCurrent = false): DesignSystemSummary {
  return {
    id: `${kind}-system`,
    kind,
    name: 'Profile',
    currentVersion: withCurrent
      ? { id: `${kind}-version`, versionNumber: 1, digest: 'digest', approvedAt: null }
      : null,
    latestDraft: null,
    pendingProposals: 0,
    parentLibrary: null,
  };
}

function productDocument(cards: unknown[]): DesignDocument {
  return {
    schema: 'reelbolt.product-map/1',
    name: 'Northwind Staffing',
    framework: { name: 'Next.js', language: 'TypeScript' },
    previewCards: cards,
  };
}

function renderOverview(overrides: Partial<ComponentProps<typeof ProfileOverview>> = {}) {
  return render(
    <MantineProvider>
      <ProfileOverview
        projectId="p1"
        design={null}
        productMap={null}
        designDocument={null}
        productDocument={null}
        canEdit
        onChanged={() => {}}
        {...overrides}
      />
    </MantineProvider>,
  );
}

describe('ProfileOverview preview cards', () => {
  it('lists each card group, name and subtitle with a count', () => {
    renderOverview({
      productMap: summary('product_map', true),
      productDocument: productDocument([
        { name: 'Core', group: 'Components', subtitle: 'Button', assets: [], runtimeRendered: true },
        { name: 'Dashboard', group: 'Pages', subtitle: 'Overview', assets: ['logo.svg'], runtimeRendered: false },
      ]),
    });

    expect(screen.getByText('Preview cards')).toBeTruthy();
    expect(screen.getByText('2 preview cards')).toBeTruthy();
    expect(screen.getByText('Core')).toBeTruthy();
    expect(screen.getByText('Components')).toBeTruthy();
    expect(screen.getByText('Button')).toBeTruthy();
    expect(screen.getByText('Dashboard')).toBeTruthy();
    expect(screen.getByText('Pages')).toBeTruthy();
    expect(screen.getByText('Overview')).toBeTruthy();
  });

  it('renders card text as text, never as markup', () => {
    const hostile = '<script>alert("card")</script>';
    const { container } = renderOverview({
      productMap: summary('product_map', true),
      productDocument: productDocument([
        { name: 'Hostile', text: hostile, assets: [], runtimeRendered: true },
      ]),
    });

    expect(screen.getByText(hostile)).toBeTruthy();
    expect(container.querySelector('script')).toBeNull();
    expect(container.querySelector('img')).toBeNull();
  });

  it('says so when the product profile has no preview cards', () => {
    renderOverview({
      productMap: summary('product_map', true),
      productDocument: productDocument([]),
    });

    expect(screen.getByText('No preview cards in the product profile.')).toBeTruthy();
  });
});

describe('ProfileOverview brand assets', () => {
  it('builds the hardened same-origin URL for a stored asset', () => {
    renderOverview({
      design: summary('design', true),
      designDocument: {
        assets: {
          logo: { fileRef: `project-file:${LOGO_ID}`, name: 'logo.svg' },
          icon: { fileRef: `project-file:${ICON_ID}`, name: 'icon.png' },
          preview: { fileRef: `project-file:${PREVIEW_ID}`, name: '.thumbnail' },
        },
      },
    });

    expect(screen.getByText('Logo, icon and preview')).toBeTruthy();
    expect(screen.getByAltText('logo.svg').getAttribute('src')).toBe(
      `/api/v1/projects/p1/design-system/assets/${LOGO_ID}`,
    );
    expect(screen.getByAltText('icon.png').getAttribute('src')).toBe(
      `/api/v1/projects/p1/design-system/assets/${ICON_ID}`,
    );
    expect(screen.getByAltText('.thumbnail').getAttribute('src')).toBe(
      `/api/v1/projects/p1/design-system/assets/${PREVIEW_ID}`,
    );
    expect(screen.getByText('Logo')).toBeTruthy();
    expect(screen.getByText('Icon')).toBeTruthy();
    expect(screen.getByText('Preview')).toBeTruthy();
  });

  it('falls back to text for an asset whose file cannot be served', () => {
    renderOverview({
      design: summary('design', true),
      designDocument: {
        assets: {
          logo: { fileRef: 'project-file:not-a-guid', name: 'logo.svg' },
          icon: { fileRef: 'project-file:not-a-guid' },
        },
      },
    });

    expect(screen.queryByAltText('logo.svg')).toBeNull();
    expect(screen.getAllByText('logo.svg').length).toBeGreaterThan(0);
    expect(screen.getByText('No file stored')).toBeTruthy();
  });

  it('drops the image and keeps the name when the route rejects the file', () => {
    renderOverview({
      design: summary('design', true),
      designDocument: {
        assets: { logo: { fileRef: `project-file:${LOGO_ID}`, name: 'logo.svg' } },
      },
    });

    fireEvent.error(screen.getByAltText('logo.svg'));

    expect(screen.queryByAltText('logo.svg')).toBeNull();
    expect(screen.getByText('logo.svg')).toBeTruthy();
  });

  it('shows a font by name, never as an image', () => {
    renderOverview({
      design: summary('design', true),
      designDocument: {
        assets: { logo: { fileRef: `project-file:${LOGO_ID}`, name: 'Inter-Regular.woff2' } },
      },
    });

    expect(screen.queryByAltText('Inter-Regular.woff2')).toBeNull();
    expect(screen.getByText('Inter-Regular.woff2')).toBeTruthy();
  });

  it('says so when the brand profile has no assets or fonts', () => {
    renderOverview({
      design: summary('design', true),
      designDocument: {
        color: { brand: { $type: 'color', $value: { hex: '#112233' } } },
      },
    });

    expect(screen.getByText('No logo, icon or preview files in the current profile.')).toBeTruthy();
  });
});

describe('ProfileOverview font files', () => {
  it('lists each family and the stored file it points at', () => {
    renderOverview({
      design: summary('design', true),
      designDocument: {
        fonts: { files: { Inter: `project-file:${FONT_ID}`, 'DM Sans': 'DM-Sans.woff2' } },
      },
    });

    expect(screen.getByText('Font files')).toBeTruthy();
    expect(screen.getByText('Inter')).toBeTruthy();
    expect(screen.getByText(FONT_ID)).toBeTruthy();
    expect(screen.getByText('DM Sans')).toBeTruthy();
    expect(screen.getByText('DM-Sans.woff2')).toBeTruthy();
  });
});
