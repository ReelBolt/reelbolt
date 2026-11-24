import { AboutPage, metadataFor } from '@/components/pages/AboutPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <AboutPage locale="en" />;
}
