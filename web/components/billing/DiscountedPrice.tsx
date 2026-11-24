import { Group, Text } from '@mantine/core';
import type { CheckoutPreview } from '@/lib/api/promotions';
import { previewAmounts } from '@/lib/utils/billing';

/**
 * A discounted price as the payment provider quoted it: its subtotal struck through, its total
 * beside it. Both strings are printed exactly as they arrived; nothing here parses or computes them.
 */
export function DiscountedPrice({ preview, note }: { preview: CheckoutPreview; note: string }) {
  const amounts = previewAmounts(preview);
  return (
    <div>
      <Group gap="xs" align="baseline">
        <Text size="sm" c="dimmed" td="line-through" data-testid="promo-preview-subtotal">
          {amounts.subtotal}
        </Text>
        <Text size="xl" fw={700} data-testid="promo-preview-total">
          {amounts.total}
        </Text>
      </Group>
      <Text size="xs" c="dimmed">{note}</Text>
    </div>
  );
}
