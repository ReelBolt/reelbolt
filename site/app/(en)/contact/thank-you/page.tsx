import { ContactThankYouPage, metadataFor } from '@/components/pages/ContactThankYouPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <ContactThankYouPage locale="en" />;
}
