import type { JSX } from 'react';
import type { PostMeta } from '@/lib/blog/types';

export const meta: PostMeta = {
  slug: 'edits-that-respect-speech',
  title: 'Footage edits that respect speech',
  description:
    'ReelBolt footage edits no longer cut through a spoken word, place sound effects only on real moments, and are reviewed against measured facts.',
  date: '2026-11-10',
  tags: ['product'],
  readingMinutes: 4,
  faqs: [
    {
      q: 'Can a cut still land inside a word?',
      a: 'Only when there is no safe place to move it. ReelBolt moves a cut that lands inside a word to the start or end of that word, and prefers a nearby pause. If no version fits without breaking the clip, the cut stays where it was and the run tells you with a warning.',
    },
    {
      q: 'Why did my interview get no sound effects?',
      a: 'Because nothing in it called for one. ReelBolt does not place sound effects where someone is speaking, and a talking-head interview is mostly speech. Zero effects is the correct result there.',
    },
    {
      q: 'What does the review step check now?',
      a: 'Facts it can measure: how many cuts fall inside a word, how much speech was dropped, and whether sound effects sit over speech. Those facts limit the score the reviewer can give, so a clipped word cannot pass on opinion alone.',
    },
    {
      q: 'Does Cut to the music cost credits?',
      a: 'Finding the beat is free. It reads only the music file. Check the result, then choose whether to move cuts onto the beat, split clips on it, add markers or fit clips to it. You can play the result before you apply it, and one Undo brings everything back.',
    },
    {
      q: 'What if the beat is hard to find?',
      a: 'The editor warns you that the beat is not clear, or that it changes during the track, and you can still go on. You can set the tempo to Half, As found or Double, and nudge every beat earlier or later in 10 ms steps.',
    },
  ],
};

export default function EditsThatRespectSpeech(): JSX.Element {
  return (
    <>
      <p>
        When ReelBolt edits real footage, it now listens before it cuts. Cuts no longer land inside a
        spoken word, the editor reads all of the speech before it chooses what to keep, and sound effects
        go only on moments that are really there.
      </p>
      <p>
        ReelBolt is in alpha, so this post sticks to what the editing does today and what it still leaves
        to you.
      </p>

      <h2 id="cuts-that-keep-the-word">Cuts that keep the word</h2>
      <p>
        A cut through the middle of a word clips it, and most people hear that at once. After every other
        step has moved the cuts (fitting shots, reaching a target length, syncing to a beat), ReelBolt
        checks each one against the transcript.
      </p>
      <ul>
        <li>A cut inside a word moves to the start or the end of that word.</li>
        <li>
          A cut that was already clear of speech is left alone, so the length and beat you asked for stay
          true.
        </li>
        <li>
          When a pause is close by, the cut prefers the pause, so it does not land in the middle of a
          sentence.
        </li>
        <li>
          If no safe version fits, the cut stays where it was and the run warns you. You decide what to do
          with it.
        </li>
      </ul>
      <p>
        Shots that come from generated clips, and footage without a transcript, are not touched.
      </p>

      <h2 id="reading-the-whole-video-first">Reading the whole video first</h2>
      <p>
        A long recording cannot be read in one go. So before choosing, the editor gets an outline that
        covers the whole video, section by section, and it can open any section or stretch of transcript
        to read it in full. A keep or a cut is chosen from what the whole recording says, not only from
        the part that fit on the page.
      </p>

      <h2 id="sound-effects-on-real-moments">Sound effects on real moments</h2>
      <p>
        ReelBolt does not place sound effects where someone is speaking. On a talking-head interview that
        often means none. That is the right result,
        and the review does not mark it down.
      </p>
      <p>
        If you do want effects, the{' '}
        <a href="/docs/recipes/music-sfx-and-grade">music, sound effects and colour grade recipes</a>{' '}
        explain what they need.
      </p>

      <h2 id="a-review-that-counts">A review that counts</h2>
      <p>
        Before, the review step judged an edit by reading about it. Now it is given measured facts:
        how many cuts fall inside a word, how much of the speech was dropped, and whether any sound effect
        sits over speech. The score is capped by those facts. An edit with a clipped word cannot get a top
        mark because the reviewer liked it.
      </p>
      <p>
        We have not published quality scores for real footage, and we are not claiming any here. What
        changed is what the review checks, not how well any one video turns out.
      </p>

      <h2 id="cut-to-the-music">Cut to the music in the editor</h2>
      <p>
        For music-led edits, the editor has <strong>Cut to the music</strong> in the timeline toolbar.
        Pick a music clip and press <strong>Find the beat</strong>. That step is free.
      </p>
      <p>
        Check the beat before you use it. Set the tempo to <strong>Half</strong>,{' '}
        <strong>As found</strong> or <strong>Double</strong>, nudge every beat earlier or later, and choose
        whether cuts go on every beat or every bar. If the beat is unclear, the editor says so. Then you
        can snap cuts to the beat, split clips on it, add markers or fit clips to the music. Play the
        result first, apply it, and undo it in one step if you change your mind.
      </p>
      <p>
        The <a href="/docs/editor/cut-to-the-music">Cut to the music guide</a> has every option.
      </p>

      <h2 id="try-it">Try it</h2>
      <p>
        The <a href="/docs/recipes/derush-edit">derush recipe</a> shows how to turn raw footage into an
        edit. The edit needs a transcript of the footage; without words, the editor only sees pauses and shot
        changes.
      </p>
    </>
  );
}
