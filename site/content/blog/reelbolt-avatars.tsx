import type { JSX } from 'react';
import Link from 'next/link';
import type { PostMeta } from '@/lib/blog/types';

export const meta: PostMeta = {
  slug: 'reelbolt-avatars',
  title: 'Avatars in ReelBolt: a real person, with their consent',
  description:
    'ReelBolt can now show a real person in short generated clips, made from their own pictures, videos and voice, and only with their recorded consent. Here is what avatars do, and what they do not.',
  date: '2026-11-06',
  tags: ['product'],
  readingMinutes: 5,
  faqs: [
    {
      q: 'Who can I make an avatar of?',
      a: 'Yourself, or a person who has given you written permission. The person must be 18 or older, and you record their consent in ReelBolt before the avatar exists. Avatars of children, of someone who did not agree, or of a public figure who is not you are not allowed. There are no stock avatars.',
    },
    {
      q: 'Which plans include avatars?',
      a: 'Avatars come with Pro, Team and Enterprise. They are not on Creator or the free trial. Avatars are still being rolled out, so they may not be switched on in your workspace yet.',
    },
    {
      q: 'Are videos made with an avatar labeled as AI?',
      a: 'Yes. Every clip of a person is marked as AI-generated in the video file itself. It also shows a small AI-generated label in the picture, unless you turn that off for the avatar and tell your viewers in another clear way.',
    },
    {
      q: 'How long can an avatar clip be?',
      a: 'Short. A talking clip lasts from 5 to 15 seconds. A spokesperson clip is a short generated clip and does not promise exact words. There is no recipe for a longer presenter video.',
    },
    {
      q: 'Does ReelBolt lip-sync dubbed footage?',
      a: 'No. A dub translates the narration and replaces the dialogue, and the speaker’s original mouth movement stays as it was. Avatars do not change that: they appear in new generated clips and never re-sync the lips of a person in footage you already have.',
    },
    {
      q: 'Can the person change their mind?',
      a: 'Yes. Withdrawing consent makes the avatar unusable at once, and it is deleted automatically 30 days later. You can also delete it straight away. The person shown can ask you, or write to us directly, and we make the avatar unusable while we look into it.',
    },
  ],
};

