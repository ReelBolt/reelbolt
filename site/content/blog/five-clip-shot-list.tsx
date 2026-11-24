import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS } from '@/lib/legal-placeholders';

// A filming guide for people who start from an idea and have no footage yet (messaging brief,
// 2026-11-11). The filming advice is general knowledge and cites nothing. Every ReelBolt figure
// comes from the plan catalog or from the constant the Terms and the pricing page already use;
// nothing here is a hand-written price. The recipe facts follow docs/user-guide/recipes
// (derush-edit, run-settings, subtitles, choose-a-recipe).
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'five-clip-shot-list',
  title: 'The five-clip shot list: film a story on your phone',
  description:
    'Only an idea and no footage yet? Film five ten-second clips on your phone: the opening, the person, the thing, the detail and the close. Then turn them into a video.',
  date: '2026-11-11',
  tags: ['guides'],
  readingMinutes: 8,
  faqs: [
    {
      q: 'Do I need a camera or a microphone?',
      a: 'No. A phone is enough for a short video. Clean the lens, face the light, film in a quiet room and keep the phone close to whoever is speaking.',
    },
    {
      q: 'How long should each clip be?',
      a: 'About ten seconds. Start recording, wait a moment, do or say the one thing the clip is for, wait a moment again, then stop. Five clips like that are less than a minute of footage, which is enough for a short video.',
    },
    {
      q: 'Should I film vertical or horizontal?',
      a: 'Decide before the first clip and keep it for all five. Hold the phone upright for a vertical video and sideways for a landscape one. In ReelBolt you pick the shape before the run, and a clip filmed in a different shape is cropped to fit, so part of the picture is lost.',
    },
    {
      q: 'Can ReelBolt make the video from my idea, without any footage?',
      a: 'Not on the free trial. No trial recipe turns only a text prompt or only photos into a video. You film the clips, and ReelBolt makes the edit from them. AI-generated clips are part of the Pro plan and the plans above it.',
    },
    {
      q: 'What if I only have one long take?',
      a: 'It can be trimmed: pauses, false starts and retakes come out. It cannot become a varied edit, because there is nothing else to cut to. That is why the list asks for five different shots.',
    },
    {
      q: 'What does it cost to try?',
      a: `The free trial needs no card and includes ${TRIAL_CREDITS} one-time credits. When you verify your email you also get ${FIRST_VIDEO_GRANT_CREDITS} credits for a first video. Trial exports are ${TRIAL_RESOLUTION} with a watermark. The run page shows the credits a run is expected to use before you start it.`,
    },
  ],
};

interface ShotList {
  id: string;
  audience: string;
  note: string;
  shots: readonly [string, string, string, string, string];
}

const SHOT_LABELS = ['The opening', 'The person', 'The thing', 'The detail', 'The close'] as const;

const SHOT_LISTS: readonly ShotList[] = [
  {
    id: 'creators',
    audience: 'Creators',
    note: 'A trip, a recipe, a workout, a room you rebuilt. The story is something you did.',
    shots: [
      'A slow look around the place where it happens.',
      'You, to the camera: one sentence on what this video is about.',
      'Your hands doing the main thing, up close.',
      'The one moment you most want people to see.',
      'You again: what you would tell a friend to try next.',
    ],
  },
  {
    id: 'small-businesses',
    audience: 'Small businesses',
    note: 'A shop, a new menu, a service, an offer. The story is what you sell and how to get it.',
    shots: [
      'The front of your place, or your product on a table, in daylight.',
      'You or a colleague, to the camera: what you make or do, in one sentence.',
      'The product or the finished result, up close.',
      'What a customer sees first: the door, the counter or the box being opened.',
      'You, to the camera: what you offer and how to get it.',
    ],
  },
  {
    id: 'educators',
    audience: 'Educators',
    note: 'One idea from a lesson or a course. The story is the one thing someone should be able to do afterwards.',
    shots: [
      'The board, the screen or the page you teach from.',
      'You, to the camera: the one thing this lesson teaches.',
      'Your hands showing the key step.',
      'A common mistake, shown once.',
      'You again: what to practice next.',
    ],
  },
  {
    id: 'nonprofits',
    audience: 'Nonprofits',
    note: 'A cause, an event, a day of work. The story is what you do and how people can help.',
    shots: [
      'The place where your work happens.',
      'You or a volunteer, to the camera: what you do, in one sentence.',
      'People doing the work, filmed with their permission.',
      'One thing that shows why it matters: a sign, a tool or a finished job.',
      'How people can help: the event, the page or the collection box.',
    ],
  },
  {
    id: 'software-teams',
    audience: 'Software teams',
    note: 'A product or a release. A screen recording counts as a clip, and it is usually sharper than a phone pointed at a monitor.',
    shots: [
      'The old way people do this today, shown once.',
      'You, to the camera: who the product is for, in one sentence.',
      'Your product on its screen, doing its main job once.',
      'One feature up close, used slowly.',
      'The last screen people should remember: the result or the sign-up page.',
    ],
  },
];

