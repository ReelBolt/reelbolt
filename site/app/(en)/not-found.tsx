import { NotFoundPage, metadataFor } from '@/components/pages/NotFoundPage';

export const generateMetadata = () => metadataFor('en');

export default function NotFound() {
  return <NotFoundPage locale="en" />;
}
