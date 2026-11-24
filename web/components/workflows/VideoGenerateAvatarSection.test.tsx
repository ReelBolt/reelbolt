// @vitest-environment jsdom
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { useState } from 'react';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { findSmallFonts } from '@/components/editor/__tests__/no-small-fonts';
import type { ProjectAvatar } from '@/lib/types/avatars';
import type { StepType, VideoGenerateStepConfig as Config } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

const H = vi.hoisted(() => ({
  requirements: { data: {} as unknown, error: undefined as unknown },
  avatars: [] as unknown[],
  files: [] as unknown[],
}));

vi.mock('@/lib/hooks/use-avatars', () => ({
  useAvatarRequirements: () => H.requirements,
  useProjectAvatars: (_p: string, enabled: boolean) => ({ data: enabled ? H.avatars : undefined }),
}));
vi.mock('@/lib/hooks/use-files', () => ({ useProjectFiles: () => ({ data: H.files, mutate: vi.fn() }) }));
vi.mock('@/lib/hooks/use-inference-providers', () => ({
  useSelectableProviders: () => ({ data: [], error: undefined, isLoading: false }),
}));

import { VideoGenerateStepConfig, createDefaultVideoGenerateStepConfig } from './VideoGenerateStepConfig';
import { FieldErrorProvider } from './config-kit';

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

const ref = (id: string, role: 'Face' | 'HalfBody' | 'Speech', kind: 'Image' | 'Video' | 'Audio') => ({
  id, projectFileId: `f-${id}`, fileName: `${id}.bin`, kind, role, ordinal: 0, width: null, height: null, durationMs: null,
  attachedAt: '2026-11-01T00:00:00Z', state: 'ok' as const,
});

function avatar(id: string, name: string, extra: Partial<ProjectAvatar> = {}): ProjectAvatar {
  return {
    id, projectId: 'p1', name, description: null, selectable: true, unselectableReason: null,
    projectVoiceId: null, projectVoiceName: null, projectVoiceSelectable: null,
    consent: {
      attestedByUserId: 'u1', attestedAt: '2026-11-01T00:00:00Z', statement: 'x', statementVersion: 'v', locale: 'en',
      subjectName: 'Subject', basis: 'Self', subjectIsAdult: true, documentFileId: null, revokedAt: null, revokedByUserId: null,
    },
    retentionUntil: '2099-01-01T00:00:00Z', disclosureLabel: 'Visible', primaryReferenceId: null,
    references: [ref('r1', 'Face', 'Image'), ref('r2', 'HalfBody', 'Image'), ref('r3', 'Speech', 'Audio')],
    createdAt: '2026-11-01T00:00:00Z', createdByUserId: 'u1', updatedAt: '2026-11-01T00:00:00Z',
    ...extra,
  };
}

const ANNA = avatar('a1', 'Anna');
const MARCO = avatar('a2', 'Marco', { selectable: false, unselectableReason: 'consent_revoked' });

function step(id: string, stepType: string): StepData {
  return { id, label: `Step ${id}`, stepType: stepType as StepType } as unknown as StepData;
}

let last: Config | null = null;

function Harness({ initial = {}, steps = [], index = 0 }: { initial?: Partial<Config>; steps?: StepData[]; index?: number }) {
  const [config, setConfig] = useState<Config>({ ...createDefaultVideoGenerateStepConfig(), ...initial });
  return (
    <VideoGenerateStepConfig
      config={config}
      onChange={(c) => { last = c; setConfig(c); }}
      allSteps={steps}
      currentStepIndex={index}
      projectId="p1"
    />
  );
}

const renderIt = (ui: React.ReactElement) => render(<MantineProvider>{ui}</MantineProvider>);

beforeEach(() => {
  H.requirements = { data: {}, error: undefined };
  H.avatars = [ANNA, MARCO];
  H.files = [];
  last = null;
});
afterEach(() => cleanup());

