import { Text } from '@mantine/core';
import { useT } from '@/i18n';

/**
 * The trademark credit line for the signed-out frame. The ReelBolt word mark is an application
 * (filed with the Italian trademark office), not a registration, so the unregistered symbol is the
 * only one allowed here: never the registered-mark symbol until a registration is granted. The
 * wording follows the operator statement on the marketing site's legal pages; the owner is named
 * the same way they are named there and nothing else about them (no address, no filing number).
 */
export function TrademarkNotice() {
  const t = useT('shell');
  return (
    <Text size="xs" c="dimmed" ta="center" mt="lg">
      {t('trademark')}
    </Text>
  );
}