export default function ReelboltAvatars(): JSX.Element {
  return (
    <>
      <p>
        ReelBolt now has avatars. You can make an avatar of a real person, from their own pictures, short
        videos and voice recordings, and show them in short generated clips. The person has to agree
        first, and you record that agreement in ReelBolt.
      </p>
      <p>
        This post covers what an avatar can do, why consent is built into every step, and the limits we
        chose to keep.
      </p>

      <h2 id="what-you-can-do-now">What you can do now</h2>
      <p>
        An avatar belongs to one project. You make it in the project’s <strong>Avatars</strong> tab, next
        to <strong>Voices</strong>, from files you have already uploaded: at least one clear, front-facing
        picture of the person, and if you like, a short video of them speaking to the camera and short
        recordings of their voice. You can also link one of the project’s voices to the avatar.
      </p>
      <p>
        Once the avatar is ready, a workflow can show that person in a short clip. On the app page you pick
        the person from an <strong>Avatar</strong> field, then press <strong>Run</strong>. Before the run
        starts you see an estimate of the credits it will use.
      </p>

      <h2 id="why-consent-comes-first">Why consent comes first</h2>
      <p>
        A face and a voice belong to a person. So an avatar works like a cloned voice in ReelBolt: it
        exists only after the person has said yes.
      </p>
      <ul>
        <li>
          You confirm that you are the person, or that you have their written permission. You can attach
          the signed permission as a file of the project.
        </li>
        <li>You confirm that the person is 18 or older. Avatars of children are not allowed.</li>
        <li>
          You read the consent statement in full and tick it. It says the person agreed to their face,
          body and voice being used to make AI videos in this project, and to their files being sent to the
          AI services the project uses for that.
        </li>
        <li>
          You choose how long to keep the avatar: one year by default, two years at most. You get an email
          a week before that date.
        </li>
      </ul>
      <p>
        Consent is not a one-time box. The avatar is checked again just before each clip is made. If
        consent was withdrawn, the keep-until date has passed, or one of its files changed, the next clip
        is not made and you are not charged for it.
      </p>
      <p>
        The person can change their mind at any time. Withdrawing consent makes the avatar unusable
        everywhere in the project at once, and it is deleted 30 days later. Deleting it removes the avatar
        and its files straight away. The person shown does not need a ReelBolt account to ask: they can
        write to us, as our <Link href="/legal/privacy">Privacy Policy</Link> explains.
      </p>
      <p>
        Some uses are off limits whatever the paperwork says: an avatar of someone who did not agree, or of
        a public figure unless the avatar is of you. The full rules are in our{' '}
        <Link href="/legal/acceptable-use">Acceptable Use Policy</Link>.
      </p>
      <p>
        One more safeguard: the assistant can tell you which avatars are ready and why one is not, but it
        cannot make, change, withdraw or delete an avatar. Consent is only ever given by you, in the{' '}
        <strong>Avatars</strong> tab.
      </p>

      <h2 id="two-kinds-of-clip">Two kinds of clip</h2>
      <p>Two ready-made recipes show an avatar.</p>
      <p>
        <strong>Spokesperson clip</strong> shows the person in a short generated clip that you describe in
        a prompt. It does not promise exact words, even when the avatar has voice recordings.
      </p>
      <p>
        <strong>Talking avatar (budget)</strong> is the cheaper option, and the one to use when the person
        should say a script. It writes a few sentences from your brief, speaks them in the voice linked to
        the avatar, makes one still picture of the person, and animates that picture so they say the words.
        The clip lasts as long as the speech: at least 5 and at most 15 seconds. The still is kept with the
        avatar and reused for later clips while the avatar is unchanged.
      </p>

      <h2 id="what-gets-sent">What gets sent, and what it costs</h2>
      <p>
        To keep the person consistent, the avatar’s pictures, videos and recordings are sent to the video
        service with every clip. That needs two separate permissions. The first is the person’s consent,
        recorded when you made the avatar. The second is a switch on the step,{' '}
        <strong>Allow sending your media to the video service</strong>, which you turn on yourself. The
        assistant can never turn it on for you.
      </p>
      <p>
        Sending pictures and video costs extra with some video services. The estimate you see before a run
        includes it. For a talking clip, the cost is one still picture plus the seconds of video.
      </p>

      <h2 id="marked-as-ai-generated">Marked as AI-generated</h2>
      <p>
        People watching a clip should know it was made with AI. Every clip of a person is marked as
        AI-generated in the video file itself, and it shows a small <strong>AI-generated</strong> label in
        the picture. You can turn the visible label off for an avatar, but only if you tell your viewers in
        another clear way. The mark in the file stays either way, and our rules do not allow removing or
        hiding it.
      </p>

      <h2 id="what-avatars-do-not-do">What avatars do not do</h2>
      <ul>
        <li>
          <strong>No stock avatars.</strong> Every avatar is a real person who agreed. There is no library of
          ready-made presenters.
        </li>
        <li>
          <strong>No long presenter videos.</strong> Avatars appear in short clips. There is no recipe for a
          presenter who talks through a whole video.
        </li>
        <li>
          <strong>No lip-sync of existing footage.</strong> Dubs still keep the speaker’s original mouth
          movement. ReelBolt does not re-sync lips in footage you already have.
        </li>
        <li>
          <strong>Limited sharing across projects.</strong> An avatar can be used only in the project it was
          made in, unless an Owner or Admin saves it to the workspace avatar library; then members of that
          workspace can use it in the workspace&apos;s other projects. It never leaves the workspace.
        </li>
      </ul>
      <p>
        If an AI presenter is the core of your video, a tool built for that may suit you better. Our{' '}
        <Link href="/blog/reelbolt-vs-heygen">ReelBolt vs HeyGen</Link> and{' '}
        <Link href="/blog/reelbolt-vs-descript">ReelBolt vs Descript</Link> comparisons go through the
        differences.
      </p>

      <h2 id="plans-and-availability">Plans and availability</h2>
      <p>
        Avatars come with Pro, Team and Enterprise. They are not on Creator or the free trial. The{' '}
        <Link href="/pricing">pricing page</Link> has the plan details.
      </p>
      <p>
        Avatars are still being rolled out and may not be switched on in your workspace yet. Making clips
        may reach your workspace later than the <strong>Avatars</strong> tab. The avatars you prepare now
        will work with it.
      </p>

      <h2 id="how-to-start">How to start</h2>
      <p>
        The <a href="/docs/projects/avatars">Avatars guide</a> walks through the files you need, the consent
        you give, how to make a clip, and how to withdraw or delete an avatar. To compare avatar clips with
        the other things ReelBolt makes, see{' '}
        <a href="/docs/recipes/choose-a-recipe">Choose a recipe</a>.
      </p>
    </>
  );
}
