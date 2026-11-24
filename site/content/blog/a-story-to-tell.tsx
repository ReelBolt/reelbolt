import type { JSX } from 'react';
import Link from 'next/link';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS } from '@/lib/legal-placeholders';

// The positioning post for the wider audience (messaging brief, 2026-11-11). Every ReelBolt plan
// figure comes from catalog(). The first-video grant is not a catalog field; its size comes from
// the constant the Terms and the pricing page already use.
export const meta: PostMeta = {
  slug: 'a-story-to-tell',
  title: 'You have a story to tell. You should not need to become a video editor.',
  description:
    'ReelBolt started with software launch videos. It is now for anyone with footage, a story and no time to edit. Who it is for, what it does today, and what it cannot do yet.',
  date: '2026-11-11',
  tags: ['product'],
  readingMinutes: 7,
  faqs: [
    {
      q: 'Who is ReelBolt for?',
      a: 'Anyone with footage, a story and no time to edit it: creators, small businesses, educators, nonprofits, software teams and agencies. Software teams are where ReelBolt started, and everything built for them is still there.',
    },
    {
      q: 'Do I need to know how to edit video?',
      a: 'No. You add the clips you shot, say what the video is for and press Run. An editor is there if you want to change something yourself. It is labeled Early preview.',
    },
    {
      q: 'Can ReelBolt make a video from only a text prompt or only photos?',
      a: 'Not on the free trial. The trial recipes start from footage you filmed, or from the source code of an app. AI-generated clips are part of the Pro plan and the plans above it.',
    },
    {
      q: 'What does the free trial include?',
      a: `The free trial needs no card and includes ${catalog('trial.creditsOneTime')} one-time credits. A new account that verifies its email also gets ${FIRST_VIDEO_GRANT_CREDITS} credits for a first video. Trial exports are ${catalog('trial.entitlements.maxOutputHeight')} with a watermark.`,
    },
    {
      q: 'How long can a ReelBolt video be?',
      a: 'ReelBolt is built for short videos, about 90 seconds or less. There is no recipe for a long presenter video.',
    },
    {
      q: 'Is ReelBolt finished?',
      a: 'No. It is a public alpha, so expect rough edges, and features may change. The timeline editor and the picture editor are labeled Early preview.',
    },
  ],
};

