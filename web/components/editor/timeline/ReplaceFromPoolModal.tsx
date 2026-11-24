'use client';

import React, { useState } from 'react';
import { Button, Group, Modal, Select, Text } from '@mantine/core';
import type { AssetRef } from '@/lib/editing/types';
import { useT } from '@/i18n';

export interface ReplaceOption {
  value: string;
  label: string;
  assetRef: AssetRef;
}

export interface ReplaceFromPoolModalProps {
  opened: boolean;
  options: readonly ReplaceOption[];
  onClose: () => void;
  /** Returns a plain sentence when the replacement was refused (the modal stays open and shows it). */
  onReplace: (option: ReplaceOption) => string | null;
}

/** R03.2 "Replace from pool": swap the selected clip's source for another from the media pool, keeping its place and length. */
export function ReplaceFromPoolModal({ opened, options, onClose, onReplace }: ReplaceFromPoolModalProps) {
  const t = useT('editor-timeline.replace');
  const [value, setValue] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const close = () => {
    setValue(null);
    setProblem(null);
    onClose();
  };
  return (
    <Modal opened={opened} onClose={close} title={t('title')} centered data-testid="replace-modal">
      <Text size="sm" mb="sm">
        {t('intro')}
      </Text>
      <Select
        label={t('source')}
        data={options.map((o) => ({ value: o.value, label: o.label }))}
        value={value}
        onChange={(v) => {
          setValue(v);
          setProblem(null);
        }}
        searchable
        nothingFoundMessage={t('noSources')}
        data-testid="replace-source"
        comboboxProps={{ withinPortal: true }}
      />
      {problem && (
        <Text size="sm" c="red" mt="xs" role="alert" data-testid="replace-problem">
          {problem}
        </Text>
      )}
      <Group justify="flex-end" mt="md">
        <Button variant="default" onClick={close}>
          {t('cancel')}
        </Button>
        <Button
          disabled={!value}
          onClick={() => {
            const option = options.find((o) => o.value === value);
            if (!option) return;
            const refused = onReplace(option);
            if (refused) setProblem(refused);
            else close();
          }}
          data-testid="replace-confirm"
        >
          {t('confirm')}
        </Button>
      </Group>
    </Modal>
  );
}
