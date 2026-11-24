import { Link } from '@/i18n/link';
import { buildMetadata } from '@/lib/seo';
import { siteConfig } from '@/lib/site-config';
import {
  COMPANY_LEGAL_NAME,
  OPERATOR_STATEMENT,
  RUNNER_EULA_LAST_UPDATED,
  SUPPORT_EMAIL,
} from '@/lib/legal-placeholders';

export const metadata = buildMetadata({
  title: 'Runner End User Licence Agreement',
  description: `The end user licence agreement for the ${siteConfig.name} desktop runner.`,
  path: '/legal/runner-eula',
});

const h2 = 'mt-10 font-display text-xl font-bold text-ink first:mt-0';
const p = 'mt-4 font-sans text-ink-muted';
const ul = 'mt-4 list-disc space-y-2 pl-5 font-sans text-ink-muted';

export default function RunnerEulaPage() {
  return (
    <>
      <p className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">
        Last updated: {RUNNER_EULA_LAST_UPDATED}
      </p>
      <h1 className="mt-2 font-display text-3xl font-bold text-ink sm:text-4xl">
        Runner End User Licence Agreement
      </h1>
      <p className={p}>
        This End User Licence Agreement (&quot;Agreement&quot;) is between you and{' '}
        {COMPANY_LEGAL_NAME} (&quot;we&quot;, &quot;us&quot;, or &quot;our&quot;), and governs your
        installation and use of the {siteConfig.name} desktop runner (the &quot;Runner&quot;). By
        installing or running the Runner, you accept this Agreement. If you do not agree, do not
        install or run the Runner. {OPERATOR_STATEMENT}
      </p>

      <h2 className={h2}>1. Licence grant</h2>
      <p className={p}>
        We grant you a limited, non-exclusive, non-transferable, revocable licence to install and
        run the Runner on computers you own or control, solely to receive and perform work from the{' '}
        {siteConfig.name} Service. You may not redistribute, resell, sublicense, reverse engineer,
        decompile, or modify the Runner except to the extent the law expressly permits
        notwithstanding this restriction.
      </p>

      <h2 className={h2}>2. Automatic updates</h2>
      <p className={p}>
        The Runner updates itself automatically so that it stays secure and compatible with the
        Service. By installing the Runner and accepting this Agreement, you consent to those
        automatic updates being downloaded and applied to the Runner on your computer.
      </p>

      <h2 className={h2}>3. Telemetry</h2>
      <p className={p}>
        The Runner sends no telemetry by default: telemetry is off unless you turn it on. You may
        opt in to telemetry from the Runner&apos;s settings, and you can turn it off again at any
        time. Before any telemetry is sent, you can review the exact payload that would be
        transmitted, so you always know what the Runner is about to send.
      </p>

      <h2 className={h2}>4. Revocation and termination</h2>
      <p className={p}>
        We may revoke this licence, and stop the Runner from working with the Service, if you breach
        this Agreement or if we are required to do so by law. You may stop using the Runner and
        unpair your device at any time. Upon termination of this Agreement you must stop using and
        uninstall the Runner.
      </p>

      <h2 className={h2}>5. No warranty</h2>
      <p className={p}>
        The Runner is provided &quot;as is&quot; and &quot;as available&quot;, without warranties of
        any kind, whether express or implied, including any implied warranties of merchantability,
        fitness for a particular purpose, or non-infringement, to the fullest extent permitted by
        applicable law. We do not warrant that the Runner will be error-free, uninterrupted, or free
        of defects.
      </p>

      <h2 className={h2}>6. General</h2>
      <p className={p}>
        To the extent not covered by this Agreement, your use of the Runner is also subject to the{' '}
        <Link href="/legal/terms" className="text-accent-ink hover:text-accent">
          {siteConfig.name} Terms &amp; Conditions
        </Link>
        , including its provisions on liability, governing law, and jurisdiction.
      </p>

      <h2 className={h2}>7. Contact</h2>
      <p className={p}>Questions about this Agreement can be sent to:</p>
      <ul className={ul}>
        <li>{COMPANY_LEGAL_NAME}</li>
        <li>
          <a href={`mailto:${SUPPORT_EMAIL}`} className="text-accent-ink hover:text-accent">{SUPPORT_EMAIL}</a>
        </li>
      </ul>
      <p className={p}>{OPERATOR_STATEMENT}</p>
    </>
  );
}
