import { PricingPage, metadataFor } from '@/components/pages/PricingPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <PricingPage locale="en" />;
}
