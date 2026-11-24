import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { Figure } from '@/components/blog/Figure';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// A guide for creators: the first-cut workflow from a week of clips to a posted video. Recipe facts
// follow docs/user-guide (recipes/derush-edit, subtitles, thumbnail-and-cover-text, run-settings,
// music-sfx-and-grade, choose-a-recipe; assistant/post-text; editor/index, export-your-film,
// cut-to-the-music). Every ReelBolt figure comes from the plan catalog or from the constants the
// Terms and the pricing page already use; nothing here is a hand-written price. No time saving is
// claimed anywhere: there is no measured figure to quote.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'stop-editing-on-weekends',
  title: 'Film all week, edit all weekend? Let an AI team make the first cut',
  description:
    'Drop in the clips from your week, say what the video is, and get a reviewed first cut. Then add subtitles, shapes, a cover and post text. Limits included.',
  date: '2026-11-11',
  tags: ['guides'],
  readingMinutes: 10,
  faqs: [
    {
      q: 'Does ReelBolt choose the takes for me?',
      a: 'An AI editor chooses which parts of your clips to keep and leaves out dead air, false starts and retakes. A reviewer then scores the result, and a low score sends it back for another pass. It is a first cut, not a final one. Say in the notes what must stay, and change anything you disagree with in the editor.',
    },
    {
      q: 'Can I add music to the cut?',
      a: 'Yes, from a track you upload. There is no built-in music library. The Footage edit with background music recipe mixes your track under the voices, and the editor can move your cuts onto the beat of a music clip. Make sure you have the rights to the track.',
    },
    {
      q: 'How long can the video be?',
      a: 'ReelBolt is built for short videos, about 90 seconds or less. The highlight edit offers 30, 60 or 90 seconds as a target length. It will not edit a long-form video for you, but it can make the short one that goes with it.',
    },
    {
      q: 'Can I get vertical, square and landscape versions of the same video?',
      a: 'You pick one shape before each run: vertical, square, landscape or portrait. A finished video cannot be re-cut to another shape yet, so each extra shape is another run, which uses credits again and can come out with slightly different cuts.',
    },
    {
      q: 'Will my trial video have a watermark?',
      a: `Yes. Trial exports are ${TRIAL_RESOLUTION} with a watermark. Paid plans export without the watermark and at higher resolutions. The pricing page lists what each plan includes.`,
    },
    {
      q: 'Does ReelBolt post the video for me?',
      a: 'No. You download the file and post it yourself. ReelBolt can write the text that goes with the post for TikTok, Instagram Reels, YouTube Shorts, LinkedIn or X, but it does not publish anything.',
    },
  ],
};

