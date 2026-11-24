import Link from 'next/link';
import { Card, Center, Container, Stack, Text, Title } from '@mantine/core';
import { BrandMark } from '@/components/shell/BrandMark';
import { useT } from '@/i18n';

/**
 * The 404 for any path the app does not have. It uses the same hairline card and brand mark as the
 * sign-in screens so a dead link still reads as ReelBolt. A Server Component, so it uses
 * `next/link` directly rather than a Mantine `component={Link}` prop.
 */
export default function NotFound() {
  const t = useT('misc.notFound');
  return (
    <Center mih="100vh" p="md">
      <Container size={420} w="100%">
        <Card radius="md" p="xl" withBorder>
          <Stack gap="md" align="center">
            <BrandMark size={56} />
            <Title order={2} ta="center">
              {t('title')}
            </Title>
            <Text size="sm" c="dimmed" ta="center">
              {t('body')}
            </Text>
            <Link href="/dashboard" style={{ color: 'var(--mantine-color-anchor)' }}>{t('back')}</Link>
          </Stack>
        </Card>
      </Container>
    </Center>
  );
}
