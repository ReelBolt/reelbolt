import { RecipesPage, metadataFor } from '@/components/pages/RecipesPage';

export const generateMetadata = () => metadataFor('en');

export default function Page() {
  return <RecipesPage locale="en" />;
}