export default function AStoryToTell(): JSX.Element {
  return (
    <>
      <p>
        The footage is already on your phone. The trip you filmed, the lesson you recorded, the opening
        night at the shop, the day your volunteers cleaned the river bank. What is missing is the edit,
        and the edit is the part nobody has time for.
      </p>
      <p>
        You did not start any of this to become a video editor. ReelBolt is an AI video team for anyone
        with a story to tell and no time to tell it. This post says who that means today, what ReelBolt
        does for each of them, what it cannot do yet, and how to try it for free.
      </p>

      <h2 id="what-changed-and-why">What changed, and why</h2>
      <p>
        When we introduced ReelBolt in October, we called it an AI video team for software companies.
        That is where we started: software teams know a video would help a launch, and most launches
        still go out with a screenshot and a changelog. The first post,{' '}
        <Link href="/blog/hello-reelbolt">What ReelBolt is</Link>, describes that starting point.
      </p>
      <p>
        The team we built for that job spends most of its effort on work that has nothing to do with
        software. It watches raw footage, takes out the dead air and the false starts, cuts on full
        sentences, adds captions and reviews the result before you see it. None of that depends on what
        the footage shows. A recording of a cooking class needs the same care as a recording of a
        product demo.
      </p>
      <p>
        So we changed how we describe ReelBolt, to match what it already does. Nothing was taken away
        from software teams. They are now one audience among six, and the frame is wider: you have a
        story, and ReelBolt makes the video.
      </p>

      <h2 id="how-a-video-gets-made">How a video gets made</h2>
      <p>
        Drop in the clips you already shot and say what the video is for, in a sentence or two. Pick a
        recipe, which is a ready-made app for one kind of video, and press Run. Before the run starts
        you see an estimate of the credits it will use.
      </p>
      <p>
        The footage edit and promo recipes end with a review: the draft is scored before you see it, and
        a low score sends it back for another pass. A small job takes minutes, a big one sometimes half
        an hour, and you can leave the page while it works.
      </p>
      <p>
        The blank timeline is the hard part. A first cut you can react to is the easy part, and you stay
        in charge of it. Ask for a change to a line, a clip or the whole cut, or open the video in the
        editor and change it yourself. The editor is an Early preview, so expect rough edges there.
      </p>

      <h2 id="who-it-is-for">Who it is for, and what it does for each today</h2>
      <p>
        One paragraph per audience, limited to what works now. Unless a plan is named, the recipes
        below run on the free trial and use credits.
      </p>

      <h3 id="creators">Creators</h3>
      <p>
        You film all week and edit all weekend. The highlight edit takes one to 20 raw clips and returns
        a first cut with the dead air, false starts and retakes removed, cut on sentences. You choose the
        shape before the run: landscape, vertical, square or portrait. Subtitles can be burned into a
        finished video. The same run writes SRT and VTT files, but the run page has no download button
        for them yet. A cover picture with a title costs very few credits, and the assistant can
        write the post text for each platform for a small amount of credits. Music has to come from a
        file you upload, because there is no built-in music library.
      </p>

      <h3 id="small-businesses">Small businesses</h3>
      <p>
        You know the shop, the launch or the offer needs a video, and you run a business, not an edit
        suite. Give ReelBolt the clips you shot and a short brief. The footage edit with titles and
        callouts puts on-screen text on the cut. The narrated story recipe writes a narration from your
        brief, speaks it in a stock voice and adds captions. The testimonial reel starts from a
        talking-head clip and a screenshot, such as a written review. A brand kit can be read from a
        brand PDF or a design export, so your colors and fonts do not have to be typed in again.
      </p>

      <h3 id="educators">Educators</h3>
      <p>
        Your best lessons are stuck in long recordings. The highlight edit tightens a recording by
        removing pauses, false starts and retakes, and cuts toward the length you ask for. Subtitles go
        on in the language spoken, or translated into another language with the original timing. A dub
        replaces the speech with a translated voice. The mouth on screen does not change, because there
        is no lip sync. One limit matters here: a lesson filmed in a single long take can be trimmed,
        but it takes several different shots to make a varied edit, and ReelBolt is built for short
        results of about 90 seconds or less.
      </p>

      <h3 id="nonprofits">Nonprofits</h3>
      <p>
        You have the stories and the clips, and nobody on the team edits. The narrated story recipe
        turns your clips and a short brief into a narrated video with captions. The voice can be a stock
        voice, or a cloned voice of a real person once that person has recorded their consent. The
        testimonial reel starts from one person speaking to the camera and a screenshot. Subtitles can
        be burned into a finished video. All of these run on the free trial, so you can judge a first
        video before any budget is involved.
      </p>

      <h3 id="software-teams">Software teams</h3>
      <p>
        You ship every week and announce it with a screenshot. This is where ReelBolt started, and
        nothing here has gone away. Tell ReelBolt you start from an app you built, and your first
        project comes with a recipe that reads its source code and builds a short promo of its real
        screens. You can connect a GitHub repository so
        new code shows up in the project, and start and check runs from Claude Code. On paid plans you
        can bring your own AI provider account.
      </p>

      <h3 id="agencies">Agencies</h3>
      <p>
        First cuts eat the margin. ReelBolt gives you a first cut to react to, and your own editor can
        take it from there. You can keep a team workspace for each client, each with its own members,
        shared brand library, plan and credits. A guest review link lets a client watch one finished
        film without an account, leave notes at exact moments, and approve it or ask for changes. The
        editor can prepare a hand-off package for another editor, with the cut and your video files.
        That hand-off is an Early preview and we have not yet opened one in the other editors
        ourselves, so treat it as a first version. On paid plans there is an{' '}
        <Link href="/blog/reelbolt-public-api">API for starting runs from your own systems</Link>, which
        is still alpha.
      </p>

      <h2 id="what-it-cannot-do-yet">What it cannot do yet</h2>
      <p>These are the limits we would want to know before signing up.</p>
      <ul>
        <li>
          It is built for short videos, about 90 seconds or less. There is no recipe for a long presenter
          video.
        </li>
        <li>
          Several different shots make a real edit. One long take can only be trimmed.
        </li>
        <li>
          On the free trial, no recipe turns only a text prompt or only photos into a video. The trial
          recipes start from footage you filmed, or from the source code of an app.
        </li>
        <li>
          AI-generated clips and b-roll are on Pro and the plans above it. Music and sound effect
          generation starts on Creator. Avatars of a consenting real person, in short clips, are on Pro
          and above and are still being rolled out.
        </li>
        <li>There is no built-in music library, and no recipe for image carousels.</li>
        <li>
          A finished video cannot yet be re-cut to another shape. You choose the shape before the run.
        </li>
        <li>A dub changes the sound only. There is no lip sync.</li>
        <li>
          ReelBolt is a public alpha, so rough edges exist, and the editor is an Early preview.
        </li>
        <li>We have no customer stories to show you yet, and we will not invent any.</li>
      </ul>
      <p>
        The guide <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> matches what you want to
        make to the recipe that makes it, and keeps its own list of what is not possible yet.
      </p>

      <h2 id="how-to-start">How to start</h2>
      <p>
        The free trial needs no card. Your first video is on us: {FIRST_VIDEO_GRANT_CREDITS} credits
        when you verify your email, on top of the {catalog('trial.creditsOneTime')} trial credits. Trial exports are{' '}
        {catalog('trial.entitlements.maxOutputHeight')} with a watermark. The first-video credits are
        granted once per person and expire, and the app shows the date. In a few cases, such as a
        sign-up from a disposable email address, they are not granted and the trial still works.
      </p>
      <p>
        From there it is three steps: add your clips, say who the video is for and what it should show,
        and press Run. Every run shows its estimate before it starts. If a step fails on our side, the
        credits held for it are refunded. <a href="/docs/basics">The basics</a> walks through a first
        video with screenshots.
      </p>
      <p>
        Paid plans remove the watermark and raise the resolution, to{' '}
        {catalog('creator.entitlements.maxOutputHeight')} on Creator and{' '}
        {catalog('pro.entitlements.maxOutputHeight')} on Pro. The plans and prices are on the{' '}
        <Link href="/pricing">pricing page</Link>.
      </p>
      <p>
        Start with the free trial, or <Link href="/contact">tell us what you are making</Link> and we
        will help you set it up. Then press play on something finished.
      </p>
    </>
  );
}
