import { describe, expect, it } from 'vitest';
import { isInAppBrowser, providerRefusesEmbedded } from './in-app-browser';

// Real user-agent shapes. The apps below open a posted link in an embedded webview, where Google
// answers its "disallowed_useragent" page instead of a sign-in.
const IN_APP: Record<string, string> = {
  'Instagram (iOS)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 355.0.0.31.105 (iPhone15,2; iOS 18_1; en_US; en; scale=3.00; 1179x2556; 654321)',
  'Instagram (Android)':
    'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/UQ1A.240205.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/130.0.6723.58 Mobile Safari/537.36 Instagram 355.0.0.31.105 Android (34/14; 420dpi; 1080x2400; Google/google; Pixel 8; shiba; shiba; en_US; 654321)',
  'Facebook (iOS)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [FBAN/FBIOS;FBAV/487.0.0.44.110;FBBV/654321;FBDV/iPhone15,2;FBMD/iPhone;FBSN/iOS;FBSV/18.1;FBSS/3;FBID/phone;FBLC/en_US;FBOP/5]',
  'Facebook (Android)':
    'Mozilla/5.0 (Linux; Android 14; SM-S918B Build/UP1A.231005.007; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/130.0.6723.58 Mobile Safari/537.36 [FB_IAB/FB4A;FBAV/487.0.0.44.110;]',
  'TikTok (iOS)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 musical_ly_36.5.0 JsSdk/2.0 NetType/WIFI Channel/App Store ByteLocale/en Region/US',
  'TikTok (Android)':
    'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/UQ1A.240205.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/130.0.6723.58 Mobile Safari/537.36 trill_360505 JsSdk/1.0 NetType/WIFI Channel/googleplay AppName/trill app_version/36.5.5 ByteLocale/en BytedanceWebview/d8a21c6',
  'LinkedIn (iOS)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 [LinkedInApp]/9.30.1234',
  'X (iOS)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Twitter for iPhone/10.70',
  'a bare Android WebView':
    'Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/UQ1A.240205.002; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/130.0.6723.58 Mobile Safari/537.36',
  'a bare iOS WKWebView':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148',
};

// Browsers where Google sign-in works: the real ones, and the tabs an app can open in them.
const REAL_BROWSERS: Record<string, string> = {
  'Safari (iPhone)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1',
  'Chrome (iPhone)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.6723.90 Mobile/15E148 Safari/604.1',
  'Firefox (iPhone)':
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/132.0 Mobile/15E148 Safari/605.1.15',
  'Chrome (Android), also a Custom Tab':
    'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.6723.58 Mobile Safari/537.36',
  'Samsung Internet':
    'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/26.0 Chrome/122.0.0.0 Mobile Safari/537.36',
  'Chrome (desktop)':
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36',
  'Safari (Mac)':
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15',
  'Firefox (desktop)': 'Mozilla/5.0 (X11; Linux x86_64; rv:132.0) Gecko/20100101 Firefox/132.0',
  'Edge (desktop)':
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0',
};

describe('isInAppBrowser', () => {
  it.each(Object.entries(IN_APP))('recognises %s as an in-app browser', (_name, userAgent) => {
    expect(isInAppBrowser(userAgent)).toBe(true);
  });

  it.each(Object.entries(REAL_BROWSERS))('never calls %s embedded', (_name, userAgent) => {
    expect(isInAppBrowser(userAgent)).toBe(false);
  });

  it('answers no without a user agent: nothing is reordered on a guess', () => {
    expect(isInAppBrowser(null)).toBe(false);
    expect(isInAppBrowser(undefined)).toBe(false);
    expect(isInAppBrowser('')).toBe(false);
    expect(isInAppBrowser('   ')).toBe(false);
  });
});

describe('providerRefusesEmbedded', () => {
  it('names Google, the provider that refuses embedded webviews, and no other', () => {
    expect(providerRefusesEmbedded('google')).toBe(true);
    expect(providerRefusesEmbedded('github')).toBe(false);
    expect(providerRefusesEmbedded('gitlab')).toBe(false);
  });
});
