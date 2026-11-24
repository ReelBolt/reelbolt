import type { JSX } from 'react';
import type { PostMeta } from '@/lib/blog/types';

export const meta: PostMeta = {
  slug: 'whats-new-reelbolt-0-3',
  title: 'What’s new in ReelBolt 0.3 alpha: the smaller changes',
  description:
    'The smaller changes in ReelBolt 0.3 alpha: one card to change a video, project history, bitrate controls, zip upload, post text and more.',
  date: '2026-11-10',
  tags: ['product'],
  readingMinutes: 5,
  faqs: [
    {
      q: 'Is ReelBolt 0.3 still an alpha?',
      a: 'Yes. It is early access with no service-level guarantee, and features may change. The timeline editor is still labeled Early preview.',
    },
    {
      q: 'Does changing a finished video cost extra?',
      a: 'The Change this video card shows about how many credits the change costs before you press the button. You review one price and confirm once, whether you change the video, some steps, or both.',
    },
    {
      q: 'How long does ReelBolt keep my project’s earlier versions?',
      a: 'Automatic versions are kept for 30 days, up to 200 per project. Versions you save with a name stay until you delete them, up to 100 per project.',
    },
    {
      q: 'Will the post text tell me which hashtags are trending?',
      a: 'No. Hashtags are suggestions, from the model or from a web search when you ask for one. ReelBolt does not say a hashtag is trending, so check the platform before you rely on it.',
    },
    {
      q: 'Does writing post text cost credits?',
      a: 'Yes, a small amount, and the reply says how much. If your credits are too low, nothing is written and nothing is charged. Each person can ask for a limited number of texts per project per hour.',
    },
    {
      q: 'Is the exported file size exact?',
      a: 'No. The size shown next to the bitrate settings is approximate, because the real file depends on your video.',
    },
  ],
};

export default function WhatsNewReelbolt03(): JSX.Element {
  return (
    <>
      <p>
        ReelBolt 0.3 alpha is out. The bigger features get their own posts. This one collects the smaller
        changes that make everyday work easier, each with a link to the guide that covers it. It is still an
        alpha, so details may change.
      </p>

      <h2 id="change-this-video">One card to change a finished video</h2>
      <p>
        On a finished run’s page, the <strong>Change this video</strong> card holds everything you can
        change. Describe what should be different in <strong>The video itself</strong>, limit it to some
        scenes with <strong>Only these scenes</strong>, or pick an earlier version to start from. You see
        one price and confirm once. Your current video is kept.{' '}
        <a href="/docs/workflows/request-changes">Request changes</a> has the details.
      </p>

      <h2 id="typed-app-inputs">Fields that match what they ask for</h2>
      <p>
        An app’s own fields come in the kinds you would expect: text, number, length, choice, switch, shape
        and model. Required fields are checked before you run, and the price above <strong>Run</strong>{' '}
        follows your choices. See <a href="/docs/workflows/app-inputs">App fields</a>.
      </p>

      <h2 id="recipes">Recipes, matched to what you want to make</h2>
      <p>
        A recipe is a ready-made app you pick under <strong>Start from a recipe</strong>. The guide{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> groups them by goal, such as promos from
        your code, footage edits, subtitles, audio and cover pictures, shows what each one needs, and lists
        what ReelBolt cannot do yet.
      </p>

      <h2 id="project-history">Project history</h2>
      <p>
        The <strong>History</strong> tab keeps earlier versions of a project so you can compare them with
        now and restore one item or the whole project. Automatic versions are kept for 30 days, up to 200.
        Saved versions, with a name of 1 to 80 characters, stay until you delete them, up to 100. Rendered
        outputs, run results, credits and voices are not part of a version. Read{' '}
        <a href="/docs/projects/history">Project history and restore</a>.
      </p>

      <h2 id="export-bitrate">Export size controls</h2>
      <p>
        When you export a film, <strong>How to set the file size</strong> offers three modes: a steady
        look, look first with a size limit, or aim for a size. Video bitrate runs from 0.2 to 100 Mbps and
        audio from 64 to 320 kbps. The size shown is approximate. See{' '}
        <a href="/docs/editor/export-your-film">Export your film</a>.
      </p>

      <h2 id="zip-upload">Zip uploads unpack themselves</h2>
      <p>
        Drop a zip of your code onto a project and ReelBolt unpacks it into a folder named after the zip,
        skipping things a video never needs, such as dependencies and build output. When it has unpacked,
        the zip is removed. If unpacking fails, the zip stays and the Files tab shows why. See{' '}
        <a href="/docs/projects/give-your-code">Give ReelBolt your code</a>.
      </p>

      <h2 id="post-text">Post text for each platform</h2>
      <p>
        Ask the assistant to write the text you post with a finished video, for TikTok, Instagram Reels,
        YouTube Shorts, LinkedIn or X. You get one version per platform, cut to the length it allows.
        It uses a small amount of credits and has an hourly limit per person and project. Hashtags are
        suggestions, not measured trends. See <a href="/docs/assistant/post-text">Post text</a>.
      </p>

      <h2 id="cut-to-the-music">Cut to the music</h2>
      <p>
        The editor can find the beat of a music clip and move or split your cuts onto it, as described in{' '}
        <a href="/docs/editor/cut-to-the-music">Cut to the music</a>.
      </p>

      <h2 id="release-notes">And in the release notes</h2>
      <p>
        The <a href="/docs/releases/0.3.0-alpha">0.3.0-alpha release notes</a> also list the Italian
        interface, a guided start for new accounts, and asking for several changes at once. They also say
        what is not finished: the timeline editor is an Early preview, and some Italian text still appears
        in English.
      </p>
    </>
  );
}
