import type { JSX } from 'react';
import type { PostMeta } from '@/lib/blog/types';

export const meta: PostMeta = {
  slug: 'enhance-a-clip',
  title: 'Enhance a clip in the ReelBolt editor, and check it before you keep it',
  description:
    'Sharpen, smooth slow motion, clean up noise, add motion blur or extend the frame on one clip. Compare before and after; nothing changes until you say so.',
  date: '2026-11-10',
  tags: ['product'],
  readingMinutes: 4,
  faqs: [
    {
      q: 'Is Enhance free?',
      a: 'Fast mode is free and runs on our servers. AI mode uses credits, and you see the price before you start. Extending the picture always uses AI.',
    },
    {
      q: 'Does Enhance change my clip without asking?',
      a: 'No. You see the new video next to your original first, and nothing changes in your timeline until you press Apply. Undo or Use original puts the old video back in one step.',
    },
    {
      q: 'Can I use AI mode on the free trial?',
      a: 'No. AI mode is not part of the free trial. Fast mode does not need a paid plan. Sizes above what your plan allows are greyed out, with a note saying what you can pick.',
    },
    {
      q: 'What happens to my video when I use AI mode?',
      a: 'The picture of your video is sent to an outside video service so it can be processed. ReelBolt asks you to agree the first time you choose AI in a project, and saves your answer for that project. Fast mode never sends anything.',
    },
    {
      q: 'Is there a limit on how long the source video can be?',
      a: 'Yes. If a video is too long, the message tells you the longest one we can enhance. Very short clips cannot be extended, and you are told the shortest length.',
    },
    {
      q: 'Is the editor finished?',
      a: 'Not yet. The timeline editor is an Early preview: your work is saved, but some tools may change. ReelBolt itself is in alpha.',
    },
  ],
};

export default function EnhanceAClip(): JSX.Element {
  return (
    <>
      <p>
        The timeline editor can now improve a single video clip. Select it, open <strong>Enhance</strong>,
        and pick what you want: a sharper picture, smoother slow motion, less noise, a natural motion blur,
        or a bigger frame.
      </p>
      <p>
        The part we care about most is what happens next. You see the result beside your original, and your
        timeline stays as it is until you decide.
      </p>

      <h2 id="what-you-can-do">What you can do</h2>
      <p>
        <strong>Enhance</strong> is in the Inspector when you select a video clip, and in the clip sheet on
        a phone. There are five choices.
      </p>
      <ul>
        <li>
          <strong>Sharper picture</strong> makes the picture bigger and crisper: 2x, 4x, to 1080p or to 4K.
        </li>
        <li>
          <strong>Smooth slow motion</strong> adds in-between frames so slowed-down video looks smooth: 2x,
          4x or 8x. AI slow motion goes up to 4x; 8x is Fast only.
        </li>
        <li>
          <strong>Clean up noise</strong> takes grain and speckle out of dark or low-light shots, Light or
          Strong. <strong>Keep film grain</strong> leaves some texture in if you want it.
        </li>
        <li>
          <strong>Motion blur</strong> adds a natural blur to fast movement: Light, Natural or Strong.
        </li>
        <li>
          <strong>Extend the picture</strong> adds new picture around the edges. Pick a shape (9:16, 1:1,
          16:9 or 4:5) or an amount for each side, and describe in plain words what the new edges should
          show.
        </li>
      </ul>

      <h2 id="fast-or-ai">Fast or AI</h2>
      <p>
        Sharper picture and smooth slow motion come in two ways. <strong>Fast</strong> is free and runs on
        our servers. <strong>AI</strong> uses credits and usually looks better. Clean up noise and motion
        blur are always Fast. Extending the picture always uses AI.
      </p>
      <p>
        Before you start, a line shows the price (Free, or about so many credits), the size of the new
        video, its frame rate, roughly how big the file will be and how long it takes.
      </p>
      <p>
        AI sends the picture of your video to an outside video service. So the first time you choose AI in
        a project, ReelBolt asks you to agree, and it remembers your answer for that project. Fast never
        sends anything and never asks.
      </p>

      <h2 id="compare-then-apply">Compare, then apply</h2>
      <p>
        While it works you see progress and a <strong>Cancel</strong> button, and you can keep editing. If
        you cancel, nothing changes.
      </p>
      <p>
        When the new video is ready, two players show <strong>Before</strong> and <strong>After</strong>{' '}
        over the part of the video your clip uses. <strong>Play both</strong> runs them together. If you
        like it, press <strong>Apply</strong>. If other clips in your timeline use the same video, they
        switch together, with the same length and trims. <strong>Undo</strong> or{' '}
        <strong>Use original</strong> puts everything back in one step.
      </p>
      <p>
        ReelBolt never applies a result on its own. If the clip has left your timeline by the time the work
        finishes, the result waits for you, and you can apply it to the clip or keep the new file in your
        media only. You can also run Enhance on a video from <strong>Media</strong>, which adds a new file
        and changes no clip.
      </p>

      <h2 id="plans-and-limits">Plans and limits</h2>
      <p>
        AI mode is not part of the free trial. Sizes your plan does not include are greyed out, with a note
        saying what your plan allows. Source videos have a maximum length, and the message tells you the
        longest we can enhance. If you run out of credits, it tells you how many you need. If a result
        comes back at a different length than the original, we do not use it and your credits are
        refunded.
      </p>

      <h2 id="early-preview">An early preview</h2>
      <p>
        The timeline editor is an Early preview, and ReelBolt is in alpha. Your work is saved, but some
        tools may change. Enhance is one we would like you to try on real clips and tell us where it falls
        short.
      </p>

      <h2 id="how-to-start">How to start</h2>
      <p>
        The <a href="/docs/editor/enhance-a-clip">Enhance a clip guide</a> goes through each choice and what
        to do when something goes wrong. For the rest of the editor, see{' '}
        <a href="/docs/editor">The timeline editor</a>.
      </p>
    </>
  );
}
