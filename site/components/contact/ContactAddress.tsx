import {
  COMPANY_LEGAL_NAME,
  CONTROLLER_NAME,
  GENERAL_EMAIL,
  PARTITA_IVA_LABEL,
  PHONE_NUMBER,
  RESPONSE_TIME_SLA,
  SUPPORT_EMAIL,
} from '@/lib/legal-placeholders';

import { useT } from '@/i18n';

export function ContactAddress({
  showPhone = false,
  showResponseTime = false,
  className,
}: {
  showPhone?: boolean;
  showResponseTime?: boolean;
  className?: string;
}) {
  const t = useT('layout.contactAddress');
  return (
    <address className={`not-italic space-y-1 text-sm text-ink-muted ${className ?? ''}`.trim()}>
      <p className="font-medium text-ink">{COMPANY_LEGAL_NAME}</p>
      <p>{CONTROLLER_NAME}</p>
      {PARTITA_IVA_LABEL && <p>{PARTITA_IVA_LABEL}</p>}
      <p>
        <a href={`mailto:${SUPPORT_EMAIL}`} className="hover:text-ink">
          {SUPPORT_EMAIL}
        </a>
      </p>
      <p>
        <a href={`mailto:${GENERAL_EMAIL}`} className="hover:text-ink">
          {GENERAL_EMAIL}
        </a>
      </p>
      {showPhone && <p>{PHONE_NUMBER}</p>}
      {showResponseTime && <p>{t('responseTime', { value: RESPONSE_TIME_SLA })}</p>}
    </address>
  );
}
