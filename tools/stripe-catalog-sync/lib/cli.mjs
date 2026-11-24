import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { buildDesired, founderEntry, mapPromotions, readJsonFile } from './catalog.mjs';
import { buildReport, collectIds, renderEnv, renderSql, renderTable, summarize } from './outputs.mjs';
import { applyPlan, buildPlan } from './reconcile.mjs';
import { ConfigError, createStripeClient, parseKeyMode, resolveApiBase, STRIPE_API_VERSION } from './stripe-client.mjs';

const FOUNDER_OFFER = fileURLToPath(new URL('../founder-offer.json', import.meta.url));
const DEFAULT_CATALOG = fileURLToPath(new URL('../../../inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json', import.meta.url));

const VALUE_FLAGS = new Set(['catalog', 'promotions', 'out', 'sql-out', 'tax-code', 'tax-behavior', 'lookup-prefix', 'founder-promotion-id', 'site-url']);
export const DEFAULT_MANAGED_PAYMENTS_TAX_CODE = 'txcd_10105002';
const BOOLEAN_FLAGS = new Set(['apply', 'i-mean-live', 'managed-payments', 'no-managed-payments', 'skip-portal', 'include-inactive', 'recreate-coupons', 'founder', 'no-founder', 'help']);

export const USAGE = `Usage: node sync.mjs [options]

Syncs the ReelBolt price catalog to Stripe. Dry run by default: reads Stripe, writes nothing.
The API key is read only from the environment variable STRIPE_API_KEY.

  --apply                  perform the writes (default: dry run)
  --i-mean-live            required together with --apply when the key is a live key
  --catalog <file>         plans.catalog.json (default: the one in this repository)
  --promotions <file>      JSON export of the promotions table (see README)
  --include-inactive       also sync Paused/Archived promotions
  --recreate-coupons       delete and re-create our coupons whose definition drifted
  --tax-behavior <v>       exclusive (default) | inclusive | unspecified; one-way door
  --tax-code <txcd_...>    product tax code (default with Managed Payments: txcd_10105002, AI as a service, business use)
  --managed-payments       Stripe Managed Payments, the default: tax_code on every product, print reminders
  --no-managed-payments    NOT the chosen model: plain Stripe with Stripe Tax; tax_code optional
  --founder / --no-founder  manage the public-alpha founder coupon from founder-offer.json (default: on)
  --founder-promotion-id <uuid>  also emit the UPDATE that points that promotion row at the coupon
  --site-url <https url>   public site (default https://reelbolt.ai): the portal's privacy and terms links point at <site>/legal/...
  --skip-portal            do not manage the Billing Portal configuration
  --lookup-prefix <p>      prefix for lookup keys and object ids (default: rb)
  --out <file>             write a machine-readable report (no secrets)
  --sql-out <file>         write the SQL section to a file
  --help

Exit codes: 0 in sync, 1 error, 2 differences found (dry run) or drift left (apply).
`;

export function parseArgs(argv) {
  const options = {};
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (!arg.startsWith('--')) {
      throw new ConfigError(`unexpected argument "${arg}"`);
    }
    const eq = arg.indexOf('=');
    const name = arg.slice(2, eq === -1 ? undefined : eq);
    if (BOOLEAN_FLAGS.has(name)) {
      if (eq !== -1) {
        throw new ConfigError(`--${name} does not take a value`);
      }
      options[name] = true;
    } else if (VALUE_FLAGS.has(name)) {
      const value = eq !== -1 ? arg.slice(eq + 1) : argv[(i += 1)];
      if (value === undefined || value === '' || (eq === -1 && value.startsWith('--'))) {
        throw new ConfigError(`--${name} needs a value`);
      }
      options[name] = value;
    } else {
      throw new ConfigError(`unknown option --${name}`);
    }
  }
  return options;
}

function validate(raw) {
  const taxBehavior = raw['tax-behavior'] ?? 'exclusive';
  if (!['exclusive', 'inclusive', 'unspecified'].includes(taxBehavior)) {
    throw new ConfigError('--tax-behavior must be exclusive, inclusive or unspecified');
  }
  if (raw['managed-payments'] && raw['no-managed-payments']) {
    throw new ConfigError('--managed-payments and --no-managed-payments cannot be combined');
  }
  const managedPayments = !raw['no-managed-payments'];
  const taxCode = raw['tax-code'] ?? (managedPayments ? DEFAULT_MANAGED_PAYMENTS_TAX_CODE : undefined);
  if (taxCode !== undefined && !/^txcd_\d{8}$/.test(taxCode)) {
    throw new ConfigError('--tax-code must look like txcd_10105002');
  }
  const siteUrl = raw['site-url'] ?? 'https://reelbolt.ai';
  if (!/^https:\/\/[a-z0-9.-]+(:\d+)?\/?$/i.test(siteUrl)) {
    throw new ConfigError('--site-url must be an https origin like https://reelbolt.ai (no path, no credentials)');
  }
  const lookupPrefix = raw['lookup-prefix'] ?? 'rb';
  if (!/^[a-z][a-z0-9]{0,15}$/.test(lookupPrefix)) {
    throw new ConfigError('--lookup-prefix must be lowercase letters and digits, starting with a letter');
  }
  if (raw.founder && raw['no-founder']) {
    throw new ConfigError('--founder and --no-founder cannot be combined');
  }
  const founder = !raw['no-founder'];
  if (raw['founder-promotion-id'] && !founder) {
    throw new ConfigError('--founder-promotion-id needs the founder coupon (drop --no-founder)');
  }
  return {
    founder,
    founderPromotionId: raw['founder-promotion-id'] ?? null,
    apply: Boolean(raw.apply),
    iMeanLive: Boolean(raw['i-mean-live']),
    managedPayments,
    skipPortal: Boolean(raw['skip-portal']),
    siteUrl,
    includeInactive: Boolean(raw['include-inactive']),
    recreateCoupons: Boolean(raw['recreate-coupons']),
    taxBehavior,
    taxCode,
    lookupPrefix,
    catalog: raw.catalog ?? DEFAULT_CATALOG,
    promotions: raw.promotions,
    out: raw.out,
    sqlOut: raw['sql-out'],
  };
}

