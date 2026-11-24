import type { JSX } from 'react';
import Link from 'next/link';
import { Callout } from '@/components/blog/Callout';
import { Cite } from '@/components/blog/Cite';
import { ComparisonTable } from '@/components/blog/ComparisonTable';
import { SourceList } from '@/components/blog/SourceList';
import { catalog } from '@/lib/blog/catalog';
import type { PostMeta } from '@/lib/blog/types';
import { FIRST_VIDEO_GRANT_CREDITS, FIRST_VIDEO_GRANT_EXPIRY } from '@/lib/legal-placeholders';

// A roundup by kind of tool, not a ranking. Every statement about another company's product comes
// from that company's own public page, read on 2026-11-11 and listed in the sources; a tool whose
// page could not be read that day is not named. No competitor price appears anywhere. ReelBolt
// facts follow docs/user-guide (recipes/choose-a-recipe, derush-edit, subtitles, voiceover,
// thumbnail-and-cover-text; assistant/post-text; editor/export-your-film). Every ReelBolt figure
// comes from the plan catalog or from the constants the Terms and the pricing page already use.
const TRIAL_CREDITS = catalog('trial.creditsOneTime');
const TRIAL_RESOLUTION = catalog('trial.entitlements.maxOutputHeight');
const CHECKED = '2026-11-11';

export const meta: PostMeta = {
  slug: 'ai-video-editors-for-people-who-do-not-edit-2026',
  title: 'AI video tools for people who do not edit (2026): what each one is actually for',
  description:
    'Six kinds of AI video tool, from transcript editors to AI teams that cut your raw clips: what each is for, which fits your need, and where ReelBolt sits.',
  date: '2026-11-11',
  tags: ['comparisons'],
  readingMinutes: 11,
  faqs: [
    {
      q: 'I have never edited a video. Which kind of AI video tool should I start with?',
      a: 'Start from what you already have. A long recording of someone talking points to a transcript editor. One long video you want short posts from points to a clip tool. A script and no footage points to a presenter tool. An idea and no footage points to generative video. A camera roll of raw clips points to a tool that makes the first cut for you, which is the kind ReelBolt belongs to.',
    },
    {
      q: 'Can these tools make a video from only a text prompt?',
      a: 'Generative video tools and presenter tools are built for that: you type, and the tool makes the picture. ReelBolt is not, on the free trial. Its trial recipes start from clips you filmed or from an app’s source code. AI-generated clips are part of the Pro plan and the plans above it.',
    },
    {
      q: 'Is ReelBolt a clip tool like OpusClip or Vizard?',
      a: 'No. A clip tool takes one long video that already exists and gives you several short excerpts from it. ReelBolt takes raw clips and a short brief and makes one short video per run, then reviews it before you see it. If you already publish long videos and want many short posts from each one, a clip tool is the better fit.',
    },
    {
      q: 'Why are there no prices in this comparison?',
      a: 'We do not state another company’s prices. They change often, plans are built differently at each company, and a figure copied here would go stale. Check each vendor’s own pricing page. ReelBolt’s plans are on its pricing page.',
    },
    {
      q: 'Why is a tool I know missing from this list?',
      a: 'We named a tool only when we could read its own public page on 2026-11-11 and describe it from that page. Some well-known tools did not load for us that day, and we left them out instead of describing them from memory. A missing name is not a judgment on the tool.',
    },
    {
      q: 'Is this comparison neutral?',
      a: 'We make ReelBolt, so we have an interest. To keep it checkable, every statement about another tool comes from that company’s own public page, linked in the sources with the date we read it, and the post says when another kind of tool is the better choice.',
    },
  ],
};

