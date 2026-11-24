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
// assumed to have a software product. Every statement about invideo comes from invideo's own public
// pages, read on 2026-11-11 and listed in the sources. Nothing was tested hands-on, and no invideo
// price appears anywhere. Left out on purpose because they could not be confirmed on that date:
// 4K export of your own footage from Starter (the pricing page no longer says it; invideo's docs say
// export is 1080p), the old help article on the free plan (gone; the plan-limits page and the terms
// replace it), the free plan's reset day, watermark and resolution, invideo's phone apps, where SSO
// starts on its business plans, the annual-billing promotion, and any statement about training on
// user data. Model names are left out too. ReelBolt facts follow docs/user-guide
// (recipes/choose-a-recipe, derush-edit, subtitles, voiceover; projects/avatars, history;
// design-system; editor/index, export-your-film, share-for-review; account/plans-and-credits;
// api/index; assistant/claude-code). Every ReelBolt figure comes from the plan catalog or from the
// constants the Terms and the pricing page already use.
const CHECKED = '2026-11-11';
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CREATOR_RESOLUTION = catalog('creator.entitlements.maxOutputHeight');
const PRO_RESOLUTION = catalog('pro.entitlements.maxOutputHeight');

export const meta: PostMeta = {
  slug: 'reelbolt-vs-invideo',
  title: 'ReelBolt vs invideo: an editor with an AI agent, or a finished first cut?',
  description:
    'invideo is a timeline editor with AI agents and many AI models. ReelBolt cuts the clips you already shot into a short finished video. See which fits you.',
  date: '2026-10-17',
  updated: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 14,
  unlisted: false,
  faqs: [
    {
      q: 'Do I need a software product to use ReelBolt?',
      a: 'No. ReelBolt works from footage you filmed: a shop, an event, a lesson, a cause, a product. You upload the clips and write a sentence or two about what the video is for. Some of its recipes make a promo video from an app’s source code; the footage recipes described here need no code.',
    },
    {
      q: 'I have no footage at all. Which one should I use?',
      a: 'invideo. Its editor gives access to a large set of image, video and audio models, and its docs describe turning a slide deck or a document into a narrated video. ReelBolt is built around footage you bring. Nothing on its free trial turns a prompt or a set of photos into a video.',
    },
    {
      q: 'Can invideo make a first cut from raw footage too?',
      a: 'Yes, going by its own docs. They say Agent Two, the agent in invideo’s editor, reviews your footage and assembles a first cut on the timeline. We have not tested it, so we make no claim about which tool cuts better. The difference we can state is how you work: invideo is an editor you sit in with an agent beside you, and ReelBolt takes your clips and a short brief and hands back a draft that has already been reviewed.',
    },
    {
      q: 'Can I finish the edit in Premiere Pro, Final Cut Pro or DaVinci Resolve?',
      a: 'With both, in different states of maturity. invideo exports an editable project for those three editors with the media packaged, and lists what to check after opening it. ReelBolt exports OTIO, EDL and Final Cut XML and can prepare a hand-off package, but that package is an Early preview and we have not yet opened one in those editors.',
    },
    {
      q: 'Can a client comment on a cut without making an account?',
      a: 'In ReelBolt, yes: a review link lets a guest watch one finished video, leave notes at exact moments and approve it or ask for changes, with no account. invideo’s docs describe a Preview only role for reviewers and say commenting always needs an invideo login.',
    },
    {
      q: 'Does ReelBolt make AI avatars?',
      a: 'In a narrow form. A ReelBolt avatar is a likeness of a real adult, made from that person’s own pictures, short videos and voice recordings. It appears in short clips of about 15 seconds at most, is part of the Pro plan and the plans above it, and is still being rolled out. There are no stock avatars. invideo offers avatar clones of yourself, human actors and virtual actors.',
    },
    {
      q: 'Can I use either tool from Claude Code or another AI assistant?',
      a: 'Yes. invideo’s docs send terminal agents such as Claude Code, Codex and Cursor to its command-line tool and keep invideo MCP for AI apps without a shell. ReelBolt connects to Claude Code, which then works on your projects with the same permissions as the assistant in its app. ReelBolt is a public alpha, so expect that connection to change.',
    },
    {
      q: 'Does either one have a free plan?',
      a: 'invideo’s terms describe a free tier with limited features, and its help pages say the Free plan has a weekly limit. It is not listed on invideo’s pricing page, and we could not confirm its other limits. ReelBolt has a free trial with one-time credits and no card, which is not a plan you can stay on. Trial exports carry a watermark.',
    },
    {
      q: 'Why are there no invideo prices in this comparison?',
      a: 'We do not state another company’s prices. They change, plans are built differently at each company, and a figure copied here would go stale. invideo’s pricing page has the current figures, and ReelBolt’s plans are on its own pricing page.',
    },
  ],
};

