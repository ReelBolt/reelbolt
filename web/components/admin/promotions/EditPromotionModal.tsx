'use client';

import { useState } from 'react';
import { Alert, Button, Checkbox, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { IconAlertCircle } from '@tabler/icons-react';
import {
  promotionAdminMessage,
  updatePromotion,
  type Promotion,
  type UpdatePromotionRequest,
} from '@/lib/api/promotions';
import { parseDateTimeLocalValue, toDateTimeLocalValue } from '@/lib/utils/billing';

/**
 * Edits the four fields a promotion keeps editable: name, headline and end date here, status from
 * the table's menu. Mount it with a `key` per promotion so it starts from that promotion's values.
 */
export function EditPromotionModal({
  promotion,
  onClose,
  onSaved,
}: {
  promotion: Promotion | null;
  onClose: () => void;
  onSaved: (promotion: Promotion) => void;
}) {
  const [name, setName] = useState(promotion?.name ?? '');
  const [headline, setHeadline] = useState(promotion?.publicHeadline ?? '');
  const [endsAt, setEndsAt] = useState(promotion?.endsAt ? toDateTimeLocalValue(new Date(promotion.endsAt)) : '');
  const [noEnd, setNoEnd] = useState(!promotion?.endsAt);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function save() {
    if (!promotion) return;
    if (!name.trim()) {
      setError('Give the promotion a name.');
      return;
    }
    const body: UpdatePromotionRequest = {};
    if (name.trim() !== promotion.name) body.name = name.trim();
    if (headline.trim() !== (promotion.publicHeadline ?? '')) body.publicHeadline = headline.trim();
    if (noEnd) {
      if (promotion.endsAt) body.clearEndsAt = true;
    } else {
      const iso = parseDateTimeLocalValue(endsAt);
      if (!iso) {
        setError('Pick an end date, or tick "No end date".');
        return;
      }
      if (iso !== promotion.endsAt) body.endsAt = iso;
    }
    if (Object.keys(body).length === 0) {
      onClose();
      return;
    }
    setSaving(true);
    setError(null);
    try {
      onSaved(await updatePromotion(promotion.id, body));
      onClose();
    } catch (e) {
      setError(promotionAdminMessage(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Modal opened={promotion !== null} onClose={onClose} title="Edit promotion">
      <form
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          void save();
        }}
      >
        <Stack gap="sm">
          <TextInput label="Name" required value={name} maxLength={120} onChange={(e) => setName(e.currentTarget.value)} />
          <TextInput
            label="Public headline"
            value={headline}
            maxLength={120}
            onChange={(e) => setHeadline(e.currentTarget.value)}
          />
          <Checkbox label="No end date" checked={noEnd} onChange={(e) => setNoEnd(e.currentTarget.checked)} />
          {!noEnd && (
            <TextInput
              type="datetime-local"
              label="Ends"
              value={endsAt}
              onChange={(e) => setEndsAt(e.currentTarget.value)}
            />
          )}
          <Text size="xs" c="dimmed">The end date is synced to the payment provider before it is saved here.</Text>
          {error && (
            <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" role="alert">
              {error}
            </Alert>
          )}
          <Group justify="flex-end">
            <Button variant="default" onClick={onClose} disabled={saving}>Cancel</Button>
            <Button type="submit" loading={saving}>Save</Button>
          </Group>
        </Stack>
      </form>
    </Modal>
  );
}
