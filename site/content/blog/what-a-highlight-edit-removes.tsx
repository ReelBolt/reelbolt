import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS } from '@/lib/legal-placeholders';

// A product explainer for the Highlight edit from footage recipe: what it takes out of raw clips,
// where it cuts, how a draft is scored, and where it does a poor job (messaging brief, 2026-11-11).
// Recipe facts follow docs/user-guide (recipes/derush-edit, recipes/run-settings,
// recipes/choose-a-recipe, workflows/execution-and-outputs, editor/clean-your-audio) and the
// customer-safe parts of docs/video-editing.md. Every ReelBolt figure comes from the plan catalog or
// from the constant the Terms and the pricing page already use; nothing here is a hand-written
// price. No amount removed, no time saved and no quality score is claimed anywhere: there is no
// measured figure to quote, and the before and after below is described in words only.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'what-a-highlight-edit-removes',
  title: 'Before and after: what a highlight edit actually removes',
  description:
    'What a ReelBolt highlight edit takes out of raw clips: long pauses, false starts and repeated takes. Where it cuts, how each draft is scored, and where it does a poor job.',
  date: '2026-11-11',
  tags: ['product'],
  readingMinutes: 11,
  faqs: [
    {
      q: 'Does a highlight edit remove every pause?',
      a: 'No. It removes dead air: long gaps while someone thinks, walks into frame or reaches for the phone. A short breath inside a sentence it keeps will stay, and a quiet shot where something is happening is judged on its own, not treated as a pause. If a quiet moment must stay in, name it in the notes.',
    },
    {
      q: 'How does it choose between several takes of the same line?',
      a: 'Shots that look alike and were filmed close together are grouped as takes of the same moment and ranked on measurements such as how steady the shot is, how well lit it is and how strong the sound is. When the words could be read, the editor also sees the same sentence said more than once. It keeps the version it judges best. That is a judgment, so if you know which take you want, say so in the notes.',
    },
    {
      q: 'Can a cut land in the middle of a word?',
      a: 'When the speech in your clips could be read, each cut is checked against the words. A cut that would land inside a word moves to the edge of that word, and to a nearby pause when there is one. If no safe place fits, the cut stays where it was and the run warns you. Without readable words, the edit only knows about pauses and shot changes, and a sentence can be cut short.',
    },
    {
      q: 'What happens if the draft scores low?',
      a: 'A low score sends the edit back to the editor with the comments of the reviewer, for up to three rounds in this recipe. If the last round still scores low, you get the video anyway and the run is marked Needs attention, with the score it reached and the score it was aiming for.',
    },
    {
      q: 'Where do I see what was removed from my own footage?',
      a: 'On the page of your own run. It has the parts that were kept and the reason the editor gave, the length of the finished video, the review score and comments, and a link that opens the cut on a timeline so you can see each cut. We do not publish an average, because the amount removed depends on what you filmed.',
    },
    {
      q: 'What footage does it handle badly?',
      a: 'One long take, which can be trimmed but gives the edit nothing to cut to. Noisy audio, which hides the pauses and the words the edit depends on. And footage with no clear subject, where nobody speaks and nothing in particular happens, so there is little to tell a good moment from a dull one.',
    },
    {
      q: 'Does the highlight edit work on the free trial?',
      a: `Yes. It uses credits, and the run page shows how many a run is expected to use before you start. The trial needs no card and includes ${TRIAL_CREDITS} one-time credits. Trial exports are ${TRIAL_RESOLUTION} with a watermark.`,
    },
  ],
};

