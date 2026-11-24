import type { JSX } from 'react';
import Link from 'next/link';
import { Cite } from '@/components/blog/Cite';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { Figure } from '@/components/blog/Figure';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// Written 2026-11-11 for a reader who has raw footage and a story, and is not assumed to have a
// software product. Every statement about VEED comes from VEED's own public pages (site, help
// center, newsroom, changelog) and its App Store listing, read on 2026-11-11 and listed in the
// sources. Nothing was tested hands-on, no VEED price or credit figure appears, and no
// third-party model that VEED offers is named. VEED's user counts are quoted as the company's own
// claims. The plan table on VEED's pricing page could not be read on that date, so plan details
// come from its help center and are attributed to it. Left out on purpose because they could not
// be confirmed, or because VEED's own pages disagree: whether VEED exports a timeline for another
// editor, whether it has a desktop app, which links its "paste a link" import accepts, whether a
// shared video can be commented on without an account, which plans include voice cloning and
// custom avatars, API webhooks and SDK languages, the VEED Lite app, accuracy and speed figures,
// language counts other than the two quoted from its translator page, and any statement about
// training on user content. ReelBolt facts follow docs/user-guide (recipes/choose-a-recipe,
// derush-edit, subtitles, voiceover, run-settings, thumbnail-and-cover-text; workflows/
// request-changes; projects/avatars; design-system; editor/index, clean-your-audio,
// export-your-film, share-for-review; account/plans-and-credits, reading-the-estimate;
// workspaces). Every ReelBolt figure comes from the plan catalog or from the constants the Terms
// and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-veed',
  title: 'ReelBolt vs VEED: a full video platform, or one reviewed first cut?',
  description:
    'VEED is a mature platform that records, generates, edits, subtitles and dubs video. ReelBolt is a public alpha that turns raw clips into one reviewed first cut.',
  date: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 15,
  unlisted: false,
  faqs: [
    {
      q: 'Is ReelBolt a replacement for VEED?',
      a: 'No. VEED is a broad video platform: a recorder, a timeline editor, transcript editing, subtitles, translation, dubbing, avatars and AI video generation, in a browser and in phone apps. ReelBolt is a public alpha that does one narrower job. It takes your raw clips and a short brief, makes a short first cut, and reviews it before you see it. If you need one tool for everything you make, choose VEED.',
    },
    {
      q: 'Does VEED also have AI that cuts raw footage?',
      a: 'Yes. VEED’s own pages describe Auto Edits, which takes raw footage and removes pauses, cleans the audio, adds subtitles and inserts B-roll, and Magic Cut, which trims pauses, filler words and mistakes in one click and supports English only. We have not tested them, so we make no claim about which product cuts better.',
    },
    {
      q: 'Does ReelBolt record my screen or camera like VEED does?',
      a: 'No. VEED has a recorder for your screen, camera and audio that runs in the browser and includes a teleprompter. ReelBolt works on footage you already have. Its editor can record a voiceover from your microphone, and nothing else.',
    },
    {
      q: 'Which one dubs a video with lip sync?',
      a: 'VEED. Its dubbing has a lip sync option that matches the mouth to the translated speech. VEED’s help center says that option is in beta, takes longer to process and downsamples the video to 1080p. A ReelBolt dub changes the sound only, so the mouth on screen stays as it was filmed.',
    },
    {
      q: 'Does ReelBolt have stock avatars like VEED?',
      a: 'No. VEED offers over 60 stock avatars, or a digital clone of yourself. A ReelBolt avatar is a likeness of a real adult, made from that person’s own pictures, short videos and voice recordings. It appears in short clips of about 15 seconds at most, is part of the Pro plan and the plans above it, and is still being rolled out. There are no stock avatars and no long presenter videos.',
    },
    {
      q: 'Does either one post my video to social platforms?',
      a: 'No. VEED’s help center says you upload exported videos to other platforms yourself. ReelBolt works the same way: you download the finished video and post it. The ReelBolt assistant can write the text that goes with the post.',
    },
    {
      q: 'Can I use both?',
      a: `Yes. A ReelBolt run ends in an MP4 file you can download, and VEED’s help center lists MP4 among the video files it accepts. You can let ReelBolt make the first cut and finish it in VEED. On the ReelBolt free trial that file is ${TRIAL_RESOLUTION} with a watermark.`,
    },
    {
      q: 'Do I need a software product to use ReelBolt?',
      a: 'No. ReelBolt works from footage you filmed: a shop, an event, a lesson, a cause, a product. Some of its recipes make a promo video from an app’s source code; the footage recipes described here need no code.',
    },
    {
      q: 'Why are there no VEED prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and a figure copied here would go stale. VEED’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsVeed(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who has VEED and ReelBolt
        on the shortlist: a creator, a small business, a teacher, a nonprofit, an agency or a
        software team. We make ReelBolt, so read it with that in mind. Every statement about VEED
        comes from VEED’s own public pages, read on {CHECKED} and linked in the sources at the end.
        We did not test VEED hands-on, and we give no VEED prices.
      </p>
      <p>
        One thing belongs at the top. VEED is the far more established product. Its about page says
        the company was founded in 2018 and has a team of over 150 people <Cite n={[2]} />, and its
        tool pages carry the line “Trusted by 1M+ creators” <Cite n={[4]} />. That count is VEED’s
        own. ReelBolt is a public alpha and has no numbers like that to show. For many readers VEED
        is the better choice, and the first half of this page says where.
      </p>
      <p>
        The short version: VEED is a broad video platform. It records your screen and camera,
        generates video from a prompt, edits on a timeline or through the transcript, and
        subtitles, translates and dubs the result. ReelBolt does one narrower job. You drop in raw
        clips and say what the video is for, and an AI team makes a short first cut and reviews it
        before you see it. If you need to record, to generate, to reach many languages or to do all
        your editing in one place, choose VEED. If the blank timeline is what keeps your video from
        getting made, that is the job ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        VEED calls itself “an AI video creation platform”: you type an idea, generate it, then
        brand it <Cite n={[2]} />. The description of its home page begins “Generate and edit in
        one workflow” and says it is “Built for marketers and solopreneurs” <Cite n={[1]} />. Its
        about page names “marketers, founders, and creators” <Cite n={[2]} />. The home page
        headline differed between our visits. The versions we saw led with AI-made video for social
        posts, and with a clone of you made from one photo and your voice <Cite n={[1]} />. The
        about page puts the idea behind the product in two sentences: “Most video tools were built
        for editors. VEED was built for everyone else.” <Cite n={[2]} />
      </p>
      <p>
        Under that sits a full editor. VEED’s help center says the timeline “is the most used place
        to edit and time the elements in your project” <Cite n={[25]} />. A tool named Edit by
        Script cuts a video through its transcript: “Delete a word and the matching footage
        disappears” <Cite n={[26]} />. For raw footage there is Auto Edits. VEED’s page says
        “Upload your raw footage and let Auto Edits handle the rest”, and lists what it does:
        removing pauses, cleaning the audio, adding subtitles and inserting B-roll{' '}
        <Cite n={[5]} />. A dashboard named VEED AI, which VEED says reached all users on 21 August
        2026, is built around a prompt, a script and a video <Cite n={[18]} />. VEED runs in a
        browser <Cite n={[4]} /> and has phone apps <Cite n={[20, 49, 55]} />.
      </p>
      <p>
        ReelBolt is an AI video team for anyone with a story to tell and no time to tell it. It is a
        public alpha, so expect some rough edges. You upload the clips you already shot, between 1
        and 20 for a highlight edit, and write a sentence or two about what the video is for. An AI
        editor chooses what to keep and leaves out dead air, false starts and retakes. A reviewer
        scores the draft, and a low score sends it back for another pass before you see it. The
        result is one short video, about 90 seconds or less, in the shape you chose. A run takes
        minutes for a small job and sometimes half an hour for a big one.
      </p>
      <p>
        Each kind of video is a recipe: a ready-made app you pick, fill in and run. Raw clips are
        the usual way in, not the only one. For software teams there is also a recipe that makes a
        promo video from an app’s source code.
      </p>

      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={`ReelBolt and VEED compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'VEED']}
        rows={[
          {
            label: 'Stage',
            cells: [
              'A public alpha with rough edges',
              { text: <>An established product: founded in 2018, a team of over 150 people, and “1M+ creators” by its own count <Cite n={[2, 4]} /></> },
            ],
          },
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>Video, audio and image files you upload; a recording you make in the browser; or a prompt, a script or the text of an article <Cite n={[7, 22, 23]} /></> },
            ],
          },
          {
            label: 'Recording',
            cells: [
              'No screen or camera recorder; the editor records a voiceover',
              { text: <>Screen, camera and audio recorder in the browser, with a teleprompter, on all plans; not from mobile devices <Cite n={[8, 24]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor, then a reviewer that scores the draft before you see it',
              { text: <>You, on the timeline or in the transcript; or its automatic tools, Auto Edits and Magic Cut <Cite n={[5, 25, 26, 27]} /></> },
            ],
          },
          {
            label: 'Transcript editing',
            cells: [
              'Yes, in the editor, when the footage was transcribed',
              { text: <>Edit by Script, on all plans, for languages that separate words with spaces <Cite n={[26]} /></> },
            ],
          },
          {
            label: 'Filler words, pauses and retakes',
            cells: [
              'Left out of the first cut; the editor can also remove silence and find fillers',
              { text: <>Magic Cut trims pauses, filler words and mistakes in one click, in English only; a separate tool removes complete silences <Cite n={[27, 28]} /></> },
            ],
          },
          {
            label: 'Captions and subtitles',
            cells: [
              'Burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>Automatic subtitles, burned in or as SRT, VTT and TXT files; its help center puts the file download on Pro and Enterprise; translation uses AI credits <Cite n={[9, 29, 30]} /></> },
            ],
          },
          {
            label: 'Dubbing',
            cells: [
              'Yes, sound only: no lip sync',
              { text: <>Dubbing in a cloned or a stock voice; lip sync is optional, in beta, and downsamples the video to 1080p <Cite n={[11, 31]} /></> },
            ],
          },
          {
            label: 'Voice',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker',
              { text: <>Text to speech; a clone of your own voice, one per workspace <Cite n={[13, 34]} /></> },
            ],
          },
          {
            label: 'Avatars',
            cells: [
              'A real adult, from their own pictures and recordings, in clips of about 15 seconds at most; Pro and up, still rolling out; no stock avatars',
              { text: <>Over 60 stock avatars, or a clone of yourself; uses AI credits; one avatar on screen; not available in five countries <Cite n={[12, 32, 33]} /></> },
            ],
          },
          {
            label: 'Generated video and music',
            cells: [
              'AI-generated clips on Pro and up; music and sound-effect generation on Creator and up; no built-in music library',
              { text: <>Generates video from prompts, scripts and images with several video models from other companies; a library of stock video and royalty-free audio <Cite n={[4, 6]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>Brand Kits: none on Free, one with fonts and colors on Creator, several with all asset types on Pro and up <Cite n={[35]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>Resize presets for YouTube, Instagram Stories, TikTok, LinkedIn and square; AI Background Expand between 9:16 and 16:9, using AI credits <Cite n={[15, 37]} /></> },
            ],
          },
          {
            label: 'Short clips from one long video',
            cells: [
              'No: each run makes one short video',
              { text: <>Yes: Clips, from a video with 1 minute to 3 hours of speech; each clip is scored <Cite n={[14, 36]} /></> },
            ],
          },
          {
            label: 'Recipes and templates',
            cells: [
              'Recipes: ready-made apps you pick, fill in and run',
              { text: <>Ready-made templates; custom templates on Studio and Enterprise <Cite n={[40]} /></> },
            ],
          },
          {
            label: 'Posting to social platforms',
            cells: [
              'No: you download the video; the assistant can write the post text',
              { text: <>No: its help center says you upload exported videos to other platforms yourself <Cite n={[44]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>Comments on shared videos; free project viewers who see only the exported video; paid plans billed per seat, with up to 3 editors on Free, Creator and Pro and 5 on Studio <Cite n={[41, 42, 43, 44]} /></> },
            ],
          },
          {
            label: 'Version history',
            cells: [
              'In the editor: automatic checkpoints kept for 30 days; a version you name does not expire',
              { text: <>On all plans; stored on your own computer, so available only on the computer where the edits were made <Cite n={[45]} /></> },
            ],
          },
          {
            label: 'Export',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>MP4, MP3 or GIF; 720p on Free, up to 1080p on Creator, up to 4K on Pro and above, per its help center <Cite n={[46, 47]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits',
              { text: <>In a browser; an app for iPhone and iPad; an Android app that VEED says is on Google Play in selected countries <Cite n={[4, 20, 55]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>A separate API for its AI video models, with its own account and credits; a connection for AI tools such as Claude and Make.com; OpenEdit, a free pipeline for coding agents, in beta <Cite n={[16, 50, 51]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A standing free plan: basic editing, 720p exports with a watermark, files up to 1 GB, and one-time trials of several AI tools <Cite n={[4, 22, 47]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-veed-is-stronger">Where VEED is the better choice</h2>
      <p>
        <strong>It is the established product.</strong> VEED was founded in March 2018{' '}
        <Cite n={[2]} />, and its home page carries the line “Powering millions of teams globally”{' '}
        <Cite n={[1]} />. That is the company’s own statement. It has a help center with an article
        for most features, and live support by chat and email from Monday to Friday, with a stated
        response time of 2 to 8 hours for paying customers <Cite n={[54]} />. ReelBolt is a public
        alpha with rough edges. If you need a product that is well past its alpha, VEED is the
        safer choice.
      </p>
      <p>
        <strong>VEED records.</strong> Its recorder captures your screen, your camera and your
        audio from the browser, includes a teleprompter, and is available on all plans{' '}
        <Cite n={[24]} />. The recorder page says recording from mobile devices is not supported{' '}
        <Cite n={[8]} />. ReelBolt does not record your screen or camera. You can record a voiceover
        in its editor, and everything else is footage you bring.
      </p>
      <p>
        <strong>One place for everything you make.</strong> VEED is a general editor. It has a
        timeline with layered tracks <Cite n={[25]} />, transcript editing on all plans{' '}
        <Cite n={[26]} />, ready-made templates <Cite n={[40]} />, and a library of stock videos
        and royalty-free audio <Cite n={[4]} />. It also generates video from text prompts, scripts
        and images, using several video models from other companies <Cite n={[6]} />, and you can
        paste a script or an article to turn into a video <Cite n={[7, 23]} />. ReelBolt has an
        editor, labeled Early preview, but no library of video templates to start from, no stock
        footage and no built-in music library. On its free trial there is no recipe that turns
        only a text prompt into a video. AI-generated clips are part of the Pro plan and the plans
        above it.
      </p>
      <p>
        <strong>Long recordings.</strong> VEED’s help center says paid plans have no size limit per
        file. It recommends keeping a project under 5 GB in total and around 4 to 5 hours long,
        and says the free plan takes files of up to 1 GB <Cite n={[22]} />. ReelBolt is built for
        short videos, and each source video has a length and size limit, given on the{' '}
        <a href="/docs/recipes/derush-edit">highlight edit page</a> of the docs. For an hour-long
        interview, webinar or lesson you mean to keep most of, VEED is the better tool.
      </p>
      <p>
        <strong>Many short clips from one long video.</strong> VEED’s Clips tool takes a video with
        between 1 minute and 3 hours of speech and returns short clips <Cite n={[36]} />. Each clip
        is scored out of 10 for Flow, Impact, Clarity and Relevance <Cite n={[14]} />, and
        auto-framing keeps the speaker in the middle of the frame at any aspect ratio{' '}
        <Cite n={[36]} />. The help center says Clips is unlimited on paid plans and a single try
        on the free plan <Cite n={[36]} />. ReelBolt makes one short video per run.
      </p>
      <p>
        <strong>Subtitles and dubbing in many languages.</strong> VEED’s translator page lists
        “125+ subtitle languages” and says it preserves the speaker’s voice across 29 languages{' '}
        <Cite n={[10]} />. Its dubbing can clone the original voice or use a stock voice, and has a
        lip sync option that matches the mouth to the translated speech <Cite n={[11]} />. The help
        center states the limits. Lip sync is in beta, takes longer to process and downsamples the
        video to 1080p. Dubbing is a single try on the Free and Creator plans and is counted in
        hours per year on Pro and Studio <Cite n={[31]} />. ReelBolt can burn in subtitles,
        translate them and dub a video, but a ReelBolt dub changes the sound only. The mouth on
        screen stays as it was filmed.
      </p>
      <p>
        <strong>A presenter nobody has to film.</strong> VEED offers “60+ stock avatars”, or a
        digital clone of yourself that you make by recording a script it gives you{' '}
        <Cite n={[12]} />. Its help center says avatars use AI credits, that one generation takes a
        script of up to 20,000 characters, and that only one avatar can be on screen{' '}
        <Cite n={[32]} />. It also says avatars are not available in Bangladesh, India, Indonesia,
        Pakistan or the Philippines <Cite n={[33]} />. ReelBolt’s{' '}
        <Link href="/blog/reelbolt-avatars">avatars</Link> are narrower. Each one is made
        from a real adult’s own pictures and recordings, and it appears only in short clips, about
        15 seconds at most. Avatars are part of the Pro plan and the plans above it and are still being rolled
        out. If you need a presenter to carry a whole video and nobody wants to be filmed, VEED
        covers that and ReelBolt does not.
      </p>
      <p>
        <strong>Fixes for a talking video.</strong> VEED’s Magic Cut trims pauses, filler words,
        mistakes and silences in one click, on all plans. VEED notes that it supports English only{' '}
        <Cite n={[27]} />. It also has eye contact correction and a Clean Audio tool, each a single
        try on the free plan <Cite n={[38, 39]} />. ReelBolt leaves dead air and retakes out of the
        first cut, and its editor can remove silence and clean up a voice. It has no eye contact
        correction.
      </p>
      <p>
        <strong>A new shape for a video you already finished.</strong> VEED resizes a video to
        presets for YouTube, Instagram Stories, TikTok, LinkedIn and square <Cite n={[15]} />. Its
        AI Background Expand fills in the background when a video goes between 9:16 and 16:9, and
        uses AI credits each time <Cite n={[37]} />. ReelBolt cannot yet re-cut a finished video to
        another shape. You choose the shape before a run, and a second shape is another run.
      </p>
      <p>
        <strong>Phone apps.</strong> VEED has an app for iPhone and iPad <Cite n={[55]} /> and says
        an Android version is on Google Play in selected countries <Cite n={[20]} />. Its help
        center says the apps and the browser are “the same product, the same account, and the same
        projects” <Cite n={[49]} />. ReelBolt has no phone app. It runs in a browser, and its
        editor has a phone view for simpler edits.
      </p>
      <p>
        <strong>A free plan you can stay on.</strong> VEED’s pricing page says “Start for free”{' '}
        <Cite n={[3]} />. Its editor page says free users get basic editing tools, 720p exports and
        one-time trials of AI tools such as Auto Edits, and that free exports include a watermark{' '}
        <Cite n={[4]} />. ReelBolt has a free trial with one-time credits. It is not a plan you can
        stay on.
      </p>
      <p>
        <strong>Reach into other tools, and company requirements.</strong> VEED offers an API as a
        separate product. It gives developers VEED’s AI video models, such as lip sync, dubbing and
        background removal, under its own account and its own prepaid credits <Cite n={[50]} />.
        VEED also has a connection that lets AI tools such as Claude and Make.com generate a
        talking video from an image <Cite n={[16]} />, and OpenEdit, a free pipeline in beta that a
        coding agent drives on your own machine <Cite n={[51]} />. Its changelog lists integrations
        with Make.com and ChatGPT <Cite n={[21]} />. On the company side, VEED states that it has
        achieved SOC 2 compliance <Cite n={[52]} />, that its servers are in the EU, and that data
        processing agreements are available to Enterprise customers <Cite n={[53]} />. ReelBolt has{' '}
        <Link href="/blog/reelbolt-public-api">an API with webhooks</Link> on its paid plans and a
        connection for Claude Code, but the whole product is a public alpha. If your company has
        security or compliance requirements today, VEED is the safer choice.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>It makes the first cut, then checks it.</strong> ReelBolt assumes you would rather
        react to a cut than build one. You do not open a timeline, pick a template or choose a tool
        first. An AI editor decides what to keep, a reviewer scores the result, and a low score
        sends the edit back for another pass before you see it. VEED has tools on similar ground.
        Auto Edits takes raw footage and removes pauses, cleans the audio, adds subtitles and
        inserts B-roll <Cite n={[5]} />, and Magic Cut trims pauses and filler words{' '}
        <Cite n={[27]} />. We have not tested them, so we make no claim about which product cuts
        better. The difference we can state is where each one starts. VEED is a platform of tools,
        with AI in many of them. ReelBolt is a brief and a run, with an editor for afterwards.
      </p>
      <p>
        <strong>It is built for a camera roll and a short video.</strong> Give it the clips from
        your phone: the shop, the event, the lesson, the product. Several different shots give the
        editor something to cut together. One long take can only be trimmed. When the speech in
        your clips could be read, cuts are placed so they do not land inside a spoken word.{' '}
        <Link href="/blog/what-a-highlight-edit-removes">What a highlight edit removes</Link> shows
        the kind of thing that gets cut.
      </p>
      <p>
        <strong>You stay in charge after the first cut.</strong> You can open the cut in ReelBolt’s
        editor, labeled Early preview, to move or trim any clip, remove silence, and edit from
        the transcript when the footage was transcribed. The editor keeps versions you can go back
        to: automatic checkpoints for 30 days, and any version you name for as long as you keep it.
        You can also run the recipe again with a different note. When a video has narration,
        generated clips or a cover picture, you can ask for one of those parts to be made again.
        The parts you did not touch are reused from the earlier run at no extra cost, and the price
        of the change is shown before you confirm. If someone else will finish the work, ReelBolt
        exports the cut as OTIO, EDL and Final Cut XML. Its hand-off package is an Early preview,
        and we have not yet opened one in Premiere Pro, DaVinci Resolve or Final Cut Pro. VEED is a
        full editor, so changing a video there is no gap on its side.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one audio clip on another and a Remove silence button above them."
        width={1280}
        height={214}
        caption="A demo timeline in the editor (Early preview), with the clips of a cut on their tracks."
      />
      <p>
        <strong>Recipes for the job, not a toolbox.</strong> You pick what you want to end up with,
        and the recipe’s page asks only for what that needs and tells you when something is
        missing. One recipe makes the highlight edit. Others add titles and callouts, write and
        voice a narration with captions, burn in subtitles and produce the SRT and VTT files,
        translate the subtitles, or dub the video. The run page has no download button for the
        subtitle files yet, which is one of the rough edges. One recipe makes a cover picture for very few credits, and the assistant can write the text that goes with the post. Music and sound
        effects come from files you upload, because there is no built-in music library.
      </p>
      <p>
        <strong>You see the cost before you spend it.</strong> Before a run starts, ReelBolt shows
        the credits it is expected to use, as a likely amount and the range it usually falls in. A
        step can finish a little over its estimate, and the difference is charged. If a step fails
        because of a problem on our side, the credits held for it are refunded. VEED shows a cost
        up front too. Its announcement of the VEED AI dashboard says “The credit cost is shown
        before you generate” <Cite n={[18]} />. So this is common ground, not a gap.
      </p>
      <Figure
        name="run-estimate"
        alt="The run estimate in ReelBolt, showing the estimated credits for a run, a rough-estimate note and the credits the workspace has available."
        width={1240}
        height={218}
        caption="Look at the credit estimate, which appears before the run starts."
      />
      <p>
        <strong>A review link for someone without an account.</strong> A ReelBolt review link lets
        a client or a boss watch one finished video without signing up, leave notes at exact
        moments, and approve it or ask for changes. The link shows only that one video, expires,
        and can be turned off. VEED has its own ways to share. You can share a video and switch on
        comments <Cite n={[44]} />, and a project viewer, who is free to invite, sees only the
        exported video <Cite n={[43]} />.
      </p>
      <p>
        <strong>A real person’s voice or likeness needs recorded consent.</strong> In ReelBolt a
        cloned voice and an avatar each need the person’s agreement, which you record in the
        project, and you can withdraw it or delete the voice or avatar at any time. The person
        behind an avatar must be an adult. VEED has its own consent steps for voice clones and
        avatars, described on its voice cloning page, in its help center, in its newsroom and in
        its terms for AI tools <Cite n={[13, 17, 19, 34]} />. We do not compare the two processes
        here.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> VEED’s Brand Kits hold your
        fonts, colors and other assets. Its help center says the free plan has none, the Creator
        plan has one that is limited to fonts and colors, and Pro, Studio and Enterprise have
        several with all asset types <Cite n={[35]} />. ReelBolt builds a brand kit from a brand
        PDF or a design-tool export, and it also reads design token and CSS files if your team
        keeps them. Reading your files is free. AI fills the gaps only after you confirm an
        estimate.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, some ReelBolt
        recipes make a promo video from an app’s source code. They sit among many others, and the
        footage recipes described here need no code.
      </p>

      <h2 id="pricing-approach">Plans and credits</h2>
      <p>
        VEED’s help center names five plans: Free, Creator, Pro, Studio and Enterprise{' '}
        <Cite n={[47]} />. Some other VEED pages use different plan names <Cite n={[48]} />, and we
        could not read the plan table on its pricing page on the day we checked. Every plan detail
        on this page therefore comes from VEED’s help center, and the pricing page is the place to
        confirm it. Paid plans are billed per seat, and pending invitations count as seats{' '}
        <Cite n={[42]} />. Plans include AI credits each month or year, and the pricing page says
        credits do not carry over <Cite n={[3]} />. The same page says all paid plans include
        subtitle generation under a fair use limit of 50 hours a month, and that a free trial is
        offered to enterprise customers, with a minimum of 3 users <Cite n={[3]} />. The help
        center says AI credits on the free plan are available in some regions and not in others{' '}
        <Cite n={[56]} />. Current figures are on{' '}
        <a href="https://www.veed.io/pricing" rel="nofollow noopener" target="_blank">
          VEED’s pricing page<span className="sr-only"> (opens in a new tab)</span>
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
        and avatars are part of Pro and the plans above it. Top-up packs are for paid plans and
        expire after {catalog('topUp.expiresAfterMonths')} months. The{' '}
        <Link href="/pricing">pricing page</Link> has the current prices and what each plan
        includes.
      </p>
      <p>
        A credit buys something different at each company, so comparing one tool’s credit with
        another’s will mislead you. Compare the cost of the video you actually need.
      </p>

      <h2 id="which-one-should-you-pick">Which one should you pick</h2>
      <p>Pick VEED if any of these is true:</p>
      <ul>
        <li>You need to record yourself or your screen.</li>
        <li>You want one editor for everything you make, with templates, stock footage and music.</li>
        <li>You want to start from a prompt or a script, with no footage of your own.</li>
        <li>You have one long video and want several short clips from it.</li>
        <li>
          You need subtitles or a dub in many languages, a dub with lip sync, or a presenter nobody
          has to film.
        </li>
        <li>You want a phone app, or a free plan you can stay on.</li>
        <li>You need a product that is well past its alpha, or your company asks about SOC 2.</li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You want one short video, about 90 seconds or less.</li>
        <li>You would rather fix a reviewed first cut than build one.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        A marketer who records, generates and translates video every week has good reasons to stay
        with VEED. A shop owner, a volunteer or a founder with a camera roll of clips and no editor
        is who ReelBolt is built for. Nothing stops you from using both: a ReelBolt run ends in an
        MP4 file you can download, and VEED lists MP4 among the files it accepts{' '}
        <Cite n={[22]} />, so the first cut can come from one and the finishing from the other.
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
          { id: 'source-1', label: 'VEED, home page', url: 'https://www.veed.io/', accessed: CHECKED },
          { id: 'source-2', label: 'VEED, about', url: 'https://www.veed.io/about', accessed: CHECKED },
          { id: 'source-3', label: 'VEED, pricing', url: 'https://www.veed.io/pricing', accessed: CHECKED },
          { id: 'source-4', label: 'VEED, online video editor', url: 'https://www.veed.io/tools/video-editor', accessed: CHECKED },
          { id: 'source-5', label: 'VEED, AI video editor', url: 'https://www.veed.io/tools/ai-video/ai-video-editor', accessed: CHECKED },
          { id: 'source-6', label: 'VEED, AI video generator', url: 'https://www.veed.io/tools/ai-video', accessed: CHECKED },
          { id: 'source-7', label: 'VEED, text to video', url: 'https://www.veed.io/tools/ai-video/text-to-video', accessed: CHECKED },
          { id: 'source-8', label: 'VEED, screen recorder', url: 'https://www.veed.io/tools/screen-recorder', accessed: CHECKED },
          { id: 'source-9', label: 'VEED, auto subtitle generator', url: 'https://www.veed.io/tools/auto-subtitle-generator-online', accessed: CHECKED },
          { id: 'source-10', label: 'VEED, video translator', url: 'https://www.veed.io/tools/video-translator', accessed: CHECKED },
          { id: 'source-11', label: 'VEED, AI dubbing', url: 'https://www.veed.io/tools/voice-dubber/ai-dubbing', accessed: CHECKED },
          { id: 'source-12', label: 'VEED, AI avatar', url: 'https://www.veed.io/tools/ai-avatar', accessed: CHECKED },
          { id: 'source-13', label: 'VEED, AI voice cloning', url: 'https://www.veed.io/tools/ai-voice-cloning', accessed: CHECKED },
          { id: 'source-14', label: 'VEED, AI clip generator', url: 'https://www.veed.io/tools/auto-video-editor/ai-clip-generator', accessed: CHECKED },
          { id: 'source-15', label: 'VEED, resize video', url: 'https://www.veed.io/tools/resize-video', accessed: CHECKED },
          { id: 'source-16', label: 'VEED, MCP', url: 'https://www.veed.io/tools/veed-mcp', accessed: CHECKED },
          { id: 'source-17', label: 'VEED, terms of use for AI tools', url: 'https://www.veed.io/terms-of-use/ai-tools', accessed: CHECKED },
          { id: 'source-18', label: 'VEED newsroom, VEED AI is live', url: 'https://www.veed.io/newsroom/veed-ai-is-live', accessed: CHECKED },
          { id: 'source-19', label: 'VEED newsroom, AI Headshot Generator can now present your videos', url: 'https://www.veed.io/newsroom/ai-headshot-generator-can-now-present-your-videos', accessed: CHECKED },
          { id: 'source-20', label: 'VEED newsroom, VEED is now on iPhone', url: 'https://www.veed.io/newsroom/veed-is-now-on-iphone', accessed: CHECKED },
          { id: 'source-21', label: 'VEED, changelog', url: 'https://changelog.veed.io/', accessed: CHECKED },
          { id: 'source-22', label: 'VEED help, upload video, audio, music or image files', url: 'https://support.veed.io/en/articles/10657034-how-to-upload-video-audio-music-or-image-files', accessed: CHECKED },
          { id: 'source-23', label: 'VEED help, how the dashboard works', url: 'https://support.veed.io/en/articles/11641509-how-veed-s-dashboard-works', accessed: CHECKED },
          { id: 'source-24', label: 'VEED help, the recorder', url: 'https://support.veed.io/en/articles/11099678-how-to-use-veeds-recorder', accessed: CHECKED },
          { id: 'source-25', label: 'VEED help, the timeline', url: 'https://support.veed.io/en/articles/10804541-what-you-need-to-know-about-veeds-timeline', accessed: CHECKED },
          { id: 'source-26', label: 'VEED help, Edit by Script', url: 'https://support.veed.io/en/articles/11137955-how-to-use-our-edit-by-script-tool', accessed: CHECKED },
          { id: 'source-27', label: 'VEED help, Magic Cut', url: 'https://support.veed.io/en/articles/11589317-how-to-use-our-magic-cut-tool', accessed: CHECKED },
          { id: 'source-28', label: 'VEED help, the silence remover', url: 'https://support.veed.io/en/articles/11172102-how-to-use-the-silence-remover-tool', accessed: CHECKED },
          { id: 'source-29', label: 'VEED help, add subtitles automatically', url: 'https://support.veed.io/en/articles/11172739-how-to-add-subtitles-to-your-video-automatically', accessed: CHECKED },
          { id: 'source-30', label: 'VEED help, translate your subtitles', url: 'https://support.veed.io/en/articles/11508601-how-to-translate-your-subtitles', accessed: CHECKED },
          { id: 'source-31', label: 'VEED help, the AI dubbing tool', url: 'https://support.veed.io/en/articles/10406623-how-to-use-the-ai-dubbing-tool', accessed: CHECKED },
          { id: 'source-32', label: 'VEED help, AI avatars or talking characters', url: 'https://support.veed.io/en/articles/8222240-how-to-create-ai-avatars-or-talking-characters', accessed: CHECKED },
          { id: 'source-33', label: 'VEED help, Gen-AI Studio', url: 'https://support.veed.io/en/articles/12303063-how-to-use-veeds-gen-ai-studio', accessed: CHECKED },
          { id: 'source-34', label: 'VEED help, text to speech', url: 'https://support.veed.io/en/articles/11662445-how-to-use-text-to-speech', accessed: CHECKED },
          { id: 'source-35', label: 'VEED help, Brand Kits', url: 'https://support.veed.io/en/articles/9616498-how-to-create-and-manage-brand-kits', accessed: CHECKED },
          { id: 'source-36', label: 'VEED help, Clips', url: 'https://support.veed.io/en/articles/11652474-how-to-use-our-clips-feature', accessed: CHECKED },
          { id: 'source-37', label: 'VEED help, AI Background Expand', url: 'https://support.veed.io/en/articles/12782199-how-to-use-the-ai-background-expand-tool', accessed: CHECKED },
          { id: 'source-38', label: 'VEED help, eye contact correction', url: 'https://support.veed.io/en/articles/11172032-how-to-use-the-eye-contact-correction-tool', accessed: CHECKED },
          { id: 'source-39', label: 'VEED help, Clean Audio', url: 'https://support.veed.io/en/articles/11662250-how-to-use-the-clean-audio-tool', accessed: CHECKED },
          { id: 'source-40', label: 'VEED help, templates', url: 'https://support.veed.io/en/articles/11550463-how-to-create-and-use-templates', accessed: CHECKED },
          { id: 'source-41', label: 'VEED help, add a collaborator', url: 'https://support.veed.io/en/articles/10539434-how-to-add-a-collaborator', accessed: CHECKED },
          { id: 'source-42', label: 'VEED help, how collaborator charges are calculated', url: 'https://support.veed.io/en/articles/8517339-how-collaborator-charges-are-calculated', accessed: CHECKED },
          { id: 'source-43', label: 'VEED help, invite a project viewer', url: 'https://support.veed.io/en/articles/10539457-how-to-invite-a-project-viewer', accessed: CHECKED },
          { id: 'source-44', label: 'VEED help, share your videos', url: 'https://support.veed.io/en/articles/11029688-how-to-share-your-videos', accessed: CHECKED },
          { id: 'source-45', label: 'VEED help, version control', url: 'https://support.veed.io/en/articles/11848535-how-version-control-works-at-veed', accessed: CHECKED },
          { id: 'source-46', label: 'VEED help, download your project', url: 'https://support.veed.io/en/articles/10531489-how-to-download-your-project', accessed: CHECKED },
          { id: 'source-47', label: 'VEED help, export your project', url: 'https://support.veed.io/en/articles/10531327-how-to-export-your-project', accessed: CHECKED },
          { id: 'source-48', label: 'VEED help, watermarks', url: 'https://support.veed.io/en/articles/7056370-will-the-videos-i-created-have-a-veed-watermark-on-them', accessed: CHECKED },
          { id: 'source-49', label: 'VEED help, subscriptions across mobile and web', url: 'https://support.veed.io/en/articles/16658823-how-do-veed-subscriptions-work-across-mobile-and-web-platforms-and-how-can-i-access-features-on-both', accessed: CHECKED },
          { id: 'source-50', label: 'VEED help, API frequently asked questions', url: 'https://support.veed.io/en/articles/17229001-veed-api-frequently-asked-questions', accessed: CHECKED },
          { id: 'source-51', label: 'VEED help, OpenEdit', url: 'https://support.veed.io/en/articles/16342833-how-to-use-openedit-veed-s-agent-driven-video-editor', accessed: CHECKED },
          { id: 'source-52', label: 'VEED help, SOC 2 compliance', url: 'https://support.veed.io/en/articles/10407864-soc-2-compliance', accessed: CHECKED },
          { id: 'source-53', label: 'VEED help, security FAQs', url: 'https://support.veed.io/en/articles/10406942-veed-security-faqs', accessed: CHECKED },
          { id: 'source-54', label: 'VEED help center', url: 'https://support.veed.io/en/', accessed: CHECKED },
          { id: 'source-55', label: 'Apple App Store, VEED: AI Video Creation', url: 'https://apps.apple.com/app/veed-ai-video-creation/id6776691383', accessed: CHECKED },
          { id: 'source-56', label: 'VEED help, the AI video generator', url: 'https://support.veed.io/en/articles/11579825-how-to-use-ai-video-generator', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Features checked on {CHECKED} from each vendor’s public pages; they may have changed. The plan table on VEED’s pricing page could not be read that day, so the plan details come from its help center. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