export default function StopEditingOnWeekends(): JSX.Element {
  return (
    <>
      <p>
        You film all week. Then the weekend goes to a timeline: finding the good take, cutting the
        pauses, typing captions, exporting one file per platform. You did not start this to become a
        video editor.
      </p>
      <p>
        This guide shows another way to split the work. You bring the clips and say what the video is.
        In ReelBolt, an AI editor makes the first cut and an AI reviewer checks it before you see it.
        You react to a cut instead of facing a blank timeline. Then you add what a post needs:
        subtitles, the shape each channel wants, a cover and the post text. Where ReelBolt stops, this
        guide says so.
      </p>

      <Callout title="Before and after">
        <p>
          <strong>Before.</strong> A camera roll full of clips. A blank timeline. A weekend gone.
        </p>
        <p>
          <strong>After.</strong> A first cut with the dead air gone, in the shape your channel wants,
          ready for you to change.
        </p>
        <p>
          We are not putting a number on the time. It depends on your footage, and any
          figure would be a guess.
        </p>
      </Callout>

      <h2 id="drop-in-the-clips">1. Drop in the week&rsquo;s clips</h2>
      <p>
        Open a project and drag your clips anywhere onto the project page, or press{' '}
        <strong>Upload files</strong>. They show up on the <strong>Files</strong> tab, under{' '}
        <strong>Sources</strong>, once the upload is done.
      </p>
      <p>
        The highlight edit works on 1 to 20 clips, and one clip can run up to an hour. Choose the clips
        for one video, not the whole camera roll. On the trial, the limit you meet first is the storage
        allowance, which the <Link href="/pricing">pricing page</Link> lists, and any editor does
        better work when every clip belongs to the same story.
      </p>
      <p>
        What you filmed matters more than how much. A week of different shots gives the editor real
        choices, and a single unbroken take leaves it nothing to do but trim. If you are still planning
        the shoot, <Link href="/blog/five-clip-shot-list">the five-clip shot list</Link> is a short way
        to cover a story with a phone.
      </p>

      <h2 id="say-what-the-video-is">2. Say what the video is</h2>
      <p>
        On the project&rsquo;s <strong>Apps</strong> tab, under <strong>Start from a recipe</strong>,
        pick <strong>Highlight edit from footage</strong>. Its page asks which clips to use, what
        language is spoken in them, how long the video should be (30, 60 or 90 seconds, as a target
        and not a guarantee), which shape you want (step 6 has more on that) and for your notes.{' '}
        <Link href="/blog/first-video-in-one-sitting#say-what-the-video-is-for">
          Your first video in one sitting
        </Link>{' '}
        goes through each field.
      </p>
      <p>
        The notes are where you say what the video is. &ldquo;A video about my trip&rdquo; is a topic.
        &ldquo;Three days in Lisbon, for people planning a first visit. Keep the tram ride and the
        market&rdquo; is a brief an editor can cut to. If a moment must stay in, name it. The editor
        removes what looks like dead air, and a quiet shot you love can look like dead air.
      </p>
      <p>
        Two more settings are on the page: a style, for the pace and the mood, and a{' '}
        <strong>Captions</strong> switch that starts off. A setting always beats the notes, so do not
        ask for 90 seconds in the notes with the length set to 30.{' '}
        <a href="/docs/recipes/run-settings">Run settings</a> explains each one.
      </p>

      <h2 id="press-run">3. Press Run and let the first cut get made</h2>
      <p>
        When the page shows a green <strong>Ready</strong> line, nothing is missing. Look at the credit
        figure on the <strong>Run</strong> button, then press it.
      </p>
      <p>
        ReelBolt reads the clips for pauses, shot changes and words, an AI editor picks what stays, and
        the cuts are made between spoken words wherever the speech could be read. A reviewer then scores
        the cut and can send it back to the editor, up to three times, before you see it.{' '}
        <Link href="/blog/first-video-in-one-sitting#press-run">Your first video in one sitting</Link>{' '}
        lists those steps one by one.
      </p>
      <p>
        That is the team in this recipe: an editor that chooses and a reviewer that checks. A second
        recipe, <strong>Highlight edit, decided by an editing team</strong>, has three AI editors
        discuss the cut before a lead editor decides. It uses more credits and takes longer, and it is
        part of the Pro plan and the plans above it, not the free trial.
      </p>
      <p>
        How long a run takes: minutes for a small job, sometimes half an hour for a big one. Nothing
        needs you while it works. Close the page if you like, find the run on the dashboard later, and
        go plan the next video.
      </p>

      <h2 id="watch-the-first-cut">4. Watch the first cut</h2>
      <p>
        A finished run puts the video on the project&rsquo;s <strong>Renders</strong> tab with a{' '}
        <strong>Final</strong> mark. The edit is also added to the project&rsquo;s files, where a later
        recipe can pick it as its video. If a yellow box sits above the run or under the player,
        something did not turn out as asked, so read it before you share anything.
      </p>
      <p>
        Watch it once the way a viewer would, without stopping. Then watch it again and write down what
        you would change. This is the part that needs you. The editor did not live your week, so it can
        keep a take you find flat or drop a pause that was the point. A first cut is something to
        disagree with, and that is easier work than a blank timeline.
      </p>

      <h2 id="add-subtitles">5. Add subtitles</h2>
      <p>There are two ways, and which one fits depends on when you decide.</p>
      <p>
        <strong>Before the run.</strong> Switch on <strong>Captions</strong> on the highlight
        edit&rsquo;s page. The words spoken in your clips are shown on screen in the finished cut. If
        you already know the video needs captions, this is the short path: one run, one video.
      </p>
      <p>
        <strong>After the run.</strong> The <strong>Subtitles for your video</strong> recipe takes a
        finished video and gives you a new one with the subtitles burned in, plus the same subtitles as
        SRT and VTT files with the same timing. Nothing else about the video changes. Its{' '}
        <strong>Caption look</strong> setting offers plain, word by word, or one big word. A second
        recipe, <strong>Subtitles in another language</strong>, translates what is said and keeps the
        original timing. Both use credits, and the price shows before you run.
      </p>
      <p>
        Two limits. Subtitles come from the speech in the video, so a sentence that was not recognized
        cannot be subtitled, and you should read them before you post. And the run page has no download
        button for the SRT and VTT files yet. Until it has one, the editor in step 9 is the way to get
        subtitle files in the app: its subtitles tab makes captions from a transcript, which uses
        credits, and downloads them as SRT or WebVTT. The{' '}
        <a href="/docs/recipes/subtitles">subtitles recipe</a> page describes the recipe itself.
      </p>

      <h2 id="make-the-shapes">6. Make the shape each channel wants</h2>
      <p>
        Every recipe that makes a video asks for a shape before it runs: vertical (9:16), square (1:1),
        landscape (16:9) or portrait (4:5). Pick the one your main channel wants.
      </p>
      <p>
        Here is the limit to know before you plan a week around it: a finished video cannot be re-cut
        to another shape yet. A second shape means running the recipe again with the other shape
        chosen. That run uses credits again, and it is a fresh edit, so its cuts may not match the first
        version exactly.
      </p>
      <p>
        The editor&rsquo;s export screen can also render the cut you have open in another format. When
        the format does not match the timeline, it tells you the picture will be cropped or get bars.
        That is the same cut in a different frame, not an edit made for that frame.
      </p>
      <p>
        The practical advice is to film in the shape you post most. Footage shot sideways and delivered
        vertical has to be fitted into the new frame, so part of the picture is lost or shown smaller.
      </p>

      <h2 id="make-the-cover">7. Make the cover</h2>
      <p>
        The <strong>Thumbnail and cover text</strong> recipe makes a cover picture from one video in
        your project. For <strong>Which frame</strong>, choose <strong>Pick the best one</strong>, or{' '}
        <strong>Use a time I choose</strong> and set the time. <strong>Cover text</strong> is optional
        and can be up to 90 characters. The result is one picture saved in the project.
      </p>
      <p>
        This recipe uses very few credits. Your video is looked at inside ReelBolt and nothing is sent to an
        outside provider. If the picture is not the frame you wanted, choose the time yourself and run
        it again.
      </p>
      <p>
        To go further, open the picture in the picture editor and work in layers: move the title, add a
        second line, cut out a subject. The picture editor is part of the Early preview, and its AI
        tools use credits each time, with the price shown first.
      </p>
      <Figure
        name="picture-editor"
        alt="The thumbnail editor with a logo picture on the canvas and a text layer named Headline in the layers panel."
        width={1280}
        height={800}
        caption="The picture editor (Early preview), with a text layer on the canvas of a demo picture."
      />

      <h2 id="get-the-post-text">8. Get the post text</h2>
      <p>
        When the video is finished, ask the assistant for the text you post with it, and name the
        platforms: &ldquo;write the post text for TikTok and Instagram Reels&rdquo;. It covers TikTok,
        Instagram Reels, YouTube Shorts, LinkedIn and X. You get one version per platform, cut to the
        length that platform allows, with a title where the platform has one, the body and the
        hashtags.
      </p>
      <p>What to know before you rely on it:</p>
      <ul>
        <li>
          The assistant writes from what ReelBolt holds about the video, such as the project details
          and your brief. It does not watch the video. To have the text written from what is in the
          video, open the video in the app and use the <strong>Write post text</strong> dialog, which
          shows the price before anything is read.
        </li>
        <li>
          Hashtags are suggestions, not measured trends. ReelBolt does not say a hashtag is trending.
        </li>
        <li>
          Each request uses a small amount of credits, and the reply says how much. There is an hourly
          limit per person and project.
        </li>
        <li>It writes the text. It does not post anything for you.</li>
      </ul>
      <p>
        The <a href="/docs/assistant/post-text">post text</a> page has the details.
      </p>

      <h2 id="change-anything-in-the-editor">9. Change anything in the editor</h2>
      <p>
        You stay in charge of the cut. Open <strong>Editor</strong> in the left menu, choose your
        project, and on the <strong>Videos</strong> tab find your video under{' '}
        <strong>From your workflow runs</strong>. <strong>Edit</strong> opens it as a timeline, with
        the clips of the cut on their tracks.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the timeline editor, with three video clips on track V1 and one audio clip on track A1."
        width={1280}
        height={214}
        caption="A demo timeline in the editor (Early preview), with the clips of a cut on their tracks."
      />
      <p>
        There you can trim a cut, move or split a clip, add a title, undo a change and go back to an
        earlier version. Your changes are saved as you go. The editor is labeled Early preview: it is
        still being finished, and some tools may change. It has a phone layout, but some tools need a
        larger screen.
      </p>
      <p>
        The editor changes the edit, not the finished file. To get a new video, render it from the
        editor&rsquo;s export screen, which shows the credits a render is expected to use before you
        start.
      </p>
      <p>
        If the change is a setting and not a cut, there is a second route:{' '}
        <strong>Change one thing and re-run</strong>, on the recipe&rsquo;s page next to a finished
        video. Change the length, your notes or the captions switch, check the price, and get a new
        run. Your first video is kept. The shape, the resolution and the style cannot be changed there.
      </p>
      <p>
        One thing that does not work on a footage edit: typing a change in plain words and getting a
        new version. That box is for animated videos.{' '}
        <a href="/docs/workflows/request-changes">Request changes</a> lists what can be changed where.
      </p>

      <h2 id="what-about-music">What about music</h2>
      <p>
        ReelBolt has no built-in music library. You bring the track, and you need the rights to use it.
        There are two places it goes:
      </p>
      <ul>
        <li>
          <strong>In the run.</strong> Upload the track to the project and use{' '}
          <strong>Footage edit with background music</strong> in place of the plain highlight edit. It
          makes the same kind of cut with your track mixed under the voices. If no uploaded audio fits,
          the music step chooses nothing and says why, and you still get the edit.
        </li>
        <li>
          <strong>In the editor.</strong> <a href="/docs/editor/cut-to-the-music">Cut to the music</a>{' '}
          finds the beat of a music clip on your timeline and moves or splits your cuts onto it.
        </li>
      </ul>
      <p>
        Generating a track from a description is part of the Creator plan and the plans above it. It is
        not in the free trial. The <a href="/docs/recipes/music-sfx-and-grade">music recipe</a> page
        covers all of this.
      </p>

      <h2 id="the-limits">The limits, plainly</h2>
      <ul>
        <li>
          <strong>Short videos.</strong> ReelBolt is built for videos of about 90 seconds or less. It
          will not edit your long-form video. It can make the short one that goes with it.
        </li>
        <li>
          <strong>You bring the footage.</strong> On the free trial, no recipe turns a text prompt alone
          or photos alone into a video.
        </li>
        <li>
          <strong>One long take can only be trimmed.</strong> A real edit needs several different
          shots.
        </li>
        <li>
          <strong>No music library.</strong> Upload your own track.
        </li>
        <li>
          <strong>One shape per run.</strong> A finished video cannot be re-cut to another shape yet.
        </li>
        <li>
          <strong>A watermark on the trial.</strong> Trial exports are {TRIAL_RESOLUTION} with a
          watermark.
        </li>
        <li>
          <strong>No posting.</strong> You download the file and publish it yourself.
        </li>
        <li>
          <strong>Public alpha.</strong> Expect some rough edges. The editor is an Early preview.
        </li>
      </ul>

      <h2 id="what-the-free-trial-covers">What the free trial covers</h2>
      <p>
        No card is required. A trial workspace starts with {TRIAL_CREDITS} one-time credits. When you
        verify your email, your account also receives a one-time grant of {FIRST_VIDEO_GRANT_CREDITS}{' '}
        credits, so your first video is on us. That grant is given once per person and runs out{' '}
        {FIRST_VIDEO_GRANT_EXPIRY} after you get it.
      </p>
      <p>
        The recipes in the steps above are part of the trial, except where a step names a paid plan.
        Trial exports are {TRIAL_RESOLUTION} with a watermark. Each run shows the credits it is
        expected to use before you start, and the cover recipe uses none. If a step fails because of a
        problem on our side, the credits held for it are refunded. Paid plans remove the watermark and export at higher
        resolutions. The <Link href="/pricing">pricing page</Link> lists what each plan includes.
      </p>

      <h2 id="where-to-go-next">Where to go next</h2>
      <p>
        If you have not made a video in ReelBolt yet,{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks the
        sign-up and the first run screen by screen. The{' '}
        <a href="/docs/recipes/derush-edit">highlight edit recipe</a> page has the details of what is
        removed and what to check after a run, and{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> lists everything else you can make
        from the same clips.
      </p>
      <p>Next weekend, it could be a cut you are fixing, not a timeline you are filling.</p>
    </>
  );
}