export default function WhatAHighlightEditRemoves(): JSX.Element {
  return (
    <>
      <p>
        Every clip you film holds the video and a good deal that should not be in it: the breath before
        you start, the pause while you look for a word, the line you said three times because the first
        two came out wrong.
      </p>
      <p>
        <strong>Highlight edit from footage</strong> is the ReelBolt recipe that takes that material
        out. This post explains what it looks for, where it cuts, how each draft is scored before you
        see it, and which kinds of footage it handles badly.
      </p>
      <p>
        You will not find a number here for how much shorter your video gets. That depends on what you
        filmed, and we have not measured a figure that would mean anything for your clips. The honest
        figures are the ones from your own run, and a later section says where to find them.
      </p>

      <Callout title="Before and after, in words">
        <p>
          <strong>Before.</strong> A camera roll. A few clips, each with a slow start, long pauses and
          three tries at the same line.
        </p>
        <p>
          <strong>After.</strong> One tight cut. The dead air is gone, one version of the line is
          left, and the cuts sit between words, not inside them.
        </p>
      </Callout>

      <h2 id="the-before">The before: what is in a raw clip</h2>
      <p>
        Most of a raw clip is not the video. Picture someone filming a short explanation of what they
        do. They press record and walk back to their spot. They start a sentence, stop, and start it
        again. They say the line that matters three times. Between thoughts they look away and think.
        At the end they reach for the phone.
      </p>
      <p>
        None of that is a mistake. It is how people film. It is also the slow part of editing, because
        finding it means watching everything, and you did not start this to become a video editor.
      </p>

      <h2 id="what-the-edit-looks-for">What the edit looks for</h2>
      <p>Before anything is chosen, ReelBolt goes through every clip and measures it. It marks:</p>
      <ul>
        <li>where the sound drops away, which is how a pause is found;</li>
        <li>where the picture changes from one shot to the next;</li>
        <li>the words that are spoken, when the speech in your clips could be read;</li>
        <li>shots that look like takes of the same moment.</li>
      </ul>
      <p>
        Then an AI editor reads all of it, together with your notes, and chooses which parts to keep.
        Everything it does not choose is left out. The editor only points at pieces that were measured.
        It never types in a time. A separate, exact step makes the cuts at the measured positions, so a
        cut cannot drift because the editor guessed.
      </p>

      <ComparisonTable
        caption="What a highlight edit does to raw clips, described in words. No figures: yours are on your run page."
        columns={['Before: the raw clips', 'After: the cut']}
        rows={[
          {
            label: 'Pauses',
            cells: [
              'Long gaps while someone thinks, walks into frame or reaches for the phone',
              'Left out. A quiet shot where something is happening can stay',
            ],
          },
          {
            label: 'False starts',
            cells: [
              'A sentence begun, dropped and begun again',
              'The half sentence is left out and the finished thought is kept',
            ],
          },
          {
            label: 'Repeated takes',
            cells: ['The same line or the same shot, several times', 'One version is kept'],
          },
          {
            label: 'Cuts',
            cells: [
              'None yet',
              'Placed clear of spoken words, at a pause when one is close, when the speech could be read',
            ],
          },
          {
            label: 'Order',
            cells: [
              'The order you happened to film in',
              'Clips in the order the editor chose. Inside one clip, the order you filmed',
            ],
          },
          {
            label: 'Start and end',
            cells: [
              'Begins when you pressed record, ends when you stopped',
              'Opens on a shot or a spoken line, with a fade in and a fade out',
            ],
          },
          {
            label: 'Length',
            cells: ['As long as you filmed', 'Near the length you asked for, when the footage allows it'],
          },
        ]}
      />

      <h2 id="what-comes-out">What comes out</h2>

      <h3 id="long-pauses">Long pauses</h3>
      <p>
        Dead air goes first: the gap between two shots, the long silence in the middle of a clip, the
        seconds at either end while the phone is picked up or put down.
      </p>
      <p>
        Not every quiet moment is a pause. A shot where something is happening and nobody is talking
        can be the strongest moment in a clip, and the editor is told to judge it on its own. It can
        still get this wrong. A quiet shot you love can look like dead air, so if a moment must stay
        in, name it in the notes.
      </p>

      <h3 id="false-starts">False starts</h3>
      <p>
        When the words could be read, the editor sees each spoken phrase and whether it finishes a
        sentence. It prefers a complete thought to a fragment, so the half sentence before a restart is
        left out and the full one stays. It also tries to open the video on the start of a thought and
        to close it on a finished one.
      </p>

      <h3 id="repeated-takes">Repeated takes</h3>
      <p>Takes are found in two ways.</p>
      <ul>
        <li>
          <strong>By the picture.</strong> Shots that look alike and were filmed close together are
          grouped as takes of the same moment. They are ranked on measurements such as how steady the
          shot is, how well lit it is and how strong the sound is, and the top one is marked.
        </li>
        <li>
          <strong>By the words.</strong> When the tries happen inside one continuous shot, the editor
          sees them in the speech: the same sentence, said more than once.
        </li>
      </ul>
      <p>
        The edit keeps one version, the one the editor judges best. It prefers the marked take unless
        the words give it a reason to choose another. That is a judgment, and the editor was not there
        when you filmed. If you know which take you want, say so in the notes. Your notes come before
        its own preferences.
      </p>

      <h3 id="the-size-of-a-piece">The size of a piece</h3>
      <p>
        The editor works in pieces the size of a shot, a pause or a spoken phrase. It is not a
        filler-word remover. A single &ldquo;um&rdquo; in the middle of a sentence it keeps will
        usually stay in.
      </p>

      <h2 id="where-the-cuts-land">Where the cuts land</h2>
      <p>
        After the editor has chosen, each cut is checked against the words. A cut that would land
        inside a spoken word moves to the edge of that word, and to a nearby pause when there is one. A
        cut that was already clear of speech is left alone. If no safe place fits, the cut stays where
        it was and the run warns you.{' '}
        <Link href="/blog/edits-that-respect-speech">Footage edits that respect speech</Link> covers
        this in detail.
      </p>
      <p>
        Kept pieces that are too short to register are dropped. The joins between cuts are softened
        automatically where the footage calls for it, and the picture and the sound fade in at the
        start and out at the end. The ending fade is a switch you can turn off.
      </p>
      <p>
        All of this depends on the words. When the speech could not be read, the edit only knows about
        pauses and shot changes, and a sentence can be cut short. The recipe asks for the spoken
        language of your clips for this reason.
      </p>

      <h2 id="what-order-things-play-in">What order things play in</h2>
      <p>
        When you give it several clips, the editor decides which clip plays when, and it is expected to
        use more than one of them unless your notes say otherwise. Inside one clip, the kept parts play
        in the order you filmed them.
      </p>
      <p>
        So the edit can open on your second clip. By default it will not move the end of a clip in
        front of its beginning. A setting described on the{' '}
        <a href="/docs/recipes/derush-edit">highlight edit recipe</a> page lets the editor do that when
        your notes ask for it, for example to open on the strongest moment. It never shows the same
        moment twice.
      </p>
      <p>
        If you asked for a length, a cut that runs long is shortened toward it. The length is a
        target, not a guarantee. A short clip cannot be stretched past what you filmed, and the run
        tells you when the result came out much shorter than you asked.
      </p>

      <h2 id="scored-before-you-see-it">Scored before you see it</h2>
      <p>
        Every draft is scored before you see it. A reviewer gives the edit a mark out of 10. In this
        recipe a mark below 8 sends the edit back to the editor with the reviewer&rsquo;s comments, for
        up to three rounds.
      </p>
      <p>
        The mark is tied to things that were measured. A cut inside a word limits how high the score
        can go, and so does a lot of dropped speech when you did not set a length, whatever the
        reviewer thought of the edit.
      </p>
      <p>
        If the last round still scores low, you get the video anyway. The run is marked{' '}
        <strong>Needs attention</strong>, with the score it reached and the score it was aiming for. We
        would rather show you that than hide it.
      </p>
      <p>
        A score is a check, not a promise. We have not published quality scores for real footage, and
        we are not claiming any here.
      </p>

      <h2 id="your-own-before-and-after">Your own before and after is on your run page</h2>
      <p>
        The before and after that matters is yours. Every run keeps its own facts, and you can read
        them on the run page by opening each step to see what it produced:
      </p>
      <ul>
        <li>
          <strong>What was kept, and why.</strong> The parts of your clips the editor kept, with its
          explanation of the edit.
        </li>
        <li>
          <strong>How long the result is.</strong> Set that beside the length of the clips you put in,
          and you have your own before and after.
        </li>
        <li>
          <strong>Each cut, on a timeline.</strong> <strong>Open in Editor</strong> shows the finished
          cut as clips on tracks, so you can see where every cut fell.
        </li>
        <li>
          <strong>The review.</strong> The score and the reviewer&rsquo;s comments.
        </li>
        <li>
          <strong>Whether the words were read.</strong> If they were not, that explains most cuts that
          sound wrong.
        </li>
      </ul>
      <p>
        A yellow box at the top of the run page means something did not turn out as asked, such as a
        video much shorter than the length you chose. Read it before you share anything.
      </p>
      <p>
        Some of these facts are shown plainly, as the output of a step, not as a tidy summary.
        ReelBolt is in public alpha, and this is one of its rough edges.{' '}
        <a href="/docs/workflows/execution-and-outputs">Running workflows and finding the results</a>{' '}
        explains the run page.
      </p>

      <h2 id="when-it-does-a-poor-job">When it does a poor job</h2>
      <p>Three kinds of footage give a weak result. It is better to know before you spend credits.</p>

      <h3 id="one-long-take">One long take</h3>
      <p>
        A single continuous shot can be trimmed. The pauses, false starts and retakes still come out.
        But there is nothing else to cut to, so every removal is a jump inside the same framing, and
        there is no order to choose. It cannot become a varied edit.
      </p>
      <p>
        Several different shots give the editor something to choose between.{' '}
        <Link href="/blog/five-clip-shot-list">The five-clip shot list</Link> is a short way to film
        them.
      </p>

      <h3 id="noisy-audio">Noisy audio</h3>
      <p>
        Pauses are found where the sound drops away, and words are found by listening. Wind, traffic
        or music under the voice hides both. A pause no longer sounds like a pause, and words are
        missed or heard wrongly. The editor then has less to go on, so dead air can stay in and a cut
        can land in a worse place.
      </p>
      <p>
        Filming in a quiet place, close to whoever is speaking, helps more than anything you can do
        afterwards. The editor does have a{' '}
        <a href="/docs/editor/clean-your-audio">Clean voice tool</a> for the sound of a clip, with free
        options for hum, wind, hiss and echo. We have not measured how much cleaner sound improves a
        highlight edit, so we make no claim about it.
      </p>

      <h3 id="no-clear-subject">No clear subject</h3>
      <p>
        The editor chooses from what was measured and what was said. In this recipe it is not given a
        description of what each shot shows. Footage where nobody speaks and nothing in particular
        happens, such as a slow walk around a room or a long look at a view, gives it little to tell a
        good moment from a dull one. It will still make a cut. It may not be the cut you would have
        made.
      </p>
      <p>
        The notes are the fix. Say what the video is about, who it is for and which moments must stay.
        One more thing to know: if you pick a shape that differs from how you filmed, the picture has
        to be fitted into the new frame, so part of it is lost or it is shown smaller. Film in the
        shape you plan to post.
      </p>

      <h3 id="other-limits">Other limits</h3>
      <ul>
        <li>
          <strong>Short videos.</strong> ReelBolt is built for videos of about 90 seconds or less. The
          recipe offers 30, 60 or 90 seconds as a target.
        </li>
        <li>
          <strong>One to twenty clips.</strong> A single clip can be up to an hour long. On the trial
          the storage allowance, listed on the <Link href="/pricing">pricing page</Link>, runs out
          well before that, so pick the clips for one video and leave the rest of the camera roll at
          home.
        </li>
        <li>
          <strong>An edit that keeps almost nothing.</strong> The run can stop with an error instead
          of handing you a sliver of video. Say in the notes what must stay, or ask for a longer
          length.
        </li>
        <li>
          <strong>It is a first cut.</strong> Expect to disagree with some of it.
        </li>
      </ul>

      <h2 id="what-to-do-with-the-first-cut">What to do with the first cut</h2>
      <p>
        You stay in charge. <strong>Edit in the editor</strong> opens the cut as a timeline, where you
        can trim or extend a clip, move it, split it and undo. The editor is labeled Early preview: it
        is still being finished, and some tools may change.
      </p>
      <p>
        If what you want to change is a setting, a finished video offers{' '}
        <strong>Change one thing and re-run</strong>. Change the length or your notes, check the price,
        and get a new run. Your first video is kept. A new run is a new edit, so its cuts may not match
        the first one.
      </p>

      <h2 id="try-it-on-your-own-clips">Try it on your own clips</h2>
      <p>
        The highlight edit is part of the free trial. No card is required. A trial workspace starts
        with {TRIAL_CREDITS} one-time credits, and when you verify your email your account also
        receives {FIRST_VIDEO_GRANT_CREDITS} credits, so your first video is on us. Trial exports are{' '}
        {TRIAL_RESOLUTION} with a watermark. The <Link href="/pricing">pricing page</Link> lists what
        each plan includes.
      </p>
      <p>
        The run page shows the credits a run is expected to use before you start. If a step fails
        because of a problem on our side, the credits held for it are refunded. A run takes minutes for
        a small job, sometimes half an hour for a big one, and you can leave the page while it works.
      </p>
      <p>
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks
        through the sign-up and the first run. The{' '}
        <a href="/docs/recipes/derush-edit">highlight edit recipe</a> page has every setting and what
        to check after a run.
      </p>
      <p>Then open your run page and read your own before and after.</p>
    </>
  );
}
