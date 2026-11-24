import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { Figure } from '@/components/blog/Figure';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// The small-business guide (messaging brief 2026-11-11, persona `business`): three jobs, the recipe
// for each, the brand kit from a brand PDF, shapes, thumbnail and post text. Every ReelBolt plan
// figure comes from the plan catalog or from the constants the Terms and the pricing page already
// use; nothing here is a hand-written price. There is no customer, quote or time saving in this
// post, and the example notes are examples of what a reader might type, not anyone's real brief.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'video-for-your-small-business',
  title: 'A video for your shop, your launch or your offer, without hiring an editor',
  description:
    'Three videos a small business needs and the ReelBolt recipe for each: a shop video with titles, a narrated launch story and a testimonial reel. Limits included.',
  date: '2026-11-11',
  tags: ['guides'],
  readingMinutes: 10,
  faqs: [
    {
      q: 'Do I need to know how to edit video?',
      a: 'No. You add the clips you shot, pick a recipe, say what the video is for and press Run. An AI editor makes the first cut and a reviewer scores it before you see it. An editor is there if you want to change something yourself. It is labeled Early preview.',
    },
    {
      q: 'Can I make a video from photos of my shop, or from a text prompt?',
      a: 'Not on the free trial. No trial recipe turns only photos or only a prompt into a video. The recipes in this guide start from clips you filmed. AI-generated clips are part of the Pro plan and the plans above it.',
    },
    {
      q: 'Will the video use my brand colors and fonts?',
      a: 'If you import your brand guide and approve the result, ReelBolt uses that brand when it designs on-screen graphics such as titles and callouts. The import is saved as a draft and nothing uses it until you approve it. Check the first video against your guide.',
    },
    {
      q: 'Can I post the same video on every channel?',
      a: 'You choose one shape before each run: vertical, square, landscape or portrait. A finished video cannot be re-cut to another shape yet, so a second shape means running the recipe again, which uses credits again.',
    },
    {
      q: 'What does the free trial include?',
      a: `No card is required. A trial workspace starts with ${TRIAL_CREDITS} one-time credits, and when you verify your email your account receives a one-time grant of ${FIRST_VIDEO_GRANT_CREDITS} credits for a first video, which expires ${FIRST_VIDEO_GRANT_EXPIRY} after it is granted. Trial exports are ${TRIAL_RESOLUTION} with a watermark. Exports without the watermark start on the Creator plan.`,
    },
    {
      q: 'Does ReelBolt check the review in my screenshot?',
      a: 'No. A screenshot is shown as a picture. ReelBolt does not read or check the words in it, so it is up to you to show only real reviews and to ask the person before you use their words, name or face.',
    },
  ],
};

