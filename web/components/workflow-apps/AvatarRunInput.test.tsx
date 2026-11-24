// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { findSmallFonts } from '@/components/editor/__tests__/no-small-fonts';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { AvatarRunInput } from './AvatarRunInput';

beforeAll(() => {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
  global.ResizeObserver = vi.fn().mockImplementation(function () {
    return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
  });
  Element.prototype.scrollIntoView = vi.fn();
});
afterEach(() => cleanup());

function av(id: string, name: string, extra: Partial<ProjectAvatar> = {}): ProjectAvatar {
  const base = { id, name, selectable: true, unselectableReason: null, retentionUntil: '2099-01-01T00:00:00Z', references: [] };
  return { ...base, ...extra } as unknown as ProjectAvatar;
}

const AVATARS = [av('a1', 'Anna'), av('a2', 'Marco', { selectable: false, unselectableReason: 'retention_expired' })];

function renderInput(value: string | null = null, onChange = vi.fn(), avatars: ProjectAvatar[] | undefined = AVATARS) {
  render(<MantineProvider><AvatarRunInput projectId="p1" avatars={avatars} value={value} onChange={onChange} /></MantineProvider>);
  return onChange;
}

describe('AvatarRunInput', () => {
  it('offers None and the usable avatars, and lists an unusable one disabled with its reason', () => {
    const onChange = renderInput();
    fireEvent.click(screen.getByRole('combobox', { name: 'Avatar' }));
    expect(screen.getByRole('option', { name: 'None' })).toBeTruthy();
    const marco = screen.getByRole('option', { name: /Marco · Expired/ });
    expect(marco.getAttribute('data-combobox-disabled')).toBe('true');
    expect(within(marco).getByText(/keep-until date has passed/)).toBeTruthy();
    fireEvent.click(screen.getByRole('option', { name: /Anna · Ready/ }));
    expect(onChange).toHaveBeenCalledWith('a1');
  });

  it('says what is sent once an avatar is chosen', () => {
    renderInput('a1');
    expect(screen.getByText(/Anna's pictures, videos and voice samples are sent to the video service/)).toBeTruthy();
  });

  it('links to the Avatars tab when the project has none', () => {
    renderInput(null, vi.fn(), []);
    expect(screen.getByRole('link', { name: 'Open the Avatars tab' }).getAttribute('href')).toBe('/projects/p1?tab=avatars');
  });

  describe('with objects', () => {
    const SHOE = av('o1', 'Shoe', { subjectKind: 'Object', retentionUntil: null });
    const CAR = av('o2', 'Car', { subjectKind: 'Object', retentionUntil: null, selectable: false, unselectableReason: 'no_image_reference' });

    it('lists people and objects under a heading each and says the video shows a person or an object', () => {
      const onChange = renderInput(null, vi.fn(), [...AVATARS, SHOE, CAR]);
      expect(screen.getByText(/The person or object the video shows/)).toBeTruthy();
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar' }));
      expect(within(screen.getByRole('group', { name: 'People' })).getByRole('option', { name: /Anna · Ready/ })).toBeTruthy();
      const objects = screen.getByRole('group', { name: 'Objects' });
      const car = within(objects).getByRole('option', { name: /Car · No picture/ });
      expect(car.getAttribute('data-combobox-disabled')).toBe('true');
      expect(within(car).getByText('It has no usable picture.')).toBeTruthy();
      fireEvent.click(within(objects).getByRole('option', { name: /Shoe · Ready/ }));
      expect(onChange).toHaveBeenCalledWith('o1');
    });

    it('lists a variant right after its original as "Shoe · Red", with its own state, and names it in what is sent', () => {
      const RED = av('o3', 'own name', { subjectKind: 'Object', retentionUntil: null, variantOfAvatarId: 'o1', variantLabel: 'Red' });
      const BLUE = av('o4', 'own name', { subjectKind: 'Object', retentionUntil: null, variantOfAvatarId: 'o1', variantLabel: 'Blue', selectable: false, unselectableReason: 'reference_missing' });
      const onChange = renderInput('o3', vi.fn(), [RED, ...AVATARS, CAR, BLUE, SHOE]);
      expect(screen.getByTestId('avatar-sends').textContent).toMatch(/^The pictures and videos of "Shoe · Red" are sent/);
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar' }));
      const options = within(screen.getByRole('group', { name: 'Objects' })).getAllByRole('option').map((o) => o.textContent);
      expect(options.map((o) => o?.split(' · ').slice(0, -1).join(' · '))).toEqual(['Shoe', 'Shoe · Blue', 'Shoe · Red', 'Car']);
      expect(screen.getByRole('option', { name: /Shoe · Blue · File missing/ }).getAttribute('data-combobox-disabled')).toBe('true');
      fireEvent.click(screen.getByRole('option', { name: /^Shoe · Ready/ }));
      expect(onChange).toHaveBeenCalledWith('o1');
    });

    it('says what is sent for an object: pictures and videos, no voice samples', () => {
      renderInput('o1', vi.fn(), [...AVATARS, SHOE]);
      const sends = screen.getByTestId('avatar-sends').textContent ?? '';
      expect(sends).toMatch(/^The pictures and videos of "Shoe" are sent to the video service/);
      expect(sends).not.toMatch(/voice/);
    });

    it('shows no heading when the project has objects only', () => {
      renderInput(null, vi.fn(), [SHOE]);
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar' }));
      expect(screen.queryByRole('group')).toBeNull();
      expect(screen.getByRole('option', { name: /Shoe · Ready/ })).toBeTruthy();
    });

    it('keeps the wording about a person when the project has people only', () => {
      renderInput();
      expect(screen.getByText(/^The person the video shows/)).toBeTruthy();
    });
  });

  it('uses no font under 12 px', () => {
    expect(findSmallFonts(readFileSync(join(__dirname, 'AvatarRunInput.tsx'), 'utf8'))).toEqual([]);
  });
});
