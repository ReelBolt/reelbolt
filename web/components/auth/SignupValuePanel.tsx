import { Box, List, ListItem, Paper, SimpleGrid, Stack, Text, ThemeIcon, Title } from '@mantine/core';
import { IconCheck } from '@tabler/icons-react';
import type { Persona } from '@/lib/attribution/persona';
import { VALUE_COPY, valueAudience } from '@/lib/auth/signup-value';
import { useT } from '@/i18n';

const TITLE_ID = 'signup-value-title';

/**
 * The value panel beside the signup form: what the visitor gets, said for the audience their link
 * named, then what happens after they press the button.
 *
 * Everything on it has to be true for every visitor on every deployment, because nothing about the
 * visitor or the plan is known before sign-in:
 *
 *  - the before/after is qualitative. No number appears: a time saving would be invented;
 *  - there is no testimonial, customer name, logo, count or rating. None exists to show;
 *  - "what happens next" lists only steps this deployment has: the welcome questions are named
 *    only while the welcome flow is switched on (`welcomeEnabled`), and the email step says it
 *    applies to an email sign-up (a provider sign-up sends no email);
 *  - the trust facts are the two that hold whatever the billing settings are. The first-video
 *    credit grant is deliberately NOT quoted here: whether it applies (billing on, the grant
 *    switched on, the address eligible) is only known after sign-in, where the dashboard says it
 *    with the real figure. The public-alpha notice is the auth layout's, right below.
 *
 * No hooks beyond the translator and no handlers, so it renders inside the server-rendered page.
 * For the same reason the list rows are the named `ListItem` export, never `List.Item`: on the
 * server `List` is a client reference, and a property read off it is undefined (a 500 on the page).
 */
export function SignupValuePanel({ persona, welcomeEnabled = true }: { persona?: Persona; welcomeEnabled?: boolean }) {
  // The root translator: the per-audience keys in lib/auth/signup-value.ts are absolute.
  const t = useT();
  const audience = valueAudience(persona);
  const copy = VALUE_COPY[audience];

  return (
    <Box component="aside" aria-labelledby={TITLE_ID} data-testid="signup-value-panel" data-audience={audience}>
      <Stack gap="lg">
        <Title order={2} id={TITLE_ID}>
          {t(copy.outcome)}
        </Title>

        <SimpleGrid cols={{ base: 1, xs: 2 }} spacing="sm">
          <Paper withBorder radius="md" p="md" data-testid="signup-value-before">
            <Text size="xs" fw={700} tt="uppercase" c="dimmed">
              {t('auth.value.beforeLabel')}
            </Text>
            <Text size="sm" mt={4}>
              {t(copy.before)}
            </Text>
          </Paper>
          <Paper withBorder radius="md" p="md" bg="var(--mantine-primary-color-light)" data-testid="signup-value-after">
            <Text size="xs" fw={700} tt="uppercase" c="var(--mantine-primary-color-light-color)">
              {t('auth.value.afterLabel')}
            </Text>
            <Text size="sm" mt={4}>
              {t(copy.after)}
            </Text>
          </Paper>
        </SimpleGrid>

        <div>
          <Title order={3} size="h5" mb="xs">
            {t('auth.value.nextTitle')}
          </Title>
          <List type="ordered" spacing="xs" size="sm" data-testid="signup-value-next">
            <ListItem>
              <Text span fw={600} inherit>
                {t('auth.value.next1Title')}
              </Text>{' '}
              {t('auth.value.next1Body')}
            </ListItem>
            {welcomeEnabled && (
              <ListItem>
                <Text span fw={600} inherit>
                  {t('auth.value.next2Title')}
                </Text>{' '}
                {t('auth.value.next2Body')}
              </ListItem>
            )}
            <ListItem>
              <Text span fw={600} inherit>
                {t('auth.value.next3Title')}
              </Text>{' '}
              {t('auth.value.next3Body')}
            </ListItem>
          </List>
        </div>

        <List
          spacing="xs"
          size="sm"
          center={false}
          data-testid="signup-value-trust"
          icon={
            <ThemeIcon size={20} radius="xl" variant="light" aria-hidden>
              <IconCheck size={14} />
            </ThemeIcon>
          }
        >
          <ListItem>{t('auth.value.trustNoCard')}</ListItem>
          <ListItem>{t('auth.value.trustReviewed')}</ListItem>
        </List>
      </Stack>
    </Box>
  );
}
