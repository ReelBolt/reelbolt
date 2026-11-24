// GET /recipes/<id> on Cloudflare Pages: the prebuilt page, else the client-rendered shell. See functions/_recipe-shell.ts.
import { recipeShell, type ShellContext } from '../_recipe-shell';

export const onRequest = (context: ShellContext) => recipeShell(context, undefined);
