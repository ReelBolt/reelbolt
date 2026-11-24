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
// assumed to have a software product. Every statement about HeyGen comes from HeyGen's own public
// pages, read on 2026-11-11 and listed in the sources. Nothing was tested hands-on, and no HeyGen
// price appears anywhere. Left out on purpose: HeyGen's language and avatar counts (its own pages
// give different figures) and any resolution for its free plan (its pricing table, help center and
// FAQ disagree). ReelBolt facts follow docs/user-guide (recipes/choose-a-recipe, derush-edit,
// subtitles, voiceover, ai-clip-from-idea, tracked-screen-insert; projects/avatars; design-system;
// editor/index, export-your-film, share-for-review; account/plans-and-credits). Every ReelBolt
// figure comes from the plan catalog or from the constants the Terms and the pricing page already
// use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-heygen',
  title: 'ReelBolt vs HeyGen: AI presenters or your own footage?',
  description:
    'HeyGen makes presenter-led video from a script, slides or a prompt. ReelBolt cuts the clips you already shot into a short finished video. See which fits you.',
  date: '2026-10-17',
  updated: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 13,
  unlisted: false,
  faqs: [
    {
      q: 'Do I need a software product to use ReelBolt?',
      a: 'No. ReelBolt works from footage you filmed: a shop, an event, a lesson, a cause, a product. You upload the clips and write a sentence or two about what the video is for. Some of its recipes make a promo video from an app’s source code; the footage recipes described here need no code.',
    },
    {
      q: 'I have no footage at all. Which one should I use?',
      a: 'HeyGen. It starts from a script, a prompt, slides or a PDF and puts an AI presenter on screen. ReelBolt is built around footage you bring. Nothing on its free trial turns a prompt or a set of photos into a video.',
    },
    {
      q: 'Can HeyGen work on footage I already shot?',
      a: 'In part, going by its own pages. HeyGen documents translating an uploaded video, clipping a long video into highlights, a screen recorder, and removing filler words and silences from a finished recording. Its Video Agent accepts videos as attachments. We have not tested what it does with a set of raw clips.',
    },
    {
      q: 'Does ReelBolt make AI avatars or digital twins?',
      a: 'In a narrower form. A ReelBolt avatar is a likeness of a real adult, made from that person’s own pictures, short videos and voice recordings. It appears in short clips of about 15 seconds at most, is part of the Pro plan and the plans above it, and is still being rolled out. There are no stock avatars and no long presenter videos. If an AI presenter carries your whole video, HeyGen is built for that.',
    },
    {
      q: 'Does ReelBolt lip-sync a translated video?',
      a: 'No. A ReelBolt dub changes the sound only, so the mouth on screen stays as it was filmed. HeyGen’s help center describes two translation engines that include lip sync and one that changes the audio only.',
    },
    {
      q: 'Which one suits a screen recording?',
      a: 'It depends on what you need done. HeyGen has a Screen Recorder in its AI Studio that captures your screen and your voice, and it documents filler-word and silence removal for recordings. ReelBolt does not record. It cuts a recording you bring into a short video, and it can place a screen recording on a green screen in another clip.',
    },
    {
      q: 'Can I use either tool from Claude Code or another AI assistant?',
      a: 'Yes. HeyGen says its remote connection for AI assistants is available on all its plans and names ChatGPT, Claude, Codex, Cursor and Gemini CLI among the clients. ReelBolt connects to Claude Code, which then works on your projects with the same permissions as the assistant in its app. ReelBolt is a public alpha, so expect that connection to change.',
    },
    {
      q: 'Why are there no HeyGen prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and a figure copied here would go stale. HeyGen’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsHeygen(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who has HeyGen and
        ReelBolt on the shortlist: a creator, a small business, a teacher, a nonprofit, an agency or
        a software team. We make ReelBolt, so read it with that in mind. Every statement about
        HeyGen comes from HeyGen’s own public pages, read on {CHECKED} and linked in the sources at
        the end. We did not test HeyGen hands-on, and we give no HeyGen prices.
      </p>
      <p>
        The short version: HeyGen makes video with an AI presenter in it. You give it a script, a
        prompt or a deck, and a ready-made presenter or a digital twin of you delivers it, with
        translation that matches the lip movement. ReelBolt works from what you already filmed. You
        drop in raw clips, say what the video is for, and get back a short first cut that has
        already been reviewed. If you have no footage, or a person talking carries the whole video,
        HeyGen is the better choice. If the footage is sitting on your phone and nobody has had time
        to edit it, that is the job ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        HeyGen describes itself as “an online video tool with built-in talking AI-avatars”{' '}
        <Cite n={[3]} />, and its home page opens with “AI videos starring you, made in minutes”{' '}
        <Cite n={[1]} />. It lists tools that turn text, an image, audio, or a PPT or PDF deck into
        a video, a text-based editor, and Video Agent, which builds a video from a prompt{' '}
        <Cite n={[1]} />. The same home page describes its customers as ranging “From solo creators
        to global enterprises” <Cite n={[1]} />. HeyGen also documents tools for footage you already have:
        translating an uploaded video, clipping a long video into highlights, recording your
        screen, and removing filler words from a finished recording <Cite n={[7, 8, 9, 11]} />. It
        runs on the web and has a mobile app <Cite n={[19]} />.
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
        caption={`ReelBolt and HeyGen compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'HeyGen']}
        rows={[
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>A script, a prompt, slides or a PDF, an image or audio; also an uploaded video, a YouTube or Google Drive link, a screen recording or a website address <Cite n={[1, 5, 7, 8, 20]} /></> },
            ],
          },
          {
            label: 'A person on screen',
            cells: [
              'The people in your footage; an avatar of a real adult, from their own pictures and recordings, in clips of about 15 seconds at most, on Pro and up, still rolling out; no stock avatars',
              { text: <>The core product: ready-made presenters, a custom avatar of you and photo avatars; videos up to 30 minutes on Creator and Pro and 60 on Business <Cite n={[1, 2]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor working from your clips, then a reviewer that scores the draft before you see it',
              { text: <>Video Agent builds a video from a prompt and your attachments, and you change it by chat or in AI Studio <Cite n={[1, 5]} /></> },
            ],
          },
          {
            label: 'Filler words, retakes and silences',
            cells: [
              'Left out of the first cut; the editor can also remove silence and find fillers',
              { text: <>Filler-word and silence removal for a finished video up to 2 hours long, through its API and its connection for AI assistants; Speech Cleanup in the app <Cite n={[4, 9, 10]} /></> },
            ],
          },
          {
            label: 'Timeline editing',
            cells: [
              'A multi-track timeline, labeled Early preview, with transcript editing, multicam and color grading',
              { text: <>A text-based editor in AI Studio; Video Agent is changed by chat; a desktop editor, HyperFrames Studio, has a timeline <Cite n={[1, 5, 21]} /></> },
            ],
          },
          {
            label: 'Captions and subtitles',
            cells: [
              'Burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>Styled captions in AI Studio; an SRT caption file to download; no closed captions for avatar videos <Cite n={[12, 13]} /></> },
            ],
          },
          {
            label: 'Translation and dubbing',
            cells: [
              'Translated subtitles; a dub that changes the sound only, with no lip sync',
              { text: <>Video translation with one audio-only engine and two with lip sync; up to 10 languages per job <Cite n={[1, 7]} /></> },
            ],
          },
          {
            label: 'Voice',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker',
              { text: <>Voice cloning on every plan, with one clone on Free <Cite n={[2]} /></> },
            ],
          },
          {
            label: 'Generated video and b-roll',
            cells: [
              'AI-generated clips on Pro and up; music and sound-effect generation on Creator and up; no built-in music library',
              { text: <>Video Agent combines presenter footage, motion graphics and b-roll; b-roll from a text prompt in Creative Studio, which is rolling out gradually <Cite n={[1, 22]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>Brand kit on paid plans; can import logos, colors and fonts from your website address <Cite n={[2, 14]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>Landscape or portrait in Video Agent; 16:9, 9:16 or 1:1 in AI Clipping <Cite n={[5, 11]} /></> },
            ],
          },
          {
            label: 'Short clips from one long video',
            cells: [
              'No: each run makes one short video',
              { text: <>Yes: AI Clipping, for free and paid users, on videos from 1 minute to 10 hours <Cite n={[11]} /></> },
            ],
          },
          {
            label: 'Recording',
            cells: [
              'No screen or camera recorder; the editor records a voiceover',
              { text: <>A Screen Recorder in AI Studio that captures your screen and your voice <Cite n={[8]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>Video comments on every plan; a share page with comments and reactions; draft collaboration, team workspace and role controls on Business and Enterprise <Cite n={[2, 15]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>Up to 1080p on Creator and up to 4K on Pro and above; caption and audio-only downloads; XML export of AI Clipping clips; SCORM on Business and Enterprise <Cite n={[2, 11, 13, 15]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits; no phone app',
              { text: <>On the web, with a mobile app; its help center says the desktop version has the most complete experience <Cite n={[19]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>A pay-as-you-go API, separate from its plans and open to free users; a connection for ChatGPT, Claude, Cursor and other AI assistants on all plans; n8n, Make, HubSpot and Zapier on Business <Cite n={[2, 4, 18]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A standing free plan, no card: a small monthly quota of short videos that carry a watermark <Cite n={[2, 16]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-heygen-is-stronger">Where HeyGen is the better choice</h2>
      <p>
        <strong>A presenter for the whole video.</strong> Avatars are the center of HeyGen. Its
        free plan lists ready-made presenters and one custom video avatar, and its pricing table
        puts the longest video at 30 minutes on Creator and Pro and 60 minutes on Business, with no
        stated maximum on Enterprise <Cite n={[2]} />. ReelBolt’s{' '}
        <Link href="/blog/reelbolt-avatars">avatars</Link> are narrower. Each one is made
        from a real adult’s own pictures and recordings, and it appears only in short clips, about
        15 seconds at most, that are marked as AI-generated. Avatars are part of the Pro plan and the plans above
        it and are still being rolled out. There are no stock avatars. If a person talking carries
        the whole video, or nobody can be filmed, HeyGen does that and ReelBolt does not.
      </p>
      <p>
        <strong>Starting with no footage.</strong> HeyGen lists tools that turn text, an image,
        audio, or a PPT or PDF deck into a video, and it describes Video Agent with the line “From
        idea to finished video with just a prompt” <Cite n={[1]} />. The result combines presenter
        footage, motion graphics and b-roll, and the motion graphics stay editable afterwards{' '}
        <Cite n={[1]} />. You can attach images, videos, logos and documents for it to use{' '}
        <Cite n={[5]} />. HeyGen’s July 2026 release notes also describe making a launch video from
        a website address <Cite n={[20]} />. ReelBolt is built around footage you bring. Nothing on
        its free trial turns a prompt or a set of photos into a video. On Pro and the plans above
        it, the most it generates from a description alone is a clip of 6 or 10 seconds. If all you have is a script
        or a deck, HeyGen is the better choice.
      </p>
      <p>
        <strong>Translation with lip sync.</strong> HeyGen’s home page says its “precise lip-sync
        aligns translated speech with facial movements” <Cite n={[1]} />. Its help center describes
        three translation engines, one that changes the audio only and two that include lip sync,
        and says you can select up to 10 languages per job <Cite n={[7]} />. You can translate an
        uploaded file or a video from a YouTube or Google Drive link <Cite n={[7]} />. The pricing
        table lists more languages on paid plans than on Free, a brand glossary from the Creator
        plan, and script proofreading from Pro <Cite n={[2]} />. ReelBolt can translate subtitles
        and dub a video, but the dub changes the sound only. The mouth on screen stays as it was
        filmed. For a talking head in several languages, that difference shows.
      </p>
      <p>
        <strong>Many short clips from one long recording.</strong> HeyGen’s AI Clipping turns a
        long video into highlight clips. Its help center says the tool is available to free and
        paid users, takes an upload or a video link, and handles videos from 1 minute to 10 hours.
        You pick the aspect ratio and a caption style, each clip gets an engagement score, and a
        clip can be published to YouTube from the same panel <Cite n={[11]} />. ReelBolt makes one
        short video per run and does not post it for you.
      </p>
      <p>
        <strong>Recording your screen, and one-step cleanup.</strong> AI Studio has a Screen
        Recorder that captures your screen and your voice, which HeyGen suggests for product demos,
        walkthroughs, training and tutorials <Cite n={[8]} />. Its developer documentation
        describes filler-word removal that takes filler words and overlong silences out of a
        finished video up to 2 hours long, “with no review step or edit decisions to make”{' '}
        <Cite n={[9]} />. Its June 2026 release notes describe Speech Cleanup, which removes filler
        words, pauses and false starts from a rough take <Cite n={[10]} />. ReelBolt does not record
        your screen or camera. You can record a voiceover in its editor, and everything else is
        footage you bring.
      </p>
      <p>
        <strong>A free plan you can stay on, and a phone app.</strong> HeyGen’s free plan needs no
        credit card and gives a small monthly quota of short videos that carry a watermark{' '}
        <Cite n={[2, 16]} />. HeyGen also has a mobile app, though its help center says the desktop
        version gives the most complete experience <Cite n={[19]} />. ReelBolt has a free trial with
        one-time credits, which is not a plan you can stay on, and it has no phone app. It runs in
        a browser, and its editor has a phone view for simpler edits.
      </p>
      <p>
        <strong>Reach into other tools, and company requirements.</strong> HeyGen’s API is pay as
        you go, sold separately from its plans and open to free users <Cite n={[18]} />. Its
        connection for AI assistants is available on all plans and names ChatGPT, Claude, Codex,
        Cursor and Gemini CLI among its clients <Cite n={[4]} />. The Business plan lists
        integrations with n8n, Make, HubSpot and Zapier, SCORM export with LMS integrations, and
        SAML/SSO <Cite n={[2]} />, and the home page shows GDPR, SOC 2 Type II and CCPA badges{' '}
        <Cite n={[1]} />. ReelBolt has{' '}
        <Link href="/blog/reelbolt-public-api">an API with webhooks</Link> on its paid plans and a
        connection for Claude Code, but the whole product is a public alpha. If your company has
        security or compliance requirements today, HeyGen is the safer choice.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It starts from a camera roll and makes the first cut.</strong> ReelBolt assumes the
        footage exists and the editing is what is missing. You give it several clips. An AI editor
        decides what to keep, a reviewer scores the result, and a low score sends the edit back for
        another pass before you see it. HeyGen’s tools for existing footage, as its pages describe
        them, each work on one recording: translate it, clip highlights out of it, or strip the
        filler words <Cite n={[7, 9, 11]} />. Video Agent accepts videos as attachments{' '}
        <Cite n={[5]} />. We have not tested what it does with a set of raw clips, so we make no
        claim about which one cuts better. The difference we can state is the starting point.
        HeyGen starts from words and puts a presenter on them. ReelBolt starts from your clips.
      </p>
      <p>
        <strong>Several clips give it something to cut.</strong> Give it the clips from your phone:
        the shop, the event, the lesson, the product. Several different shots make a real edit. One
        long take can only be trimmed. When the speech in your clips could be read, cuts are placed
        so they do not land inside a spoken word.{' '}
        <Link href="/blog/what-a-highlight-edit-removes">What a highlight edit removes</Link> shows
        the kind of thing that gets cut.
      </p>
      <p>
        <strong>An editor for afterwards.</strong> You stay in charge of the cut. ReelBolt’s
        editor, labeled Early preview, is a multi-track timeline with transcript editing, silence
        removal, multicam and color grading. HeyGen describes a text-based editor in AI Studio{' '}
        <Cite n={[1]} />. Its help center says Video Agent builds a video “Instead of editing on a
        static timeline”, and that you refine the result by chat or in a copy opened in AI Studio{' '}
        <Cite n={[5]} />. Its October 2026 release notes describe HyperFrames Studio, a desktop
        editor built around an agent in which you can also edit the timeline yourself{' '}
        <Cite n={[21]} />. On the pages we read we did not find multicam editing or color grading
        for your own footage in HeyGen. That is what we did not find, not something HeyGen says.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one clip on the track below and a Remove silence button above them."
        width={1280}
        height={214}
        caption="Look at the Remove silence button above the clips on the timeline."
      />
      <p>
        <strong>The same clips can go further.</strong> Other recipes add titles and callouts, write
        and voice a narration with captions, burn in subtitles and produce the SRT and VTT files,
        translate the subtitles, or dub the video. The run page has no download button for the
        subtitle files yet, which is one of the rough edges. One makes a cover picture for very few credits, and the assistant can write the text that goes with the post. Another places a screen
        recording, a video or a picture on a green screen in your clip. You choose the shape before
        each run: landscape, vertical, square or portrait. A finished video cannot be re-cut to
        another shape yet, so a second shape is another run. Music and sound effects come from files
        you upload, because there is no built-in music library.
      </p>
      <p>
        <strong>Feedback from someone without an account.</strong> A ReelBolt review link lets a
        client or a boss watch one finished video without signing up, leave notes at exact moments,
        and approve it or ask for changes. HeyGen has video comments on every plan and a share page
        with comments, reactions and viewing analytics. Draft collaboration, a team workspace and
        role controls are part of its Business and Enterprise plans <Cite n={[2, 15]} />. Both
        cover review, in different ways.
      </p>
      <p>
        <strong>Hand-off to your own editor.</strong> ReelBolt exports OTIO, EDL and Final Cut XML
        and can prepare a hand-off package with the cut and your video files. That package is an
        Early preview, and we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut
        Pro. HeyGen’s AI Clipping offers an XML export of a clip “to continue editing in external
        software”, without naming the format or the editors, and HeyGen lets you download captions
        or the audio on their own <Cite n={[11, 13, 15]} />. We found no hand-off of a whole AI Studio
        project on the pages we read.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> Both tools keep a brand kit.
        HeyGen’s is part of its paid plans, and you can fill it by pasting your website address,
        which detects logos, colors and fonts, or by starting from a template{' '}
        <Cite n={[2, 14]} />. ReelBolt builds a brand kit from a brand PDF or a design-tool export,
        and it also reads design token and CSS files if your team keeps them. Reading your files is
        free. AI fills the gaps only after you confirm an estimate.
      </p>
      <p>
        <strong>A real person’s voice or likeness needs recorded consent.</strong> In ReelBolt a
        cloned voice and an avatar each need the person’s agreement, which you record in the
        project, and you can withdraw it or delete the voice or avatar at any time. The person
        behind an avatar must be an adult. HeyGen has its own consent steps for digital twins and
        voices, described in its documentation and terms <Cite n={[23, 24]} />. We do not compare
        the two processes here.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, a ReelBolt recipe
        makes a promo video from an app’s source code. HeyGen’s nearest equivalents are the
        website-to-video feature in its July 2026 release notes and HyperFrames, its open-source
        framework that lets AI agents make videos with HTML, CSS and JavaScript{' '}
        <Cite n={[1, 20]} />. We found no code repository input on the HeyGen pages we read{' '}
        <Cite n={[25]} />.
      </p>

      <h2 id="pricing-approach">Pricing approach</h2>
      <p>
        HeyGen has five plans: Free, Creator, Pro, Business and Enterprise <Cite n={[2]} />. The
        free plan does not use credits. It has a monthly quota instead: up to three videos, each up
        to a minute long and watermarked, and the help center says the number depends on your
        region <Cite n={[2, 16]} />. HeyGen’s own pages disagree about the resolution of free-plan
        videos, so we give no figure. Every paid plan includes a monthly credit allocation{' '}
        <Cite n={[2]} />. Watermark removal starts on Creator <Cite n={[2, 17]} />. Export goes up
        to 1080p on Creator and up to 4K on Pro and above <Cite n={[2, 13]} />. Business adds a fee
        for each extra seat, and the help center says extra seats share one credit pool and do not
        add credits <Cite n={[2, 16]} />. Enterprise goes through sales <Cite n={[2]} />.
      </p>
      <p>
        On monthly billing, unused HeyGen credits roll over for one more month. On annual billing
        they accumulate until the renewal date. Credits do not carry over after you cancel{' '}
        <Cite n={[2]} />. The API is billed separately, pay as you go <Cite n={[18]} />, and use
        through the connection for AI assistants draws on your plan’s credits <Cite n={[4]} />.
        Current figures are on{' '}
        <a href="https://www.heygen.com/pricing" rel="nofollow noopener" target="_blank">
          HeyGen’s pricing page<span className="sr-only"> (opens in a new tab)</span>
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
        Both tools show a cost before they spend your credits. HeyGen’s help center says Video
        Agent displays the estimated credits on a confirmation card before it generates, and that
        planning in chat uses none <Cite n={[6]} />. Before a ReelBolt run starts, you see the
        credits it is expected to use, as a likely amount and the range it usually falls in. If a
        step fails because of a problem on our side, the credits held for it are refunded. Top-up
        packs are for paid plans and expire after {catalog('topUp.expiresAfterMonths')} months.
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
      <p>Pick HeyGen if any of these is true:</p>
      <ul>
        <li>You have a script, a deck or an idea, and no footage.</li>
        <li>
          The video is a person talking for its whole length, such as a course module, a training
          video or sales outreach.
        </li>
        <li>Nobody can be filmed, and a ready-made presenter will do.</li>
        <li>You need the same video in several languages with matching lip movement.</li>
        <li>You want many short clips from one long recording.</li>
        <li>You want a free plan you can stay on, a phone app, or SSO for your company.</li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You want the real people and places in your footage on screen, not a presenter.</li>
        <li>You want a short video, about 90 seconds or less.</li>
        <li>You would rather fix a first cut than build one.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        A trainer with a script and a course to deliver is better served by HeyGen. A shop
        owner, a volunteer or a founder with a camera roll of clips and no editor is who ReelBolt
        is built for. A presenter video and a cut of real footage are different jobs, and nothing
        stops you from using a tool built for each.
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
          { id: 'source-1', label: 'HeyGen, home page', url: 'https://www.heygen.com/', accessed: CHECKED },
          { id: 'source-2', label: 'HeyGen, pricing', url: 'https://www.heygen.com/pricing', accessed: CHECKED },
          { id: 'source-3', label: 'HeyGen, FAQ', url: 'https://www.heygen.com/faq', accessed: CHECKED },
          { id: 'source-4', label: 'HeyGen developers, MCP overview', url: 'https://developers.heygen.com/mcp/overview', accessed: CHECKED },
          { id: 'source-5', label: 'HeyGen help, how to get started with Video Agent', url: 'https://help.heygen.com/en/articles/12402907-how-to-get-started-with-video-agent', accessed: CHECKED },
          { id: 'source-6', label: 'HeyGen help, Video Agent FAQ', url: 'https://help.heygen.com/en/articles/16007192-video-agent-faq', accessed: CHECKED },
          { id: 'source-7', label: 'HeyGen help, how to get started with video translation', url: 'https://help.heygen.com/en/articles/10029081-how-to-get-started-with-video-translation', accessed: CHECKED },
          { id: 'source-8', label: 'HeyGen help, how to use Screen Recorder in AI Studio', url: 'https://help.heygen.com/en/articles/14251628-how-to-use-screen-recorder-in-ai-studio', accessed: CHECKED },
          { id: 'source-9', label: 'HeyGen developers, filler word removal', url: 'https://developers.heygen.com/filler-word-removal.md', accessed: CHECKED },
          { id: 'source-10', label: 'HeyGen blog, June 2026 release', url: 'https://www.heygen.com/blog/heygen-june-2026-release', accessed: CHECKED },
          { id: 'source-11', label: 'HeyGen help, AI Clipping explained', url: 'https://help.heygen.com/en/articles/9278954-ai-clipping-explained', accessed: CHECKED },
          { id: 'source-12', label: 'HeyGen help, how to use captions', url: 'https://help.heygen.com/en/articles/8305536-how-to-use-captions', accessed: CHECKED },
          { id: 'source-13', label: 'HeyGen help, how to download or export a video', url: 'https://help.heygen.com/en/articles/9834825-how-to-download-or-export-a-video', accessed: CHECKED },
          { id: 'source-14', label: 'HeyGen help, how to create a Brand System', url: 'https://help.heygen.com/en/articles/9889198-how-to-create-a-brand-system', accessed: CHECKED },
          { id: 'source-15', label: 'HeyGen help, how to share your videos', url: 'https://help.heygen.com/en/articles/11788079-how-to-share-your-videos', accessed: CHECKED },
          { id: 'source-16', label: 'HeyGen help, credit-based pricing plans and subscriptions explained', url: 'https://help.heygen.com/en/articles/15125761-heygen-credit-based-pricing-plans-subscriptions-explained', accessed: CHECKED },
          { id: 'source-17', label: 'HeyGen help, how to remove the HeyGen watermark', url: 'https://help.heygen.com/en/articles/11057301-how-to-remove-the-heygen-watermark', accessed: CHECKED },
          { id: 'source-18', label: 'HeyGen help, API pricing and credits explained', url: 'https://help.heygen.com/en/articles/10060327-heygen-api-pricing-credits-explained', accessed: CHECKED },
          { id: 'source-19', label: 'HeyGen help, how to get started with HeyGen for mobile', url: 'https://help.heygen.com/en/articles/10207693-how-to-get-started-with-heygen-for-mobile', accessed: CHECKED },
          { id: 'source-20', label: 'HeyGen blog, July 2026 release', url: 'https://www.heygen.com/blog/heygen-july-2026-release', accessed: CHECKED },
          { id: 'source-21', label: 'HeyGen blog, October 2026 release', url: 'https://www.heygen.com/blog/heygen-october-2026-release', accessed: CHECKED },
          { id: 'source-22', label: 'HeyGen help, how to create b-roll with HeyGen Creative Studio', url: 'https://help.heygen.com/en/articles/17680953-how-to-create-b-roll-with-heygen-creative-studio', accessed: CHECKED },
          { id: 'source-23', label: 'HeyGen developers, avatar consent', url: 'https://developers.heygen.com/docs/avatar-consent.md', accessed: CHECKED },
          { id: 'source-24', label: 'HeyGen, terms', url: 'https://www.heygen.com/terms', accessed: CHECKED },
          { id: 'source-25', label: 'HeyGen developers, documentation index', url: 'https://developers.heygen.com/llms.txt', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on {CHECKED} from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
