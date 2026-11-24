import { RunnerPage, metadataFor } from '@/components/pages/RunnerPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <RunnerPage locale="en" />;
}
