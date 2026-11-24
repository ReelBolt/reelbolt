// Rate-limit key for the self-hosted contact route (app/api/contact/route.ts).
//
// nginx sets X-Real-IP from $remote_addr and overwrites any value the client sent, so it is the
// address to trust. X-Forwarded-For is only a fallback, and only its rightmost entry: nginx appends
// the peer address there, while every entry to its left is whatever the client chose to send.
export const UNKNOWN_CLIENT_KEY = 'unknown';

export function clientIpKey(request: Request): string {
  const realIp = request.headers.get('x-real-ip')?.trim();
  if (realIp) return realIp;

  const forwardedFor = request.headers.get('x-forwarded-for');
  if (forwardedFor) {
    const entries = forwardedFor.split(',').map((entry) => entry.trim()).filter(Boolean);
    const last = entries[entries.length - 1];
    if (last) return last;
  }
  return UNKNOWN_CLIENT_KEY;
}
