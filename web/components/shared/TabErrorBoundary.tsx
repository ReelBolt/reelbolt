'use client';

import { Component, type ErrorInfo, type ReactNode } from 'react';
import { Button, Group, Paper, Stack, Text } from '@mantine/core';
import { FeedbackErrorAction } from '@/components/feedback/FeedbackErrorAction';
import { useT } from '@/i18n';


interface TabErrorBoundaryProps {
  children: ReactNode;
}

interface TabErrorBoundaryState {
  failed: boolean;
}

/**
 * Keeps a render error inside one tab body from taking the whole page down: shows a friendly card
 * with a retry button that re-mounts the children.
 */
function TabErrorCard({ onReload }: { onReload: () => void }) {
  const t = useT('errors.state');
  return (
    <Paper withBorder radius="lg" p="lg" role="alert" data-testid="tab-error">
      <Stack gap="sm" align="flex-start">
        <Text fw={600}>{t('tabFailed')}</Text>
        <Group gap="sm">
          <Button variant="light" onClick={onReload}>
            {t('tabReload')}
          </Button>
          {/* T-019: report the failure from the error state itself. */}
          <FeedbackErrorAction />
        </Group>
      </Stack>
    </Paper>
  );
}

export class TabErrorBoundary extends Component<TabErrorBoundaryProps, TabErrorBoundaryState> {
  state: TabErrorBoundaryState = { failed: false };

  static getDerivedStateFromError(): TabErrorBoundaryState {
    return { failed: true };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error('Tab render failed', error, info.componentStack);
  }

  render(): ReactNode {
    if (!this.state.failed) return this.props.children;
    return <TabErrorCard onReload={() => this.setState({ failed: false })} />;
  }
}
