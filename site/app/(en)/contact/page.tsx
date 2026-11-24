import { ContactPage, metadataFor } from '@/components/pages/ContactPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <ContactPage locale="en" />;
}
