import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { Cite } from '@/components/blog/Cite';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// A guide for the educator audience (messaging brief, 2026-11-11): coaches, teachers and course
// makers. Recipe facts follow docs/user-guide/recipes (derush-edit, subtitles, voiceover,
// choose-a-recipe, run-settings) and docs/user-guide/workflows/templates.md. Every ReelBolt plan
// figure comes from catalog() or from the constants the Terms and the pricing page already use;
// nothing here is a hand-written price. The accessibility statements are general and each one is
// cited to a W3C page in the source list; the post makes no compliance claim for ReelBolt.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'lessons-into-short-clips',
  title: 'Your best lesson is stuck in an hour-long recording',
  description:
    'For coaches, teachers and course makers: cut a recorded lesson into short captioned clips, add translated subtitles or a dub, and know the limits before you start.',
  date: '2026-11-11',
  tags: ['guides'],
  readingMinutes: 13,
  faqs: [
    {
      q: 'Can I upload a full hour-long lesson and get short clips back?',
      a: 'Not as one file. ReelBolt works from 1 to 20 clips and is built for short results. Several different shots give the edit something to choose between, and one long take can only be trimmed. Cut the recording into short segments first, or record in segments, and upload those.',
    },
    {
      q: 'How long are the clips ReelBolt makes?',
      a: 'Short. The highlight edit offers 30, 60 or 90 seconds as a target, and ReelBolt is built for videos of about 90 seconds or less. The length is a target, not a guarantee.',
    },
    {
      q: 'Do I get subtitle files, or only subtitles burned into the picture?',
      a: 'The subtitles recipe returns the video with the subtitles burned in. It also writes the same subtitles as SRT and VTT files, but the run page has no download button for them yet. To get subtitle files in the app today, open the video in the editor, which is labeled Early preview: its subtitles tab makes captions from a transcript and downloads them as SRT or WebVTT. Transcribing there uses credits.',
    },
    {
      q: 'Does the dub match my lip movements?',
      a: 'No. A dub changes the sound only. The mouth on screen is the one from the original take, and the result says so. If that would distract your viewers, use subtitles in another language instead.',
    },
    {
      q: 'Can ReelBolt turn my slides into a video?',
      a: 'No. There is no recipe that turns a slide deck into a video. If the slides matter, record your screen while you present them and upload that recording as a clip.',
    },
    {
      q: 'Are ReelBolt subtitles enough to meet an accessibility requirement?',
      a: 'We make no compliance claim. The subtitles are made by automatic transcription of the words spoken, so they can contain mistakes, and they do not name speakers or describe sounds. Read and correct them before you publish, and have them checked against any formal requirement you work under.',
    },
    {
      q: 'Do these recipes work on the free trial?',
      a: `Yes. The highlight edit, both subtitles recipes, the dub and the narrated recipes run on the free trial, and each uses credits. No card is required. Trial exports are ${TRIAL_RESOLUTION} with a watermark.`,
    },
  ],
};

