import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { Cite } from '@/components/blog/Cite';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// A guide for the nonprofit audience (messaging brief 2026-11-11, persona `nonprofit`): the narrated
// story, the testimonial reel, subtitles and translated subtitles, and the consent rules the product
// itself enforces for cloned voices and avatars. Recipe facts follow docs/user-guide/recipes
// (voiceover, subtitles, picture-over-video, run-settings, choose-a-recipe), workflows/templates.md
// and projects/avatars.md. Every ReelBolt plan figure comes from catalog() or from the constants the
// Terms and the pricing page already use; nothing here is a hand-written price. There is no customer,
// quote, statistic or time saving in this post. The example briefs are examples of what a reader might
// type, not anyone's real brief. The section on filming people is general good practice and says so;
// it is not legal advice. ReelBolt has no nonprofit plan or discount, and the post says that plainly.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'tell-your-cause',
  title: 'Tell your cause’s story when nobody on the team edits video',
  description:
    'For nonprofits and community groups: a narrated story from your clips, a testimonial reel, and subtitles in more than one language. Consent and costs included.',
  date: '2026-11-11',
  tags: ['guides'],
  readingMinutes: 13,
  faqs: [
    {
      q: 'Is there a free plan or a discount for nonprofits?',
      a: `No. ReelBolt has no nonprofit plan and no nonprofit discount. The free trial is the same for everyone: no card is required, a trial workspace starts with ${TRIAL_CREDITS} one-time credits, and when you verify your email your account receives a one-time grant of ${FIRST_VIDEO_GRANT_CREDITS} credits for a first video, which expires ${FIRST_VIDEO_GRANT_EXPIRY} after it is granted. Trial exports are ${TRIAL_RESOLUTION} with a watermark.`,
    },
    {
      q: 'Does someone on the team need to know how to edit video?',
      a: 'No. You add the clips you filmed, pick a recipe, say what the video is for and press Run. An AI editor makes the first cut and a reviewer scores it before you see it. An editor is there if you want to change something yourself. It is labeled Early preview.',
    },
    {
      q: 'Can ReelBolt make a video from our photos or from a written story alone?',
      a: 'Not on the free trial. No trial recipe turns only photos or only a text prompt into a video. The recipes in this guide start from video clips you filmed. AI-generated clips are part of the Pro plan and the plans above it.',
    },
    {
      q: 'Can we subtitle one video in several languages?',
      a: 'Yes, one language per run. The translated subtitles keep the timing of the original speech, and each run uses credits. The translation is made by an AI model, so ask someone who speaks the language to read it before you publish.',
    },
    {
      q: 'Whose voice narrates the video?',
      a: 'A stock AI voice, unless your project has a cloned voice of a real person. A voice can be cloned only after you confirm the consent statement and name the person, and that record is kept with the voice. Consent can be revoked at any time, and the voice stops working at once.',
    },
    {
      q: 'Can we use an avatar instead of filming someone?',
      a: 'Only of a real adult who agreed, and only on the Pro plan and the plans above it. Avatars are still being rolled out, so they may not be switched on for you yet, and a talking clip is about 15 seconds at most. An avatar is not a way to show a person who did not want to be filmed.',
    },
    {
      q: 'Does ReelBolt check that the people in our clips agreed to be filmed?',
      a: 'No. ReelBolt edits the clips you give it. Asking each person before you film, and before you publish, is up to you. ReelBolt records consent only for the two things it builds from a person: a cloned voice and an avatar.',
    },
  ],
};

