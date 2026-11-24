// GET /api/recipes/facets. Twin of app/api/recipes/facets/route.ts; see functions/_recipes-proxy.ts.
import { handleRecipes, type RecipesEnv } from '../../_recipes-proxy';

export const onRequest = ({ request, env }: { request: Request; env: RecipesEnv }) => handleRecipes('facets', request, env);
