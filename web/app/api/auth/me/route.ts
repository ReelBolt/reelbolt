import { cookies } from 'next/headers';
import { NextResponse } from 'next/server';
import { sessionTokenConfig, verifySessionToken, isPlatformAdminClaims } from '@/lib/auth/session-token';

// The answer is per-user and decides admin UI: never stored by a browser or a shared cache.
function answer(body: unknown, init?: ResponseInit) {
  const response = NextResponse.json(body, init);
  response.headers.set('Cache-Control', 'private, no-store');
  return response;
}

export async function GET() {
  const cookieStore = await cookies();
  const token = cookieStore.get('reelbolt_token')?.value;

  if (!token) {
    return answer({ user: null }, { status: 401 });
  }

  const config = sessionTokenConfig();
  if (!config) {
    return answer({ user: null }, { status: 500 });
  }

  const claims = verifySessionToken(token, config);
  if (!claims) {
    return answer({ user: null }, { status: 401 });
  }

  return answer({
    user: {
      email: claims.email,
      isAdmin: isPlatformAdminClaims(claims),
      isPlatformAdmin: isPlatformAdminClaims(claims),
      emailVerified: Boolean(claims.emailVerified),
      org: claims.org,
      orgRole: claims.orgRole,
      mustChangePassword: Boolean(claims.mustChangePassword),
    },
  });
}
