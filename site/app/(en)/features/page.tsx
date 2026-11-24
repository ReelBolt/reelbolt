import { FeaturesPage, metadataFor } from '@/components/pages/FeaturesPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <FeaturesPage locale="en" />;
}