export default function TellYourCause(): JSX.Element {
  return (
    <>
      <p>
        The stories are already there. The Saturday the hall filled up. The volunteer who has not
        missed a week in years. Some of it is even on film, in clips spread across a few phones. What
        is missing is someone who edits video, and nobody on the team does.
      </p>
      <p>
        This guide is for nonprofits and community groups. It covers what ReelBolt can make from clips
        you already have: a narrated story with captions, a testimonial reel, and subtitles in more
        than one language. It also covers asking people before you film them, what the product
        requires before it will copy a voice or a face, and what all of this costs. We make ReelBolt,
        so read it with that in mind.
      </p>

      <Callout title="The short version">
        <p>
          You bring video clips, filmed with the permission of the people in them, and a few sentences
          about what the video is for. On the free trial, no recipe makes a video from photos alone or
          from a text prompt alone.
        </p>
        <p>
          The videos are short, about 90 seconds or less. Everything in this guide except avatars runs
          on the free trial and uses credits.
        </p>
        <p>
          There is no nonprofit plan and no nonprofit discount. ReelBolt is in public alpha, so expect
          some rough edges.
        </p>
      </Callout>

      <h2 id="before-and-after">Before and after, in words</h2>
      <p>
        Before: clips on a few phones, a blank timeline, and a plan to &ldquo;do something with the
        footage&rdquo; that has been on the agenda since spring.
      </p>
      <p>
        After: a short video that says what you do and why it matters, with the words on screen, in
        the shape the channel wants. You watch it, ask for changes, and share it.
      </p>
      <p>
        How much time that gives back to a team like yours is something nobody has measured, so we
        will not guess. The one figure we can stand behind is the length of a run: minutes for a small
        job, sometimes half an hour for a big one, and nobody has to sit and watch it.
      </p>

      <h2 id="ask-before-you-film">Ask before you film</h2>
      <p>
        A video about a cause is a video about people. Before the first clip, a few habits are worth
        having. This is common sense, not legal advice. Rules about filming and publishing people
        differ from place to place, and if your organization has a safeguarding or media policy, that
        policy comes first.
      </p>
      <ul>
        <li>
          <strong>Ask each person, and say where the video will go.</strong> &ldquo;Can I film you for
          our page?&rdquo; is a different question from &ldquo;Can I film you?&rdquo; People can
          only agree to what they know about.
        </li>
        <li>
          <strong>Make no an easy answer.</strong> Nobody should have to explain why they would rather
          not be on camera. You can film hands, the room and the work itself, and still tell the
          story.
        </li>
        <li>
          <strong>Take extra care with children and with the people you serve.</strong> For a child,
          ask a parent or guardian. For someone who comes to you for help, remember that being seen
          there can be private in itself.
        </li>
        <li>
          <strong>Keep a note of who agreed, and to what.</strong> If someone changes their mind
          later, take the video down.
        </li>
      </ul>
      <p>
        ReelBolt cannot check any of this. It edits the clips you give it. The only consent it records
        is for the two things it builds from a person, a cloned voice and an avatar, and both are
        covered <a href="#voices-and-avatars">further down</a>.
      </p>

      <h2 id="how-a-recipe-works">How a recipe works</h2>
      <p>
        If recipes are new to you: each one is a ready-made app for a single kind of video, and you
        find them in a project under <strong>Start from a recipe</strong>. You fill in a short page
        with your clips, a length, a shape and some notes. A readiness line above <strong>Run</strong>{' '}
        names anything that is missing, and the button itself carries the expected credits, so the
        figure is in front of you before any are spent.
      </p>
      <p>
        The recipes here that make a new cut finish with a review. A reviewer scores the draft, and a
        low score means another pass before you see anything, three rounds at most. If you have never
        made a video in ReelBolt,{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks
        through the whole path from sign-up to a downloaded file.
      </p>

      <h2 id="a-narrated-story">Tell the story of your work, narrated</h2>
      <p>
        <strong>The recipe:</strong> Narrated product story. The name says product, but it works from
        whatever clips and brief you give it. For a cause, the &ldquo;product&rdquo; is the work you
        do.
      </p>
      <p>
        <strong>What you film.</strong> Short clips of the work, from a few angles: the place, people
        doing the thing, one detail that shows why it matters. Silent clips work, since the narrator
        tells the story and no one has to speak on camera. That helps when the people involved would
        rather not. If you have no footage yet,{' '}
        <Link href="/blog/five-clip-shot-list#nonprofits">the five-clip shot list</Link> has a list
        written for nonprofits.
      </p>
      <p>
        <strong>What you get.</strong> A short narrated video with captions. The story comes first: a
        script is written from your brief and from what each clip shows, and your clips are then
        arranged to follow it. A voice reads the script over them, and the captions on screen keep
        pace with the voice. The small-business guide goes through the same recipe under{' '}
        <Link href="/blog/video-for-your-small-business#a-launch-told-as-a-story">
          a launch, told as a story
        </Link>
        .
      </p>
      <p>
        <strong>What to write in the brief.</strong> Say what you do, who should watch, how it should
        sound and roughly how long it should run. For example: &ldquo;Our Saturday food pantry, for
        neighbors who might volunteer. Warm and plain. About 45 seconds. Say that we are open every
        Saturday morning and that new volunteers are welcome.&rdquo; The writer&rsquo;s instruction is
        to stay inside your brief and your footage and to make up no names and no numbers. A date, a
        place or a way to help that must be said out loud therefore has to be in the brief.
      </p>
      <p>
        <strong>Good to know.</strong> The writer is an AI model following that instruction, not a
        fact-checker. Listen to the whole narration before you share it, the way you would proofread
        a newsletter. If the review asks for another pass, the script is written again along with the
        cut, so a second draft can tell the story differently. If the narrator says a name wrongly, the project&rsquo;s <strong>Pronunciation</strong> tab
        lets you teach it the word. The docs have more on{' '}
        <a href="/docs/workflows/templates#narrated-product-story-narrated-story">
          the narrated story
        </a>{' '}
        and on <a href="/docs/recipes/voiceover">voiceovers and captions</a>.
      </p>

      <h2 id="a-testimonial-reel">Let one person say it: the testimonial reel</h2>
      <p>
        <strong>The recipe:</strong> Testimonial reel from a talking-head clip and a screenshot.
      </p>
      <p>
        <strong>What you bring.</strong> A clip of one person talking to the camera: a volunteer, a
        member of the community, or you. They agreed to be filmed and know where the video will be
        shown. And a picture to show while they talk, such as a thank-you message, a letter or a
        photo of a finished job. Put the clip in <strong>Your clips</strong> and the picture in{' '}
        <strong>A picture or video to show</strong>, which takes up to four files.
      </p>
      <p>
        <strong>What you get.</strong> The person&rsquo;s clip, trimmed to the parts worth keeping,
        with your picture brought in at a calm moment the editor picks. A letter or a message fills
        the screen, whole, and is held long enough to read. A logo or a small photo goes in a corner
        so the speaker is still in view. Turn on <strong>Captions</strong>, which starts off, to put
        the person&rsquo;s words on screen.
      </p>
      <p>
        <strong>What to write in the notes.</strong> Who is speaking, what about, and the moment the
        picture belongs to. For example: &ldquo;Show the thank-you note when she talks about the first
        winter.&rdquo; The setting <strong>Under a full-screen picture</strong> decides the sound
        while the picture is up: the person keeps talking, the sound fades out, or the editor chooses.
      </p>
      <p>
        <strong>Good to know.</strong> ReelBolt shows a screenshot as a picture. It does not read or
        check the words in it. So it is on you to show only real messages, to ask the person who
        wrote one before you use it, and to crop out phone numbers, addresses and other people&rsquo;s
        names before you upload it. After the run, the section <strong>Pictures over the film</strong>{' '}
        lists each picture and the moment it appeared, and <strong>Not shown</strong> lists anything
        you offered that was left out, with the reason. A single take gives the editor nothing to cut
        between, so if you can, film the person from a second angle or add a clip of them at work.
        See{' '}
        <a href="/docs/recipes/picture-over-video">Show a picture or video over the film</a>.
      </p>

      <h2 id="more-than-one-language">For a community that speaks more than one language</h2>
      <p>
        Many groups serve people who do not share a first language. There are four ways to handle
        that in ReelBolt, and they do different things.
      </p>
      <h3 id="subtitles">Subtitles in the language that is spoken</h3>
      <p>
        <strong>Subtitles for your video</strong> takes one finished video that is in your project
        and has speech in it. It returns a new video with the spoken words burned in. Nothing else
        about the video changes: no cuts, no music, no new voice. It also writes the same subtitles
        as two files, <code>subtitles.srt</code> and <code>subtitles.vtt</code>.
      </p>
      <p>
        Captions are how people who cannot hear the audio follow a video, and they help anyone
        watching with the sound off. <Cite n={[1]} /> Subtitles burned into the picture are always
        there and cannot be turned off. A subtitle file is uploaded next to the video on a platform
        that accepts one, and each viewer can show or hide it. <Cite n={[1]} />
      </p>
      <h3 id="translated-subtitles">Subtitles in another language</h3>
      <p>
        <strong>Subtitles in another language</strong> listens to what is said, translates it into
        the language you name and burns the translation in. The timing is always the original&rsquo;s.
        The translation replaces the words one sentence for one sentence, and a sentence the
        translation missed stays in the original language instead of disappearing. The speaker&rsquo;s
        own voice is untouched, which matters when the voice is part of the testimony.
      </p>
      <p>
        Each run makes one language. A second language is a second run on the same video, and it
        uses credits again.
      </p>
      <h3 id="narrate-in-another-language">A narrated story in another language</h3>
      <p>
        The narrated story has a language setting for the narration and the captions. To tell the
        same story to a second community, run the recipe again with the other language. Each run
        writes its own script, so the two versions may not match line for line.
      </p>
      <h3 id="dub">A dub</h3>
      <p>
        <strong>Dubbed video in another language</strong> makes an edit of clips that have speech in
        them, writes narration for that edit, translates it and speaks the translation in place of
        the original speech, with subtitles of what is spoken. There is no lip sync: the mouth on
        screen is the one from the original take, and the result says so. It is a translated
        narration, not a word-for-word interpretation. For a person telling their own story,
        translated subtitles are usually the better fit, because they keep that person&rsquo;s voice.
      </p>
      <h3 id="check-every-translation">Check every line, in every language</h3>
      <ul>
        <li>
          The subtitles come from automatic transcription. Accessibility guidance from the W3C says
          automatically generated captions usually need significant editing. <Cite n={[1]} /> Read
          every line, and look twice at names.
        </li>
        <li>
          The translation is made by an AI model. Ask someone who speaks the language to watch the
          result before the community does. A wrong word in a message about someone&rsquo;s life is
          worse than a late video.
        </li>
        <li>
          There is no published list of languages we have tested. Try yours on one short clip before
          you plan a campaign around it.
        </li>
        <li>
          The run page has no download button for the two subtitle files yet, although they are made
          on every run. To get subtitle files in the app today, open the video in the editor, which is
          an Early preview: its subtitles tab makes captions from a transcript, which uses credits,
          and downloads them as SRT or WebVTT.
        </li>
      </ul>

      <h2 id="voices-and-avatars">Voices and avatars: what ReelBolt asks before it copies a person</h2>
      <p>
        Two features build something from a real person. Both have consent built into the product.
      </p>
      <h3 id="voices">The narration voice</h3>
      <p>
        A narration is spoken in a stock AI voice, which belongs to no one on your team. The other
        option is a cloned voice: a copy of a real person&rsquo;s voice, built from recordings of
        them that are already in the project. Your director, say, or a long-time volunteer.
      </p>
      <ul>
        <li>
          Before anything is sent to make the voice, you confirm a consent statement and name the
          person. The statement, who confirmed it and when are kept with the voice.
        </li>
        <li>You choose how long the voice is kept, up to two years. After that it is deleted.</li>
        <li>
          <strong>Revoke consent</strong> stops every use of the voice at once.{' '}
          <strong>Delete</strong> removes the voice and the recordings it was made from, and reports
          what it removed.
        </li>
        <li>
          A run that asks for a cloned voice without valid consent fails. It does not quietly fall
          back to another voice.
        </li>
        <li>
          If a project has exactly one usable cloned voice, a narration with no voice chosen uses it.
          Tell the person that before they agree.
        </li>
      </ul>
      <p>
        The record in ReelBolt is your confirmation that the person agreed. It does not replace
        asking them. The{' '}
        <a href="/docs/recipes/voiceover#cloning-a-voice-with-consent">voiceover page</a> of the docs
        has the steps.
      </p>
      <h3 id="avatars">Avatars</h3>
      <p>
        An avatar is a likeness of a real person, made from their own pictures, short videos and
        voice recordings, that can appear in short generated clips. For a cause, the rules matter
        more than the feature.
      </p>
      <ul>
        <li>
          <strong>Only a consenting real person.</strong> Either you are the person, or you have
          their written permission, and you record that in ReelBolt when you make the avatar. There
          are no stock avatars.
        </li>
        <li>
          <strong>Adults only.</strong> The person must be 18 or older. Avatars of children are not
          allowed.
        </li>
        <li>
          <strong>Paid plans only.</strong> Avatars are on the Pro plan and the plans above it, not
          on the free trial. They are still being rolled out, so they may not be switched on for you
          yet.
        </li>
        <li>
          <strong>Short clips.</strong> A talking clip is about 15 seconds at most. There is no
          recipe for a long presenter video.
        </li>
        <li>
          <strong>Marked as AI-generated.</strong> Every clip of a person is marked as AI-generated
          in the video file, and shows a small label in the picture unless you chose to tell viewers
          another way.
        </li>
        <li>
          <strong>Consent can be withdrawn.</strong> Withdrawing it makes the avatar unusable at
          once. Videos you already made stay your files, so delete them yourself if the person asks.
        </li>
      </ul>
      <p>
        An avatar is not a way around a no. If someone did not want to be filmed, they did not agree
        to this either. The details are in{' '}
        <Link href="/blog/reelbolt-avatars">Avatars in ReelBolt</Link> and on the{' '}
        <a href="/docs/projects/avatars">avatars page</a> of the docs.
      </p>

      <h2 id="what-it-costs">What it costs, plainly</h2>
      <p>
        There is no nonprofit plan and no nonprofit discount. We would rather say that here than have
        you find out when the budget is already promised. The free trial is the same for everyone.
      </p>
      <p>
        No card is required. A trial workspace starts with {TRIAL_CREDITS} one-time credits. When you
        verify your email, your account also receives a one-time grant of{' '}
        {FIRST_VIDEO_GRANT_CREDITS} credits, so your first video is on us. It has to be used within{' '}
        {FIRST_VIDEO_GRANT_EXPIRY}. The narrated story, the testimonial reel, the
        subtitles, the translated subtitles and the dub all run on the trial, and each uses credits.
        The <strong>Run</strong> button shows the expected figure first. If a step fails on our side,
        the credits held for it are refunded.
      </p>
      <p>Three things to know before you plan around the trial:</p>
      <ul>
        <li>
          <strong>Trial exports are {TRIAL_RESOLUTION} with a watermark.</strong> That is enough to
          judge the result and to show a board. A video you publish will probably want the watermark
          gone. Exports without it start on the Creator plan, at up to {CREATOR_RESOLUTION}.
        </li>
        <li>
          <strong>Trial credits are one-time.</strong> They do not come back each month. If video
          becomes a regular part of your work, that means a paid plan.
        </li>
        <li>
          <strong>The trial has a small storage allowance.</strong> Upload the clips for one video,
          not the whole archive.
        </li>
      </ul>
      <p>
        The <Link href="/pricing">pricing page</Link> lists what each plan includes and what it
        costs. Make one video on the trial before anyone asks for a budget. Then you are deciding
        about something you have seen.
      </p>

      <h2 id="honest-limits">Honest limits</h2>
      <ul>
        <li>
          ReelBolt is built for short videos, about 90 seconds or less. It is not the tool for the
          ten-minute film at the annual dinner.
        </li>
        <li>
          You bring the footage. On the free trial, no recipe makes a video from photos alone or from
          a text prompt alone, and there is no recipe for image carousels.
        </li>
        <li>
          An edit needs several different shots to choose from. A single long take can be shortened
          and nothing more.
        </li>
        <li>
          There is no built-in music library. Background music comes only from a file you upload, in
          a separate recipe.
        </li>
        <li>
          You choose the shape before the run: vertical, square, landscape or portrait. A finished
          video cannot be re-cut to another shape yet, so each shape is its own run.
        </li>
        <li>A dub has no lip sync.</li>
        <li>
          ReelBolt does not check who is in your clips, whether they agreed, or whether a screenshot
          is real.
        </li>
        <li>
          ReelBolt is a public alpha with rough edges, and its editor is an Early preview.
        </li>
        <li>
          No other nonprofit&rsquo;s story is quoted in this guide, because we have none to show yet
          and will not make one up.
        </li>
      </ul>

      <h2 id="where-to-start">Where to start</h2>
      <p>
        Pick one story. The one you tell at every meeting is a good choice, because you already know
        how it goes. Ask the people in it. Film five short clips on a phone, each a different shot.
        Create an account, add the clips to a project, choose the narrated story from{' '}
        <strong>Start from a recipe</strong>, and write three sentences: what you do, who the video
        is for, and how they can help. Then press <strong>Run</strong> and judge the result for
        yourself.
      </p>
      <p>
        If the first cut misses the point, a finished video offers{' '}
        <strong>Change one thing and re-run</strong>. Change the brief or a setting, see what that
        costs, and get a new version without losing the first. And if the video you want is not in this
        guide, look it up in <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a>, which also
        lists what cannot be made yet.
      </p>

      <SourceList
        sources={[
          {
            id: 'source-1',
            label:
              'W3C Web Accessibility Initiative, Making Audio and Video Media Accessible: Captions/Subtitles',
            url: 'https://www.w3.org/WAI/media/av/captions/',
            accessed: '2026-11-11',
          },
        ]}
      />
    </>
  );
}
