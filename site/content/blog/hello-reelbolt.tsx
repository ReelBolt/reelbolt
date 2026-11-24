import type { JSX } from 'react';
import Link from 'next/link';
import type { PostMeta } from '@/lib/blog/types';

// Copy from the copy deck (docs/marketing/copy-deck.md, section 12). Keep the meta shape and the
// exported names so the registry does not change.
export const meta: PostMeta = {
  slug: 'hello-reelbolt',
  title: 'What ReelBolt is',
  description:
    'ReelBolt is an AI video team for software companies. Here is what it makes, who it is for, how a video gets made, and how credits and plans work today.',
  date: '2026-10-17',
  tags: ['product'],
  readingMinutes: 3,
  faqs: [
    {
      q: 'Who is ReelBolt for?',
      a: 'Product marketing teams that want a finished promotional video for each release, made from footage of the product they already have.',
    },
    {
      q: 'Can I change what the AI does?',
      a: 'Yes. Every project has a timeline editor and a picture editor, and assistant suggestions are shown for you to accept or reject.',
    },
  ],
};

export default function HelloReelBolt(): JSX.Element {
  return (
    <>
      <p>
        Most software teams know a video would help their launch. Most launches still go out with a
        screenshot and a changelog, because making a video means an agency quote, a few weeks of
        waiting, or somebody stopping their real work to fight with an editing timeline.
      </p>

      <p>
        ReelBolt is our answer to that. It is an AI video team for software companies: you point it at
        your product, your brand and any footage you already have, and it hands back a finished promo
        video you can publish, or open and change.
      </p>

      <h2 id="what-it-makes">What it makes</h2>
      <p>
        Launch videos, feature demos, social ads, onboarding clips and sales outreach. The output is a
        video file at the resolution and shape you pick: landscape for your site, vertical or square for
        social, or audio only when that is all you need.
      </p>

      <h2 id="how-a-video-gets-made">How a video gets made</h2>
      <p>
        Specialist AI agents split the work the way a production team would. A researcher learns what
        your product does and who it is for, a scriptwriter and a director turn that into a story, and
        an editor cuts your raw recordings on full sentences, without the pauses and weaker takes.
        Titles, color, music and sound effects go in last.
      </p>
      <p>
        Then a reviewer scores the draft against your brief. If it falls short, it goes back for another
        pass, so the first version you see has already been checked.
      </p>
      <p>
        On the plans that include them, editing rooms put several AI editors and a director on the big
        calls, like the cut or the color look, and generated b-roll fills in the cutaways, cold opens and
        end cards you never shot, under a spending cap you set.
      </p>

      <h2 id="you-keep-the-last-word">You keep the last word</h2>
      <p>
        Every finished video opens as an editable timeline: trim it, re-cut it from the transcript, change
        the grade or add subtitles. A layered picture editor handles thumbnails, and you can describe a
        graphic, such as a badge or an arrow, and have it drawn in your brand colors and fonts.
      </p>
      <p>
        Narration is written to fit the cut. It can be spoken in a stock voice or in a cloned voice, once
        the speaker has recorded their consent, and the same voice can fix a flubbed line or carry a dub
        into another language.
      </p>

      <h2 id="it-fits-the-way-you-already-work">It fits the way you already work</h2>
      <p>
        Connect a GitHub repo, a folder on your computer or a cloud drive, and new files show up in the
        project. A brand kit, shared across the workspace, keeps your colors and fonts consistent. The
        assistant can explain a failed run or start a new one, and every change it proposes shows the
        exact edit and its cost before you approve it. The same tools work from Claude Code.
      </p>
      <p>
        If you want more control, bring your own AI provider account and choose which model each agent
        uses, on the plans that include it. Work done with your own provider, or on a machine you own and
        pair with your workspace, costs no ReelBolt credits.
      </p>

      <h2 id="how-pricing-works">How pricing works</h2>
      <p>
        Each plan comes with monthly credits. Before every run you see an estimate of what it will use,
        and if a run fails because of a problem on our side, the credits come back. You can cancel anytime
        and manage your plan yourself. The current plans and prices are on the{' '}
        <Link href="/pricing">pricing page</Link>.
      </p>

      <h2 id="try-it-on-your-own-product">Try it on your own product</h2>
      <p>
        The quickest way to judge ReelBolt is to point it at something you built. Start with the free trial,
        or tell us what you are making and we will help you set it up.
      </p>
    </>
  );
}
