// @vitest-environment jsdom
/** The shared subject picker: grouped list, the "three at most, one person" rule, and plain reasons. */
import React, { useState } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { testAvatar, testObject } from '@/test/avatars';
import { renderWithI18n } from '@/test/i18n';
import { SubjectPicker, type SubjectPickerProps } from './SubjectPicker';

vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });

const anna = testAvatar({ references: [{ id: 'r1', projectFileId: 'f-anna', fileName: 'anna.jpg', kind: 'Image', role: 'Face', ordinal: 0, width: 800, height: 800, durationMs: null, attachedAt: '', state: 'ok' }], primaryReferenceId: 'r1' });
const bea = testAvatar({ id: 'bea', name: 'Bea' });
const bottle = testObject();
const car = testObject({ id: 'car', name: 'Car' });
const shop = testObject({ id: 'shop', name: 'Shop' });
const tower = testObject({ id: 'tower', name: 'Tower' });
const revoked = testAvatar({ id: 'old', name: 'Old', selectable: false, unselectableReason: 'consent_revoked' });
const videoOnly = testAvatar({ id: 'vid', name: 'Vid', coversPictures: false });
const ALL: ProjectAvatar[] = [anna, bea, bottle, car, shop, tower, revoked, videoOnly];

function Harness({ initial = [], onIds, ...props }: Partial<SubjectPickerProps> & { initial?: string[]; onIds?: (ids: string[]) => void }) {
  const [value, setValue] = useState<string[]>(initial);
  return (
    <SubjectPicker
      projectId="p1"
      avatars={ALL}
      {...props}
      value={value}
      onChange={(ids) => {
        setValue(ids);
        onIds?.(ids);
      }}
    />
  );
}

const mount = (props: Parameters<typeof Harness>[0] = {}) => render(<MantineProvider><Harness {...props} /></MantineProvider>);
const box = (id: string) => screen.getByTestId(`subject-option-${id}`) as HTMLInputElement;
const reason = (id: string) => screen.queryByTestId(`subject-reason-${id}`)?.textContent ?? null;

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
});
afterEach(cleanup);