export default function VideoForYourSmallBusiness(): JSX.Element {
  return (
    <>
      <p>
        You know the shop needs a video. So does the launch next month, and the offer you are running
        this week. You run a business. You did not start it to become a video editor.
      </p>
      <p>
        This guide takes three videos a small business tends to need and matches each one to the
        ReelBolt recipe that makes it. It also covers setting up your brand from a PDF, picking the
        shape for each channel, and making the cover picture and the post text. We make ReelBolt, so
        read it with that in mind. The limits are in here too, and so is what needs a paid plan.
      </p>

      <Callout title="What you need">
        <p>
          A handful of clips you already shot. A phone is fine. For the testimonial reel, also a
          screenshot of a real review or message. An email address. No card.
        </p>
        <p>
          Optional: your brand or style guide as a PDF. ReelBolt is in public alpha, so expect some
          rough edges.
        </p>
      </Callout>

      <h2 id="before-and-after">Before and after, in words</h2>
      <p>
        Before: a camera roll full of clips, a blank timeline, and a weekend gone before anything is
        ready to post.
      </p>
      <p>
        After: a finished cut with the dead air gone, your words on screen, in the shape the channel
        wants. You watch it, ask for changes, and post it.
      </p>
      <p>
        We are not going to put a number on that difference. We have no customer results to quote
        yet, and we will not invent a time saving. What we can say is how long a run takes: minutes
        for a small job, sometimes half an hour for a big one. You can leave the page while it works.
      </p>

      <h2 id="how-a-recipe-works">How a recipe works</h2>
      <p>
        A recipe is a ready-made app for one kind of video. In a project, choose{' '}
        <strong>Start from a recipe</strong> and pick the one you want. Its page asks for a few
        things: your clips, a length, a shape and a box for notes. A <strong>Ready</strong> line says
        whether everything the recipe needs is in place, and names what is missing if not. The{' '}
        <strong>Run</strong> button shows the credits the run is expected to use, so you see the
        figure before you spend anything.
      </p>
      <Figure
        name="run-estimate"
        alt="The run dialog estimate for a workflow, showing the estimated credits, a rough-estimate note and the credits the workspace has available."
        width={1280}
        height={218}
        caption="The credit estimate for a run in a demo workspace, shown before the run starts."
      />
      <p>
        Each of the three recipes below ends with a review. The draft is scored before you see it,
        and a low score sends it back for another pass, for up to three rounds.
      </p>
      <p>
        If you have never made a video in ReelBolt, read{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> first.
        When you start from clips, your first project comes with the plain highlight edit. The three
        recipes here are the next step.
      </p>

      <h2 id="a-video-for-your-shop">Job 1: a video for your shop</h2>
      <p>
        <strong>The recipe:</strong> Footage edit with titles and callouts.
      </p>
      <p>
        <strong>What you film.</strong> Several different shots. The front of the shop, the counter,
        hands doing the work, and you saying one sentence to the camera. Several shots give the
        editor something to cut together. One long take can only be trimmed.
      </p>
      <p>
        <strong>What you get.</strong> Your clips cut down to the good parts, with the dead air, the
        false starts and the retakes left out. On top of the cut, ReelBolt adds on-screen text: a
        title, a name tag when someone is introduced, a callout for a key point. The graphics are
        placed where the picture has room, away from faces, at moments that match what is being said.
      </p>
      <p>
        <strong>What to write in the notes.</strong> The exact words you want on screen, and where.
        For example: &ldquo;A title with the shop&rsquo;s name at the start. A name tag when I
        introduce myself. One callout with our opening hours near the end.&rdquo; The designer is
        told to prefer no graphics over a cluttered picture, so name the ones you want.
      </p>
      <p>
        <strong>Good to know.</strong> This recipe takes longer than a plain edit, because each
        graphic is designed and rendered on its own. Ask for a few, not a dozen. When a graphic
        cannot be made as an animation, it falls back to plain text on a see-through box. A{' '}
        <strong>Captions</strong> switch, off by default, also puts the words spoken in your clips on
        screen. Read every word on screen before you post. The details are on the{' '}
        <a href="/docs/recipes/motion-graphics-overlays">titles and callouts</a> page of the guide.
      </p>

      <h2 id="a-launch-told-as-a-story">Job 2: a launch, told as a story</h2>
      <p>
        <strong>The recipe:</strong> Narrated product story. The name says product, but the product
        can be a new menu, a class or a service. What it needs is clips and a brief.
      </p>
      <p>
        <strong>What you film.</strong> Short clips of the thing you are launching, from a few
        angles. Clips with no sound of their own are fine, because the narration carries the story.
        Nobody has to talk to the camera.
      </p>
      <p>
        <strong>What you get.</strong> This recipe writes the story first and then cuts your clips to
        it. A writer reads your brief and what was seen in each clip, and writes a short script with
        a beginning, a middle and an end. Each line is tied to the clip it should play over. The
        clips are put in the script&rsquo;s order, the narration is spoken by a stock AI voice, and
        captions that follow the narration are burned into the picture.
      </p>
      <p>
        <strong>What to write in the brief.</strong> What the video is about, who it is for, the
        tone, and how long it should be. For example: &ldquo;Our new autumn menu, for people who live
        nearby. Warm and plain. About 30 seconds.&rdquo; The writer is told to say only what your
        brief and your footage support, with no made-up names, prices or numbers. So if a date or an
        offer must be said out loud, put it in the brief.
      </p>
      <p>
        <strong>Good to know.</strong> When the review sends a draft back, the story is rewritten as
        well as the cut. A cloned voice of a real person is possible only after that person&rsquo;s
        consent is recorded in ReelBolt. Listen to the whole narration before you post, the way you
        would proofread a flyer. The guide has more on{' '}
        <a href="/docs/workflows/templates#narrated-product-story-narrated-story">
          the narrated story
        </a>{' '}
        and on <a href="/docs/recipes/voiceover">voiceovers and captions</a>.
      </p>

      <h2 id="an-offer-backed-by-a-customer">Job 3: an offer, backed by a customer&rsquo;s words</h2>
      <p>
        <strong>The recipe:</strong> Testimonial reel from a talking-head clip and a screenshot.
      </p>
      <p>
        <strong>What you bring.</strong> A clip of one person talking to the camera: you, or a
        customer who agreed to be filmed. And a screenshot, such as a written review, a message or a
        price table. Put the clip in <strong>Your clips</strong> and the screenshot in{' '}
        <strong>A picture or video to show</strong>, which takes up to four files.
      </p>
      <p>
        <strong>What you get.</strong> The clip cut down, with the screenshot shown at a calm moment
        the editor chooses. Something the viewer has to read is shown full screen, long enough to be
        read, and it is fitted whole. A tall phone screenshot keeps its shape, with bars at the
        sides. Something like a logo sits in a corner while the speaker stays visible.
      </p>
      <p>
        <strong>What to write in the notes.</strong> What the clip is about and when the screenshot
        should appear. For example: &ldquo;Show the review when she talks about the delivery.&rdquo;
        With the setting <strong>Under a full-screen picture</strong> you can keep the speaker
        talking under the screenshot, fade the sound out for it, or leave the choice to the editor.
      </p>
      <p>
        <strong>Good to know.</strong> When the run finishes, a section called{' '}
        <strong>Pictures over the film</strong> lists what was shown and when. Anything offered but
        not used is listed under <strong>Not shown</strong> with the reason. ReelBolt shows the
        screenshot as a picture. It does not read or check the words in it. Show only real reviews,
        and ask the person before you use their words, name or face. See{' '}
        <a href="/docs/recipes/picture-over-video">Show a picture or video over the film</a>.
      </p>

      <h2 id="if-the-first-cut-is-not-right">If the first cut is not right</h2>
      <p>
        You stay in charge. On the recipe&rsquo;s page, a finished video offers{' '}
        <strong>Change one thing and re-run</strong>. Change the notes or a setting, see which parts
        are made again and what that costs, and get a new version. The first one is kept. You can
        also open the cut in the editor and trim or reorder it yourself. The editor is labeled Early
        preview, and some tools in it are still being finished.
      </p>

      <h2 id="brand-from-a-pdf">Set up your brand once, from a brand PDF</h2>
      <p>
        If you have a brand or style guide as a PDF, open the project&rsquo;s{' '}
        <strong>Brand &amp; product</strong> tab and import it. ReelBolt reads the text of the guide
        and pulls out the colors and the typography it describes. The file can be up to 20 MB, and it
        needs real text in it. A PDF with no text cannot be read.
      </p>
      <Figure
        name="brand-kit"
        alt="The Brand and product tab of a demo project, showing the approved brand colors and typography."
        width={1280}
        height={800}
        caption="The Brand and product tab of a demo project, with approved colors and typography."
      />
      <p>
        The result is saved as a draft. Nothing uses a draft until you approve it, so check the
        colors and fonts first and correct what is wrong. Importing and approving are free. A
        separate build step can fill gaps with an AI model. That part uses credits, and it shows you
        an estimate before it starts.
      </p>
      <p>
        No PDF? A design export works too: a zip saved from a design tool, a CSS file with your
        colors and fonts, or a design token file. The{' '}
        <a href="/docs/design-system">Brand and product profile</a> page lists every format.
      </p>
      <p>
        Once a brand is approved, ReelBolt uses it when it designs on-screen graphics, such as the
        titles and callouts in job 1. The run page shows which brand version a video used. The brand
        guides the graphics. It does not write your narration, so put the tone you want in the brief.
        Check the first video against your guide the way you would check a print proof.
      </p>

      <h2 id="shape-for-each-channel">Pick the shape for each channel</h2>
      <p>Every recipe here makes one of four shapes:</p>
      <ul>
        <li>
          <strong>Vertical (9:16).</strong> Fills a phone screen. This is the shape of short-video
          feeds such as TikTok, Instagram Reels and YouTube Shorts.
        </li>
        <li>
          <strong>Landscape (16:9).</strong> For your website, a regular YouTube upload or a screen
          in the shop.
        </li>
        <li>
          <strong>Square (1:1) and portrait (4:5).</strong> For posts that sit in a feed.
        </li>
      </ul>
      <p>
        Platforms change what they accept, so check each one before you choose. On a vertical video,
        captions are kept out of the bottom and the top of the frame, where those apps draw their own
        buttons and text.
      </p>
      <p>
        The limit to plan around: you choose the shape before the run, and a finished video cannot be
        re-cut to another shape yet. A second shape means running the recipe again, which uses
        credits again. Decide where the video is going first, and make that shape first.
      </p>

      <h2 id="thumbnail-and-post-text">The cover picture and the post text</h2>
      <p>
        A video still needs a cover and something to say next to it. Both are short jobs.
      </p>
      <p>
        <strong>Thumbnail and cover text</strong> is a recipe. It takes a frame from a video in your
        project, either the one it judges best or a time you choose, and puts an optional title on
        it, up to 90 characters. It uses very few credits. The picture is saved in the project. The{' '}
        <a href="/docs/recipes/thumbnail-and-cover-text">thumbnail page</a> has the details.
      </p>
      <p>
        <strong>Write post text</strong> is on a finished video. Pick where you will post: TikTok,
        Instagram Reels, YouTube Shorts, LinkedIn or X. You get one version per platform, cut to the
        length each one allows, with a title where the platform has one, the body and hashtags. It
        writes from the text ReelBolt already holds, such as your brief and the narration. An option
        called <strong>Also read the video</strong> adds the spoken words and a few still frames, and
        costs more credits. The price shows before anything is written.
      </p>
      <p>
        Post text uses a small amount of credits. ReelBolt does not post for you, and the hashtags
        are suggestions, not measured trends. See{' '}
        <a href="/docs/assistant/post-text">Post text for a finished video</a>.
      </p>

      <h2 id="free-trial-and-paid-plans">What the free trial covers, and what needs a paid plan</h2>
      <p>
        All three recipes, the brand import, the thumbnail and the post text work on the free trial.
        No card is required. A trial workspace starts with {TRIAL_CREDITS} one-time credits. When you
        verify your email, your account also receives a one-time grant of{' '}
        {FIRST_VIDEO_GRANT_CREDITS} credits for a first video, which expires{' '}
        {FIRST_VIDEO_GRANT_EXPIRY} after it is granted. If a step fails on our side, the credits held
        for it are refunded.
      </p>
      <p>
        Trial exports are {TRIAL_RESOLUTION} with a watermark. That is enough to judge the result. A
        video you put in front of customers will probably want the watermark gone, and that needs a
        paid plan. Here is what sits behind one:
      </p>
      <ul>
        <li>
          <strong>No watermark and sharper exports.</strong> Exports without the watermark start on
          Creator, at up to {CREATOR_RESOLUTION}. Pro goes up to {PRO_RESOLUTION}.
        </li>
        <li>
          <strong>Generated music and sound effects.</strong> These start on Creator. On any plan,
          another recipe adds background music from a file you upload. There is no built-in music
          library.
        </li>
        <li>
          <strong>AI-generated clips and b-roll.</strong> On Pro and the plans above it.
        </li>
        <li>
          <strong>Avatars.</strong> Short clips of a real person who has given recorded consent. On
          Pro and above, and still being rolled out, so they may not be switched on for you yet.
        </li>
      </ul>
      <p>
        The <Link href="/pricing">pricing page</Link> lists what each plan includes and what it
        costs.
      </p>

      <h2 id="honest-limits">Honest limits</h2>
      <ul>
        <li>
          ReelBolt is built for short videos, about 90 seconds or less. There is no recipe for a long
          presenter video.
        </li>
        <li>
          You bring the footage. On the free trial, no recipe makes a video from photos alone or
          from a text prompt alone.
        </li>
        <li>Several different shots make a real edit. One long take can only be trimmed.</li>
        <li>There is no recipe for image carousels, and no built-in music library.</li>
        <li>
          A finished video cannot be re-cut to another shape yet. Each shape is its own run.
        </li>
        <li>
          A screenshot in a testimonial reel is shown, not read. ReelBolt cannot tell you whether a
          review is real.
        </li>
        <li>
          It is a public alpha, so rough edges exist, and the editor is an Early preview.
        </li>
        <li>We have no customer stories to show you yet, and we will not invent any.</li>
      </ul>

      <h2 id="where-to-start">Where to start</h2>
      <p>
        Pick the one video you need most this month. Film five short clips for it on your phone,
        each a different shot. Create an account, add the clips to a project, choose the recipe from{' '}
        <strong>Start from a recipe</strong>, and write two sentences about who the video is for and
        what it should show. Then press <strong>Run</strong> and judge the result for yourself.
      </p>
      <p>
        If the video you want is not one of these three,{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> matches what you want to make to
        the recipe that makes it, and keeps its own list of what is not possible yet.
      </p>
    </>
  );
}
