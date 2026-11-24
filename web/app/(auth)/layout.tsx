import { Box, Center, Container } from '@mantine/core';
import { LanguageSelect } from '@/components/shared/LanguageSelect';
import { AlphaBanner } from '@/components/shell/AlphaBanner';
import { TrademarkNotice } from '@/components/shell/TrademarkNotice';

/**
 * The signed-out frame. It carries the same alpha notice as the dashboard shell, so login, signup,
 * forgot/reset, verify-email and accept-terms all say what build the visitor is looking at — the
 * notice is the one thing this layout adds beyond centring the card, plus the trademark credit
 * line under it. The notice sits after the page content so the heading comes first in the DOM and
 * the form is never pushed down on a phone.
 */
export default function AuthLayout({ children }: { children: React.ReactNode }) {
  return (
    <Center mih="100vh" className="touch-targets">
      <Container size={420} w="100%">
        {children}
        <Box mt="md" maw={200} mx="auto">
          <LanguageSelect withLabel={false} />
        </Box>
        <Box mt="md">
          <AlphaBanner />
        </Box>
        <TrademarkNotice />
      </Container>
    </Center>
  );
}