describe('SubjectPicker', () => {
  it('lists people and objects under a heading each, with the first picture or an icon by kind', () => {
    mount();
    const people = within(screen.getByTestId('subject-group-people'));
    const objects = within(screen.getByTestId('subject-group-objects'));
    expect(people.getByText('Anna')).toBeTruthy();
    expect(people.getByText('Bea')).toBeTruthy();
    expect(objects.getByText('Bottle')).toBeTruthy();
    expect(people.queryByText('Bottle')).toBeNull();
    expect(screen.getByRole('group', { name: 'People' })).toBeTruthy();
    expect(screen.getByRole('group', { name: 'Objects' })).toBeTruthy();
    const img = screen.getByTestId('subject-thumb-anna').querySelector('img');
    expect(img?.getAttribute('src')).toBe('/api/v1/projects/p1/files/f-anna/media');
    expect(screen.getByTestId('subject-thumb-bottle').querySelector('img')).toBeNull();
    expect(screen.getByTestId('subject-thumb-bottle').querySelector('svg')).toBeTruthy();
    // a picture that does not load falls back to the icon
    fireEvent.error(img!);
    expect(screen.getByTestId('subject-thumb-anna').querySelector('img')).toBeNull();
  });

  it('emits the chosen ids in the order chosen, and unticking removes one', () => {
    const onIds = vi.fn();
    mount({ onIds });
    fireEvent.click(box('bottle'));
    fireEvent.click(box('anna'));
    expect(onIds).toHaveBeenLastCalledWith(['bottle', 'anna']);
    expect(screen.getByTestId('subject-picker-count').textContent).toBe('2 of 3 chosen');
    fireEvent.click(box('bottle'));
    expect(onIds).toHaveBeenLastCalledWith(['anna']);
  });

  it('allows one person only: the others are greyed with the reason, and free again once she is unticked', () => {
    mount({ initial: ['anna'] });
    expect(box('bea').disabled).toBe(true);
    expect(reason('bea')).toBe('Only one person at a time. Untick the other person to choose this one.');
    expect(box('bottle').disabled).toBe(false);
    expect(box('anna').disabled).toBe(false);
    fireEvent.click(box('anna'));
    expect(box('bea').disabled).toBe(false);
    expect(reason('bea')).toBeNull();
  });

  it('stops at three: a fourth is greyed with the reason, and a chosen one can still be unticked', () => {
    mount({ initial: ['bottle', 'car', 'shop'] });
    expect(box('tower').disabled).toBe(true);
    expect(reason('tower')).toBe('You can choose up to 3. Untick one to choose this.');
    expect(box('anna').disabled).toBe(true);
    expect(box('car').disabled).toBe(false);
    fireEvent.click(box('car'));
    expect(box('tower').disabled).toBe(false);
  });

  it('keeps an unusable one in the list, greyed, with why in plain words', () => {
    mount();
    expect(box('old').disabled).toBe(true);
    expect(reason('old')).toBe('Consent was withdrawn. The avatar is deleted automatically after the waiting period.');
  });

  it('for pictures, greys a person whose consent does not cover pictures; for videos that person is free', () => {
    mount({ forPictures: true });
    expect(box('vid').disabled).toBe(true);
    expect(reason('vid')).toBe('Confirm consent again in the Avatars tab to use this person in pictures');
    cleanup();
    mount();
    expect(box('vid').disabled).toBe(false);
    expect(reason('vid')).toBeNull();
  });

  it('leaves a ticked one that stopped being usable ticked, with the reason, so it can be unticked', () => {
    const onIds = vi.fn();
    mount({ initial: ['old'], onIds });
    expect(box('old').checked).toBe(true);
    expect(box('old').disabled).toBe(false);
    expect(reason('old')).toContain('Consent was withdrawn');
    fireEvent.click(box('old'));
    expect(onIds).toHaveBeenLastCalledWith([]);
  });

  it('takes a smaller limit, says so, and can be switched off whole', () => {
    mount({ max: 1, initial: ['bottle'] });
    expect(box('car').disabled).toBe(true);
    expect(reason('car')).toBe('You can choose up to 1. Untick one to choose this.');
    cleanup();
    mount({ disabled: true });
    expect(box('anna').disabled).toBe(true);
    expect(box('bottle').disabled).toBe(true);
  });

  it('is operable by keyboard: every option is a labelled checkbox in a named group', () => {
    mount();
    expect(screen.getByRole('group', { name: 'People and objects to show' })).toBeTruthy();
    const anna2 = screen.getByRole('checkbox', { name: /Anna/ }) as HTMLInputElement;
    anna2.focus();
    expect(document.activeElement).toBe(anna2);
    fireEvent.click(anna2);
    expect(anna2.checked).toBe(true);
  });

  it('gives touch rows a 44 px height and says so when the project has none', () => {
    mount({ touch: true });
    expect((box('anna').closest('.mantine-Checkbox-root') as HTMLElement).style.minHeight).toBe('44px');
    cleanup();
    mount({ avatars: [] });
    expect(screen.getByTestId('subject-picker-empty').textContent).toContain("Add them in the project's Avatars tab");
  });

  it('reads in Italian', () => {
    function It() {
      const [value, setValue] = useState<string[]>(['anna']);
      return <SubjectPicker projectId="p1" avatars={ALL} value={value} onChange={setValue} forPictures />;
    }
    renderWithI18n(<It />, { locale: 'it' });
    expect(screen.getByRole('group', { name: 'Persone' })).toBeTruthy();
    expect(screen.getByRole('group', { name: 'Oggetti' })).toBeTruthy();
    expect(reason('bea')).toBe("Una sola persona alla volta. Togli la spunta all'altra persona per scegliere questa.");
    expect(reason('vid')).toBe('Conferma di nuovo il consenso nella scheda Avatar per usare questa persona nelle immagini');
  });
});
