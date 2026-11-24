// The third-party AI providers a workflow can send your content to. ONE source of truth: the Terms,
// the Privacy Policy and the developer doc (docs/provider-data-handling.md) all read from here, so
// the list of vendors cannot differ from one page to another.
//
// WHAT THIS TABLE STATES. Each provider handles content under its own terms, so each row links to
// them. We do not summarise a vendor's terms: a paraphrase goes stale. The one exception is a
// vendor whose published terms state a right to train on what is sent. A user is entitled to be told
// that before running a workflow, so it is stated as a fact (`trainingWarning`) in the same words on
// every page.

export interface ProviderTermsLink {
  label: string;
  url: string;
}

export interface ProviderDataHandling {
  /** The vendor as a user would recognise it. */
  vendor: string;
  /** What a ReelBolt workflow sends this vendor, in a user's terms. */
  sent: string;
  /** The vendor's own terms, which govern what it does with the content. */
  terms: readonly ProviderTermsLink[];
  /** Set only where the vendor's published terms state a right to train on inputs and outputs. */
  trainingWarning?: string;
}

/** The vendors this product can send your content to. */
export const PROVIDER_DATA_HANDLING: ProviderDataHandling[] = [
  {
    vendor: 'Black Forest Labs (FLUX)',
    sent:
      'A frame of your footage, or your prompt, when a workflow uses a FLUX model. That includes an ' +
      'image generated or edited on a FLUX endpoint (a thumbnail cover, or a subject moved in the ' +
      'image editor); in that case we send the source image you chose plus the instruction.',
    terms: [{ label: 'FLUX API Service Terms', url: 'https://bfl.ai/legal/flux-api-service-terms' }],
    trainingWarning:
      'Their API terms state that the developer grants a fully paid, royalty-free, perpetual, irrevocable, worldwide, sublicensable licence over inputs and outputs, and that they may use inputs and outputs to train and improve their models.',
  },
  {
    vendor: 'fal.ai (subject detection, depth, image generation and editing, reference-to-video, talking-avatar video and lip-sync)',
    sent:
      'The image you ask to cut a subject out of, measure depth on, edit or extend, together with ' +
      'what you typed (a subject name such as "the person", an instruction, or a description of what to ' +
      'generate) and, for an erase or a fill, the mask you painted. The image and the mask are fetched ' +
      'by fal itself from short-lived links we create for that one request, never from public addresses. ' +
      'For a talking-avatar video: up to three of the avatar\'s reference pictures (to make a still of the ' +
      'person) and then that still with the narration audio (to animate it), sent with each request, with ' +
      'a request that fal does not keep them. Avatar and object reference pictures also go to fal for ' +
      'reference-to-video (a clip that shows avatars), for composing a scene picture, for pictures with ' +
      'avatars in the picture editor, for making a variant of an object, and for talking-avatar video and ' +
      'lip-sync; for cutting an object out of a video, stills of your video go to fal, and only stills that ' +
      'show no person. The models fal runs for these are made by other companies, and your content reaches ' +
      'them through fal.ai: Alibaba (Wan) and Google (Veo and the picture-edit model) for reference-to-video, ' +
      'scene pictures and pictures with avatars; MiniMax for lip-sync; Kuaishou (Kling) for a talking-avatar ' +
      'video, when the workspace administrator selects that model; Meta (Segment Anything) for cutting an ' +
      'object out of a video.',
    terms: [
      { label: 'Terms of Service', url: 'https://fal.ai/legal/terms-of-service' },
      { label: 'Privacy Policy', url: 'https://fal.ai/legal/privacy-policy' },
    ],
  },
  {
    vendor: 'MiniMax',
    sent:
      'A frame of your footage, a reference image, or your prompt, when a workflow uses a MiniMax model. ' +
      'That includes image generation and image-to-image editing through their image endpoint, where the ' +
      'reference image is fetched by the vendor from a link we give it. For a video that uses an avatar: the ' +
      'avatar\'s reference pictures, short reference videos and short speech samples, sent with each request.',
    terms: [{ label: 'Terms of Service', url: 'https://platform.minimax.io/protocol/terms-of-service' }],
  },
  {
    vendor: 'Higgsfield',
    sent:
      'Only when the workspace administrator selects a Higgsfield video model. Your prompt and, for a clip made ' +
      'from a first frame, that starting picture, which can be a scene picture composed from an avatar or an ' +
      'object, or a picture you chose. We upload the picture to Higgsfield\'s file storage and send the link. An ' +
      'object variant made from a video also uses the workspace\'s video model, so it goes the same way when ' +
      'that model is Higgsfield. A Higgsfield model is not used for a clip that shows avatars in reference mode.',
    terms: [
      {
        label: 'API billing and retention',
        url: 'https://docs.higgsfield.ai/docs/concepts/billing-and-retention.md',
      },
    ],
  },
  {
    vendor: 'OpenAI and Azure OpenAI',
    sent:
      'A prompt, a transcript, a frame, or a voice recording, depending on which capability a step uses. ' +
      'Also up to 20 downscaled frames and the text or transcript of your uploads and created files, for ' +
      'automatic safety screening, whichever capabilities a step uses. To make a transcript, up to 10 minutes ' +
      'of the audio goes to a transcription provider. ' +
      'Also, to check for a person, each picture of an object and one frame of each video of it, and, when you cut an ' +
      'object out of a video, up to six stills of that video and every frame that was cut out; those stills may show ' +
      'people, which is what the check is for. This applies when the workspace\'s vision model provider is this one.',
    terms: [
      { label: 'OpenAI business terms', url: 'https://openai.com/policies/business-terms/' },
      {
        label: 'Azure OpenAI data privacy',
        url: 'https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy',
      },
    ],
  },
  {
    vendor: 'Anthropic, Google (Gemini), DeepSeek',
    sent:
      'A prompt, a transcript, or a frame, depending on which capability a step uses. Also each picture of an ' +
      'object and one frame of each video of it, and, when you cut an object out of a video, up to six stills of ' +
      'that video and every frame that was cut out, to check for a person; those stills may show people, which is ' +
      'what the check is for. This applies when the workspace\'s vision model provider is one of these.',
    terms: [
      { label: 'Anthropic commercial terms', url: 'https://www.anthropic.com/legal/commercial-terms' },
      { label: 'Gemini API terms', url: 'https://ai.google.dev/gemini-api/terms' },
      {
        label: 'DeepSeek terms of service',
        url: 'https://cdn.deepseek.com/policies/en-US/deepseek-open-platform-terms-of-service.html',
      },
    ],
  },
  {
    vendor: 'Fish Audio',
    sent: 'The narration text, and for a cloned voice the reference recording you supply.',
    terms: [{ label: 'Terms', url: 'https://fish.audio/terms/' }],
  },
];

/** Vendors whose published terms state a training right over what you send. Named in the Terms. */
export const TRAINING_CLAIM_VENDORS = PROVIDER_DATA_HANDLING.filter(
  (row) => row.trainingWarning !== undefined,
).map((row) => row.vendor);
