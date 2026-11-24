'use client';

import { Anchor, Checkbox, Text } from '@mantine/core';
import { LEGAL_PRIVACY_URL } from '@/lib/auth/legal';
import type { MarketingWording } from '@/lib/api/marketing';

const PRIVACY_PHRASE = 'Privacy Policy';

/**
 * The registry's wording, verbatim, with its "Privacy Policy" mention made a live link. The text is
 * never rephrased here: the version sent back with a grant names exactly this text.
 */
export function MarketingWordingText({ text }: { text: string }) {
  const at = text.indexOf(PRIVACY_PHRASE);
  if (at < 0) return <>{text}</>;
  return (
    <>
      {text.slice(0, at)}
      <Anchor href={LEGAL_PRIVACY_URL} target="_blank" rel="noreferrer" inherit>
        {PRIVACY_PHRASE}
      </Anchor>
      {text.slice(at + PRIVACY_PHRASE.length)}
    </>
  );
}

/**
 * The optional marketing-email box shown at signup, at /accept-terms and before checkout.
 *
 * Unticked by default, never required, and always separate from any terms checkbox: the form it
 * sits in completes the same whether or not it is ticked (GDPR 7(2), 7(4)). Visually secondary
 * (small, dimmed) so it never reads as part of the action the page is for.
 */
export function MarketingOptInCheckbox({
  wording,
  checked,
  onChange,
  disabled,
}: {
  wording: MarketingWording;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <Checkbox
      size="xs"
      data-testid="marketing-optin-checkbox"
      checked={checked}
      disabled={disabled}
      onChange={(event) => onChange(event.currentTarget.checked)}
      label={
        <Text size="xs" c="dimmed" component="span">
          <MarketingWordingText text={wording.text} />
        </Text>
      }
    />
  );
}
