'use client';

import React, { useState } from 'react';
import { Button, Select, TextInput } from '@mantine/core';
import { IconWand } from '@tabler/icons-react';
import { PROPOSAL_AGENTS, type ProposalAgent } from '@/lib/editing/sync/api';
import classes from './simple.module.css';
import { useT } from '@/i18n';

export interface AskBoxProps {
  readOnly: boolean;
  /** Opens the suggestions panel with this helper and these words filled in. Nothing is asked, or charged, until the user asks there. */
  onAsk: (agent: ProposalAgent, brief: string) => void;
}

/**
 * "Ask for a change": one line under the viewer. The words go to one of the editor's five helpers, picked here.
 * This box only opens the suggestions panel; the price is shown there before anything is sent.
 */
export function AskBox({ readOnly, onAsk }: AskBoxProps) {
  const t = useT('editor-simple.ask');
  const [agent, setAgent] = useState<ProposalAgent>('recut');
  const [brief, setBrief] = useState('');
  const submit = () => {
    onAsk(agent, brief.trim());
    setBrief('');
  };
  return (
    <form
      className={classes.ask}
      data-testid="simple-ask"
      onSubmit={(event) => {
        event.preventDefault();
        submit();
      }}
    >
      <IconWand size={16} aria-hidden style={{ flexShrink: 0 }} />
      <Select
        size="xs"
        w={150}
        aria-label={t('helper')}
        data={PROPOSAL_AGENTS.map((a) => ({ value: a, label: t(`helpers.${a}`) }))}
        value={agent}
        allowDeselect={false}
        disabled={readOnly}
        onChange={(value) => value && setAgent(value as ProposalAgent)}
        comboboxProps={{ withinPortal: true }}
        data-testid="simple-ask-helper"
        style={{ flexShrink: 0 }}
      />
      <TextInput
        size="xs"
        style={{ flex: 1, minWidth: 0 }}
        aria-label={t('label')}
        placeholder={t(`placeholders.${agent}`)}
        value={brief}
        maxLength={1000}
        disabled={readOnly}
        onChange={(event) => setBrief(event.currentTarget.value)}
        // The editor's shortcuts are single keys: typing here must never run one.
        onKeyDown={(event) => event.stopPropagation()}
        data-testid="simple-ask-text"
      />
      <Button type="submit" size="xs" fz="xs" variant="light" disabled={readOnly} data-testid="simple-ask-go" style={{ flexShrink: 0 }}>
        {t('go')}
      </Button>
    </form>
  );
}