export default function LessonsIntoShortClips(): JSX.Element {
  return (
    <>
      <p>
        The lesson went well. An hour in the room or on the call, and somewhere in the middle, a few
        minutes where the idea finally landed. Those minutes are now buried in a recording that few
        people will sit through, and you are the only person who knows where they are.
      </p>
      <p>
        This guide is for coaches, teachers and course makers. It covers what ReelBolt can do with a
        recorded lesson today: cut the strongest moments into a short clip, put subtitles on it,
        translate those subtitles, dub the clip into another language, or add a narration. It also
        says what ReelBolt cannot do, because two of the limits decide how you should record.
      </p>

      <Callout title="The short version">
        <p>
          ReelBolt makes short clips, about 90 seconds or less. It does not publish your whole lesson,
          and it does not turn slides into a video.
        </p>
        <p>
          It works from several short clips, not from one hour-long file. A single long take can only be
          trimmed, so record in segments or cut the recording into segments before you upload.
        </p>
        <p>
          Subtitles, subtitles in another language and a dub are separate recipes. A dub has no lip
          sync. The recipes in this guide run on the free trial, and all but one use credits. ReelBolt
          is in public alpha, so expect some rough edges.
        </p>
      </Callout>

      <h2 id="what-a-short-clip-is-for">What a short clip is for</h2>
      <p>
        A short clip does not replace the lesson. It carries one idea, such as the worked example or
        the answer to the question everyone asks. It is short enough to watch on a phone between two
        other things, and it can point people to the full lesson.
      </p>
      <p>
        That is the job ReelBolt is built for. If you need the full hour cleaned up and published as a
        full hour, this is not the tool for it. The limits are listed in full{' '}
        <a href="#what-reelbolt-does-not-do">further down</a>.
      </p>

      <h2 id="record-or-upload-segments">Do not upload the hour</h2>
      <p>
        The highlight edit takes between 1 and 20 clips and chooses what to keep from them. Several
        different shots give it something to choose between and to cut together. One long take gives it
        a single shot, which it can only trim.
      </p>
      <p>So give it segments. There are two ways to get them.</p>
      <ul>
        <li>
          <strong>For a recording you already have:</strong> cut out the parts worth keeping before you
          upload. Any basic trim tool will do, including the one on your phone. You know where the good
          minutes are, and finding them is the one step nobody else can do for you.
        </li>
        <li>
          <strong>For the next lesson:</strong> stop and start the recording for each idea, each example
          and each question. You end the session with a set of short clips and no hour-long file to dig
          through.
        </li>
      </ul>
      <p>There are also practical reasons to keep each file short.</p>
      <ul>
        <li>
          The trial plan includes a small storage allowance, and an hour-long recording can be larger
          than all of it. The <Link href="/pricing">pricing page</Link> lists the allowance for each
          plan.
        </li>
        <li>
          One file can be an hour long at most, and a longer one is refused before anything is
          charged. Aim far below that.

        </li>
        <li>
          Longer clips take longer to go through. A run takes minutes for a small job and sometimes half
          an hour for a big one.
        </li>
      </ul>
      <p>
        A good segment has one idea, a first sentence that makes sense without the lesson around it,
        and a clear end. If you can, vary what the camera sees: you talking, the board or the screen,
        your hands doing the thing. A screen recording with your voice on it counts as a clip. If you
        have no footage at all yet,{' '}
        <Link href="/blog/five-clip-shot-list">the five-clip shot list</Link> has a list written for a
        lesson.
      </p>

      <h2 id="which-recipe-does-what">Which recipe does what</h2>
      <p>
        A recipe is a ready-made app. You pick it from <strong>Start from a recipe</strong> in a project,
        fill in a few settings and press <strong>Run</strong>. These are the ones this guide uses.
      </p>
      <ComparisonTable
        caption="ReelBolt recipes for a recorded lesson, as of 2026-11-11"
        columns={['Recipe', 'What you get']}
        rows={[
          {
            label: 'The strongest moments of a lesson',
            cells: [
              'Highlight edit from footage',
              'A short cut of your clips with dead air, false starts and retakes removed',
            ],
          },
          {
            label: 'Subtitles on a finished clip',
            cells: [
              'Subtitles for your video',
              'The same clip with the spoken words burned in, plus SRT and VTT files',
            ],
          },
          {
            label: 'Subtitles in a language you did not speak',
            cells: [
              'Subtitles in another language',
              'The same clip with translated subtitles on the original timing, plus SRT and VTT files',
            ],
          },
          {
            label: 'The clip spoken in another language',
            cells: [
              'Dubbed video in another language',
              'An edit with a translated narration in place of the original speech, and subtitles of what is spoken. No lip sync',
            ],
          },
          {
            label: 'A voice for footage that has none',
            cells: [
              'Footage edit with narration and captions, or Narrated product story',
              'An edit with a written, spoken narration and burned-in captions',
            ],
          },
        ]}
      />

      <h2 id="cut-the-strongest-moments">Cut the strongest moments</h2>
      <p>
        Start with <strong>Highlight edit from footage</strong>. Its page asks for your clips, the
        language spoken in them, a length, a shape and a note.
      </p>
      <ul>
        <li>
          <strong>Length.</strong> 30, 60 or 90 seconds are offered. It is a target, not a guarantee.
        </li>
        <li>
          <strong>Shape.</strong> Vertical, square, landscape or portrait. Choose it before you run. A
          finished video cannot be changed to another shape afterwards, so a second shape is a second
          run.
        </li>
        <li>
          <strong>Notes.</strong> Say who the clip is for and what must stay. &ldquo;For parents new to
          the method. Keep the worked example with the fractions and my summary at the end&rdquo; is a
          brief the editor can cut to. &ldquo;A clip from my lesson&rdquo; is not.
        </li>
      </ul>
      <p>
        When you press <strong>Run</strong>, ReelBolt goes through the clips and finds the pauses, the
        shot changes and the words. An AI editor chooses which parts to keep. Dead air, false starts and
        retakes are left out, and when the speech could be read, cuts do not land inside a spoken word.
        A reviewer then scores the result, and a low score sends the edit back for another pass, for up
        to three rounds.
      </p>
      <p>
        The editor works from what it can read in your clips and from your notes. It does not know your
        subject. If the moment that matters is a quiet one, such as a pause while a student works
        something out, name it in the notes, or it may be removed as dead air.
      </p>
      <p>
        This recipe uses the sound of your own clips. It has no narration and no music library. It does
        have a <strong>Captions</strong> switch, off by default, that shows the words spoken in your
        clips on screen. For subtitle files as well, run the subtitles recipe on the result, which is
        the next step.{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks
        through this recipe screen by screen.
      </p>

      <h2 id="add-subtitles">Add subtitles</h2>
      <p>
        <strong>Subtitles for your video</strong> takes a finished video with speech in it and returns a
        new video with the subtitles burned in. Nothing else about the video changes: no cuts, no music,
        no new voice. The highlight edit you just made is saved in the project&rsquo;s files, so you can
        pick it as the video.
      </p>
      <p>
        Set the spoken language if the automatic detection guesses wrong. The{' '}
        <strong>Caption look</strong> field offers plain subtitles, word by word, or one big word at a
        time. Where the bottom of the frame is busy with text of its own, the subtitles move to the
        top.
      </p>
      <p>
        The recipe also writes the same subtitles as two files, <code>subtitles.srt</code> and{' '}
        <code>subtitles.vtt</code>, with the same sentences and timing. In the files, each cue is at
        most 42 characters wide and split between words. The difference between the two kinds
        matters. Subtitles burned into the picture are always there, wherever the clip is posted,
        and nobody can turn them off. A subtitle file is uploaded next to the video on a platform that
        accepts one, and each viewer can show or hide it. Accessibility guidance calls the first kind
        open captions and the second closed captions. <Cite n={[1, 2]} />
      </p>
      <p>Two limits to know before you rely on it:</p>
      <ul>
        <li>
          The run page has no download button for the two files yet, although they are made on every
          run. To get subtitle files in the app today, use the editor, which is labeled Early preview:
          its subtitles tab makes captions from a transcript and lets you download them as SRT or
          WebVTT. Transcribing there uses credits. The{' '}
          <a href="/docs/recipes/subtitles">subtitles page</a> of the docs describes the recipe&rsquo;s
          own files.
        </li>
        <li>
          A sentence the transcription did not recognize cannot be subtitled. If lines are missing,
          check the spoken-language setting and run again.
        </li>
      </ul>

      <h2 id="captions-and-accessibility">Captions and the people who need them</h2>
      <p>
        Captions are how people who are deaf or hard of hearing follow what is said in a video.{' '}
        <Cite n={[1]} /> They are also used by people who take in written text better than audio,{' '}
        <Cite n={[2]} /> and they help anyone watching with the sound off. The W3C&rsquo;s Web Content
        Accessibility Guidelines ask for captions on prerecorded video that has sound, at Level A, the
        first of their three levels. <Cite n={[1]} />
      </p>
      <p>
        In teaching, the stakes are plain. A learner who cannot hear the recording misses everything
        that is said in a clip without captions, however good the lesson was.
      </p>
      <p>What ReelBolt gives you is a starting point, and it helps to be exact about where it stops.</p>
      <ul>
        <li>
          <strong>The subtitles are the words spoken.</strong> The same guidance describes full captions
          as also saying who is speaking and noting meaningful sounds. <Cite n={[1]} /> The subtitles
          recipe does not add speaker names or sound descriptions. If your clip needs them, add them by
          hand.
        </li>
        <li>
          <strong>They are made by automatic transcription.</strong> The W3C&rsquo;s guidance says
          automatically generated captions usually need editing, and that one wrong word can change the
          meaning. <Cite n={[2]} /> Read every line before you publish, and look twice at names and at
          the terms of your subject.
        </li>
        <li>
          <strong>Correcting a word takes the editor.</strong> The subtitles recipe burns in what the
          transcription heard. In the editor, each caption is an ordinary clip whose text you can edit,
          and the export can burn the captions in, give you SRT and WebVTT files, or both. An SRT file
          is also plain text, so it opens in any text editor.
        </li>
        <li>
          <strong>A translation is not a substitute.</strong> The guidance treats captions in the
          language spoken as the accessibility measure. Subtitles in another language serve a different
          audience. <Cite n={[2]} />
        </li>
      </ul>
      <p>
        ReelBolt does not check a video against any accessibility standard, and we make no compliance
        claim. If your school, platform or client has a formal requirement, have the captions checked
        against it.
      </p>

      <h2 id="another-language">Reach learners in another language</h2>
      <p>There are two recipes, and they do different things.</p>
      <h3 id="translated-subtitles">Subtitles in another language</h3>
      <p>
        <strong>Subtitles in another language</strong> listens to what is said, translates it into the
        language you name and burns the translation in. The timing is always the original&rsquo;s. The
        translation replaces the words one sentence for one sentence, so a subtitle never appears
        earlier or later than the sentence it translates. A sentence the translation missed stays in
        the original language instead of disappearing. You get the SRT and VTT files here too. Your own
        voice is untouched.
      </p>
      <h3 id="dub">A dub</h3>
      <p>
        <strong>Dubbed video in another language</strong> needs footage with speech in it. It makes an
        edit of your clips, writes narration lines for that edit, translates them into the language you
        name when you run it, speaks the translation in place of the original speech and burns in
        subtitles of what is spoken. It is spoken by a stock voice, or by a cloned voice of a real
        person who has given recorded consent, if your project has one.
      </p>
      <p>Three things follow from how it works.</p>
      <ul>
        <li>
          <strong>There is no lip sync.</strong> The picture is not changed. The mouth on screen is the
          one from the original take, and the result says so. We suggest a dub for footage where your
          mouth is not the center of the frame, such as a screen recording or a shot of the board.
        </li>
        <li>
          <strong>It is a translated narration of your edit.</strong> It is not a word-for-word
          interpretation of every sentence you said.
        </li>
        <li>
          <strong>The translation is made by an AI model.</strong> Have someone who speaks the language
          watch the result before your students do. That goes for translated subtitles as well.
        </li>
      </ul>
      <p>
        If you are unsure which to use, start with subtitles in another language. They keep your voice,
        and there is no mismatch between the mouth and the sound.
      </p>

      <h2 id="add-a-narration">Add a narration</h2>
      <p>
        Some teaching footage does not speak for itself: a silent screen recording, or a demonstration
        filmed without commentary. Two recipes add a voice, and they differ in what comes first.
      </p>
      <ul>
        <li>
          <strong>Footage edit with narration and captions</strong> cuts your clips first, then writes a
          narration line for the shots it kept, speaks it and burns in captions.
        </li>
        <li>
          <strong>Narrated product story</strong> writes a short story from your brief first, then picks
          and orders your clips to tell it. The name says product, but it works from whatever clips and
          brief you give it. Clips with no sound of their own are fine.
        </li>
      </ul>
      <p>
        In both, ReelBolt writes the words from your brief and from what it finds in the clips. You are
        the expert on the subject, so listen to the whole narration before you share it. If the narrator
        says a name or a term wrongly, the project&rsquo;s <strong>Pronunciation</strong> tab lets you
        teach it the word. The narrator is a stock voice, unless you choose a cloned voice you have
        consent for.
      </p>
      <p>
        If your clips already have you talking, their sound keeps playing under the narration by
        default, so these two recipes fit silent footage best. The{' '}
        <a href="/docs/recipes/voiceover">voiceover page</a> of the docs has the details.
      </p>

      <h2 id="a-routine-that-fits-a-teaching-week">A routine that fits a teaching week</h2>
      <p>
        Before, the lesson is an hour-long file on a drive. After, it is a short captioned clip about
        one idea. The steps between the two are short enough to repeat.
      </p>
      <ol>
        <li>Record in segments, one idea each.</li>
        <li>Upload a handful of segments from one lesson to a project.</li>
        <li>
          Run the highlight edit, with a note that says who the clip is for and what must stay.
        </li>
        <li>
          Watch the cut. If it missed the point, a finished video offers{' '}
          <strong>Change one thing and re-run</strong>, where you can change the length or the note. Your
          first version is kept.
        </li>
        <li>Run the subtitles recipe on the result, and read every line.</li>
        <li>
          If you teach across languages, run the translated subtitles or the dub, and ask a speaker of
          that language to check it.
        </li>
      </ol>
      <p>
        One more recipe is useful at the end. <strong>Thumbnail and cover text</strong> picks a frame
        from your video and puts a title on it, and it uses very few credits.
      </p>

      <h2 id="what-it-costs-to-try">What it costs to try</h2>
      <p>
        No card is required. A trial workspace starts with {TRIAL_CREDITS} one-time credits. When you
        verify your email, your account also receives a one-time grant of {FIRST_VIDEO_GRANT_CREDITS}{' '}
        credits for a first video, which expires {FIRST_VIDEO_GRANT_EXPIRY} after it is granted.
      </p>
      <p>
        Every recipe in this guide except the thumbnail uses credits, and the <strong>Run</strong>{' '}
        button shows the credits a run is expected to use before you press it. If a step fails because
        of a problem on our side, the credits held for it are refunded. Trial exports are{' '}
        {TRIAL_RESOLUTION} with a watermark. Paid plans export without the watermark and at higher
        resolutions, and the <Link href="/pricing">pricing page</Link> lists what each plan includes.
      </p>

      <h2 id="what-reelbolt-does-not-do">What ReelBolt does not do</h2>
      <ul>
        <li>
          <strong>Full-length lessons.</strong> It is built for short videos, about 90 seconds or less.
          It does not clean up an hour and hand back an hour.
        </li>
        <li>
          <strong>Long presenter videos.</strong> There is no recipe for a lesson delivered by a
          presenter from start to finish. Avatars of a consenting real person exist on the Pro plan and
          the plans above it, they are still being rolled out, and a talking clip is about 15 seconds
          at most.
        </li>
        <li>
          <strong>Slides to video.</strong> No recipe turns a slide deck into a video. Record your screen
          while you present the slides, and upload that recording as a clip.
        </li>
        <li>
          <strong>One long take, edited.</strong> A single continuous recording can only be trimmed. A
          real edit needs several shots.
        </li>
        <li>
          <strong>A new shape for a finished clip.</strong> You choose the shape before the run. A
          finished video cannot be re-cut to another shape yet.
        </li>
        <li>
          <strong>Lip sync.</strong> A dub changes the sound and leaves the picture as it was.
        </li>
        <li>
          <strong>A music library.</strong> Background music comes only from files you upload, in a
          separate recipe.
        </li>
      </ul>

      <h2 id="where-to-go-next">Where to go next</h2>
      <p>
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> matches what you want to make to the
        recipe that makes it, and lists what ReelBolt cannot do yet.{' '}
        <a href="/docs/recipes/run-settings">Run settings</a> explains each setting on a recipe&rsquo;s
        page. For the whole path from sign-up to a downloaded file, read{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link>.
      </p>

      <SourceList
        sources={[
          {
            id: 'source-1',
            label: 'W3C, Understanding Success Criterion 1.2.2: Captions (Prerecorded), WCAG 2.2',
            url: 'https://www.w3.org/WAI/WCAG22/Understanding/captions-prerecorded.html',
            accessed: '2026-11-11',
          },
          {
            id: 'source-2',
            label: 'W3C Web Accessibility Initiative, Making Audio and Video Media Accessible: Captions/Subtitles',
            url: 'https://www.w3.org/WAI/media/av/captions/',
            accessed: '2026-11-11',
          },
        ]}
      />
    </>
  );
}
