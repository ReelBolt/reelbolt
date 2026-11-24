'use client';

import { Button, Card, Center, Container, Stack, Text, Title, ThemeIcon } from '@mantine/core';
import { IconWifiOff } from '@tabler/icons-react';
import { useT } from '@/i18n';

// Served by the service worker (public/sw.js) as the fallback for a failed page navigation while
// offline. ReelBolt is a live agent platform — execution progress streams over SSE, file uploads
// go straight to storage — so there's no meaningful offline mode to offer here. This page says
// that plainly instead of implying the dashboard works without a connection.
export default function OfflinePage() {
  const t = useT('misc.offline');
  return (
    <Center mih="100vh">
      <Container size={420} w="100%">
        <Card radius="md" p="xl" withBorder>
          <Stack gap="md" align="center">
            <ThemeIcon size={64} variant="light" color="gray" radius="xl">
              <IconWifiOff size={32} />
            </ThemeIcon>
            <Title order={2} ta="center">
              {t('title')}
            </Title>
            <Text size="sm" c="dimmed" ta="center">
              {t('body')}
            </Text>
            <Button onClick={() => location.reload()}>{t('retry')}</Button>
          </Stack>
        </Card>
      </Container>
    </Center>
  );
}
