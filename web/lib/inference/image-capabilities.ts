/**
 * The picture editor's per-job provider capabilities, as the admin pages need them: which capability
 * each AI tool resolves, a readable name for each, and the fal.ai endpoints each one can be pointed at.
 *
 * Mirrors the backend (`ImageJobCapabilities` and `FalImageCatalog` in `inference/src/ReelBolt.Shared/
 * Inference`). `FALAI_IMAGE_MODELS` is checked against the backend catalogue by a .NET test
 * (`ImageJobCapabilityTests.The_web_mirror_of_the_fal_catalogue_matches_it`), which reads this file, so
 * keep each list on one line, default first.
 *
 * Pure: no React.
 */

import type { InferenceProviderCapability, SelectableCapability } from '@/lib/types/inference-provider';

/** The capabilities the picture editor's AI tools resolve (subject cut-outs use `Segmentation`). */
export type ImageToolCapability =
  | 'ImageGeneration'
  | 'ImageEdit'
  | 'ImageErase'
  | 'ImageFill'
  | 'ImageExpand'
  | 'ImageBackgroundReplace'
  | 'ImageRelight'
  | 'ImageUpscale';

export const IMAGE_TOOL_CAPABILITIES: readonly ImageToolCapability[] = [
  'ImageGeneration',
  'ImageEdit',
  'ImageErase',
  'ImageFill',
  'ImageExpand',
  'ImageBackgroundReplace',
  'ImageRelight',
  'ImageUpscale',
];

/** The six capabilities split out of `ImageEdit`: each one a single editor tool. */
export const IMAGE_JOB_CAPABILITIES: readonly SelectableCapability[] = [
  'ImageErase',
  'ImageFill',
  'ImageExpand',
  'ImageBackgroundReplace',
  'ImageRelight',
  'ImageUpscale',
];

export function isImageToolCapability(capability: InferenceProviderCapability): capability is ImageToolCapability {
  return (IMAGE_TOOL_CAPABILITIES as readonly string[]).includes(capability);
}

/**
 * The fal.ai endpoints each picture capability can be pointed at, default first. A fal.ai row of a job
 * capability must name one of its own job's endpoints (the API refuses anything else); an `ImageEdit`
 * row may also keep an older editing endpoint, from before each tool had its own capability.
 */
export const FALAI_IMAGE_MODELS: Record<ImageToolCapability, readonly string[]> = {
  ImageErase: ['fal-ai/object-removal/mask', 'fal-ai/flux-pro/v1/erase'],
  ImageFill: ['fal-ai/flux-pro/v1/fill', 'ideogram/v4.5/edit'],
  ImageExpand: ['fal-ai/bria/expand', 'fal-ai/flux-2-pro/outpaint'],
  ImageBackgroundReplace: ['fal-ai/bria/background/replace'],
  ImageRelight: ['fal-ai/image-apps-v2/relighting', 'fal-ai/iclight-v2', 'bria/fibo-edit/relight'],
  ImageUpscale: ['fal-ai/seedvr/upscale/image', 'topaz/upscale/image/precision', 'topaz/upscale/image/generative'],
  ImageEdit: ['google/nano-banana-2.1/edit', 'blackforestlabs/flux-3/edit-image', 'openai/gpt-image-2.5/sunburst/edit', 'openai/gpt-image-2.5/flare/edit', 'bytedance/seedream/v5/pro/edit', 'fal-ai/qwen-image-edit-2511', 'bria/fibo-edit/edit', 'bria/fibo-edit/erase_by_text', 'fal-ai/image-editing/youtube-thumbnails'],
  ImageGeneration: ['ideogram/v4.5', 'google/nano-banana-2.1', 'blackforestlabs/flux-3/text-to-image', 'bytedance/seedream/v5/pro/text-to-image', 'fal-ai/ideogram/v3/generate-transparent'],
};

/** Which editor tool a picture capability serves, in the words the editor uses. */
export const IMAGE_TOOL_FOR_CAPABILITY: Record<ImageToolCapability, string> = {
  ImageGeneration: 'Create a picture',
  ImageEdit: 'Edit from a description',
  ImageErase: 'Remove objects',
  ImageFill: 'Fill an area',
  ImageExpand: 'Expand',
  ImageBackgroundReplace: 'Replace background',
  ImageRelight: 'Relight',
  ImageUpscale: 'Upscale',
};
