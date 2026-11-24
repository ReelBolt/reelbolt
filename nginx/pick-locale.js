// Which language `/` should open in. ONE implementation, vendored byte-identically (node
// i18n/sync.mjs, CI --check) into nginx/pick-locale.js (njs, what serves reelbolt.ai) and
// site/functions/_pick-locale.js (legacy, unused Pages Function), and tested against pick-locale.vectors.json.
//
// Plain ES5 on purpose: njs has no modern syntax and the same file must load in Node, the Workers
// runtime and njs. No arrow functions, let/const, template strings, Array.prototype.includes.
//
//   pickLocale(cookieHeader, acceptLanguage, locales) -> a code from `locales`
//
// - cookieHeader: the raw `Cookie` request header (or null); the `rb_locale` cookie wins when it
//   names a registered locale.
// - acceptLanguage: the raw `Accept-Language` header (or null): q-values honoured, q=0 refused,
//   region and script stripped ("it-IT" -> "it"), "*" ignored; the first registered match wins.
// - locales: the registry codes, default language first. Anything unregistered is never returned
//   (the allowlist is what keeps a crafted header from ever becoming a redirect target).
// - nothing matches: locales[0], the default.
var LOCALE_COOKIE = 'rb_locale';

function trim(s) {
  return String(s).replace(/^\s+|\s+$/g, '');
}

function indexOf(list, value) {
  for (var i = 0; i < list.length; i++) {
    if (list[i] === value) return i;
  }
  return -1;
}

function cookieValue(cookieHeader, name) {
  if (!cookieHeader) return '';
  var parts = String(cookieHeader).split(';');
  for (var i = 0; i < parts.length; i++) {
    var part = trim(parts[i]);
    var eq = part.indexOf('=');
    if (eq > 0 && part.substring(0, eq) === name) {
      var raw = part.substring(eq + 1);
      try {
        return decodeURIComponent(raw);
      } catch (e) {
        return raw;
      }
    }
  }
  return '';
}

function parseAcceptLanguage(header) {
  var out = [];
  if (!header) return out;
  var items = String(header).split(',');
  for (var i = 0; i < items.length; i++) {
    var fields = items[i].split(';');
    var tag = trim(fields[0]).toLowerCase();
    if (!tag || tag === '*') continue;
    var q = 1;
    for (var j = 1; j < fields.length; j++) {
      var kv = trim(fields[j]);
      if (kv.toLowerCase().indexOf('q=') === 0) {
        var parsed = parseFloat(kv.substring(2));
        q = parsed === parsed ? parsed : 0; // NaN -> refused
      }
    }
    if (q <= 0) continue;
    out.push({ lang: tag.split('-')[0].split('_')[0], q: q, order: i });
  }
  // Highest q first; equal q keeps the header's own order.
  out.sort(function (a, b) {
    return b.q - a.q || a.order - b.order;
  });
  return out;
}

function pickLocale(cookieHeader, acceptLanguage, locales) {
  var fallback = locales[0];
  var chosen = cookieValue(cookieHeader, LOCALE_COOKIE).toLowerCase();
  if (chosen && indexOf(locales, chosen) !== -1) return chosen;
  var wanted = parseAcceptLanguage(acceptLanguage);
  for (var i = 0; i < wanted.length; i++) {
    if (indexOf(locales, wanted[i].lang) !== -1) return wanted[i].lang;
  }
  return fallback;
}

export default { pickLocale: pickLocale, LOCALE_COOKIE: LOCALE_COOKIE };
