import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { Figure } from '@/components/blog/Figure';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// A walkthrough of the new-user path (docs/design/new-user-funnel.md, section 3) for the footage
// start: sign up, two welcome questions, the project made for you, clips, the brief, run, review,
// change, download. Every ReelBolt figure comes from the plan catalog or from the constants the
// Terms and the pricing page already use; nothing here is a hand-written price.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'first-video-in-one-sitting',
  title: 'Your first video in one sitting: from raw clips to a finished cut',
  description:
    'Sign up, answer two questions, drop in your clips, say what the video is for and press Run. What to expect from a first ReelBolt video, limits included.',
  date: '2026-11-11',
  tags: ['guides'],
  readingMinutes: 9,
  faqs: [
    {
      q: 'Do I need a card to make my first video?',
      a: `No card is required. A trial workspace starts with ${TRIAL_CREDITS} one-time credits, and when you verify your email your account receives a one-time grant of ${FIRST_VIDEO_GRANT_CREDITS} credits for the first video. The grant expires ${FIRST_VIDEO_GRANT_EXPIRY} after it is granted and is given once per person.`,
    },
    {
      q: 'How long does a first video take?',
      a: 'Minutes for a small job, sometimes half an hour for a big one. Longer clips take longer to go through. You can leave the page while the run works, and the dashboard shows it under runs in progress.',
    },
    {
      q: 'Can I make a video from a text prompt alone?',
      a: 'Not on the free trial. There is no trial recipe that turns only a prompt or only photos into a video. If you start from an idea, ReelBolt gives you a list of five short clips to film on your phone, and the edit is made from those.',
    },
    {
      q: 'Why does my trial video have a watermark?',
      a: `Trial exports are ${TRIAL_RESOLUTION} with a watermark. Paid plans export without the watermark and at higher resolutions. The pricing page lists what each plan includes.`,
    },
    {
      q: 'What happens to my credits if a run fails?',
      a: 'If a step fails because of a problem on our side, the credits held for it are refunded. If it fails because of the input, such as a file that cannot be read, or because you stopped it, the credits it actually used are kept. Resume carries on from the step that did not finish without charging again for the steps already done.',
    },
    {
      q: 'Can I change the shape of a finished video?',
      a: 'No. Pick vertical, square, landscape or portrait before you press Run. A finished video cannot be re-cut to another shape yet, so a second shape means running the recipe again.',
    },
  ],
};

