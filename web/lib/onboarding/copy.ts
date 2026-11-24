/**
 * Every user-visible string of the tour, the "Getting started" card and their menu entries, in ONE
 * place (so extraction and translation have a single source). Plain words, no internal names, and
 * the editor is always "Early preview". Functions interpolate; aria labels live here too.
 */
import type { ChecklistKey } from './state';
import type { TourStepId } from './guide-links';

export interface TourStepCopy {
  title: string;
  body: string;
}

/**
 * @deprecated The English reference copy, kept for tests (`copy.test.ts`, and a parity test against
 * `messages/en/onboarding.json`). The UI reads the `onboarding` catalogue through `useT`/`msg`.
 */
export const onboardingCopy = {
  menu: {
    help: 'Help',
    takeTheTour: 'Take the tour',
    readTheGuides: 'Read the guides',
  },
  tour: {
    steps: {
      welcome: {
        title: 'Welcome to ReelBolt',
        body: 'This menu takes you to your projects, the editor and your settings. On a phone it sits behind the menu button at the top.',
      },
      'new-project': {
        title: 'Start with a project',
        body: 'A project is a folder for one video job. Create one, then add your clips, pictures or audio to it.',
      },
      apps: {
        title: 'Pick an app and run it',
        body: 'Inside a project, the Apps tab lists what you can make. Choose one, set a few options and press Run.',
      },
      progress: {
        title: 'Watch it work',
        body: 'Each run shows its progress as it goes. You can leave the page: your video keeps going and the result is waiting when you are back.',
      },
      assistant: {
        title: 'Ask the assistant',
        body: 'Not sure where to start? Ask in your own words. The assistant can explain things, suggest next steps and check with you before it changes anything.',
      },
      editor: {
        title: 'Fine-tune in the editor',
        body: 'Open a finished video to trim, reorder and add text. The editor is an Early preview, so some things are still being polished.',
      },
      pictures: {
        title: 'Pictures and thumbnails',
        body: 'Make a cover picture or thumbnail for your video from the same place, then download it or use it in your video.',
      },
      credits: {
        title: 'Credits and your plan',
        body: 'Runs use credits. Open this menu to see your plan and balance, and to take this tour again any time.',
      },
    } satisfies Record<TourStepId, TourStepCopy>,
    /** The apps step when the Apps tab is switched off. */
    appsOff: {
      title: 'Pick an app and run it',
      body: 'Inside a project, the Workflows tab lists what you can run. Choose one, set a few options and press Run.',
    } satisfies TourStepCopy,
    learnMore: 'Learn more (opens in a new tab)',
    skip: 'Skip tour',
    back: 'Back',
    next: 'Next',
    finish: 'Finish',
    progress: (n: number, total: number) => `Step ${n} of ${total}`,
    announce: (n: number, total: number, title: string) => `Step ${n} of ${total}: ${title}`,
  },
  checklist: {
    title: 'Getting started',
    doneTitle: 'All done',
    doneBody: 'You have tried everything on the list. Nicely done.',
    progress: (done: number, total: number) => `${done} of ${total} done`,
    dismiss: 'Dismiss Getting started',
    closeDone: 'Close Getting started',
    showSteps: 'Show steps',
    hideSteps: 'Hide steps',
    how: 'How',
    howLabel: (label: string) => `How: ${label} (opens in a new tab)`,
    doneMark: ' (done)',
    freeFirstVideo: 'your first one is free',
    items: {
      create_project: 'Create a project',
      upload_file: 'Upload a file',
      run_video: 'Run your first video',
      open_result: 'Open the result',
      try_assistant: 'Try the assistant',
      open_editor: 'Open the editor (Early preview)',
    } satisfies Record<ChecklistKey, string>,
    assistantPrompt:
      'What can I do from the dashboard? Suggest the most useful next step given my projects and recent runs.',
  },
} as const;
