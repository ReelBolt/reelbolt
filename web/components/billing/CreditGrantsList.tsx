import { Badge, Group, Stack, Text } from '@mantine/core';
import type { BillingCreditGrant } from '@/lib/types/billing';
import { useFormat, useT, type T } from '@/i18n';
import { formatCredits } from '@/lib/utils/usage-labels';

/** The buyer's name for where a grant came from. An unknown source reads as plain "Credits". */
export function creditSourceLabel(t: T, source: string): string {
  switch (source.toLowerCase()) {
    case 'trial': return t('source.trial');
    case 'plan': return t('source.plan');
    case 'topup': return t('source.topup');
    case 'promo': return t('source.promo');
    default: return t('source.other');
  }
}

/**
 * The credits in the balance, grouped by where they came from, soonest expiry first — the order the
 * server sends them in, and the order they are spent in.
 */
export function CreditGrantsList({ grants }: { grants: BillingCreditGrant[] }) {
  const t = useT('billing.grants');
  const f = useFormat();
  return (
    <Stack gap="xs" data-testid="credit-grants-list">
      <Text size="xs" c="dimmed" fw={600}>{t('title')}</Text>
      {grants.length === 0 ? (
        <Text size="sm" c="dimmed">{t('empty')}</Text>
      ) : (
        grants.map((grant, index) => (
          <Group key={`${grant.source}-${grant.expiresAt ?? 'none'}-${index}`} justify="space-between" gap="xs" wrap="wrap">
            <Group gap="xs" wrap="wrap">
              <Text size="sm" fw={600} style={{ fontVariantNumeric: 'tabular-nums' }}>
                {t('amount', { amount: formatCredits(grant.remaining) })}
              </Text>
              <Badge variant="light" color={grant.source.toLowerCase() === 'promo' ? 'grape' : 'gray'} size="sm"
                styles={{ root: { fontSize: 12, textTransform: 'none', maxWidth: 'none', height: 'auto', minHeight: 20, paddingBlock: 2 }, label: { whiteSpace: 'normal', overflow: 'visible', textOverflow: 'clip' } }}
              >
                {creditSourceLabel(t, grant.source)}
              </Badge>
            </Group>
            <Text size="sm" c="dimmed">
              {grant.expiresAt ? t('expires', { date: f.date(grant.expiresAt, 'short') }) : t('noExpiry')}
            </Text>
          </Group>
        ))
      )}
    </Stack>
  );
}
