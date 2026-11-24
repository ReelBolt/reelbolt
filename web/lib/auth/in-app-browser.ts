// Is this page open inside another app's built-in browser (an "in-app browser", an embedded
// webview)? Pure: a user-agent string in, a boolean out.
//
// Why it matters: Google refuses OAuth sign-in from embedded webviews (its `disallowed_useragent`
// error page), and the Instagram, Facebook, TikTok, LinkedIn and similar apps open a posted link in
// exactly such a webview. A visitor who arrives from one of those posts and taps "Continue with
// Google" would land on an error page they cannot get past. The signed-out pages therefore lead
// with the email form there and say how to open the page in the real browser instead.
//
// The answer only ever reorders the page and swaps one button for a sentence. It is never an
// authorization decision, and a wrong guess costs one extra step, so the rules lean on well-known
// markers and stay conservative: an ordinary browser (Chrome, Safari, Firefox, Edge, a Custom Tab,
// the Safari view controller) is never called embedded.

/** Apps whose built-in browser is an embedded webview, by the marker they add to the user agent. */
const APP_MARKERS = [
  'FBAN', // Facebook (iOS)
  'FBAV', // Facebook (iOS and Android)
  'FB_IAB', // Facebook in-app browser (Android)
  'FBIOS',
  'Instagram',
  'LinkedInApp',
  'musical_ly', // TikTok
  'BytedanceWebview', // TikTok
  'TikTok',
  'Snapchat',
  'Pinterest',
  'Twitter for', // X ("Twitter for iPhone" / "Twitter for Android")
  'Line/',
  'MicroMessenger', // WeChat
  'KAKAOTALK',
] as const;

/**
 * True when the user agent names a known in-app browser, an Android WebView (`; wv)`), or an
 * iPhone/iPad webview (WebKit with no `Safari/` token: Safari, the Safari view controller and every
 * iOS browser app carry it, a bare `WKWebView` does not). Absent or empty is false.
 */
export function isInAppBrowser(userAgent: string | null | undefined): boolean {
  const ua = (userAgent ?? '').trim();
  if (!ua) return false;
  if (APP_MARKERS.some((marker) => ua.includes(marker))) return true;
  if (/Android/.test(ua) && /;\s*wv\)/.test(ua)) return true;
  if (/\b(iPhone|iPad|iPod)\b/.test(ua) && /AppleWebKit/.test(ua) && !/Safari\//.test(ua)) return true;
  return false;
}

/** Providers that refuse to sign people in from an embedded webview. */
const REFUSES_EMBEDDED = new Set(['google']);

/** Whether `provider` would answer an error page instead of a sign-in from an in-app browser. */
export function providerRefusesEmbedded(provider: string): boolean {
  return REFUSES_EMBEDDED.has(provider);
}
