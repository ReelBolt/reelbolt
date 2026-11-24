// Currency amounts in the catalog are decimal numbers in major units (29, 18, 12.5).
// Stripe wants integers in the currency's minor unit. This module converts with integer
// math only, and refuses anything that is not exactly representable.

// Zero-decimal currencies per Stripe's currency list.
const ZERO_DECIMAL = new Set([
  'BIF', 'CLP', 'DJF', 'GNF', 'JPY', 'KMF', 'KRW', 'MGA', 'PYG', 'RWF', 'UGX', 'VND', 'VUV', 'XAF', 'XOF', 'XPF',
]);

// Three-decimal currencies need an extra "multiple of 10" rule on Stripe's side. We do not
// need them, so refuse instead of guessing.
const THREE_DECIMAL = new Set(['BHD', 'IQD', 'JOD', 'KWD', 'LYD', 'OMR', 'TND']);

export function currencyExponent(currency) {
  const code = String(currency ?? '').toUpperCase();
  if (!/^[A-Z]{3}$/.test(code)) {
    throw new Error(`invalid currency code: ${JSON.stringify(currency)}`);
  }
  if (THREE_DECIMAL.has(code)) {
    throw new Error(`currency ${code} has three decimals and is not supported by this tool`);
  }
  return ZERO_DECIMAL.has(code) ? 0 : 2;
}

/**
 * Convert a major-unit amount to an integer of minor units, exactly.
 * Accepts a number or a plain decimal string. Rejects zero, negatives, exponent notation and
 * anything with more fraction digits than the currency has (0.001 USD).
 */
export function toMinorUnits(amount, currency) {
  const exponent = currencyExponent(currency);
  const text = typeof amount === 'number' ? numberToPlainString(amount) : String(amount).trim();
  const match = /^(\d+)(?:\.(\d+))?$/.exec(text);
  if (!match) {
    throw new Error(`amount ${JSON.stringify(amount)} is not a plain positive decimal`);
  }
  const whole = match[1];
  const fraction = (match[2] ?? '').replace(/0+$/, '');
  if (fraction.length > exponent) {
    throw new Error(`amount ${text} ${String(currency).toUpperCase()} is not representable in minor units (max ${exponent} decimals)`);
  }
  const padded = fraction.padEnd(exponent, '0');
  const minor = BigInt(whole) * 10n ** BigInt(exponent) + BigInt(padded === '' ? '0' : padded);
  if (minor <= 0n) {
    throw new Error(`amount ${text} must be greater than zero`);
  }
  if (minor > BigInt(Number.MAX_SAFE_INTEGER)) {
    throw new Error(`amount ${text} is too large`);
  }
  return Number(minor);
}

function numberToPlainString(value) {
  if (!Number.isFinite(value)) {
    throw new Error(`amount ${value} is not finite`);
  }
  const text = String(value);
  if (/e/i.test(text)) {
    throw new Error(`amount ${text} is not a plain decimal`);
  }
  return text;
}