export default function FiveClipShotList(): JSX.Element {
  return (
    <>
      <p>
        You have the idea. A video about the shop, the lesson, the cause, the thing you built. What you
        do not have is footage, and &ldquo;go and film something&rdquo; is too vague to act on, so the
        idea stays an idea.
      </p>
      <p>
        A shot list fixes that. It is a short list of exactly what to point the phone at. This guide
        gives you one with five clips of about ten seconds each, a version of it for five kinds of
        story, the few filming habits that matter on a phone, and what happens to the clips once they
        are in ReelBolt, with its limits stated.
      </p>

      <Callout title="What you need">
        <p>
          A phone, a quiet spot with good light, and one sentence that says what the video is about. If
          you cannot write that sentence yet, write it first. Every clip on the list serves it.
        </p>
      </Callout>

      <h2 id="why-five-clips">Why five clips</h2>
      <p>
        Five clips of about ten seconds each come to less than a minute of footage. Ten minutes is a fair
        allowance for finding the spot, filming them and doing one or two again. If it takes you
        twenty, nothing is lost.
      </p>
      <p>
        The number matters less than the variety. An edit is made by cutting between shots that differ:
        a place, a face, a pair of hands, something small, an ending. Five different shots give an
        editor, human or AI, something to choose between. One long take gives it nothing to cut to.
      </p>

      <h2 id="the-five-clips">The five clips</h2>
      <p>Each clip has one job. Film them in any order.</p>
      <ol>
        <li>
          <strong>The opening.</strong> Where the story happens. Stand a few steps back and show the
          place: the street, the kitchen, the classroom, the desk. Nobody has to speak.
        </li>
        <li>
          <strong>The person.</strong> Someone looking at the camera and saying one sentence: what this
          is, or who it is for. Usually that person is you.
        </li>
        <li>
          <strong>The thing.</strong> The subject of the video, in use. Hands kneading the dough, the
          product being switched on, the step being solved on the board.
        </li>
        <li>
          <strong>The detail.</strong> Move closer than feels natural and film one small thing: a
          label, a texture, a tool, a number on a screen. This is the clip people skip, and it is the
          one that makes a cut look considered.
        </li>
        <li>
          <strong>The close.</strong> What you want people to do or remember. Say it to the camera in
          one sentence, or show it: the door, the sign-up page, the finished result.
        </li>
      </ol>

      <h2 id="one-list-for-each-kind-of-story">One list for each kind of story</h2>
      <p>
        The five jobs stay the same. What you point the phone at changes with the story. Pick the list
        closest to yours and change the nouns.
      </p>
      {SHOT_LISTS.map((list) => (
        <section key={list.id}>
          <h3 id={list.id}>{list.audience}</h3>
          <p>{list.note}</p>
          <ol>
            {list.shots.map((shot, index) => (
              <li key={SHOT_LABELS[index]}>
                <strong>{SHOT_LABELS[index]}.</strong> {shot}
              </li>
            ))}
          </ol>
        </section>
      ))}
      <p>
        Two notes on these lists. If other people appear in a clip, ask them first, and ask a parent or
        guardian when the person is a child. And software teams have a second route that needs no
        filming: ReelBolt has a recipe that reads the source code of an app and builds a short promo of
        its real screens. The{' '}
        <a href="/docs/recipes/promo-from-code">promo from your code</a> page covers it.
      </p>

      <h2 id="filming-basics">Filming basics that matter on a phone</h2>
      <p>None of this needs equipment. It needs a minute of attention before you press record.</p>
      <ul>
        <li>
          <strong>Choose vertical or horizontal on purpose.</strong> Decide where the video will be
          posted, then hold the phone upright for a vertical video or sideways for a landscape one.
          Keep it the same for all five clips.
        </li>
        <li>
          <strong>Put the light in front of the subject.</strong> Face a window. Do not stand with the
          window behind you, or the camera will expose for the glass and your face goes dark. Daylight
          from a window usually looks better than a ceiling lamp.
        </li>
        <li>
          <strong>Ten seconds per clip.</strong> Start recording, wait a moment, do the one thing, wait
          a moment again, then stop. The quiet moments at each end give the edit room to cut.
        </li>
        <li>
          <strong>One sentence per clip.</strong> Decide the sentence before you press record, and say
          only that. If you stumble, say it again or film the clip again. Taking out false starts
          and retakes is what the edit is for.
        </li>
        <li>
          <strong>Find a quiet room.</strong> Turn off the music, the television and the fan. The
          microphone is in the phone, so keep the phone within arm&rsquo;s length of whoever is
          speaking.
        </li>
        <li>
          <strong>Hold still.</strong> Use two hands and tuck your elbows in, or lean the phone against
          something. Wipe the lens with a soft cloth first.
        </li>
      </ul>

      <h2 id="from-five-clips-to-a-video">From five clips to a video in ReelBolt</h2>
      <p>
        The clips are now on your phone, and the blank timeline is still the hard part. This is the
        part ReelBolt does. The recipe for filmed clips is called{' '}
        <strong>Highlight edit from footage</strong>, and it runs on the free trial.
      </p>

      <h3 id="highlight-edit">The highlight edit</h3>
      <p>
        Create a project, upload the five clips, and open the recipe. It takes between 1 and 20 clips.
        You tell it the language spoken in them, a target length, a shape, and a note on what the video
        is for and what must stay in. The note is where your one sentence goes.
      </p>
      <p>
        An AI editor then chooses which parts to keep. Dead air, false starts and retakes are left
        out, which is why a second try at a sentence is worth filming. When the speech in your clips
        could be read, cuts are placed so they do not land inside a spoken word. The draft is scored
        before you see it, and a low score sends it back for another pass.
      </p>
      <p>
        The recipe offers 30, 60 or 90 seconds as a target. With five ten-second clips, choose 30.
        Footage cannot be stretched past what you filmed, and the edit removes material; it does not
        add any. The editor also decides the order of the clips, so if the video should open or end a
        certain way, say so in the note.
      </p>

      <h3 id="captions">Captions</h3>
      <p>
        The run page has a <strong>Captions</strong> switch, off by default. Switch it on and the words
        spoken in your clips are shown on screen. This is one more reason for one clear sentence per
        clip in a quiet room: captions are made from what the microphone heard.
      </p>
      <p>
        A separate recipe adds subtitles to a video that is already finished, in the language spoken
        or translated into another one. See{' '}
        <a href="/docs/recipes/subtitles">Add subtitles to a video</a>.
      </p>

      <h3 id="shapes">Shapes</h3>
      <p>
        You pick the shape on the run page: vertical, square, landscape or portrait. Vertical and
        landscape match the two ways of holding a phone. When the shape is taller or squarer than a
        clip, ReelBolt crops the clip around its subject, so part of the picture is lost. The editor
        is told the shape of each clip and prefers the ones that already match, which is the practical
        reason to film all five the same way.
      </p>
      <p>
        The shape is chosen before the run. A finished video cannot be re-cut to another shape yet, so
        a second shape means running the recipe again, which uses credits again.
      </p>

      <h3 id="running-it">Running it</h3>
      <p>
        The <strong>Run</strong> button shows the credits the run is expected to use before you press
        it. A small job takes minutes, a big one sometimes half an hour, and you can leave the page
        while it works. If a step fails on our side, the credits held for it are refunded.
      </p>
      <p>
        Our guide{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks
        through every screen, from sign-up to download. If you choose{' '}
        <strong>Just an idea so far</strong> when you first sign in, ReelBolt shows a five-clip list of
        its own for the kind of video you picked. It is not word for word the list above, and either
        one works.
      </p>

      <h2 id="the-limits">The limits, stated plainly</h2>
      <ul>
        <li>
          Several different shots make a real edit. One long take can only be trimmed. That is the
          reason this guide asks for five different clips and not one minute of talking.
        </li>
        <li>
          ReelBolt is built for short videos, about 90 seconds or less. It is not for a long presenter
          video.
        </li>
        <li>
          It does not film for you. On the free trial, no recipe turns only a text prompt or only
          photos into a video. AI-generated clips are on the Pro plan and the plans above it.
        </li>
        <li>
          The highlight edit uses the sound of your own clips. It has no narration, and there is no
          built-in music library. Other recipes add a narration with captions, or background music from
          a file you upload.
        </li>
        <li>
          Trial exports are {TRIAL_RESOLUTION} with a watermark. Paid plans remove the watermark and
          raise the resolution.
        </li>
        <li>
          ReelBolt is a public alpha, so rough edges exist. The editor you can open a cut in is labeled
          Early preview.
        </li>
      </ul>

      <h2 id="film-first">Film first, then decide</h2>
      <p>
        The five clips are worth filming whatever you edit them with. If you want to see what ReelBolt
        makes of them, the free trial needs no card. Your first video is on us:{' '}
        {FIRST_VIDEO_GRANT_CREDITS} credits when you verify your email, on top of the {TRIAL_CREDITS}{' '}
        trial credits. Plans and prices are on the <Link href="/pricing">pricing page</Link>.
      </p>
      <p>
        The details of the recipe are in the{' '}
        <a href="/docs/recipes/derush-edit">highlight edit</a> page of the docs, and{' '}
        <a href="/docs/recipes/run-settings">Run settings</a> explains each setting on the run page.
      </p>
    </>
  );
}
