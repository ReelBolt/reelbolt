import { OpenSourcePage, metadataFor } from '@/components/pages/OpenSourcePage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <OpenSourcePage locale="en" />;
}
