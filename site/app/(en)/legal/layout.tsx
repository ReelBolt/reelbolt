import type { ReactNode } from 'react';
import { Link } from '@/i18n/link';
import { Container } from '@/components/layout/Container';
import { Section } from '@/components/layout/Section';

// A small in-group nav so the legal documents are reachable from one another. The footer
// also links Terms and Privacy (and the runner EULA), but someone who lands on one document
// should not have to leave the legal group to find the next one.
const LEGAL_NAV = [
  { label: 'Terms of service', href: '/legal/terms' },
  { label: 'Privacy policy', href: '/legal/privacy' },
  { label: 'Acceptable use', href: '/legal/acceptable-use' },
  { label: 'Refunds', href: '/legal/refunds' },
  { label: 'Runner EULA', href: '/legal/runner-eula' },
] as const;

export default function LegalLayout({ children }: { children: ReactNode }) {
  return (
    <Section>
      <Container>
        <div className="mx-auto max-w-3xl">
          <nav aria-label="Legal documents">
            <ul className="flex flex-wrap gap-x-6 gap-y-2 pb-6">
              {LEGAL_NAV.map((item) => (
                <li key={item.href}>
                  <Link
                    href={item.href}
                    className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted hover:text-ink"
                  >
                    {item.label}
                  </Link>
                </li>
              ))}
            </ul>
          </nav>
          <div className="mt-8">{children}</div>
        </div>
      </Container>
    </Section>
  );
}