describe('VideoGenerate avatar section', () => {
  it('is hidden when the avatars API answers 404 (switch off)', () => {
    H.requirements = { data: undefined, error: new Error('404') };
    renderIt(<Harness />);
    expect(screen.queryByTestId('video-generate-avatar-section')).toBeNull();
    expect(screen.queryByRole('combobox', { name: 'Avatar to show' })).toBeNull();
  });

  it('is hidden without a project', () => {
    render(<MantineProvider><VideoGenerateStepConfig config={createDefaultVideoGenerateStepConfig()} onChange={vi.fn()} /></MantineProvider>);
    expect(screen.queryByTestId('video-generate-avatar-section')).toBeNull();
  });

  it('lists the avatars with status, disables an unusable one with the reason, and offers None', () => {
    renderIt(<Harness />);
    fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
    expect(screen.getByRole('option', { name: 'None' })).toBeTruthy();
    const anna = screen.getByRole('option', { name: /Anna · Ready/ });
    expect(anna.getAttribute('data-combobox-disabled')).toBeNull();
    const marco = screen.getByRole('option', { name: /Marco · Consent withdrawn/ });
    expect(marco.getAttribute('data-combobox-disabled')).toBe('true');
    expect(within(marco).getByText(/Consent was withdrawn/)).toBeTruthy();
  });

  it('picking an avatar stores it, shows the roles present and warns that the permission is off without turning it on', () => {
    renderIt(<Harness />);
    fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
    fireEvent.click(screen.getByRole('option', { name: /Anna · Ready/ }));
    expect(last?.avatarId).toBe('a1');
    expect(last?.avatarReferenceRoles).toBeNull();
    expect(last?.allowSourceMediaEgress).toBe(false);
    expect(screen.getByRole('checkbox', { name: 'Face' })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Upper body' })).toBeTruthy();
    expect(screen.getByRole('checkbox', { name: 'Speaking' })).toBeTruthy();
    expect(screen.queryByRole('checkbox', { name: 'Full body' })).toBeNull();
    expect(screen.getByTestId('avatar-egress-hint').textContent).toMatch(/Allow sending your media to the video service/);
    // The permission switch says why an avatar needs it.
    expect(screen.getByText(/An avatar needs this switched on/)).toBeTruthy();
  });

  it('keeps a picture role: unticking every picture shows the save rule', () => {
    renderIt(<Harness initial={{ avatarId: 'a1', allowSourceMediaEgress: true }} />);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Speaking' }));
    expect(last?.avatarReferenceRoles).toEqual(['Face', 'HalfBody']);
    fireEvent.click(screen.getByRole('checkbox', { name: 'Face' }));
    fireEvent.click(screen.getByRole('checkbox', { name: 'Upper body' }));
    expect(last?.avatarReferenceRoles).toEqual([]);
    expect(screen.getByText(/Keep at least one picture/)).toBeTruthy();
    expect(screen.queryByTestId('avatar-egress-hint')).toBeNull();
  });

  it('says a chosen avatar that became unusable cannot be used, in the Avatars tab words', () => {
    renderIt(<Harness initial={{ avatarId: 'a2' }} />);
    expect(screen.getByTestId('avatar-unusable').textContent).toMatch(/"Marco" cannot be used: Consent was withdrawn/);
  });

  it('is disabled with a reason on a plan-sourced step', () => {
    renderIt(<Harness initial={{ planSource: 'StepRef' }} />);
    const select = screen.getByRole('combobox', { name: 'Avatar to show' }) as HTMLInputElement;
    expect(select.disabled).toBe(true);
    expect(screen.getByText(/An avatar works only with "Inline prompt"/)).toBeTruthy();
  });

  it('is disabled with a reason when a first frame is set', () => {
    renderIt(<Harness initial={{ firstFrameProjectFileId: 'img1' }} />);
    expect((screen.getByRole('combobox', { name: 'Avatar to show' }) as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByText(/cannot be combined with a first or last frame/)).toBeTruthy();
  });

  it('takes the driving speech from an earlier Voiceover step and explains the model', () => {
    const steps = [step('s1', 'Agent'), step('s2', 'Voiceover'), step('s3', 'VideoGenerate')];
    renderIt(<Harness steps={steps} index={2} initial={{ model: 'minimax/h3-max/lip-sync/image-to-video' }} />);
    expect(screen.getByTestId('avatar-speech-hint').textContent).toMatch(/word for word/);
    fireEvent.click(screen.getByRole('radio', { name: 'Voiceover step' }));
    expect(last?.avatarAudio).toEqual({ from: 'Step', stepOrder: 2 });
    expect(last?.avatarAudioProjectFileId).toBeNull();
  });

  it('takes the driving speech from a project audio file', () => {
    H.files = [{ id: 'au1', originalFileName: 'lines.wav', mimeType: 'audio/wav' }, { id: 'im1', originalFileName: 'x.png', mimeType: 'image/png' }];
    renderIt(<Harness />);
    expect(screen.getByTestId('avatar-speech-hint').textContent).toMatch(/does not make the person say a speech track.*Talking avatar \(budget\)/);
    fireEvent.click(screen.getByRole('radio', { name: 'Audio file' }));
    expect(last?.avatarAudioProjectFileId).toBe('au1');
    expect(last?.avatarAudio).toBeNull();
  });

  it('shows a refused save next to the avatar field', () => {
    render(
      <MantineProvider>
        <FieldErrorProvider errors={new Map([['config.avatarId', 'avatar a9 was not found in this project.']])}>
          <Harness initial={{ avatarId: 'a1' }} />
        </FieldErrorProvider>
      </MantineProvider>,
    );
    expect(screen.getAllByRole('alert').some((a) => a.textContent === 'avatar a9 was not found in this project.')).toBe(true);
  });

  describe('with objects', () => {
    const objRef = (id: string, role: 'Front' | 'Angle' | 'Motion', kind: 'Image' | 'Video') => ({ ...ref(id, 'Face', kind), role });
    const SHOE = avatar('o1', 'Shoe', {
      subjectKind: 'Object', retentionUntil: null, references: [objRef('r1', 'Front', 'Image'), objRef('r2', 'Angle', 'Image'), objRef('r3', 'Motion', 'Video')],
    });
    const CAR = avatar('o2', 'Car', { subjectKind: 'Object', retentionUntil: null, selectable: false, unselectableReason: 'consent_missing' });
    const both = { kinds: { person: { enabled: true, entitled: true }, object: { enabled: true, entitled: true } } };

    it('lists people and objects under a heading each, with an unusable object disabled in an object\'s words', () => {
      H.requirements = { data: both, error: undefined };
      H.avatars = [ANNA, MARCO, SHOE, CAR];
      renderIt(<Harness />);
      expect(screen.getByText(/Show a person or an object from the project's Avatars tab/)).toBeTruthy();
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
      expect(screen.getByRole('group', { name: 'People' })).toBeTruthy();
      const objects = screen.getByRole('group', { name: 'Objects' });
      expect(within(objects).getByRole('option', { name: /Shoe · Ready/ })).toBeTruthy();
      const car = within(objects).getByRole('option', { name: /Car · Not confirmed/ });
      expect(car.getAttribute('data-combobox-disabled')).toBe('true');
      expect(within(car).getByText(/The statement for this object is not on record/)).toBeTruthy();
      expect(within(screen.getByRole('group', { name: 'People' })).getByRole('option', { name: /Anna · Ready/ })).toBeTruthy();
    });

    it('lists a variant right after its original as "Car · Red", with its own state', () => {
      H.requirements = { data: both, error: undefined };
      const RED = avatar('o3', 'Car · Red', { subjectKind: 'Object', retentionUntil: null, variantOfAvatarId: 'o2', variantLabel: 'Red', variantDescription: 'matte red paint' });
      const NAVY = avatar('o4', 'own name', { subjectKind: 'Object', retentionUntil: null, variantOfAvatarId: 'o2', variantLabel: 'Navy', selectable: false, unselectableReason: 'reference_missing' });
      H.avatars = [RED, ANNA, NAVY, SHOE, { ...CAR, name: 'Truck', variantCount: 2 }];
      renderIt(<Harness initial={{ avatarId: 'o4' }} />);
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
      const options = within(screen.getByRole('group', { name: 'Objects' })).getAllByRole('option').map((o) => o.textContent);
      expect(options[0]).toMatch(/^Shoe · Ready/);
      expect(options[1]).toMatch(/^Truck · Not confirmed/);
      // The variants follow a rename of their original and sit right under it.
      expect(options[2]).toMatch(/^Truck · Navy · File missing/);
      expect(options[3]).toMatch(/^Truck · Red · Ready/);
      // A usable variant of an unusable original can be chosen.
      expect(screen.getByRole('option', { name: /Truck · Red · Ready/ }).getAttribute('data-combobox-disabled')).toBeNull();
      expect(screen.getByTestId('avatar-unusable').textContent).toContain('"Truck · Navy" cannot be used');
      fireEvent.click(screen.getByRole('option', { name: /Truck · Red · Ready/ }));
      expect(last?.avatarId).toBe('o3');
    });

    it('shows no heading when the project has people only', () => {
      H.requirements = { data: both, error: undefined };
      renderIt(<Harness />);
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
      expect(screen.queryByRole('group')).toBeNull();
    });

    it('says only objects when only objects are switched on', () => {
      H.requirements = { data: { kinds: { person: { enabled: false, entitled: false }, object: { enabled: true, entitled: true } } }, error: undefined };
      H.avatars = [SHOE];
      renderIt(<Harness />);
      expect(screen.getByText(/^Show an object from the project's Avatars tab/)).toBeTruthy();
    });

    it('choosing an object shows its own roles, hides the speech controls and drops a speech track', () => {
      H.requirements = { data: both, error: undefined };
      H.avatars = [ANNA, SHOE];
      const steps = [step('s1', 'Voiceover'), step('s2', 'VideoGenerate')];
      renderIt(<Harness steps={steps} index={1} initial={{ avatarId: 'a1', avatarAudio: { from: 'Step', stepOrder: 1 } }} />);
      expect(screen.getByRole('radio', { name: 'Voiceover step' })).toBeTruthy();
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
      fireEvent.click(screen.getByRole('option', { name: /Shoe · Ready/ }));
      expect(last?.avatarId).toBe('o1');
      expect(last?.avatarAudio).toBeNull();
      expect(last?.avatarAudioProjectFileId).toBeNull();
      expect(last?.allowSourceMediaEgress).toBe(false);
      for (const role of ['Front view', 'Another angle', 'Moving']) expect(screen.getByRole('checkbox', { name: role })).toBeTruthy();
      for (const role of ['Face', 'Speaking']) expect(screen.queryByRole('checkbox', { name: role })).toBeNull();
      expect(screen.queryByRole('radio', { name: 'Voiceover step' })).toBeNull();
      expect(screen.queryByTestId('avatar-speech-hint')).toBeNull();
      expect(screen.getByTestId('avatar-speech-not-for-objects').textContent).toBe('An object says nothing, so there is no speech to choose.');
      expect(screen.getByTestId('avatar-egress-hint')).toBeTruthy();
    });

    it('keeps a picture of the object: a video alone shows the save rule in an object\'s words', () => {
      H.requirements = { data: both, error: undefined };
      H.avatars = [SHOE];
      renderIt(<Harness initial={{ avatarId: 'o1', allowSourceMediaEgress: true }} />);
      fireEvent.click(screen.getByRole('checkbox', { name: 'Front view' }));
      expect(last?.avatarReferenceRoles).toEqual(['Motion', 'Angle']);
      fireEvent.click(screen.getByRole('checkbox', { name: 'Another angle' }));
      expect(last?.avatarReferenceRoles).toEqual(['Motion']);
      expect(screen.getByText('Keep at least one picture (Front view, Another angle or Close-up detail).')).toBeTruthy();
    });

    it('choosing a person keeps the speech controls and a speech track', () => {
      H.requirements = { data: both, error: undefined };
      H.avatars = [ANNA, SHOE];
      renderIt(<Harness initial={{ avatarAudioProjectFileId: 'au1' }} />);
      fireEvent.click(screen.getByRole('combobox', { name: 'Avatar to show' }));
      fireEvent.click(screen.getByRole('option', { name: /Anna · Ready/ }));
      expect(last?.avatarId).toBe('a1');
      expect(last?.avatarAudioProjectFileId).toBe('au1');
      expect(screen.getByTestId('avatar-speech-hint')).toBeTruthy();
    });
  });

  it('uses no font under 12 px', () => {
    const src = readFileSync(join(__dirname, 'VideoGenerateAvatarSection.tsx'), 'utf8');
    expect(findSmallFonts(src)).toEqual([]);
  });
});