/**
 * Entry point. Everything environmental is injectable so tests run with a fake fetch and no
 * network: { env, stdout, stderr, fetch, sleep, now }.
 */
export async function main(argv, io) {
  const { env, stdout, stderr } = io;
  try {
    const raw = parseArgs(argv);
    if (raw.help) {
      stdout(USAGE);
      return 0;
    }
    const options = validate(raw);

    const key = env.STRIPE_API_KEY;
    if (!key) {
      throw new ConfigError('STRIPE_API_KEY is not set. Export a restricted (rk_) or secret (sk_) key in the environment; it is never read from a flag or file.');
    }
    const { mode } = parseKeyMode(key);
    if (mode === 'live' && options.apply && !options.iMeanLive) {
      throw new ConfigError('refusing to write to a LIVE Stripe account without --i-mean-live. Run the dry run first, review it, then add --i-mean-live.');
    }
    const baseUrl = resolveApiBase(env.STRIPE_API_BASE);

    const catalog = await readJsonFile(options.catalog, 'catalog');
    const desired = buildDesired(catalog, { lookupPrefix: options.lookupPrefix });
    let promotions = null;
    if (options.promotions) {
      const rows = await readJsonFile(options.promotions, 'promotions export');
      promotions = mapPromotions(rows, desired, {
        includeInactive: options.includeInactive,
        lookupPrefix: options.lookupPrefix,
        now: io.now ?? new Date(),
      });
    }

    if (options.founder) {
      const offer = await readJsonFile(FOUNDER_OFFER, 'founder offer');
      const entry = founderEntry(offer, desired, {
        lookupPrefix: options.lookupPrefix,
        promotionId: options.founderPromotionId,
        now: io.now ?? new Date(),
      });
      promotions = promotions ?? { eligible: [], skipped: [] };
      if (entry.skip) {
        promotions.skipped.push({ promotionId: entry.couponId, reason: entry.reason });
      } else {
        // The founder promotion row may also be in the exported promotions: its coupon is rb_founder_alpha,
        // so it must not get a second coupon of its own.
        if (entry.promotionId) {
          const dup = promotions.eligible.filter((p) => p.promotionId === entry.promotionId);
          if (dup.length > 0) {
            promotions.eligible = promotions.eligible.filter((p) => p.promotionId !== entry.promotionId);
            promotions.skipped.push({ promotionId: entry.promotionId, reason: `this is the founder promotion: its coupon is ${entry.couponId}, no separate coupon is created` });
          }
        }
        promotions.eligible.push(entry);
      }
    }

    const client = createStripeClient({
      key,
      baseUrl,
      fetchImpl: io.fetch ?? globalThis.fetch,
      sleep: io.sleep,
      readOnly: !options.apply,
    });

    stdout(`stripe-catalog-sync: ${mode.toUpperCase()} mode, ${options.apply ? 'APPLY' : 'dry run (no writes)'}, API ${STRIPE_API_VERSION}\n`);
    if (options.managedPayments) {
      stdout(`Managed Payments (default): tax_code ${options.taxCode} is set on every product. Reminder: Managed Payments must be activated in the Stripe Dashboard and the account must pass Stripe\'s eligibility review; this tool does not check either.\n`);
    } else {
      stdout('Managed Payments is OFF (--no-managed-payments): this is not the chosen tax model; you would be the seller and need Stripe Tax registrations.\n');
    }
    stdout('\n');

    const items = await buildPlan({ client, desired, options, promotions });
    if (options.apply) {
      await applyPlan(items, client);
    }

    const summary = summarize(items, { apply: options.apply });
    const ids = collectIds(items, desired, promotions);
    const sql = renderSql(ids, { dryRun: !options.apply });
    const envText = renderEnv(ids);

    stdout(renderTable(items));
    stdout(`\n-- SQL (never executed by this tool)\n${sql}`);
    stdout(`\n# Environment\n${envText}`);
    if (options.taxBehavior !== 'unspecified') {
      stdout(`\nNote: prices carry tax_behavior=${options.taxBehavior}. Once a price has it, it can never be changed.\n`);
    }

    if (options.sqlOut) {
      await writeFile(options.sqlOut, sql, 'utf8');
    }
    if (options.out) {
      const report = buildReport({ items, ids, summary, mode, apply: options.apply, currency: desired.currency });
      await writeFile(options.out, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
    }

    if (summary.errors > 0) {
      stderr(`${summary.errors} item(s) failed or were refused; see the table above.\n`);
      return 1;
    }
    if (summary.differences) {
      stdout(options.apply ? '\nDrift remains (see table).\n' : '\nDry run found differences. Re-run with --apply to write them.\n');
      return 2;
    }
    stdout('\nStripe is in sync with the catalog.\n');
    return 0;
  } catch (error) {
    stderr(`error: ${scrub(error?.message ?? String(error), io.env?.STRIPE_API_KEY)}\n`);
    return 1;
  }
}

function scrub(message, key) {
  return key ? String(message).split(key).join('[redacted]') : String(message);
}
