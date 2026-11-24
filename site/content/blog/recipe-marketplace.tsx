import type { JSX } from 'react';
import type { PostMeta } from '@/lib/blog/types';

export const meta: PostMeta = {
  slug: 'recipe-marketplace',
  // Held out of listings while the live gallery has no shared recipes; remove once it does.
  unlisted: true,
  title: 'The community recipe gallery: copy a video workflow someone else built',
  description:
    'Browse recipes made by other ReelBolt users, read their prompts, and copy one into your project for free. How the gallery works and what a copy needs from you.',
  date: '2026-11-10',
  tags: ['product'],
  readingMinutes: 5,
  faqs: [
    {
      q: 'Do I need an account to browse the gallery?',
      a: 'No. Anyone can search and filter the community gallery and watch the preview videos. To copy a recipe you sign in with a verified email address.',
    },
    {
      q: 'Does copying a recipe cost anything?',
      a: 'No. Nothing runs and nothing is charged when you copy. What a recipe costs to run is a separate matter, and each recipe page has a note about it.',
    },
    {
      q: 'What happens to my copy if the author unpublishes the recipe?',
      a: 'Nothing. The copy is an ordinary app in your project with no link back to the recipe. If the author changes it, unpublishes it or loses it, your copy stays exactly as it is.',
    },
    {
      q: 'Will a copy send my footage to outside services?',
      a: 'Not until you say so. The setting that allows footage to be sent to outside services is switched off in every copy. If a step needs it, you switch it on yourself.',
    },
    {
      q: 'What if a recipe breaks the rules?',
      a: 'Report it from the recipe page. You need a verified email address and can send up to 10 reports a day. When several different people report the same recipe, it is hidden from the gallery until a person on the ReelBolt team has looked at it.',
    },
    {
      q: 'Can I publish my own recipe?',
      a: 'Yes, with a verified email address and a finished video to use as the preview. Each recipe is checked before it appears, and some wait for a person to review them. Authors can publish 5 recipes a day, and a workspace can have 50 live at once.',
    },
  ],
};

export default function RecipeMarketplace(): JSX.Element {
  return (
    <>
      <p>
        The community gallery holds recipes made by other ReelBolt users. You can look through them,
        check what each one does, and copy the one you like into your own project.
      </p>

      <h2 id="browse-without-an-account">Browse without an account</h2>
      <p>
        The gallery is open to everyone, on the ReelBolt site and inside the app. You can search, filter
        by <strong>Category</strong>, <strong>Language</strong>, <strong>Cost</strong> and{' '}
        <strong>Step type</strong>, and sort by <strong>Newest</strong>, <strong>Most copied</strong> or{' '}
        <strong>Top rated</strong>.
      </p>
      <p>
        Each recipe shows a short preview video, its steps, its prompts and a note about what it costs to
        run. You can see how a recipe is built before you sign up for anything.
      </p>

      <h2 id="read-the-prompts-first">Read the prompts first</h2>
      <p>
        Other people write these recipes, so check them. The recipe page has a section called{' '}
        <strong>What it does</strong> and another listing the text the recipe sends to AI models. A copy
        does what the recipe says, so read those before you copy.
      </p>

      <h2 id="copy-a-recipe">Copy a recipe</h2>
      <p>
        Sign in with a verified email address, open the recipe and choose <strong>Copy to my project</strong>.
        Pick the project, change the name if you want, and confirm. The copy lands in your project as an
        ordinary app.
      </p>
      <p>
        Copying costs nothing. Nothing runs and nothing is charged until you run the app yourself. If you
        copy the same recipe again with the same name into the same project, you get the copy you already
        have.
      </p>

      <h2 id="what-you-fill-in">What you fill in yourself</h2>
      <p>
        An author cannot share everything. Files from their project, their voice choices, and the
        connection to their AI service are left out of the recipe. Their account and project details never
        travel with it either. The recipe page lists what you will need, and your copy shows each missing
        piece next to the step it belongs to: an AI connection, a voice, or a file.
      </p>
      <p>
        One setting stays off in every copy. Allowing your footage to be sent to outside services is
        switched off, so a copy never sends your media anywhere on its own. If a step needs it, you turn it
        on.
      </p>

      <h2 id="your-copy-is-yours">Your copy is yours</h2>
      <p>
        A copy has no link back to the recipe. If the author edits the recipe, unpublishes it or deletes
        their account, your copy stays as it was when you made it. A removed recipe cannot be copied again,
        but copies that already exist are untouched.
      </p>

      <h2 id="rating-and-reporting">Rating and reporting</h2>
      <p>
        You can give a recipe one to five stars after your workspace has copied it and a run of that copy
        has finished. Each account rates once and can change the rating later. There are no written
        reviews. The average shows only once a recipe has at least 5 ratings, and the usage number counts
        the different workspaces that copied it.
      </p>
      <p>
        If a recipe is illegal, sexual, copied from someone else, spam, or misleading, report it from its
        page. The author never sees who reported. When several different people report the same recipe, it
        is hidden from the gallery until a person on the ReelBolt team has looked at it.
      </p>

      <h2 id="publishing-your-own">Publishing your own</h2>
      <p>
        To share one of your apps, choose <strong>Share as recipe</strong>. You add a name, a description,
        a category and a language, and pick a finished video as the preview. Before it goes public,
        ReelBolt shows you what it removes from the recipe, so read that list. The text you wrote into
        prompts, narration and overlays stays and is public. The preview video is public too, so use one
        you have the right to show.
      </p>
      <p>
        Every recipe and every new version is checked automatically. Some wait for a person on the team,
        for example when the author has not shared a recipe before and is not on a paid plan. You can
        unpublish at any time, and copies already made stay with the people who made them. The{' '}
        <a href="/docs/recipes/share-a-recipe">Share a recipe guide</a> has the details and limits.
      </p>

      <h2 id="availability">Availability</h2>
      <p>
        ReelBolt is in alpha, and the community gallery is new. It is open to everyone, with or without an
        account, and it grows as people share their apps. What it offers can change as we learn from how people use it.
      </p>

      <h2 id="how-to-start">How to start</h2>
      <p>
        The <a href="/docs/recipes/use-a-community-recipe">Use a community recipe guide</a> walks through
        browsing and copying. To rate or report a recipe, see{' '}
        <a href="/docs/recipes/rate-and-report-a-recipe">Rate and report a recipe</a>.
      </p>
    </>
  );
}