export default function FirstVideoInOneSitting(): JSX.Element {
  return (
    <>
      <p>
        You filmed the clips. What stands between them and a video you can post is usually a blank
        timeline and an evening you do not have.
      </p>
      <p>
        This guide walks the whole path to a first video in ReelBolt, in the order the app takes you
        through it: create an account, answer two questions, drop in your clips, say what the video is
        for, press <strong>Run</strong>, watch the first cut, change something, download it. It also says
        plainly what the free trial gives you, what to do when a run fails, and what ReelBolt does not
        do yet.
      </p>

      <Callout title="What you need">
        <p>
          Between 1 and 20 clips you already shot, from a phone, a camera or a screen recording. An email
          address. No card.
        </p>
        <p>
          ReelBolt is in public alpha, so expect some rough edges. This guide follows the path for people
          who start from footage. Starting from an app you built is a different recipe, linked below.
        </p>
      </Callout>

      <h2 id="create-your-account">1. Create your account</h2>
      <p>
        Sign up with your name, your email and a password, or with a Google or GitHub account where the
        sign-up page offers one. No card is asked for.
      </p>
      <p>
        If you sign up with email, ReelBolt sends you one confirmation link. Open it and you are signed
        in. Runs that use credits stay switched off until your address is confirmed, so do this first.
        It can take a minute to arrive. If nothing does, check your spam folder and use{' '}
        <strong>Resend email</strong>.
      </p>
      <p>
        If your ReelBolt is run by your own administrator, they create your account and the first
        screens can differ. <a href="/docs/getting-started">Getting started</a> covers that case.
      </p>

      <h2 id="answer-two-questions">2. Answer two questions</h2>
      <p>The first screen after sign-in asks two things. Each is one click.</p>
      <ol>
        <li>
          <strong>What are you here to make a video for?</strong> Pick the closest one: your channel or
          your followers, your business, your lessons or your course, your cause or your community, your
          app or software product, your clients, or something else. The answer decides what is set up
          for you first. You can make anything later.
        </li>
        <li>
          <strong>What do you have to start from?</strong> Clips you already shot, an app or website you
          built, or just an idea so far.
        </li>
      </ol>
      <p>
        Choose <strong>Clips I already shot</strong> and ReelBolt creates a project called{' '}
        <strong>My first video</strong>, puts the <strong>Highlight edit from footage</strong> recipe in
        it, and opens it on the place where your clips go. You do not pick from a gallery, and the
        product tour does not start by itself. It stays in the Help menu for later.
      </p>
      <p>
        The other two answers work like this. <strong>Just an idea so far</strong> gives you a list of
        five short clips to film on your phone, about ten seconds each, written for the kind of video you
        chose. You film them and add them the same way. It does not generate a video from the idea.{' '}
        <strong>An app or website I built</strong> starts your project with a different recipe, which
        makes a promo video from the app&rsquo;s source code. The{' '}
        <a href="/docs/recipes/promo-from-code">promo from your code</a> page covers that path.
      </p>

      <h2 id="drop-in-your-clips">3. Drop in your clips</h2>
      <p>
        Drag your clips anywhere onto the project page, or press <strong>Upload files</strong>. When the
        upload finishes they are listed on the <strong>Files</strong> tab under <strong>Sources</strong>.
      </p>
      <Figure
        name="project-files"
        alt="The Files tab of the Acme Notes demo project, listing five uploaded source files with sizes and upload times."
        width={1280}
        height={800}
        caption="The Files tab of a demo project. Uploaded files are listed under Sources."
      />
      <p>
        The highlight edit works on 1 to 20 clips. The trial plan includes a small storage allowance,
        which the <Link href="/pricing">pricing page</Link> lists, so a handful of short clips is the
        right size for a first video.
      </p>
      <p>
        What you film matters more than how much. Several different shots give the editor something to
        choose between and to cut together. One long take can only be trimmed.
      </p>

      <h2 id="say-what-the-video-is-for">4. Say what the video is for</h2>
      <p>
        Open the recipe from the project&rsquo;s <strong>Apps</strong> tab, or follow the{' '}
        <strong>Your first video</strong> panel on the dashboard, which links straight to it. The page
        asks for a few things:
      </p>
      <ul>
        <li>
          <strong>Your clips.</strong> Pick them from the project&rsquo;s files.
        </li>
        <li>
          <strong>Spoken language.</strong> The language people speak in the clips. It helps ReelBolt
          find the words in them.
        </li>
        <li>
          <strong>Length.</strong> 30, 60 or 90 seconds are offered. It is a target, not a guarantee, and
          footage cannot be stretched past what you filmed.
        </li>
        <li>
          <strong>Shape.</strong> Vertical, square, landscape or portrait. Pick the one your channel
          wants now. A finished video cannot be changed to another shape afterwards.
        </li>
        <li>
          <strong>Notes.</strong> One or two sentences on who the video is for and what it should show.
        </li>
      </ul>
      <p>
        The notes are the part worth a minute of thought. The dashboard shows an example for the kind of
        video you chose, such as &ldquo;The best moments of this trip, for my followers&rdquo; or
        &ldquo;Our new autumn menu, for people who live nearby&rdquo;. If something must stay in, say so
        here. When the notes disagree with a setting, the setting wins.
      </p>
      <p>
        The page also has a style, which sets the pace and the mood, and a <strong>Captions</strong>{' '}
        switch, which is off by default. Switch it on to show the words spoken in your clips on screen.
        There is no narration setting and no music library. The highlight edit uses the sound of your
        own clips.
      </p>

      <h2 id="press-run">5. Press Run</h2>
      <p>
        A green <strong>Ready</strong> line says everything the recipe needs is in place. If something is
        missing, such as a clip you have not picked, the line names it. The <strong>Run</strong> button
        shows the credits the run is expected to use. Check the figure, then press it.
      </p>
      <p>Four things then happen, in this order:</p>
      <ol>
        <li>ReelBolt goes through your clips and finds the pauses, the shot changes and the words.</li>
        <li>An AI editor chooses which parts to keep. Dead air, false starts and retakes are left out.</li>
        <li>
          ReelBolt makes the cuts. When the speech in your clips could be read, cuts are placed so they
          do not land inside a spoken word.
        </li>
        <li>
          A reviewer scores the result. A low score sends the edit back to the editor with the
          reviewer&rsquo;s comments, for up to three rounds.
        </li>
      </ol>
      <p>
        How long it takes: minutes for a small job, sometimes half an hour for a big one. Longer clips
        take longer to go through. You can leave the page. The run carries on, and the dashboard shows it
        under runs in progress.
      </p>

      <h2 id="watch-the-first-cut">6. Watch the first cut</h2>
      <p>
        When the status reads <strong>Finished</strong>, the video is on the project&rsquo;s{' '}
        <strong>Renders</strong> tab, marked <strong>Final</strong>. A round the reviewer sent back leaves
        a <strong>Draft</strong> next to it. Press play on the final one.
      </p>
      <p>
        A yellow box at the top of the run page, or under the player, means something did not turn out
        as asked, for example a video much shorter than the length you set. Read it before you share
        anything.
      </p>
      <p>
        The blank timeline was the hard part. A first cut you can react to is the easy part, and
        reacting is the next step.
      </p>

      <h2 id="change-something">7. Change something</h2>
      <p>You stay in charge of the cut. There are two ways to change it.</p>
      <p>
        <strong>Change a setting and make it again.</strong> On the recipe&rsquo;s page, a finished video
        offers <strong>Change one thing and re-run</strong>. The dialog shows the settings this run used,
        such as the length, your notes and the captions switch. Change the ones you want. It lists which
        parts are made again and the credits that will cost, and the parts a change does not touch are
        kept from the first run for free. The result is a new run. Your first video is kept. The shape,
        the resolution and the style cannot be changed there, so for those you run the recipe again.
      </p>
      <p>
        <strong>Open the cut in the editor.</strong> The editor shows the edit as a timeline, where you
        can trim, reorder and add text. It is labeled Early preview, and some tools in it are still
        being finished. The editor changes the edit, not the finished file, so to get a new video you
        render it from the editor&rsquo;s export screen.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the timeline editor, with three video clips on track V1 and one audio clip on track A1."
        width={1280}
        height={214}
        caption="A demo timeline in the editor (Early preview), with the clips of a cut on their tracks."
      />
      <p>
        One thing to know before you look for it: the box where you type a change in plain words and get
        a new version works on animated videos, such as a promo made from code. It does not work on a
        footage edit. See <a href="/docs/workflows/request-changes">Request changes</a> for what can be
        changed where.
      </p>

      <h2 id="download-it">8. Download it</h2>
      <p>
        On the <strong>Renders</strong> tab, press <strong>Download</strong> to save the file. A new video
        can show <strong>Checking your video</strong> for a moment first. It appears by itself when the
        check is done.
      </p>
      <p>
        <strong>Share</strong> opens your device&rsquo;s share sheet or copies a link, but that link only
        opens for people signed in to your project. To send the video to someone else, download it and
        send the file.
      </p>

      <h2 id="what-the-free-trial-gives-you">What the free trial gives you</h2>
      <p>
        A trial workspace starts with {TRIAL_CREDITS} one-time credits. When you verify your email, your
        account also receives a one-time grant of {FIRST_VIDEO_GRANT_CREDITS} credits, so your first
        video is on us. The grant expires {FIRST_VIDEO_GRANT_EXPIRY} after it is granted and is given
        once per person. Your dashboard shows how many of those credits are left and the date to use
        them by. If the grant is not available for your account, the dashboard says so, and the trial
        credits still apply.
      </p>
      <p>
        Trial exports are {TRIAL_RESOLUTION} with a watermark. Paid plans export without the watermark
        and at higher resolutions. The <Link href="/pricing">pricing page</Link> lists what each plan
        includes.
      </p>

      <h2 id="if-a-run-fails">If a run fails</h2>
      <p>
        Runs fail sometimes. The run page shows <strong>Failed</strong> and which step stopped. What
        happens to the credits depends on why:
      </p>
      <ul>
        <li>If a step fails because of a problem on our side, the credits held for it are refunded.</li>
        <li>
          If it fails because of the input, such as a file that cannot be read, or because you stopped
          the run, the credits it actually used are kept.
        </li>
      </ul>
      <p>
        <strong>Resume</strong> carries on from the step that did not finish. Steps that already finished
        are not run again and not charged again. <strong>Run again</strong> starts over from the first
        step. If you cannot tell why a run failed, open the assistant and ask &ldquo;Why did my last run
        fail?&rdquo;.
      </p>
      <p>Two problems are common on a first edit, and both have a fix:</p>
      <ul>
        <li>
          <strong>The run stops because no clip was chosen.</strong> Pick your clips on the recipe&rsquo;s
          page before you run.
        </li>
        <li>
          <strong>The edit keeps almost nothing, or fails its check.</strong> Say in the notes what must
          stay, or ask for a longer length.
        </li>
      </ul>

      <h2 id="how-to-get-a-better-cut">How to get a better cut</h2>
      <ul>
        <li>
          <strong>Film several different shots.</strong> A wide shot of the place, your hands doing the
          main thing, you talking to the camera. Five different shots give the edit something to work
          with.
        </li>
        <li>
          <strong>Say who it is for and what it should show.</strong> &ldquo;A video about our
          shop&rdquo; is a topic. &ldquo;Our new autumn menu, for people who live nearby&rdquo; is a
          brief the editor can cut to.
        </li>
        <li>
          <strong>Say what must stay.</strong> The editor removes what looks like dead air. If a quiet
          moment matters, name it in the notes.
        </li>
        <li>
          <strong>Keep it short.</strong> ReelBolt is built for short videos, about 90 seconds or less.
        </li>
        <li>
          <strong>Give the camera a chance.</strong> Hold the phone still, find good light, and let each
          clip run a second longer than feels natural.
        </li>
      </ul>

      <h2 id="what-it-does-not-do-yet">What it does not do yet</h2>
      <ul>
        <li>
          It does not make a video from a text prompt alone, or from photos alone, on the free trial.
          You bring the clips.
        </li>
        <li>It cannot re-cut a finished video to another shape. You choose the shape before the run.</li>
        <li>
          The highlight edit has no built-in music and no narration. Other recipes add background music
          from a file you upload, or a narration with captions.
        </li>
        <li>It is not for long presenter videos. Short videos are what it is built for.</li>
      </ul>

      <h2 id="where-to-go-next">Where to go next</h2>
      <p>
        The <a href="/docs/recipes/derush-edit">highlight edit recipe</a> page has the details of what is
        removed and what to check after a run, and <a href="/docs/recipes/run-settings">Run settings</a>{' '}
        explains each setting on the page. When the first video is done,{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> lists what else you can make, such as
        subtitles for the finished video or a cover picture.
      </p>
    </>
  );
}
