import type { JSX } from 'react';
import type { PostMeta } from '@/lib/blog/types';

export const meta: PostMeta = {
  slug: 'reelbolt-public-api',
  title: 'Start ReelBolt runs from your own systems with the API and webhooks',
  description:
    'Create an API key, start a run from your own script, and get a signed webhook when it ends. What the ReelBolt API does today, and what is still alpha.',
  date: '2026-11-10',
  tags: ['guides'],
  readingMinutes: 5,
  faqs: [
    {
      q: 'Who can create an API key?',
      a: 'Only Owners and Admins of a workspace create, see, change and revoke keys. The secret is shown once, when you create the key, so copy it then. If you lose it, create a new key and revoke the old one.',
    },
    {
      q: 'Which plans include the API?',
      a: 'The API is available on paid plans, Creator and up. On a trial workspace, creating a key is refused with a clear message. The API guide lists the plans that include it.',
    },
    {
      q: 'Can I change a key after I create it?',
      a: 'You can change its name and its credit caps. Scopes, the project list and the expiry cannot be edited, so to change one of them, create a new key and revoke the old one.',
    },
    {
      q: 'Do API runs cost more than runs in the app?',
      a: 'No. Runs started through the API cost the same credits at the same prices as runs started in the dashboard, and they draw from the same workspace balance. You can also give each key a daily, weekly or monthly credit cap.',
    },
    {
      q: 'Which events can a webhook send?',
      a: 'By default, run.succeeded, run.failed and run.cancelled. You can also ask for an event when each step finishes, and for events about queued starts. The address must be a public https URL.',
    },
    {
      q: 'Is the API stable?',
      a: 'Not yet. ReelBolt is in alpha, and the API may change. Check the API guide before you build something that depends on a detail.',
    },
  ],
};

export default function ReelboltPublicApi(): JSX.Element {
  return (
    <>
      <p>
        If your videos start somewhere else, such as a content calendar, a product catalogue or a script you
        already run, opening the dashboard to press <strong>Run</strong> gets old fast. The ReelBolt API
        lets your own scripts and services do what you do in the app: upload files, create a workflow from a
        template, start a run, follow it and download the result.
      </p>
      <p>
        Webhooks close the loop. Instead of asking again and again whether a run is done, your server gets a
        message when it ends.
      </p>

      <h2 id="keys">Keys you control</h2>
      <p>
        An Owner or Admin of the workspace makes an API key on the API keys page. You give it a name and pick
        its scopes. A key can do only what its scopes allow, so a script that only reads results gets only
        read access. A key never gets administrator rights, and it cannot reach workspace settings, billing
        or members.
      </p>
      <p>
        You can also limit a key to chosen projects, so it sees nothing else, and set an expiry. If you set
        one, ReelBolt mails a warning a week before and again a day before. The key list marks keys with no
        expiry and keys nobody has used for 30 days, so you can clean up.
      </p>
      <p>
        The secret starts with <code>rbk_</code> and is shown once. ReelBolt keeps only a fingerprint, so it
        cannot show it again. Scopes, projects and expiry cannot be edited after you create the key. If
        something needs to change, make a new key and revoke the old one. Revoking works immediately.
      </p>
      <p>
        Keep the secret on your server, never in browser or mobile code or in a repository. If it leaks,
        revoke it and create a new one.
      </p>

      <h2 id="start-a-run">Start a run, then read the result</h2>
      <p>
        A run start takes the same run request the dashboard sends. You can ask for an estimate first and
        confirm the cost you saw when you start. Then you can read the run&rsquo;s status, its steps and its
        credits, stop, resume or retry it, and list its outputs. An output comes as a link that works for 15
        minutes.
      </p>
      <p>
        Runs started this way cost the same credits at the same prices as runs started in the dashboard.
        Each key can have a daily, weekly or monthly credit cap, so a script that goes wrong cannot spend
        the whole balance. Each key can also make 300 requests a minute, and a start over the hourly run
        limit is queued instead of refused.
      </p>

      <h2 id="webhooks">Webhooks that tell you when a run ends</h2>
      <p>
        You give ReelBolt a public https address and the events you want. By default you get{' '}
        <code>run.succeeded</code>, <code>run.failed</code> and <code>run.cancelled</code>. Each message
        includes the run, the project, the status, a failure code if there is one, and the credits charged.
      </p>
      <p>
        Every message is signed, and the format follows the{' '}
        <a href="https://www.standardwebhooks.com">Standard Webhooks</a> convention, so existing libraries can
        check it. The signing secret starts with <code>whsec_</code> and is shown once. You can rotate it
        later, and for 24 hours both the old and the new secret work, so you can deploy without missing an
        event.
      </p>
      <p>
        If your server does not answer in time, ReelBolt tries again, for about a day in total. An endpoint
        that only fails for 3 days is switched off, and the workspace Owners and Admins get a mail. In the
        dashboard you can see the delivery log for an endpoint, send a test event, and send any delivery
        again. Deliveries are kept for 30 days.
      </p>

      <h2 id="availability">Plans and availability</h2>
      <p>
        The API is available on paid plans, Creator and up. A trial workspace gets a clear refusal when it
        tries to create a key. The <a href="/docs/api">API guide</a> lists which plans include it.
      </p>

      <h2 id="alpha">Still alpha</h2>
      <p>
        ReelBolt is in alpha, and the API may change as we learn how people use it. Build with that in mind:
        read the guide before you rely on a detail, and keep a key&rsquo;s scopes small.
      </p>

      <h2 id="how-to-start">How to start</h2>
      <p>
        The <a href="/docs/api">API guide</a> has a short quick start, from creating a key to fetching an
        output link. For the details, see <a href="/docs/api/keys">Keys</a> and{' '}
        <a href="/docs/api/webhooks">Webhooks</a>, and <a href="/docs/api/runs">Runs</a> for starting and
        following a run.
      </p>
    </>
  );
}
