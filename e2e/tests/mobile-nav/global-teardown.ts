import { removeAuthStates } from './logs-dir';

export default function globalTeardown(): void {
  const dir = process.env.MOBILE_NAV_LOGS;
  if (dir) removeAuthStates(dir);
}
