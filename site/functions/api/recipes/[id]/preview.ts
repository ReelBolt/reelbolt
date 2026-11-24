// GET /api/recipes/<id>/preview. Twin of the app/api/recipes route handler; see functions/_recipes-proxy.ts.
import { handleRecipes, type RecipesEnv } from '../../../_recipes-proxy';

export const onRequest = ({ request, env, params }: { request: Request; env: RecipesEnv; params: { id?: string | string[] } }) =>
  handleRecipes('preview', request, env, Array.isArray(params.id) ? params.id[0] : params.id);
