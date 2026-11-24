// GET /api/recipes: list and search. Twin of app/api/recipes/route.ts; see functions/_recipes-proxy.ts.
import { handleRecipes, type RecipesEnv } from '../../_recipes-proxy';

export const onRequest = ({ request, env }: { request: Request; env: RecipesEnv }) => handleRecipes('list', request, env);
