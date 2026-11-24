// GET /<locale>/recipes/<id> on Cloudflare Pages (the same fallback as functions/recipes/[id].ts, per language).
// A path whose first segment is not a registered language is passed on untouched. See functions/_recipe-shell.ts.
import { recipeShell, type ShellContext } from '../../_recipe-shell';

export const onRequest = (context: ShellContext & { params: { locale?: string | string[] } }) => {
  const { locale } = context.params;
  return recipeShell(context, Array.isArray(locale) ? locale[0] : locale);
};