export default function AiVideoEditorsForPeopleWhoDoNotEdit2026(): JSX.Element {
  return (
    <>
      <p>
        Most lists of AI video tools rank them. This one sorts them, because the tools on those lists
        do different jobs. A tool that turns a script into a presenter video and a tool that cuts the
        pauses out of your recording are not competing for the same afternoon.
      </p>
      <p>
        This guide is for people who have a story to tell and no wish to become a video editor:
        creators, small business owners, teachers, nonprofits, software teams and agencies. It maps six
        kinds of tool, says what each one is for, and names a few well-known examples of each. We make
        ReelBolt, which belongs to the sixth kind, so read it with that in mind.
      </p>
      <p>
        Every statement about another company’s product comes from that company’s own public pages,
        read on {CHECKED} and linked in the sources. The examples are tools whose pages we could read
        that day. They are examples, not a ranking, and a name that is missing was not judged. We give
        no prices for other companies’ products.
      </p>
      <p>
        Under each kind, the paragraph headed “Where it stops” is our own judgment about that kind of
        tool as a whole. It is not a statement about any one product, unless it names the product and
        cites its page.
      </p>
      <p>
        The short version: start from what you already have, not from the tool. A recording of someone
        talking, one long video, a script, an idea and a camera roll of raw clips each point to a
        different kind.
      </p>

      <h2 id="at-a-glance">At a glance</h2>
      <ComparisonTable
        caption={`Six kinds of AI video tool, with examples from each company’s public pages on ${CHECKED}`}
        columns={['You bring', 'You still do', 'Examples we read']}
        rows={[
          {
            label: 'Transcript editors',
            cells: [
              'A recording with speech in it',
              'Edit the words; the video follows',
              { text: <>Descript <Cite n={[1]} /></> },
            ],
          },
          {
            label: 'Clip-from-long-video tools',
            cells: [
              'One long video that already exists',
              'Choose among the clips it offers',
              { text: <>OpusClip <Cite n={[2]} />, Vizard <Cite n={[3]} />, Riverside Magic Clips <Cite n={[4]} /></> },
            ],
          },
          {
            label: 'Template and all-in-one editors',
            cells: [
              'Clips, pictures and the time to edit',
              'The edit itself, with shortcuts',
              { text: <>CapCut <Cite n={[5]} />, Kapwing <Cite n={[6]} /></> },
            ],
          },
          {
            label: 'Avatar and presenter tools',
            cells: [
              'A script, slides or a document',
              'Write and check the script',
              { text: <>HeyGen <Cite n={[7]} />, Synthesia <Cite n={[8]} /></> },
            ],
          },
          {
            label: 'Generative video',
            cells: [
              'A prompt or reference pictures',
              'Direct each shot and choose among results',
              { text: <>Runway <Cite n={[9, 10]} />, Pika <Cite n={[11]} /></> },
            ],
          },
          {
            label: 'AI video teams that cut your raw footage',
            cells: [
              'Raw clips and a short brief',
              'React to a first cut',
              { text: <>ReelBolt, Gling <Cite n={[12]} />, Eddie AI <Cite n={[13]} />, Captions <Cite n={[14]} />, invideo <Cite n={[15]} /></> },
            ],
          },
        ]}
      />
      <p>
        The kinds overlap. Many products now carry features from two or three of them. Kapwing lists
        transcript trimming, silence removal and a clip maker in one product <Cite n={[6]} />, and
        Captions lists AI avatars next to its footage editing <Cite n={[14]} />. We filed each example
        where we judged most of its page fits. That filing is our judgment, not the vendor’s.
      </p>

      <h2 id="transcript-editors">1. Transcript editors</h2>
      <p>
        The tool turns the speech in your recording into text, and you edit the video by editing the
        text. Delete a sentence and that part of the video goes with it.
      </p>
      <p>
        Descript describes itself as an AI video and audio editor that lets you edit by editing text:
        you record or upload, it transcribes, and you cut, trim and rearrange the video by changing the
        transcript <Cite n={[1]} />. Its page also lists a tool that cuts filler words and retakes, a
        screen recorder, remote recording, and an AI editor named Underlord, which it says can turn a
        raw recording into a tight cut <Cite n={[1]} />.
      </p>
      <p>
        <strong>What it is for.</strong> Podcasts, interviews, webinars, lessons. Anything long where
        the words carry the video.
      </p>
      <p>
        <strong>Where it stops.</strong> Reading a transcript is easier than scrubbing through a
        timeline, but the transcript is still yours to read and decide on, unless you hand that to the
        tool’s AI editor.
      </p>
      <p>
        <strong>Choose this kind when</strong> the video is mostly talking, it runs long, and you are
        willing to read it through.
      </p>

      <h2 id="clip-tools">2. Clip-from-long-video tools</h2>
      <p>
        You give the tool one long video. It finds short moments in it, cuts them out, fits them to a
        vertical frame and adds captions.
      </p>
      <p>
        OpusClip says it picks the highlight moments of a long video, rearranges them into a short one
        and adds captions, and that it publishes the clips to social platforms <Cite n={[2]} />. Vizard
        says it turns long-form videos into short clips for TikTok, Instagram and YouTube Shorts, and
        resizes a video to fit each platform <Cite n={[3]} />. Riverside, a recording platform, has
        Magic Clips, which it describes as an AI clip generator that spots the moments that matter and
        turns them into ready-to-share clips <Cite n={[4]} />.
      </p>
      <p>
        <strong>What it is for.</strong> Podcasters, streamers, speakers and teachers with recordings.
        People who already publish long videos and want short posts from each one.
      </p>
      <p>
        <strong>Where it stops.</strong> The clips are excerpts. The tool selects from a video that
        already holds together at full length. It is not built to make one story out of scattered
        footage.
      </p>
      <p>
        <strong>Choose this kind when</strong> the long video exists and what you lack is the short
        versions.
      </p>

      <h2 id="template-editors">3. Template and all-in-one editors</h2>
      <p>
        This is a full editor with templates to start from, subtitle tools, and a growing set of AI
        tools around the timeline.
      </p>
      <p>
        CapCut calls itself an “AI-Powered Photo &amp; Video Editor for Everyone” and lists templates,
        text to speech and an AI video generator <Cite n={[5]} />. Kapwing describes a platform built
        for creative teams, with a video editor, a subtitler, silence removal, transcript trimming and
        templates <Cite n={[6]} />.
      </p>
      <p>
        <strong>What it is for.</strong> People who know what they want the video to look like, or who
        repeat one format every week and want it to look the same each time.
      </p>
      <p>
        <strong>Where it stops.</strong> A template gives you a look. Choosing the takes, putting them
        in order and timing the cuts is still your work, and it still takes the time editing takes.
      </p>
      <p>
        <strong>Choose this kind when</strong> you want your hands on every decision and you have the
        hour.
      </p>

      <h2 id="presenter-tools">4. Avatar and presenter tools</h2>
      <p>
        You write a script, or upload slides or a document, and an AI presenter delivers it. No camera
        is involved.
      </p>
      <p>
        HeyGen’s home page opens with “AI videos starring you, made in minutes”. It lists avatars, a
        tool that turns each slide of a PPT or PDF into a scene with an avatar and a voiceover, and
        video translation with lip-sync <Cite n={[7]} />. Synthesia calls itself an “AI Video and Avatar
        platform for business”. It says you can start from a PowerPoint, a document or a link and get a
        draft with a script, scenes and an avatar presenter, and publish the result as a link, an embed
        or SCORM <Cite n={[8]} />.
      </p>
      <p>
        <strong>What it is for.</strong> Training, onboarding, internal updates, explainers in several
        languages, and anyone who does not want to be on camera.
      </p>
      <p>
        <strong>Where it stops.</strong> The video starts from a script and a presenter, not from the
        clips on your phone. Both tools here also take material you upload: Synthesia says you can
        upload your own images, footage and voiceovers, and each of them translates a video you upload{' '}
        <Cite n={[7, 8]} />. If the point of the video is the real place, the real product or the real
        person as they were filmed, start from a kind that is built around footage.
      </p>
      <p>
        <strong>Choose this kind when</strong> the message is information, there is no footage, and you
        may need it in more than one language.
      </p>

      <h2 id="generative-video">5. Generative video</h2>
      <p>
        You describe a scene, or supply reference pictures, and a model makes footage that nobody
        filmed.
      </p>
      <p>
        Runway describes a platform to generate and edit video, images and audio in one workspace{' '}
        <Cite n={[9]} />. Its product page lists generating video from text, images, video or audio,
        editing one frame and applying the change to the rest of the video, and an Agent that creates
        videos through conversation <Cite n={[10]} />. Pika calls itself an AI creative platform. Its
        page lists a Video Studio that generates anything from a short clip to a 30-second multi-shot
        video, and a tool that extends a clip <Cite n={[11]} />.
      </p>
      <p>
        <strong>What it is for.</strong> Ads, concept films, mood pieces, and shots that could not be
        filmed.
      </p>
      <p>
        <strong>Where it stops.</strong> You are the director. You describe each shot and choose among
        the results. A generated scene is also not your shop, your students or your event, so it does
        not replace footage of the real thing.
      </p>
      <p>
        <strong>Choose this kind when</strong> most of the video does not exist yet and does not need to
        be real.
      </p>

      <h2 id="ai-video-teams">6. AI video teams that cut your raw footage</h2>
      <p>
        You upload the clips you shot and say what the video is for. The tool decides what to keep and
        hands you a first cut to react to. This is the newest kind, and the name for it is not settled.
      </p>
      <p>
        Gling, which describes itself as an AI video editor for YouTube creators, says it removes bad
        takes, silences and filler words, and that it works with Final Cut Pro, DaVinci Resolve and
        Adobe Premiere or exports a media file <Cite n={[12]} />. Eddie AI describes an assistant editor built for
        professional video editors: you describe the story, and it turns raw interview footage into a
        rough cut that opens in the editor you already use <Cite n={[13]} />. Captions says its AI
        editing turns footage into a fully edited video that is ready to share <Cite n={[14]} />.
        invideo calls itself “the agentic video editor for serious creatives”, and its page shows the
        steps as upload footage, describe the edit, review the cut and export <Cite n={[15]} />.
        ReelBolt, which we make, belongs here too.
      </p>
      <p>
        <strong>What it is for.</strong> People whose story is already in the camera roll and who would
        rather react to a cut than start one.
      </p>
      <p>
        <strong>Where it stops.</strong> A first cut is not a final cut. The tool did not live your
        week, so it can keep a take you find flat or drop a pause that was the point. You still watch
        it and say what to change.
      </p>
      <p>
        <strong>Choose this kind when</strong> you have real footage, no editor, and the blank timeline
        is what keeps the video from getting made.
      </p>

      <h2 id="where-reelbolt-sits">Where ReelBolt sits</h2>
      <p>
        ReelBolt is a public alpha, so expect some rough edges. It makes and reviews a first cut from
        your raw clips. You drop in between 1 and 20 clips and say in a sentence or two what the video
        is for. An AI editor chooses which parts to keep and leaves out dead air, false starts and
        retakes. When the speech in your clips could be read, cuts are placed so they do not land
        inside a spoken word. A reviewer then scores the result, and a low score sends the edit back
        for another pass before you see it.
      </p>
      <p>
        From the same clips it can add titles and callouts, mix in music or sound effects from files
        you upload, write and voice a narration with captions, burn subtitles into a finished video,
        translate the subtitles, or dub the video in another language. A subtitles run also writes SRT
        and VTT files, but the run page has no download button for them yet. It can make a
        cover picture and write the text that goes with the post. You choose the shape before each
        run: landscape, vertical, square or portrait. An editor, labeled Early preview, lets you
        change the cut yourself. For software teams there is also a recipe that makes a promo video
        from an app’s source code.
      </p>
      <Callout title="What ReelBolt does not do">
        <p>
          <strong>Long videos.</strong> It is built for short videos, about 90 seconds or less.
        </p>
        <p>
          <strong>Video from a prompt alone, on the trial.</strong> No trial recipe turns only a text
          prompt or only photos into a video. AI-generated clips are part of the Pro plan and the
          plans above it.
        </p>
        <p>
          <strong>A real edit from one long take.</strong> It needs several different shots to cut
          together, and can do no more than trim a single take.
        </p>
        <p>
          <strong>A batch of clips from one long video.</strong> Each run makes one video, and ReelBolt
          does not post it for you.
        </p>
        <p>
          <strong>Long presenter videos or stock avatars.</strong> Avatars are of a real person who has
          given recorded consent, appear in short clips, are part of the Pro plan and the plans above
          it, and are still being rolled out.
        </p>
        <p>
          <strong>Lip-synced dubs.</strong> A dub keeps the speaker’s original mouth movement.
        </p>
        <p>
          <strong>A music library.</strong> You upload your own track. Generating music is part of the
          Creator plan and the plans above it.
        </p>
        <p>
          <strong>A new shape for a finished video.</strong> A finished video cannot be re-cut to
          another shape yet. A second shape is another run.
        </p>
      </Callout>

      <h2 id="when-another-kind-is-better">When another kind is the better choice</h2>
      <ul>
        <li>
          Your video is an hour of talking and you want to keep most of it: a transcript editor.
        </li>
        <li>
          You already publish long videos and want a week of short posts from each one: a clip tool.
        </li>
        <li>
          You have a script and no footage, and the message is information: a presenter tool.
        </li>
        <li>
          You need scenes that do not exist and do not need to be real: generative video.
        </li>
        <li>
          You enjoy editing, or you want to place every cut yourself: a template or all-in-one editor.
        </li>
        <li>
          A professional editor will finish the video in Premiere Pro, DaVinci Resolve or Final Cut
          Pro: a tool that works with those editors. Eddie AI describes itself as an assistant for
          professional editors <Cite n={[13]} />, and Gling says it works with all three{' '}
          <Cite n={[12]} />. ReelBolt can export a hand-off package for another editor, but that
          feature is an Early preview.
        </li>
      </ul>
      <p>
        Pick ReelBolt when you have clips of something real, such as a shop, an event, a lesson, a
        product or a cause, you want a short video from them, and you would rather fix a first cut
        than build one.
      </p>

      <h2 id="trying-reelbolt">Trying ReelBolt, and what it costs to find out</h2>
      <p>
        No card is required. A trial workspace starts with {TRIAL_CREDITS} one-time credits. When you
        verify your email, your account also receives a one-time grant of {FIRST_VIDEO_GRANT_CREDITS}{' '}
        credits, so your first video is on us. Those credits last {FIRST_VIDEO_GRANT_EXPIRY}. Trial
        exports are {TRIAL_RESOLUTION} with a watermark. Paid plans remove the
        watermark and export at higher resolutions, and the <Link href="/pricing">pricing page</Link>{' '}
        lists what each plan includes.
      </p>
      <p>
        Each run shows the credits it is expected to use before you start it. If a step fails because
        of a problem on our side, the credits held for it are refunded. A run takes minutes for a small
        job and sometimes half an hour for a big one.
      </p>
      <p>
        <Link href="/blog/first-video-in-one-sitting">Your first video in one sitting</Link> walks the
        first run screen by screen, and <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a>{' '}
        lists everything ReelBolt can make today and what it cannot.
      </p>
      <p>
        For the other tools, use each company’s own pricing page. A plan, a credit and a free tier mean
        something different at each one, so compare the cost of the video you need, not the headline
        figure.
      </p>

      <SourceList
        sources={[
          { id: 'source-1', label: 'Descript, home page', url: 'https://www.descript.com/', accessed: CHECKED },
          { id: 'source-2', label: 'OpusClip, home page', url: 'https://www.opus.pro/', accessed: CHECKED },
          { id: 'source-3', label: 'Vizard, home page', url: 'https://vizard.ai/', accessed: CHECKED },
          { id: 'source-4', label: 'Riverside, Magic Clips', url: 'https://riverside.com/magic-clips', accessed: CHECKED },
          { id: 'source-5', label: 'CapCut, home page', url: 'https://www.capcut.com/', accessed: CHECKED },
          { id: 'source-6', label: 'Kapwing, home page', url: 'https://www.kapwing.com/', accessed: CHECKED },
          { id: 'source-7', label: 'HeyGen, home page', url: 'https://www.heygen.com/', accessed: CHECKED },
          { id: 'source-8', label: 'Synthesia, home page', url: 'https://www.synthesia.io/', accessed: CHECKED },
          { id: 'source-9', label: 'Runway, home page', url: 'https://runway.com/', accessed: CHECKED },
          { id: 'source-10', label: 'Runway, product', url: 'https://runway.com/product', accessed: CHECKED },
          { id: 'source-11', label: 'Pika, home page', url: 'https://pika.art/', accessed: CHECKED },
          { id: 'source-12', label: 'Gling, home page', url: 'https://www.gling.ai/', accessed: CHECKED },
          { id: 'source-13', label: 'Eddie AI, home page', url: 'https://www.heyeddie.ai/', accessed: CHECKED },
          { id: 'source-14', label: 'Captions, home page', url: 'https://www.captions.ai/', accessed: CHECKED },
          { id: 'source-15', label: 'invideo, home page', url: 'https://invideo.io/', accessed: CHECKED },
        ]}
      />
      <p className="mt-10 font-sans text-sm leading-relaxed text-ink-faint">Features checked on {CHECKED} from each vendor’s public pages; they may have changed. No vendor pricing page was read for this guide, and it gives no prices for other companies’ products. Product names are trademarks of their owners and are used only to identify the products.</p>
    </>
  );
}
