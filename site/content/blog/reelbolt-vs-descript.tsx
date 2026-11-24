import type { JSX } from 'react';
import Link from 'next/link';
import { Cite } from '@/components/blog/Cite';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { Figure } from '@/components/blog/Figure';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// Refreshed 2026-11-11 for the broader frame: the reader has raw footage and a story, and is not
// assumed to have a software product. Every statement about Descript comes from Descript's own
// public pages, read on 2026-11-11 and listed in the sources. Nothing was tested hands-on, and no
// Descript price appears anywhere. ReelBolt facts follow docs/user-guide (recipes/choose-a-recipe,
// derush-edit, subtitles, voiceover, run-settings; projects/avatars; design-system;
// editor/export-your-film, share-for-review; account/plans-and-credits). Every ReelBolt figure
// comes from the plan catalog or from the constants the Terms and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-descript',
  title: 'ReelBolt vs Descript: which one fits the footage you already have?',
  description:
    'Descript and ReelBolt both turn raw footage into video with AI. See where Descript is the better choice, where ReelBolt differs, and who should pick which.',
  date: '2026-10-17',
  updated: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 11,
  unlisted: false,
  hero: {
    src: 'timeline-editor',
    alt: 'The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one audio clip on another and a Remove silence button above them.',
    width: 1280,
    height: 214,
  },
  faqs: [
    {
      q: 'Do I need a software product to use ReelBolt?',
      a: 'No. ReelBolt works from footage you filmed: a shop, an event, a lesson, a cause, a product. Some of its recipes make a promo video from an app’s source code; the footage recipes described here need no code.',
    },
    {
      q: 'I have never edited a video. Which one is easier to start with?',
      a: 'They ask different things of you. Descript says you edit by deleting words in a transcript and that most people make their first cuts within minutes. ReelBolt asks for your clips and a sentence or two about the video, then makes and reviews a first cut for you to react to. If you like reading the recording through and deciding yourself, start with Descript. If you would rather react to a draft, start with ReelBolt.',
    },
    {
      q: 'Does ReelBolt record my screen or camera like Descript does?',
      a: 'No. Descript has a screen, webcam and microphone recorder and remote recording in the browser. ReelBolt works on footage you already have. Its editor can record a voiceover from your microphone, and nothing else.',
    },
    {
      q: 'Which one dubs a video with lip sync?',
      a: 'Descript. Its help center says lip sync aligns the speaker’s mouth with the translated audio, from its Creator plan up, and that it does not support multiple speakers. A ReelBolt dub changes the sound only, so the mouth on screen stays as it was filmed.',
    },
    {
      q: 'Does ReelBolt make AI avatars like Descript?',
      a: 'In a narrower form. A ReelBolt avatar is a likeness of a real adult, made from that person’s own pictures, short videos and voice recordings. It appears in short clips of about 15 seconds at most, is part of the Pro plan and the plans above it, and is still being rolled out. There are no stock avatars and no long presenter videos. Descript offers avatars from a photo, a text prompt or a stock gallery.',
    },
    {
      q: 'Can I finish the edit in another editor?',
      a: 'With both, in different ways. Descript exports a timeline to Premiere, DaVinci Resolve, Final Cut Pro and several audio tools from its Creator plan up. ReelBolt exports OTIO, EDL and Final Cut XML and can prepare a hand-off package with the cut and your video files. That package is an Early preview, and we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut Pro.',
    },
    {
      q: 'Why are there no Descript prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and a figure copied here would go stale. Descript’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsDescript(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who has Descript and
        ReelBolt on the shortlist: a creator, a small business, a teacher, a nonprofit, an agency or a
        software team. We make ReelBolt, so read it with that in mind. Every statement about Descript
        comes from Descript’s own public pages, read on {CHECKED} and linked in the sources at the
        end. We did not test Descript hands-on, and we give no Descript prices.
      </p>
      <p>
        The short version: Descript is an editor you work in. You record or upload, then edit the
        video by editing its transcript, with an AI co-editor to help. ReelBolt works more like
        handing the footage to someone. You drop in raw clips, say what the video is for, and get
        back a short first cut that has already been reviewed. If you need to record, to edit long
        recordings yourself or to dub with lip sync, Descript is the better choice. If the blank
        timeline is what keeps your video from getting made, that is the job ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        Descript describes itself as “an AI video and audio editor that lets you edit by editing
        text”: you record or upload, it transcribes, and you cut, trim and rearrange the video by
        changing the transcript <Cite n={[1]} />. Its home page says you can direct an AI co-editor,
        named Underlord, to do the editing for you, or do it yourself <Cite n={[1]} />. It starts
        from footage, a script or a recorded call <Cite n={[1]} />, and its AI Video Maker turns a
        prompt, a script or footage into an editable video <Cite n={[21]} />. Descript runs as a web
        editor and as desktop apps for macOS and Windows <Cite n={[18, 19]} />.
      </p>
      <p>
        ReelBolt is an AI video team for anyone with a story to tell and no time to tell it. It is a
        public alpha, so expect some rough edges. You upload the clips you already shot, between 1
        and 20 for a highlight edit, and write a sentence or two about what the video is for. An AI
        editor chooses what to keep and leaves out dead air, false starts and retakes. A reviewer
        scores the draft, and a low score sends it back for another pass before you see it. The
        result is a short video, about 90 seconds or less, in the shape you chose. A run takes
        minutes for a small job and sometimes half an hour for a big one. After that you can ask for
        changes, or open the cut in an editor that is labeled Early preview.
      </p>
      <p>
        Raw clips are the usual way in, not the only one. For software teams there is also a recipe
        that makes a promo video from an app’s source code.
      </p>

      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={`ReelBolt and Descript compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'Descript']}
        rows={[
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>A recording you make in Descript or files you upload; also a script or a prompt; imports from Zoom, Google Drive and PDF slide decks <Cite n={[1, 20, 21]} /></> },
            ],
          },
          {
            label: 'Recording',
            cells: [
              'No screen or camera recorder; the editor records a voiceover',
              { text: <>Screen, webcam and microphone recorder; remote recording in the browser <Cite n={[5, 6]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor, then a reviewer that scores the draft before you see it',
              { text: <>You, by editing the transcript, or the AI co-editor Underlord, which is limited on the free plan <Cite n={[1, 2]} /></> },
            ],
          },
          {
            label: 'Transcript editing',
            cells: [
              'Yes, in the editor, when the footage was transcribed',
              { text: <>The center of the product, on every plan including Free <Cite n={[1, 2]} /></> },
            ],
          },
          {
            label: 'Filler words, retakes and silences',
            cells: [
              'Left out of the first cut; the editor can also remove silence and find fillers',
              { text: <>Tools to remove filler words and retakes and to shorten word gaps <Cite n={[1, 2]} /></> },
            ],
          },
          {
            label: 'Captions and subtitles',
            cells: [
              'Burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>Dynamic captions on every listed plan; SRT and VTT export; caption translation in 61 languages, limited on Free <Cite n={[2, 24]} /></> },
            ],
          },
          {
            label: 'Dubbing',
            cells: [
              'Yes, sound only: no lip sync',
              { text: <>Dubbing in 30 languages, limited on Free; lip sync from the Creator plan, for one speaker <Cite n={[2, 11, 12]} /></> },
            ],
          },
          {
            label: 'Voice',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker',
              { text: <>AI speech with custom voice clones, limited on Free <Cite n={[2, 15]} /></> },
            ],
          },
          {
            label: 'Avatars',
            cells: [
              'A real adult, from their own pictures and recordings, in clips of about 15 seconds at most; Pro and up, still rolling out; no stock avatars',
              { text: <>From a photo, a text prompt or a stock gallery of over 35; limited on Free and included on paid plans, per the pricing table <Cite n={[2, 13]} /></> },
            ],
          },
          {
            label: 'Generated video and music',
            cells: [
              'AI-generated clips on Pro and up; music and sound-effect generation on Creator and up; no built-in music library',
              { text: <>Generates video, voice audio, music and images; video generation limited on Free <Cite n={[1, 2]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>Brand Studio on Business and Enterprise; layout packs generated from brand guidelines on paid plans <Cite n={[9, 16, 30]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>Square, vertical, horizontal or custom; can reframe around the active speaker <Cite n={[22, 23]} /></> },
            ],
          },
          {
            label: 'Short clips from one long video',
            cells: [
              'No: each run makes one short video',
              { text: <>Yes: Create Clips makes 1 to 20 clips from a recording <Cite n={[7, 29]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>Commenting and live collaboration on every listed plan; SSO, SCIM and audit logs on Enterprise <Cite n={[2]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>720p with a watermark on Free, 4K from the Creator plan; timeline export to Premiere, DaVinci Resolve, Final Cut Pro and audio tools from Creator <Cite n={[2, 17]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits',
              { text: <>Web editor and desktop apps for macOS and Windows; not designed for phones or tablets <Cite n={[18, 19]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>API for every paying user, in early access; a connection for Claude, ChatGPT and other AI assistants; a command line; Zapier <Cite n={[3, 4]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A standing free plan, no card; exports carry a watermark <Cite n={[1, 2]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-descript-is-stronger">Where Descript is the better choice</h2>
      <p>
        <strong>Descript records.</strong> It has a recorder for your screen, webcam and microphone{' '}
        <Cite n={[5]} />, and Rooms, a remote recording studio that runs in the browser and records
        each participant locally in up to 4K <Cite n={[6]} />. ReelBolt does not record your screen
        or camera. You can record a voiceover in its editor, and everything else is footage you
        bring.
      </p>
      <p>
        <strong>Long recordings where the words carry the video.</strong> Editing through the
        transcript is the center of Descript, and it is part of the free plan{' '}
        <Cite n={[1, 2]} />. Descript says most people make their first cuts within minutes by
        deleting words in a transcript, with the timeline there for fine control <Cite n={[1]} />.
        It lists tools that remove filler words and retakes and shorten silences, and Studio Sound
        for noise removal and voice enhancement <Cite n={[1, 2]} />. ReelBolt’s editor can also cut
        from the transcript, remove silence and clean up a voice, but its main view is a multi-track
        timeline, and the product is built around short videos. For an hour-long interview, podcast
        or lesson you mean to keep most of, Descript is the better tool.
      </p>
      <p>
        <strong>A free plan you can stay on.</strong> Descript’s free plan needs no credit card. It
        includes text-based editing, captions, commenting and live collaboration, limits the AI
        tools, and exports at 720p with a watermark <Cite n={[1, 2]} />. ReelBolt has a free trial
        with one-time credits. It is not a plan you can stay on.
      </p>
      <p>
        <strong>Dubbing with lip sync.</strong> Descript’s pricing table lists caption translation
        in 61 languages and dubbing in 30, both limited on the free plan and included on paid
        plans, with translation proofreading on Business <Cite n={[2, 11]} />. Lip sync, which
        matches the speaker’s mouth to the translated audio, is available from the Creator plan,
        and Descript’s help center says it does not support multiple speakers <Cite n={[12]} />.
        ReelBolt dubs the sound only. The mouth on screen stays as it was filmed.
      </p>
      <p>
        <strong>A presenter nobody has to film.</strong> A Descript avatar can come from a photo, a
        text prompt or a stock gallery of over 35 <Cite n={[2, 13]} />, and its avatar page says you
        can “create an entire video just by typing” <Cite n={[8]} />. The pricing table lists
        avatars as limited on the free plan and included on paid plans <Cite n={[2]} />. The same
        table lists eye-contact correction and a green screen effect that removes a background
        without a physical screen <Cite n={[2]} />. ReelBolt’s{' '}
        <Link href="/blog/reelbolt-avatars">avatars</Link> are narrower. Each one is made
        from a real adult’s own pictures and recordings, and it appears only in short clips, about
        15 seconds at most, that are marked as AI-generated. Avatars are part of the Pro plan and the plans above
        it and are still being rolled out. ReelBolt has no eye-contact correction, and its
        green-screen insert needs a real screen in the shot. If you need a presenter to carry a whole
        video and nobody wants to be filmed, Descript covers that and ReelBolt does not.
      </p>
      <p>
        <strong>Many short clips from one long video, and publishing.</strong> Descript’s Create
        Clips finds moments in a recording and makes from 1 to 20 clips, with portrait and square
        layouts <Cite n={[7, 29]} />. Descript also publishes to YouTube, Wistia, HubSpot and podcast
        hosts <Cite n={[10]} />. ReelBolt makes one short video per run and does not post it for
        you.
      </p>
      <p>
        <strong>Hand-off to a professional editor.</strong> From the Creator plan, a Descript
        timeline exports to Premiere, DaVinci Resolve, Final Cut Pro, Pro Tools, Logic, Audition
        and Reaper, though Descript notes that most timeline formats do not carry sequences or
        avatars <Cite n={[17]} />. Its November 2026 release notes say those exports now carry
        your comments as markers <Cite n={[28]} />. ReelBolt exports OTIO, EDL and Final Cut XML
        and can prepare a hand-off package with the cut and your video files. That package is an
        Early preview, and we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut
        Pro.
      </p>
      <p>
        <strong>Reach into other tools, and company requirements.</strong> Descript gives every
        paying user API access at no additional cost, in early access, next to a connection for AI
        assistants such as Claude and ChatGPT, a command line and Zapier{' '}
        <Cite n={[3, 4, 27]} />. Its Enterprise plan lists SOC 2 Type II, SSO, SCIM and audit logs{' '}
        <Cite n={[2]} />. ReelBolt has{' '}
        <Link href="/blog/reelbolt-public-api">an API with webhooks</Link> on its paid plans and a
        connection for Claude Code, but the whole product is a public alpha. If your company has
        security or compliance requirements today, Descript is the safer choice.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It makes the first cut, then checks it.</strong> ReelBolt assumes you would rather
        react to a cut than build one. You do not open a timeline or read a transcript first. An
        AI editor decides what to keep, a reviewer scores the result, and a low score sends the
        edit back for another pass before you see it. Descript’s Underlord aims at similar ground:
        Descript says it can turn a raw recording into a tight cut in minutes <Cite n={[1]} />. We
        have not tested it, so we make no claim about which one cuts better. The difference we can
        state is where each one starts. Descript is an editor with an AI co-editor inside it.
        ReelBolt is a brief and a run, with an editor for afterwards.
      </p>
      <p>
        <strong>It is built for a camera roll, not one recording.</strong> Give it the clips from
        your phone: the shop, the event, the lesson, the product. Several different shots give the
        editor something to cut together. One long take can only be trimmed. When the speech in
        your clips could be read, cuts are placed so they do not land inside a spoken word.{' '}
        <Link href="/blog/what-a-highlight-edit-removes">What a highlight edit removes</Link> shows
        the kind of thing that gets cut.
      </p>
      <p>
        <strong>The same clips can go further.</strong> Other recipes add titles and callouts, write
        and voice a narration with captions, burn in subtitles and produce the SRT and VTT files,
        translate the subtitles, or dub the video. The run page has no download button for the
        subtitle files yet, which is one of the rough edges. One makes a cover picture for very few credits, and the assistant can write the text that goes with the post. You choose the shape
        before each run: landscape, vertical, square or portrait. A finished video cannot be re-cut
        to another shape yet, so a second shape is another run. Music and sound effects come from
        files you upload, because there is no built-in music library.
      </p>
      <p>
        <strong>A real person’s voice or likeness needs recorded consent.</strong> In ReelBolt a
        cloned voice and an avatar each need the person’s agreement, which you record in the
        project, and you can withdraw it or delete the voice or avatar at any time. The person
        behind an avatar must be an adult. Descript has its own consent step for voice clones and
        its own checks on avatar photos, described in its help center <Cite n={[14, 15]} />. We do
        not compare the two processes here.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> Both tools can work from
        brand guidelines. Descript’s Brand Studio is for Business and Enterprise customers, with
        one brand kit per Drive <Cite n={[9, 30]} />, and on paid plans Descript can generate a layout
        pack from brand guideline PDFs, Markdown files or images, plus your logos and fonts{' '}
        <Cite n={[16]} />. ReelBolt builds a brand kit from a brand PDF or a design-tool export,
        and it also reads design token and CSS files if your team keeps them. Reading your files
        is free. AI fills the gaps only after you confirm an estimate.
      </p>
      <Figure
        name="brand-kit"
        alt="The Brand and product tab of a demo project in ReelBolt, showing the approved brand colors and typography."
        width={1280}
        height={800}
        caption="Look at the brand colors and fonts listed for the project."
      />
      <p>
        <strong>A phone view of the editor.</strong> ReelBolt runs in a browser, and its editor
        has a phone view for simpler edits. Some tools need a larger screen and say so. Descript’s
        help center says its web editor is not designed for small mobile screens or tablets{' '}
        <Cite n={[18]} />.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, a ReelBolt recipe
        makes a promo video from an app’s source code. We found no code repository input on the
        Descript pages we read <Cite n={[10]} />.
      </p>

      <h2 id="pricing-approach">Pricing approach</h2>
      <p>
        Descript has five plans: Free, Hobbyist, Creator, Business and Enterprise. Paid plans are
        priced per person per month, with monthly or annual billing, and each lists a monthly
        allowance of media hours and AI credits <Cite n={[2]} />. Unused media minutes and AI
        credits do not roll over to the next cycle <Cite n={[25]} />. Watermark-free export starts
        on Hobbyist and 4K export on Creator <Cite n={[2]} />. Top-ups are available only on
        Creator and Business and expire 12 months after purchase <Cite n={[26]} />, and Enterprise
        goes through sales <Cite n={[2]} />. Current figures are on{' '}
        <a href="https://www.descript.com/pricing" rel="nofollow noopener" target="_blank">
          Descript’s pricing page<span className="sr-only"> (opens in a new tab)</span>
        </a>
        .
      </p>
      <p>
        ReelBolt plans are credit based, and no card is required to start. A trial workspace has{' '}
        {TRIAL_CREDITS} one-time credits. When you verify your email, the account also receives a
        one-time grant of {FIRST_VIDEO_GRANT_CREDITS} credits, which expires{' '}
        {FIRST_VIDEO_GRANT_EXPIRY} after it is granted. Trial exports are {TRIAL_RESOLUTION} with a
        watermark. The paid plans are Creator, Pro, Team and Enterprise. Creator exports up to{' '}
        {CREATOR_RESOLUTION} with no watermark and Pro up to {PRO_RESOLUTION}. AI-generated clips
        and avatars are part of Pro and the plans above it. The{' '}
        <Link href="/pricing">pricing page</Link> has the current prices and what each plan
        includes.
      </p>
      <p>
        Before a run starts, ReelBolt shows the credits it is expected to use, as a likely amount
        and the range it usually falls in. If a step fails because of a problem on our side, the
        credits held for it are refunded. Top-up packs are for paid plans and expire after{' '}
        {catalog('topUp.expiresAfterMonths')} months.
      </p>
      <Figure
        name="run-estimate"
        alt="The run estimate in ReelBolt, showing the estimated credits for a run, a rough-estimate note and the credits the workspace has available."
        width={1240}
        height={218}
        caption="Look at the credit estimate, which appears before the run starts."
      />
      <p>
        A credit buys something different at each company, so comparing one tool’s credit with
        another’s will mislead you. Compare the cost of the video you actually need.
      </p>

      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <p>Pick Descript if any of these is true:</p>
      <ul>
        <li>You need to record yourself, your screen or remote guests.</li>
        <li>
          Your video is long and mostly talking, such as a podcast, an interview or a full lesson,
          and you are willing to read it through.
        </li>
        <li>You want several short clips from one long recording.</li>
        <li>You need a dub with lip sync, or a presenter nobody has to film.</li>
        <li>You want a free plan you can stay on, or your company needs SSO and audit logs.</li>
        <li>A professional editor will finish the work in Premiere, DaVinci Resolve or Final Cut Pro.</li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You want a short video, about 90 seconds or less.</li>
        <li>You would rather fix a first cut than build one.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        A teacher with an hour-long lecture recording is better served by Descript. A shop owner, a
        volunteer or a founder with a camera roll of clips and no editor is who ReelBolt is built
        for. Many people will have both kinds of video, and nothing stops you from using both
        tools.
      </p>
      <p>
        If ReelBolt sounds like the fit,{' '}
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks
        the first run screen by screen, and{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a> lists everything ReelBolt can
        make today and what it cannot. For the wider field,{' '}
        <Link href="/blog/ai-video-editors-for-people-who-do-not-edit-2026">
          AI video tools for people who do not edit
        </Link>{' '}
        sorts the tools by kind.
      </p>

      <SourceList
        sources={[
          { id: 'source-1', label: 'Descript, home page', url: 'https://www.descript.com/', accessed: CHECKED },
          { id: 'source-2', label: 'Descript, pricing', url: 'https://www.descript.com/pricing', accessed: CHECKED },
          { id: 'source-3', label: 'Descript, API', url: 'https://www.descript.com/api', accessed: CHECKED },
          { id: 'source-4', label: 'Descript, MCP', url: 'https://www.descript.com/mcp', accessed: CHECKED },
          { id: 'source-5', label: 'Descript, screen recording', url: 'https://www.descript.com/screen-recording', accessed: CHECKED },
          { id: 'source-6', label: 'Descript, Rooms', url: 'https://www.descript.com/rooms', accessed: CHECKED },
          { id: 'source-7', label: 'Descript, clips', url: 'https://www.descript.com/clips', accessed: CHECKED },
          { id: 'source-8', label: 'Descript, AI avatars', url: 'https://www.descript.com/ai-avatars', accessed: CHECKED },
          { id: 'source-9', label: 'Descript, Brand Studio', url: 'https://www.descript.com/brand-studio', accessed: CHECKED },
          { id: 'source-10', label: 'Descript, integrations', url: 'https://www.descript.com/integrations', accessed: CHECKED },
          { id: 'source-11', label: 'Descript help, translate overview', url: 'https://help.descript.com/repurpose/translate-overview', accessed: CHECKED },
          { id: 'source-12', label: 'Descript help, lip sync', url: 'https://help.descript.com/repurpose/lip-sync', accessed: CHECKED },
          { id: 'source-13', label: 'Descript help, avatars overview', url: 'https://help.descript.com/generative-media/overview', accessed: CHECKED },
          { id: 'source-14', label: 'Descript help, create your avatar', url: 'https://help.descript.com/generative-media/create-your-avatar', accessed: CHECKED },
          { id: 'source-15', label: 'Descript help, create a custom voice clone', url: 'https://help.descript.com/ai-speech/custom-speaker', accessed: CHECKED },
          { id: 'source-16', label: 'Descript help, generate a layout pack', url: 'https://help.descript.com/templates-and-presets/layouts-generate', accessed: CHECKED },
          { id: 'source-17', label: 'Descript help, timeline exports', url: 'https://help.descript.com/export-and-share/timeline-exports', accessed: CHECKED },
          { id: 'source-18', label: 'Descript help, Descript for web', url: 'https://help.descript.com/getting-started/descript-for-web', accessed: CHECKED },
          { id: 'source-19', label: 'Descript help, install', url: 'https://help.descript.com/getting-started/install', accessed: CHECKED },
          { id: 'source-20', label: 'Descript help, import overview', url: 'https://help.descript.com/getting-started/import-overview', accessed: CHECKED },
          { id: 'source-21', label: 'Descript help, AI Video Maker', url: 'https://help.descript.com/ai-assistant/ai-video-maker', accessed: CHECKED },
          { id: 'source-22', label: 'Descript help, aspect ratio', url: 'https://help.descript.com/working-in-projects/aspect-ratio', accessed: CHECKED },
          { id: 'source-23', label: 'Descript help, center active speaker', url: 'https://help.descript.com/visuals/center-active-speaker', accessed: CHECKED },
          { id: 'source-24', label: 'Descript help, subtitles', url: 'https://help.descript.com/export-and-share/subtitles', accessed: CHECKED },
          { id: 'source-25', label: 'Descript help, media minutes and AI credits', url: 'https://help.descript.com/billing-payments-plans/track-and-understand-your-media-minutes-and-ai-credits', accessed: CHECKED },
          { id: 'source-26', label: 'Descript help, top ups', url: 'https://help.descript.com/billing-payments-plans/top-ups', accessed: CHECKED },
          { id: 'source-27', label: 'Descript help, developers', url: 'https://help.descript.com/developers', accessed: CHECKED },
          { id: 'source-28', label: 'Descript, changelog', url: 'https://feedback.descript.com/changelog', accessed: CHECKED },
          { id: 'source-29', label: 'Descript help, create clips from your content', url: 'https://help.descript.com/repurpose/create-clips-from-your-content', accessed: CHECKED },
          { id: 'source-30', label: 'Descript help, Brand Studio', url: 'https://help.descript.com/branding/brand-studio', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on {CHECKED} from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
