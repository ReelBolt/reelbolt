import { ExamplesPage, metadataFor } from '@/components/pages/ExamplesPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <ExamplesPage locale="en" />;
}
