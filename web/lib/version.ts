/**
 * The one product version the `web` dashboard reports.
 *
 * This is a second source of the literal, not the source of truth: the repository-root `VERSION`
 * file is canonical and `infra/scripts/check-version.sh` fails the build if this file drifts from
 * it. The literal below is the exact string that script greps for, so it is written once, whole,
 * and never assembled from parts.
 */
export const PRODUCT_VERSION = '0.4.0-alpha';