export default function ReelboltVsInvideo(): JSX.Element {
  return (
    <>
      <p>
        This comparison is for anyone with raw footage and a story to tell who has invideo and
        ReelBolt on the shortlist: a creator, a small business, a teacher, a nonprofit, an agency or
        a software team. We make ReelBolt, so read it with that in mind. Every statement about
        invideo comes from invideo’s own public pages, read on {CHECKED} and linked in the sources
        at the end. We did not test invideo hands-on, and we give no invideo prices.
      </p>
      <p>
        The short version: invideo is a full video editor in your browser with an AI agent beside
        the timeline and a large set of AI models inside it. It suits people who want to sit in an
        editor and direct the work, with or without footage. ReelBolt asks less of you. You drop in
        the clips you already shot, say what the video is for, and get back a short first cut that
        has already been reviewed. If you want an editor, many models or real-time collaboration,
        invideo is the better choice. If the footage is sitting on your phone and you do not want
        to open a timeline at all, that is the job ReelBolt is built for.
      </p>

      <h2 id="what-each-one-is">What each one is</h2>
      <p>
        invideo calls itself “The agentic video editor for serious creatives”, for people ranging
        “From solo creatives to creative enterprises” <Cite n={[1]} />. Its docs describe invideo
        Editor as “a professional video editor in your browser” with “a multitrack timeline,
        keyframes, color grading, a full audio mixer, effects and transitions”{' '}
        <Cite n={[6]} />. You talk to its agent, Agent Two, in a panel next to the timeline, and
        the docs say “Every change shows up as real clips and cuts that you can grab, reshape or
        undo” <Cite n={[6]} />. You can upload video, images and audio <Cite n={[39]} />, and the
        editor gives “Access to 200+ image, video and audio models” <Cite n={[3]} />. invideo’s
        docs also say Agent Two “reviews your footage and assembles a first cut”{' '}
        <Cite n={[11]} />, so raw footage is one of the things it works from.
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
        caption={`ReelBolt and invideo compared, from each company’s public pages on ${CHECKED}`}
        columns={['ReelBolt', 'invideo']}
        rows={[
          {
            label: 'Starts from',
            cells: [
              'Raw clips you upload (1 to 20 for a highlight edit) and a short brief',
              { text: <>A prompt, or video, images and audio you upload; also a PDF or PowerPoint deck, a document, or a project file from another editor <Cite n={[3, 6, 25, 26, 39]} /></> },
            ],
          },
          {
            label: 'How you work',
            cells: [
              'Pick a recipe, fill in a few settings and press Run; an editor, labeled Early preview, for changes afterwards',
              { text: <>A professional timeline editor in the browser, with an agent in a side panel that works directly on your timeline <Cite n={[6]} /></> },
            ],
          },
          {
            label: 'Who makes the first cut',
            cells: [
              'An AI editor working from your clips, then a reviewer that scores the draft before you see it',
              { text: <>Agent Two reviews your footage and assembles a first cut on the timeline <Cite n={[11]} /></> },
            ],
          },
          {
            label: 'Filler words, retakes and silences',
            cells: [
              'Left out of the first cut; the editor can also remove silence and find fillers',
              { text: <>Removed by the agent as cuts on a branch you can undo; it needs a transcript, which costs credits <Cite n={[12]} /></> },
            ],
          },
          {
            label: 'Transcript editing',
            cells: [
              'Yes, in the editor, when the footage was transcribed',
              { text: <>No: “invideo shows a timeline; the transcript is something your agent reads” <Cite n={[34]} /></> },
            ],
          },
          {
            label: 'Manual editing tools',
            cells: [
              'A multi-track timeline, labeled Early preview, with multicam synced by sound, color grading and a setting that steadies shaky footage',
              { text: <>Multitrack timeline, keyframes, color grading, a full audio mixer, effects and transitions; its docs say it has no sync tool for multicam and no stabilizing <Cite n={[6, 19, 37]} /></> },
            ],
          },
          {
            label: 'Recording',
            cells: [
              'No screen or camera recorder; the editor records a voiceover',
              { text: <>No screen recorder: “invideo does not capture web pages or record your screen”; a recording is uploaded as a file <Cite n={[37]} /></> },
            ],
          },
          {
            label: 'Captions and subtitles',
            cells: [
              'Burned-in subtitles plus SRT and VTT files; subtitles in another language',
              { text: <>A caption track on the timeline, burned in when you export; SRT or VTT files <Cite n={[13, 14]} /></> },
            ],
          },
          {
            label: 'Translation and dubbing',
            cells: [
              'Translated subtitles; a dub that changes the sound only, with no lip sync',
              { text: <>No one-shot dubbing: the agent dubs step by step, with the lips re-synced where faces show <Cite n={[15, 16]} /></> },
            ],
          },
          {
            label: 'Voice',
            cells: [
              'AI narration with a stock voice, or a cloned voice of a real speaker',
              { text: <>Voice cloning and voice generation, both of which use credits <Cite n={[32]} /></> },
            ],
          },
          {
            label: 'Avatars',
            cells: [
              'A real adult, from their own pictures and recordings, in clips of about 15 seconds at most; Pro and up, still rolling out; no stock avatars',
              { text: <>Avatar clones of yourself, human actors and virtual actors; invideo says it runs avatars “as clips, not as a presenter studio” <Cite n={[15, 29]} /></> },
            ],
          },
          {
            label: 'Generated video and music',
            cells: [
              'AI-generated clips on Pro and up; music and sound-effect generation on Creator and up; no built-in music library',
              { text: <>“200+ image, video and audio models” inside the editor; some are limited to the Plus and Max plans <Cite n={[2, 3]} /></> },
            ],
          },
          {
            label: 'Brand',
            cells: [
              'Brand kit from a brand PDF, a design-tool export, or design token and CSS files',
              { text: <>A brand kit entry the agent writes into the project as a plain note, from your colors, font files and logo <Cite n={[33]} /></> },
            ],
          },
          {
            label: 'Shapes',
            cells: [
              'Landscape, vertical, square or portrait, chosen before a run; a finished video cannot be re-cut to another shape yet',
              { text: <>Resizes a video to 16:9, 9:16, 4:5 and 1:1; its docs note the reframe model does not track a face <Cite n={[17]} /></> },
            ],
          },
          {
            label: 'Short clips from one long video',
            cells: [
              'No: each run makes one short video',
              { text: <>Yes: vertical clips from raw footage, with captions; it does not score clips for virality <Cite n={[18]} /></> },
            ],
          },
          {
            label: 'Versions',
            cells: [
              'Automatic and named versions you can go back to; no branches',
              { text: <>Branches, tags and a return to an earlier state; no merge of a branch into the main edit <Cite n={[3, 37]} /></> },
            ],
          },
          {
            label: 'Review and teams',
            cells: [
              'Timecoded comments; a review link for a guest with no account; Team plan with a shared workspace and roles',
              { text: <>Real-time multiplayer editing; a Preview only role that can comment, with an invideo login; free guest seats on Plus and Max <Cite n={[1, 2, 20]} /></> },
            ],
          },
          {
            label: 'Export and hand-off',
            cells: [
              'MP4 at a resolution set by plan; OTIO, EDL and Final Cut XML; a hand-off package that is an Early preview',
              { text: <>MP4 at 1080p, with 4K by upscaling; audio only or a still frame; an editable project for Premiere Pro, Final Cut Pro or DaVinci Resolve <Cite n={[22, 23, 24]} /></> },
            ],
          },
          {
            label: 'Where it runs',
            cells: [
              'In a browser; the editor has a phone view for simpler edits; no phone app',
              { text: <>In a browser; it needs a connection and has no offline mode <Cite n={[6, 35]} /></> },
            ],
          },
          {
            label: 'Automation',
            cells: [
              'API keys and webhooks on paid plans; a connection for Claude Code and other AI assistants; all alpha',
              { text: <>No API keys, by design: invideo MCP for AI apps, a command-line tool for scripts and terminal agents, and device sign-in for servers <Cite n={[9, 10]} /></> },
            ],
          },
          {
            label: 'What uses credits',
            cells: [
              'Runs and renders; cover pictures, file summaries and storage within your plan do not',
              { text: <>Generation and transcripts; “Edits and exports are free” <Cite n={[7]} /></> },
            ],
          },
          {
            label: 'Free option',
            cells: [
              'A free trial with one-time credits, no card; exports carry a watermark',
              { text: <>A Free plan with a weekly limit; it is not listed on the pricing page <Cite n={[2, 4, 5]} /></> },
            ],
          },
        ]}
      />

      <h2 id="where-invideo-is-stronger">Where invideo is the better choice</h2>
      <p>
        <strong>A full editor, and editing that costs no credits.</strong> invideo’s home page
        describes its timeline editor with the line “Picture Premiere Pro with full AI”{' '}
        <Cite n={[1]} />, and its plan cards list color, audio and 3D tools on Starter, Plus and
        Max <Cite n={[2]} />. Its docs say “Edits and exports are free. Generation and transcripts
        cost credits”, and that the editor itself is the same on every plan <Cite n={[7]} />.
        ReelBolt’s editor is labeled Early preview, and rendering a film from it uses credits. If
        you want to spend your time in a timeline, invideo gives you more of one.
      </p>
      <p>
        <strong>Starting with no footage.</strong> invideo brings “200+ image, video and audio
        models” into its editor <Cite n={[3]} />. Its docs also describe turning a PDF or
        PowerPoint deck into a narrated training video, and making an explainer from a PDF, a DOCX
        file, a saved web page or pasted text <Cite n={[25, 26]} />. ReelBolt is built around
        footage you bring. Nothing on its free trial turns a prompt or a set of photos into a
        video. On Pro and the plans above it, the most it generates from a description alone is a
        clip of 6 or 10 seconds. If all you have is an idea, a deck or a document, invideo is the
        better choice.
      </p>
      <p>
        <strong>Many clips and many shapes from one recording.</strong> invideo’s docs describe
        turning raw footage into vertical clips with captions, and resizing one video to 16:9,
        9:16, 4:5 and 1:1 <Cite n={[17, 18]} />. They are plain about the limits: invideo does not
        score clips for virality, and its reframe model fills new picture around the shot and does
        not track a face <Cite n={[17, 18]} />. ReelBolt makes one short video per run, in a shape you choose
        beforehand, and cannot yet re-cut a finished video to another shape.
      </p>
      <p>
        <strong>Dubbing with lip sync, and stock presenters.</strong> invideo’s docs describe
        dubbing footage you already have with “the lips re-synced where faces show”{' '}
        <Cite n={[16]} />. They also say there is no one-shot dubbing model, so the agent does it
        step by step <Cite n={[15]} />. A ReelBolt dub changes the sound only, and the mouth on
        screen stays as it was filmed. For avatars, invideo offers clones of yourself, human actors
        and virtual actors <Cite n={[29]} />, and its avatar generator page mentions “50+
        languages” <Cite n={[31]} />. Two caveats come from invideo itself: its docs say it runs avatar models
        “as clips, not as a presenter studio” <Cite n={[15]} />, and that AI twins sit in the
        previous version of its flow, reached through “Older versions” <Cite n={[30]} />.
        ReelBolt’s <Link href="/blog/reelbolt-avatars">avatars</Link> are narrower still: a
        likeness of a real adult, made from that person’s own pictures and recordings, in clips of
        about 15 seconds at most, on Pro and the plans above it, and still being rolled out.
      </p>
      <p>
        <strong>Working together live.</strong> invideo’s multiplayer mode lets people “Collaborate
        in real time with live cursors” <Cite n={[1]} />. Its Plus and Max plans include free guest
        seats <Cite n={[2]} />, and its docs describe project roles of Editor, Viewer and Preview
        only, plus a way to connect someone to a single project without using a seat{' '}
        <Cite n={[21]} />. ReelBolt has a Team plan with a shared workspace and roles, and people
        comment on a cut, but two people do not edit the same timeline at the same moment. If a
        second person saves a newer version, the editor turns read-only until you reload.
      </p>
      <p>
        <strong>Branches for trying things.</strong> invideo describes version control on the
        timeline: “Branch the edit, tag versions and return to an earlier state”{' '}
        <Cite n={[3]} />. Its docs add that a branch cannot be merged back into the main edit{' '}
        <Cite n={[37]} />. ReelBolt keeps automatic and named versions of a timeline and of a
        project, and you can restore any of them. It has no branches.
      </p>
      <p>
        <strong>Agents you build or bring.</strong> In invideo you can “Create custom agents to
        fill specific roles like cinematographer, music designer, and more”, or connect an agent
        you already use <Cite n={[1]} />. Its docs say an outside agent “edits for free and pays
        only for generation, transcripts and paid stock”, and that it tells you the price and
        waits for your yes before any call that costs a credit or more <Cite n={[7, 8]} />.
        ReelBolt’s recipes arrive with the roles already chosen. You can change the steps in an
        advanced view, but the product is built so that you do not have to. If shaping the agent
        team is the part you enjoy, invideo gives you more room.
      </p>
      <p>
        <strong>Hand-off to a professional editor, both ways.</strong> invideo exports an editable
        project for Adobe Premiere Pro, Final Cut Pro or DaVinci Resolve with its media in a folder
        or a zip, and its docs list what to check after opening it, such as transitions, text,
        color grades and audio levels <Cite n={[3, 24]} />. It also imports a Final Cut Pro 7 XML
        or OpenTimelineIO file exported from Premiere Pro or DaVinci Resolve <Cite n={[6]} />.
        ReelBolt exports OTIO, EDL and Final Cut XML and imports OpenTimelineIO. Its hand-off
        package is an Early preview, and we have not yet opened one in those editors.
      </p>
      <p>
        <strong>A free plan you can stay on, and company requirements.</strong> invideo’s terms
        describe “a free tier with limited features” <Cite n={[5]} />, and its help pages say that
        when you cancel, your account moves to the Free plan with your workspace and projects
        intact <Cite n={[38]} />. Its Enterprise page states “SOC 2 Type II across all five AICPA
        criteria and full GDPR compliance” <Cite n={[36]} />. ReelBolt has a free trial with
        one-time credits, which is not a plan you can stay on, and the whole product is a public
        alpha. If your company has security or compliance requirements today, invideo is the safer
        choice.
      </p>

      <h2 id="where-reelbolt-is-different">Where ReelBolt is different</h2>
      <p>
        <strong>You do not open an editor to get a first cut.</strong> Both tools can make a first
        cut from raw footage. invideo’s docs say Agent Two reviews your footage and assembles one
        on the timeline, and that its agent “watches every clip and logs it in memory, so you find
        any moment by describing it” <Cite n={[1, 11]} />. We have not tested it, so we make no
        claim about which one cuts better. The difference we can state is how you work. invideo is
        an editor you sit in, with an agent beside you. In ReelBolt you pick a recipe, choose your
        clips, a length and a shape, write a note on what to keep, and press Run. A reviewer scores
        the draft, and a low score sends it back for another pass before you see it. You did not
        start this to become a video editor, and ReelBolt is built for people who feel that way.
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
        <strong>The same clips can go further.</strong> Other recipes add titles and callouts,
        write and voice a narration with captions, burn in subtitles and produce the SRT and VTT
        files, translate the subtitles, or dub the video. The run page has no download button for
        the subtitle files yet, which is one of the rough edges. One recipe makes a cover picture
        for very few credits, and the assistant can write the text that goes with the post. You choose
        the shape before each run: landscape, vertical, square or portrait. Music and sound effects
        come from files you upload, because there is no built-in music library.
      </p>
      <p>
        <strong>An editor for afterwards, with the transcript in your hands.</strong> You stay in
        charge of the cut. ReelBolt’s editor, labeled Early preview, lets you edit from the
        transcript when the footage was transcribed: select words in the text, delete them, and
        those frames are cut and the gap closed. invideo’s docs put it differently: “invideo shows a timeline; the transcript is
        something your agent reads” <Cite n={[34]} />. The ReelBolt editor also lines up several
        cameras of one moment by their sound and has a setting that steadies shaky footage in the
        render. invideo’s docs say it “has no sync tool”, so its agent lines cameras up by matching
        words in each transcript, and that it has no stabilizing operation{' '}
        <Cite n={[19, 37]} />. In most other respects invideo’s manual editor is the deeper one, as
        the section above says.
      </p>
      <Figure
        name="timeline-editor"
        alt="The timeline tracks of a demo timeline in the ReelBolt editor, with three video clips on one track, one clip on the track below and a Remove silence button above them."
        width={1280}
        height={214}
        caption="Look at the Remove silence button above the clips on the timeline."
      />
      <p>
        <strong>Feedback from someone without an account.</strong> A ReelBolt review link lets a
        client or a boss watch one finished video without signing up, leave notes at exact moments,
        and approve it or ask for changes. invideo’s docs describe sharing a project with a
        reviewer as Preview only, and say “Commenting always needs an invideo login”{' '}
        <Cite n={[20]} />. Both cover review. The difference is whether your reviewer has to make
        an account.
      </p>
      <p>
        <strong>Export above 1080p without a second step.</strong> ReelBolt sets the top export
        resolution by plan: {TRIAL_RESOLUTION} with a watermark on the trial, up to{' '}
        {CREATOR_RESOLUTION} on Creator and up to {PRO_RESOLUTION} on Pro. invideo’s docs say its
        export “makes a 1080p MP4”, and that a 4K file comes from upscaling that file with a model
        or from finishing in Premiere Pro or DaVinci Resolve <Cite n={[23]} />. In fairness,
        invideo’s exports cost no credits <Cite n={[7]} />, and a ReelBolt render does.
      </p>
      <p>
        <strong>Your brand, from the files you already have.</strong> ReelBolt builds a brand kit
        from a brand PDF or a design-tool export, and it also reads design token and CSS files if
        your team keeps them. Reading your files is free. AI fills the gaps only after you confirm
        an estimate. In invideo’s docs, the brand kit is an entry the agent writes into the project
        from your colors, font files and logo: “The brand kit entry is plain markdown. The agent
        and anyone in the project read it before they work” <Cite n={[33]} />.
      </p>
      <p>
        <strong>API keys and webhooks.</strong> ReelBolt has{' '}
        <Link href="/blog/reelbolt-public-api">an API with keys and webhooks</Link> on its paid
        plans, so your own script can start a run and be told when it ends. It is alpha and may
        change. invideo takes a different route on purpose. Its docs say “There are no API keys, by
        design”, and that you reach it through invideo MCP, its command-line tool, or device
        sign-in for servers with no browser <Cite n={[9]} />. Both can be automated. Which route
        suits you depends on what your systems already speak.
      </p>
      <p>
        <strong>A real person’s voice or likeness needs recorded consent.</strong> In ReelBolt a
        cloned voice and an avatar each need the person’s agreement, which you record in the
        project, and you can withdraw it or delete the voice or avatar at any time. The person
        behind an avatar must be an adult. invideo has its own consent steps for voice clones and
        custom avatars, described in its docs and terms <Cite n={[5, 30]} />. We do not compare the
        two processes here.
      </p>
      <p>
        <strong>One path that starts from code.</strong> For software teams, a ReelBolt recipe
        makes a promo video from an app’s source code. We found no code repository input in
        invideo’s docs <Cite n={[37]} />. Its docs say “invideo does not capture web pages”, so an
        outside agent brings the screenshots <Cite n={[27]} />, and they describe rebuilding a
        video composition written in code as clips you can edit on the timeline{' '}
        <Cite n={[28]} />.
      </p>

      <h2 id="pricing-approach">Pricing approach</h2>
      <p>
        invideo’s pricing page lists three plans, Starter, Plus and Max, and a separate tab of
        business plans <Cite n={[2]} />. They are priced per seat, billed monthly or annually, and
        each plan sets how many seats you can have. Starter has no guest seats, and Plus and Max
        include free ones <Cite n={[2]} />. Some generative models, and the higher tiers of Agent
        Two, are limited to Plus and Max <Cite n={[2]} />. The page says unused credits do not roll
        over to the next month, and that invideo reserves the right to update credit costs at any
        time without prior notice <Cite n={[2]} />.
      </p>
      <p>
        Credits at invideo pay for generation and transcripts. Timeline edits and exports are free,
        and the editor, invideo MCP and the command-line tool are part of every plan{' '}
        <Cite n={[7]} />. There is also a Free plan. invideo’s terms describe it as a free tier
        with limited features, and its plan-limits page says it has a weekly limit{' '}
        <Cite n={[4, 5]} />. It is not listed on the pricing page, and we could not confirm its
        other limits, so we give none. Current figures are on{' '}
        <a href="https://invideo.io/pricing/" rel="nofollow noopener" target="_blank">
          invideo’s pricing page<span className="sr-only"> (opens in a new tab)</span>
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
        Both tools show a cost before they spend your credits. invideo’s docs say an outside agent
        runs a dry run first and, for any call that costs a credit or more, tells you the price and
        waits for your yes <Cite n={[8]} />. Before a ReelBolt run starts, you see the credits it
        is expected to use, as a likely amount and the range it usually falls in. If a step fails
        because of a problem on our side, the credits held for it are refunded. Top-up packs are
        for paid plans and expire after {catalog('topUp.expiresAfterMonths')} months.
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
      <p>Pick invideo if any of these is true:</p>
      <ul>
        <li>You want to work in a full timeline editor and direct an AI agent yourself.</li>
        <li>You have an idea, a deck or a document, and no footage.</li>
        <li>You want to choose among many image, video and audio models.</li>
        <li>You want many short clips, or several shapes, from one long recording.</li>
        <li>You need a dub with the lips re-synced, or a stock presenter.</li>
        <li>Several people need to edit the same timeline at the same time.</li>
        <li>
          You want a free plan you can stay on, or your company needs the SOC 2 and GDPR compliance
          that invideo’s Enterprise page describes.
        </li>
      </ul>
      <p>Pick ReelBolt if these describe you:</p>
      <ul>
        <li>The footage is already shot, in several clips, and nobody has had time to edit it.</li>
        <li>You do not want to learn an editor to get a first cut.</li>
        <li>You want a short video, about 90 seconds or less.</li>
        <li>The person who signs off on the video will not make an account to comment.</li>
        <li>You are comfortable with a public alpha and its rough edges.</li>
      </ul>
      <p>
        Both can be driven from Claude Code, invideo through its command-line tool and ReelBolt
        through its own connection, so that alone should not decide it. The deciding question is
        how much of the editing you want to do. A filmmaker or a social media team that wants
        control over every cut and every model is better served by invideo. A shop owner, a
        teacher, a volunteer or a founder with a camera roll of clips and no editor is who ReelBolt
        is built for.
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
          { id: 'source-1', label: 'invideo, home page', url: 'https://invideo.io/', accessed: CHECKED },
          { id: 'source-2', label: 'invideo, pricing', url: 'https://invideo.io/pricing/', accessed: CHECKED },
          { id: 'source-3', label: 'invideo, “Introducing invideo MCP” (published 2026-09-30)', url: 'https://invideo.io/news/introducing-invideo-mcp/', accessed: CHECKED },
          { id: 'source-4', label: 'invideo help, plan limits', url: 'https://invideo.io/help/billing/plan-limits/', accessed: CHECKED },
          { id: 'source-5', label: 'invideo, terms of service (last updated November 6, 2026)', url: 'https://invideo.io/terms-of-service/', accessed: CHECKED },
          { id: 'source-6', label: 'invideo docs, introducing invideo Editor', url: 'https://docs.invideo.io/getting-started/introducing-invideo-editor', accessed: CHECKED },
          { id: 'source-7', label: 'invideo docs, plans', url: 'https://docs.invideo.io/account/plans', accessed: CHECKED },
          { id: 'source-8', label: 'invideo docs, credits', url: 'https://docs.invideo.io/account/credits', accessed: CHECKED },
          { id: 'source-9', label: 'invideo docs, API', url: 'https://docs.invideo.io/api', accessed: CHECKED },
          { id: 'source-10', label: 'invideo, MCP', url: 'https://invideo.io/mcp/', accessed: CHECKED },
          { id: 'source-11', label: 'invideo docs, how to create a first cut from raw footage', url: 'https://docs.invideo.io/guides/how-to-create-a-first-cut-from-raw-footage-in-invideo-editor', accessed: CHECKED },
          { id: 'source-12', label: 'invideo docs, cut ums and retakes', url: 'https://docs.invideo.io/how-to/creators/cut-ums-and-retakes', accessed: CHECKED },
          { id: 'source-13', label: 'invideo docs, word-by-word captions', url: 'https://docs.invideo.io/how-to/creators/word-by-word-captions', accessed: CHECKED },
          { id: 'source-14', label: 'invideo docs, SRT for an LMS', url: 'https://docs.invideo.io/how-to/training/srt-for-an-lms', accessed: CHECKED },
          { id: 'source-15', label: 'invideo docs, invideo compared with HeyGen', url: 'https://docs.invideo.io/compare/heygen', accessed: CHECKED },
          { id: 'source-16', label: 'invideo docs, dub existing footage', url: 'https://docs.invideo.io/how-to/training/dub-existing-footage', accessed: CHECKED },
          { id: 'source-17', label: 'invideo docs, repurpose for every platform', url: 'https://docs.invideo.io/how-to/creators/repurpose-for-every-platform', accessed: CHECKED },
          { id: 'source-18', label: 'invideo docs, reels and shorts from your footage', url: 'https://docs.invideo.io/how-to/creators/reels-and-shorts-from-your-footage', accessed: CHECKED },
          { id: 'source-19', label: 'invideo docs, podcast edit and clips', url: 'https://docs.invideo.io/how-to/creators/podcast-edit-and-clips', accessed: CHECKED },
          { id: 'source-20', label: 'invideo docs, how to share a timeline for review', url: 'https://docs.invideo.io/projects-and-exports/how-to-share-a-timeline-for-review-in-invideo-editor', accessed: CHECKED },
          { id: 'source-21', label: 'invideo docs, seats', url: 'https://docs.invideo.io/account/seats', accessed: CHECKED },
          { id: 'source-22', label: 'invideo docs, how to export video, audio or a PNG still', url: 'https://docs.invideo.io/projects-and-exports/how-to-export-video-audio-or-a-png-still-from-invideo-editor', accessed: CHECKED },
          { id: 'source-23', label: 'invideo docs, 4K and HDR delivery', url: 'https://docs.invideo.io/how-to/film/4k-and-hdr-delivery', accessed: CHECKED },
          { id: 'source-24', label: 'invideo docs, export to Premiere, Resolve and Final Cut', url: 'https://docs.invideo.io/how-to/export-to-premiere-resolve-final-cut', accessed: CHECKED },
          { id: 'source-25', label: 'invideo docs, slides to narrated video', url: 'https://docs.invideo.io/how-to/training/slides-to-narrated-video', accessed: CHECKED },
          { id: 'source-26', label: 'invideo docs, explainer from a document', url: 'https://docs.invideo.io/how-to/training/explainer-from-a-document', accessed: CHECKED },
          { id: 'source-27', label: 'invideo docs, launch video', url: 'https://docs.invideo.io/how-to/marketing/launch-video', accessed: CHECKED },
          { id: 'source-28', label: 'invideo docs, composition to timeline', url: 'https://docs.invideo.io/how-to/developers/composition-to-timeline', accessed: CHECKED },
          { id: 'source-29', label: 'invideo docs, what types of avatars does invideo offer', url: 'https://docs.invideo.io/avatars/what-types-of-avatars-does-invideo-offer', accessed: CHECKED },
          { id: 'source-30', label: 'invideo docs, how to clone your voice', url: 'https://docs.invideo.io/avatars/how-to-clone-your-voice', accessed: CHECKED },
          { id: 'source-31', label: 'invideo, AI avatar generator', url: 'https://invideo.io/make/ai-avatar-generator/', accessed: CHECKED },
          { id: 'source-32', label: 'invideo, AI voice cloning', url: 'https://invideo.io/tools/ai-voice-cloning/', accessed: CHECKED },
          { id: 'source-33', label: 'invideo docs, employer branding video', url: 'https://docs.invideo.io/how-to/marketing/employer-branding-video', accessed: CHECKED },
          { id: 'source-34', label: 'invideo docs, invideo compared with Descript', url: 'https://docs.invideo.io/compare/descript', accessed: CHECKED },
          { id: 'source-35', label: 'invideo docs, alternatives', url: 'https://docs.invideo.io/compare/alternatives', accessed: CHECKED },
          { id: 'source-36', label: 'invideo, Enterprise', url: 'https://invideo.io/enterprise/', accessed: CHECKED },
          { id: 'source-37', label: 'invideo docs, full text of the documentation', url: 'https://docs.invideo.io/llms-full.txt', accessed: CHECKED },
          { id: 'source-38', label: 'invideo help, subscriptions and refunds', url: 'https://invideo.io/help/billing/subscriptions-and-refunds/', accessed: CHECKED },
          { id: 'source-39', label: 'invideo docs, how to upload and organise media', url: 'https://docs.invideo.io/editor/how-to-upload-and-organise-media-in-invideo-editor', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Prices and features checked on {CHECKED} from each vendor’s public pages; they may have changed. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
