// @vitest-environment jsdom
/* eslint @typescript-eslint/no-unused-vars: ["warn", { "argsIgnorePattern": "^_" }] */

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { render, screen, fireEvent, cleanup, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { InferenceProviderForm } from './InferenceProviderForm';

const HIGGSFIELD_DATA_TERMS_TITLE = "Check Higgsfield's data terms before sending footage";
const HIGGSFIELD_DEFAULT_ENDPOINT = 'https://api.higgsfield.ai';

// Polyfill ResizeObserver for jsdom
vi.stubGlobal(
  'ResizeObserver',
  class ResizeObserver {
    constructor(_callback: ResizeObserverCallback) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  }
);

// Polyfill scrollIntoView for jsdom
Element.prototype.scrollIntoView = vi.fn();

vi.mock('@/lib/api/inference-providers', async (orig) => ({
  // `isByoNotInPlan` is deliberately the real one: the plan-limit branch is what these tests are
  // about, so the predicate under test must not itself be stubbed out.
  ...(await orig<typeof import('@/lib/api/inference-providers')>()),
  createProvider: vi.fn(),
  updateProvider: vi.fn(),
  testProviderConfig: vi.fn(),
}));

import { ApiError } from '@/lib/api/client';
import { createProvider, updateProvider } from '@/lib/api/inference-providers';
import type { InferenceProvider } from '@/lib/types/inference-provider';

describe('InferenceProviderForm', () => {
  beforeEach(() => {
    global.matchMedia = (query: string): MediaQueryList =>
      ({
        matches: false,
        media: query,
        onchange: null,
        addListener: () => {},
        removeListener: () => {},
        addEventListener: () => {},
        removeEventListener: () => {},
        dispatchEvent: () => true,
      }) as MediaQueryList;
  });

  afterEach(() => {
    cleanup();
    vi.clearAllMocks();
  });

  it('Kind <Select> offers a TypeSafe option', () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open the Kind dropdown by clicking on it
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    // Check that TypeSafe option is available
    const typeSafeOption = screen.getByText('TypeSafe AI (Jev)');
    expect(typeSafeOption).toBeDefined();
  });

  it('Capability <Select> offers a Decision option', () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open the Capability dropdown by clicking on it
    const capabilitySelect = screen.getAllByRole('combobox')[1];
    fireEvent.click(capabilitySelect);

    // Check that Decision option is available
    const decisionOption = screen.getByText('Decision (Jev / logprob)');
    expect(decisionOption).toBeDefined();
  });

  it('Selecting TypeSafe as Kind auto-sets Capability to Decision and prefills endpoint to https://api.typesafe.ai', async () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open Kind dropdown and select TypeSafe
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    const typeSafeOption = screen.getByText('TypeSafe AI (Jev)');
    fireEvent.click(typeSafeOption);

    // Wait for the capability to be auto-set to Decision
    await waitFor(() => {
      const capabilityInput = screen.getByDisplayValue('Decision (Jev / logprob)');
      expect(capabilityInput).toBeDefined();
    });

    // Verify endpoint was prefilled
    const endpointInput = screen.getByDisplayValue('https://api.typesafe.ai');
    expect(endpointInput).toBeDefined();
  });

  it('With Kind = Anthropic, Capability options do NOT include Decision', async () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open Kind dropdown and select Anthropic
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    const anthropicOption = screen.getByText('Anthropic (Claude)');
    fireEvent.click(anthropicOption);

    await waitFor(() => {
      // Open Capability dropdown
      const capabilitySelect = screen.getAllByRole('combobox')[1];
      fireEvent.click(capabilitySelect);
    });

    // Decision should not be in the options
    expect(screen.queryByText('Decision (Jev / logprob)')).toBeNull();

    // But other options should be available
    expect(screen.getByText('Chat')).toBeDefined();
    expect(screen.getByText('Vision (shot captioning)')).toBeDefined();
  });

  it('With Kind = Gemini, Capability options do NOT include Decision', async () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open Kind dropdown and select Gemini
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    const geminiOption = screen.getByText('Google Gemini');
    fireEvent.click(geminiOption);

    await waitFor(() => {
      // Open Capability dropdown
      const capabilitySelect = screen.getAllByRole('combobox')[1];
      fireEvent.click(capabilitySelect);
    });

    // Decision should not be in the options
    expect(screen.queryByText('Decision (Jev / logprob)')).toBeNull();

    // But other options should be available
    expect(screen.getByText('Chat')).toBeDefined();
    expect(screen.getByText('Vision (shot captioning)')).toBeDefined();
  });

  it('Selecting MiniMax as Kind prefills the endpoint to exactly https://api.minimax.io', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('MiniMax'));

    await waitFor(() => {
      expect(screen.getByDisplayValue('https://api.minimax.io')).toBeDefined();
    });
  });

  it('Selecting MiniMax as Kind sets the Model placeholder to exactly MiniMax-H3', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('MiniMax'));

    await waitFor(() => {
      expect(screen.getByRole('combobox', { name: 'Model' }).getAttribute('placeholder')).toBe('MiniMax-H3');
    });
  });

  it('Kind <Select> offers a FishAudio option', () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open the Kind dropdown by clicking on it
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    // Check that FishAudio option is available
    const fishAudioOption = screen.getByText('Fish Audio');
    expect(fishAudioOption).toBeDefined();
  });

  // Fish Audio publishes BOTH text-to-speech (POST /v1/tts) and speech-to-text (POST /v1/asr) and
  // nothing else, so the form offers exactly those two. It used to offer SpeechSynthesis alone,
  // which is what this test pinned until the ASR client shipped.
  it('Selecting Kind: FishAudio narrows the Capability <Select> options to SpeechSynthesis and Transcription', async () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open Kind dropdown and select FishAudio
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    const fishAudioOption = screen.getByText('Fish Audio');
    fireEvent.click(fishAudioOption);

    await waitFor(() => {
      // Open Capability dropdown
      const capabilitySelect = screen.getAllByRole('combobox')[1];
      fireEvent.click(capabilitySelect);
    });

    // Both of the capabilities Fish Audio actually serves.
    expect(screen.getByText('Speech Synthesis (TTS / voiceover)')).toBeDefined();
    // getAllByText: the label appears both as the option and inside the ASR notice the form shows
    // for this pairing, which is correct and more than the assertion needs to distinguish.
    expect(screen.getAllByText('Transcription (ASR)').length).toBeGreaterThan(0);

    // Everything else should NOT be in the options.
    expect(screen.queryByText('Chat')).toBeNull();
    expect(screen.queryByText('Vision (shot captioning)')).toBeNull();
    expect(screen.queryByText('Decision (Jev / logprob)')).toBeNull();
  });

  it('Selecting Kind: FishAudio renders the licence notice containing the substring "no documented non-commercial restriction"', async () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open Kind dropdown and select FishAudio
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    const fishAudioOption = screen.getByText('Fish Audio');
    fireEvent.click(fishAudioOption);

    // Wait for the licence notice to appear
    await waitFor(() => {
      const licenceNotice = screen.getByText((content, _element) =>
        content.includes('no documented non-commercial restriction')
      );
      expect(licenceNotice).toBeDefined();
    });
  });

  it('Selecting Kind: AzureOpenAI does NOT render the FishAudio licence notice', async () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open Kind dropdown and select AzureOpenAI
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    const azureOpenAIOption = screen.getByText('Azure OpenAI');
    fireEvent.click(azureOpenAIOption);

    // Wait and verify the licence notice is NOT rendered
    await waitFor(() => {
      const licenceNotice = screen.queryByText((content, _element) =>
        content.includes('no documented non-commercial restriction')
      );
      expect(licenceNotice).toBeNull();
    });
  });

  // ───────────────────────────────────────────────────────────────────────────
  // The image capabilities and the FLUX kind.
  //
  // Three kinds serve image generation and image editing (MiniMax, OpenAI-Compatible and Black
  // Forest Labs) and every other kind refuses both, so these tests pin the two halves that failed
  // silently before: a kind whose capability list was never widened, and a kind that kept a
  // sole-capability pin after it started serving more than one.
  // ───────────────────────────────────────────────────────────────────────────

  it('Kind <Select> offers a Black Forest Labs option', () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    expect(screen.getByText('Black Forest Labs (FLUX)')).toBeDefined();
  });

  it('Omits both image capabilities from a kind the backend does not allow them for', () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    // The form opens on Azure OpenAI, which is not on the backend's image allowlist: only MiniMax,
    // OpenAI-Compatible and Black Forest Labs serve ImageGeneration/ImageEdit.
    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.queryByText('Image Generation (text-to-image)')).toBeNull();
    expect(screen.queryByText('Image Edit (edit from a description)')).toBeNull();
  });

  it('Selecting Kind: BlackForestLabs offers both image capabilities, prefills the endpoint, and replaces the invalidated Chat selection', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('Black Forest Labs (FLUX)'));

    // The fixed vendor URL is prefilled, like every other kind with a well-known endpoint.
    await waitFor(() => expect(screen.getByDisplayValue('https://api.bfl.ai')).toBeDefined());

    // Chat is refused by this kind, so the stale selection is replaced rather than left blank.
    expect(screen.getByDisplayValue('Image Generation (text-to-image)')).toBeDefined();

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Image Edit (edit from a description)')).toBeDefined();
    // A FLUX row is an image row: no chat, no video, no embeddings.
    expect(screen.queryByText('Chat')).toBeNull();
    expect(screen.queryByText('Video Generation')).toBeNull();
    expect(screen.queryByText('Embedding (semantic file search)')).toBeNull();
  });

  it('Selecting Kind: MiniMax offers all three of its capabilities instead of pinning VideoGeneration', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('MiniMax'));

    // MiniMax serves no chat at all, so the fallback cannot be 'Chat' — that would render a value
    // absent from the option list below, i.e. a blank Select submitting a 400.
    await waitFor(() => expect(screen.getByDisplayValue('Video Generation')).toBeDefined());

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Image Generation (text-to-image)')).toBeDefined();
    expect(screen.getByText('Image Edit (edit from a description)')).toBeDefined();
    expect(screen.queryByText('Chat')).toBeNull();
  });

  it('Selecting Kind: fal.ai offers Video Generation, and a video row suggests video endpoint ids', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('fal.ai'));

    // fal.ai serves video as well as the picture capabilities, and subject detection stays the value
    // selected on switch — so the video capability has to be ON the list, not merely accepted by the API.
    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Video Generation')).toBeDefined();
    fireEvent.click(screen.getByText('Video Generation'));

    // The default image-to-video endpoint, not one of the image ids: a Segmentation endpoint can never
    // stand in for a video take, and the backend refuses the row at save time if it is one. (A fal row
    // calls this field "Endpoint id" — the vendor names a model as the path itself.)
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Endpoint id' }).getAttribute('placeholder')).toBe(
        'fal-ai/vidu/q4/image-to-video'
      )
    );
  });

  it('Selecting Kind: OpenAICompatible offers both image capabilities as well as speech synthesis', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('OpenAI-compatible'));

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Image Generation (text-to-image)')).toBeDefined();
    expect(screen.getByText('Image Edit (edit from a description)')).toBeDefined();
    expect(screen.getByText('Speech Synthesis (TTS / voiceover)')).toBeDefined();
    // Video generation stays refused on this kind.
    expect(screen.queryByText('Video Generation')).toBeNull();
  });

  it('Swaps the model placeholder to image-01 on a MiniMax image row', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('MiniMax'));
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Model' }).getAttribute('placeholder')).toBe('MiniMax-H3'),
    );

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    fireEvent.click(screen.getByText('Image Generation (text-to-image)'));

    // MiniMax-H3 is a video model and cannot stand in for the image one, which is image-01.
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Model' }).getAttribute('placeholder')).toBe('image-01'),
    );
  });

  it('Names the image capabilities in the Set-as-default switch label', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('Black Forest Labs (FLUX)'));
    await waitFor(() => expect(screen.getByText('Set as default image generation provider')).toBeDefined());

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    fireEvent.click(screen.getByText('Image Edit (edit from a description)'));
    await waitFor(() => expect(screen.getByText('Set as default image edit provider')).toBeDefined());
  });

  // ───────────────────────────────────────────────────────────────────────────
  // The picture editor's per-tool capabilities (split out of ImageEdit).
  // ───────────────────────────────────────────────────────────────────────────

  it('Offers every picture tool on a fal.ai row, and suggests the endpoints of that tool only', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('fal.ai'));
    await waitFor(() => expect(screen.getByDisplayValue('https://queue.fal.run')).toBeDefined());

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    for (const label of [
      'Image Erase (remove objects)',
      'Image Fill (paint in an area)',
      'Image Expand (outpaint)',
      'Image Background Replace',
      'Image Relight',
      'Image Upscale',
    ]) {
      expect(screen.getByText(label)).toBeDefined();
    }
    fireEvent.click(screen.getByText('Image Upscale'));

    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Endpoint id' }).getAttribute('placeholder')).toBe(
        'fal-ai/seedvr/upscale/image',
      ),
    );
    await waitFor(() => expect(screen.getByText('Set as default upscale provider')).toBeDefined());
  });

  it('Offers the two mask tools, and only those, to OpenAI-compatible and Black Forest Labs rows', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('Black Forest Labs (FLUX)'));
    await waitFor(() => expect(screen.getByDisplayValue('https://api.bfl.ai')).toBeDefined());

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Image Erase (remove objects)')).toBeDefined();
    expect(screen.getByText('Image Fill (paint in an area)')).toBeDefined();
    // Expand, background, relight and upscale are wired for fal.ai alone.
    expect(screen.queryByText('Image Upscale')).toBeNull();
    expect(screen.queryByText('Image Relight')).toBeNull();
    expect(screen.queryByText('Image Expand (outpaint)')).toBeNull();
  });

  // ───────────────────────────────────────────────────────────────────────────
  // The segmentation and depth capabilities, and the fal.ai kind.
  //
  // fal.ai is the one kind that serves them and every other kind refuses both, so these tests pin
  // the two halves that fail silently otherwise: a kind whose capability list was never widened,
  // and a new kind whose allowlist was copied from a kind that serves something else.
  // ───────────────────────────────────────────────────────────────────────────

  it('Kind <Select> offers a fal.ai option', () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    expect(screen.getByText('fal.ai')).toBeDefined();
  });

  it('Omits Segmentation and Depth from a kind the backend does not allow them for', () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    // The form opens on Azure OpenAI, which serves neither: fal.ai is the only segmentation and
    // depth vendor the backend allows.
    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.queryByText('Segmentation (subject cut-out)')).toBeNull();
    expect(screen.queryByText('Depth (depth map)')).toBeNull();
  });

  it('Selecting Kind: FalAi prefills the queue URL and offers video alongside the picture capabilities', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('fal.ai'));

    // The fixed vendor URL is prefilled, like every other kind with a well-known endpoint.
    await waitFor(() => expect(screen.getByDisplayValue('https://queue.fal.run')).toBeDefined());

    // Chat is refused by this kind, so the stale selection is replaced rather than left blank.
    expect(screen.getByDisplayValue('Segmentation (subject cut-out)')).toBeDefined();

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Depth (depth map)')).toBeDefined();
    // fal.ai serves segmentation, depth, video and both image capabilities: no chat, no embeddings.
    expect(screen.queryByText('Chat')).toBeNull();
    expect(screen.getByText('Image Generation (text-to-image)')).toBeDefined();
    expect(screen.getByText('Video Generation')).toBeDefined();
    expect(screen.queryByText('Embedding (semantic file search)')).toBeNull();
  });

  it('Does not offer the dormant ElevenLabs kind (decision 7: music and sound effects through fal only)', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    expect(screen.getByText('fal.ai')).toBeDefined();
    expect(screen.queryByText(/ElevenLabs/)).toBeNull();
  });

  it('Selecting Kind: fal.ai offers music and sound effects alongside its picture capabilities', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('fal.ai'));
    await waitFor(() => expect(screen.getByDisplayValue('https://queue.fal.run')).toBeDefined());

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.getByText('Music Generation (AudioGenerate steps)')).toBeDefined();
    expect(screen.getByText('Sound Effect Generation (AudioGenerate steps)')).toBeDefined();
  });

  it('Selecting Kind: Azure OpenAI does not offer the audio capabilities', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );
    fireEvent.click(screen.getAllByRole('combobox')[1]);
    expect(screen.queryByText('Music Generation (AudioGenerate steps)')).toBeNull();
  });

  it('Names the real fal endpoint ids in the Segmentation hint', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('fal.ai'));

    // The hint the Capability <Select> shows lists the endpoints that actually work, so an operator
    // copying one out of the form gets a real model rather than a plausible-looking invention.
    await waitFor(() =>
      expect(
        screen.getByText((content) =>
          content.includes('fal-ai/sam-3-1/image, fal-ai/evf-sam, fal-ai/sam2/image, fal-ai/birefnet/v2'),
        ),
      ).toBeDefined(),
    );
  });

  it('Swaps the model placeholder to the depth endpoint on a fal.ai depth row', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('fal.ai'));
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Endpoint id' }).getAttribute('placeholder')).toBe(
        'fal-ai/sam-3-1/image',
      ),
    );

    fireEvent.click(screen.getAllByRole('combobox')[1]);
    fireEvent.click(screen.getByText('Depth (depth map)'));

    // A segmentation endpoint id can never stand in for a depth one.
    await waitFor(() =>
      expect(screen.getByRole('combobox', { name: 'Endpoint id' }).getAttribute('placeholder')).toBe(
        'fal-ai/imageutils/depth',
      ),
    );
    await waitFor(() => expect(screen.getByText('Set as default depth provider')).toBeDefined());
  });

  /**
   * A17-UI: the SAME form drives the workspace's own providers through `scope="org"`. Two rules
   * differ from the platform scope (decision D3), and one outcome does:
   *   - no PersonalOAuthMode,
   *   - no `env:` ambient-credential key,
   *   - a 402 `byo_not_in_plan` is a PLAN LIMIT with the upgrade path, never a red error.
   */
  describe('scope="org"', () => {
    const renderOrg = () =>
      render(
        <MantineProvider>
          <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} scope="org" />
        </MantineProvider>
      );

    async function fillRequiredFields() {
      fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Acme vLLM' } });
      fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'https://vllm.acme.example/v1' } });
      fireEvent.change(screen.getByRole('combobox', { name: 'Deployment name' }), { target: { value: 'gpt-4o-mini' } });
    }

    it('writes through the workspace endpoint, not the platform one', async () => {
      vi.mocked(createProvider).mockResolvedValue({} as never);
      renderOrg();
      await fillRequiredFields();
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      await waitFor(() => expect(createProvider).toHaveBeenCalled());
      expect(vi.mocked(createProvider).mock.calls[0][1]).toBe('org');
      expect(vi.mocked(createProvider).mock.calls[0][0]).toMatchObject({ name: 'Acme vLLM' });
      // A workspace row never carries PersonalOAuthMode, whatever the kind.
      expect(vi.mocked(createProvider).mock.calls[0][0].personalOAuthMode).toBe(false);
    });

    it('never offers the Dev/OAuth switch, even for Anthropic', async () => {
      renderOrg();
      fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
      fireEvent.click(screen.getByText('Anthropic (Claude)'));

      await waitFor(() => {
        expect(screen.getByDisplayValue('https://api.anthropic.com')).toBeDefined();
      });
      expect(screen.queryByText(/Dev\/OAuth \(Personal use only\)/)).toBeNull();
    });

    it('never offers the env: ambient-credential key', () => {
      renderOrg();
      expect(screen.queryByText(/Enter env:/)).toBeNull();
      expect(screen.getByText(/never to a shared deployment credential/)).toBeTruthy();
    });

    it('renders a 402 byo_not_in_plan as a plan limit with the upgrade path, not an error', async () => {
      vi.mocked(createProvider).mockRejectedValue(
        new ApiError('Your plan does not include bringing your own inference provider. Upgrade to add one.', 402),
      );
      renderOrg();
      await fillRequiredFields();
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      await waitFor(() =>
        expect(screen.getByText('Your plan does not include your own inference provider')).toBeTruthy(),
      );
      const upgrade = screen.getByRole('link', { name: 'See plans and credits' });
      expect(upgrade.getAttribute('href')).toBe('/settings/billing');
      // The plan limit is NOT an error: the modal stays open, nothing is red.
      expect(screen.queryByText(/Operation failed/)).toBeNull();
    });
  });

  it('Kind <Select> offers a ComfyUI option and selecting it narrows Capability to Video Generation', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);
    fireEvent.click(screen.getByText('ComfyUI (self-hosted)'));

    // The licence note names what is and is not offered, so an admin is never surprised later.
    expect(await screen.findByText(/LTX-2 is deliberately NOT offered/)).toBeDefined();
    expect(screen.getByText(/Apache-2.0/)).toBeDefined();

    const capabilitySelect = screen.getByRole('combobox', { name: /Capability/i }) as HTMLInputElement;
    expect(capabilitySelect.value).toBe('Video Generation');
  });

  it('Kind <Select> offers a Higgsfield option', () => {
    const onClose = vi.fn();
    const onSuccess = vi.fn();

    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={onClose} onSuccess={onSuccess} />
      </MantineProvider>
    );

    // Open the Kind dropdown by clicking on it
    const kindSelect = screen.getByRole('combobox', { name: /Kind/i });
    fireEvent.click(kindSelect);

    // Check that Higgsfield option is available
    const higgsfieldOption = screen.getByText('Higgsfield');
    expect(higgsfieldOption).toBeDefined();
  });

  it('does NOT render the Higgsfield data-terms warning for a kind that is not Higgsfield', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    // The default kind (Azure OpenAI) carries no such warning.
    expect(screen.queryByText(HIGGSFIELD_DATA_TERMS_TITLE)).toBeNull();

    // Nor does a second, unrelated kind — MiniMax, the other VideoGeneration-only provider.
    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('MiniMax'));
    await waitFor(() => {
      expect(screen.getByDisplayValue('https://api.minimax.io')).toBeDefined();
    });

    expect(screen.queryByText(HIGGSFIELD_DATA_TERMS_TITLE)).toBeNull();
  });

  it('renders the Higgsfield data-terms warning, with its exact title, when Kind is Higgsfield', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    // Mirror the TypeSafe test's switch pattern: open the select, pick the option, await the
    // selection having taken effect (here via the endpoint it prefills).
    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('Higgsfield'));

    await waitFor(() => {
      expect(screen.getByDisplayValue(HIGGSFIELD_DEFAULT_ENDPOINT)).toBeDefined();
    });

    expect(screen.getByText(HIGGSFIELD_DATA_TERMS_TITLE)).toBeDefined();
    // A warning, not an error and not silence: it is a message the admin is meant to act on.
    expect(screen.getByText(/read Higgsfield's terms of service yourself/)).toBeDefined();
  });

  it('describes the Higgsfield API key as the two-part ID:SECRET shape', async () => {
    render(
      <MantineProvider>
        <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
      </MantineProvider>
    );

    // Not Higgsfield yet: no ID:SECRET wording anywhere on the form.
    expect(screen.queryByText(/ID:SECRET/)).toBeNull();

    fireEvent.click(screen.getByRole('combobox', { name: /Kind/i }));
    fireEvent.click(screen.getByText('Higgsfield'));

    await waitFor(() => {
      expect(screen.getByDisplayValue(HIGGSFIELD_DEFAULT_ENDPOINT)).toBeDefined();
    });

    // The helper text has to say the field takes two joined parts, since pasting only one of them
    // stores a key that cannot authenticate.
    const apiKey = screen.getByLabelText('API Key');
    const describedBy = apiKey.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();

    const helper = document.getElementById(describedBy!);
    expect(helper).not.toBeNull();
    expect(helper!.textContent).toContain('ID:SECRET');
  });

  // ───────────────────────────────────────────────────────────────────────────
  // The model catalog: a provider is an ACCOUNT. Creating one also creates its first model and
  // capability; editing one changes the account only.
  // ───────────────────────────────────────────────────────────────────────────

  describe('model catalog', () => {
    const SAVED: InferenceProvider = {
      id: 'p-1', organizationId: null, name: 'Azure', kind: 'AzureOpenAI', endpoint: 'https://a.example',
      hasApiKey: true, apiKeyLastFour: 'wxyz', isEnabled: true, isSystem: false, timeoutSeconds: null,
      personalOAuthMode: false, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-01T00:00:00Z',
      models: [
        { id: 'm-1', modelCode: 'gpt-4o', displayName: 'GPT-4o', isEnabled: true, capabilities: [] },
        { id: 'm-2', modelCode: 'whisper', displayName: 'Whisper', isEnabled: true, capabilities: [] },
      ],
    };

    const renderEdit = () =>
      render(
        <MantineProvider>
          <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} provider={SAVED} />
        </MantineProvider>
      );

    it('creates the provider with its first model and capability in one request', async () => {
      vi.mocked(createProvider).mockResolvedValue({} as never);
      render(
        <MantineProvider>
          <InferenceProviderForm opened onClose={vi.fn()} onSuccess={vi.fn()} />
        </MantineProvider>
      );
      fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Azure' } });
      fireEvent.change(screen.getByLabelText('Endpoint'), { target: { value: 'https://a.example' } });
      fireEvent.change(screen.getByRole('combobox', { name: 'Deployment name' }), { target: { value: 'gpt-4o' } });
      fireEvent.change(screen.getByLabelText('Display name'), { target: { value: 'GPT-4o' } });
      fireEvent.click(screen.getByText('Set as default chat provider'));
      fireEvent.click(screen.getByRole('button', { name: 'Create' }));

      await waitFor(() => expect(createProvider).toHaveBeenCalled());
      expect(vi.mocked(createProvider).mock.calls[0][0]).toEqual({
        name: 'Azure',
        kind: 'AzureOpenAI',
        endpoint: 'https://a.example',
        apiKey: undefined,
        timeoutSeconds: undefined,
        isEnabled: true,
        personalOAuthMode: false,
        models: [
          {
            modelCode: 'gpt-4o',
            displayName: 'GPT-4o',
            isEnabled: true,
            capabilities: [{ capability: 'Chat', isDefault: true, isEnabled: true }],
          },
        ],
      });
    });

    it('edits the account only: kind read-only, no model or capability fields, and a note about every model', () => {
      renderEdit();
      expect((screen.getByRole('combobox', { name: /Kind/i }) as HTMLInputElement).disabled).toBe(true);
      expect(screen.queryByRole('combobox', { name: /Capability/i })).toBeNull();
      expect(screen.queryByLabelText('Display name')).toBeNull();
      expect(screen.getByText(/applies to every model under this provider \(2 models\)/)).toBeDefined();
    });

    it('keeps the stored key when the field is left blank', async () => {
      vi.mocked(updateProvider).mockResolvedValue({} as never);
      renderEdit();
      fireEvent.click(screen.getByRole('button', { name: 'Update' }));
      await waitFor(() => expect(updateProvider).toHaveBeenCalled());
      const [id, body, scope] = vi.mocked(updateProvider).mock.calls[0];
      expect(id).toBe('p-1');
      expect(scope).toBe('platform');
      expect(body.apiKey).toBeUndefined();
      expect(body).not.toHaveProperty('kind');
    });

    it('replaces the key with a typed one, and clears it with "" when asked to', async () => {
      vi.mocked(updateProvider).mockResolvedValue({} as never);
      renderEdit();
      fireEvent.change(screen.getByLabelText('API Key'), { target: { value: 'sk-new' } });
      fireEvent.click(screen.getByRole('button', { name: 'Update' }));
      await waitFor(() => expect(updateProvider).toHaveBeenCalledTimes(1));
      expect(vi.mocked(updateProvider).mock.calls[0][1].apiKey).toBe('sk-new');

      cleanup();
      renderEdit();
      fireEvent.click(screen.getByLabelText('Remove the stored key'));
      fireEvent.click(screen.getByRole('button', { name: 'Update' }));
      await waitFor(() => expect(updateProvider).toHaveBeenCalledTimes(2));
      expect(vi.mocked(updateProvider).mock.calls[1][1].apiKey).toBe('');
    });
  });
});
