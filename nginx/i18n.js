// njs: which language `GET /` opens in, so every deployment (production, staging, self-host) picks the same
// language. Only `location = /` uses it; deep links are never
// redirected, so /it/... can never loop. The picker is i18n/pick-locale.js, vendored here
// byte-identically (node i18n/sync.mjs); the language registry is /etc/nginx/locales.json, vendored
// the same way. See docs/i18n.md.
import pick from './pick-locale.js';
import fs from 'fs';

var LOCALES_FILE = '/etc/nginx/locales.json';

function registryCodes() {
    var registry = JSON.parse(fs.readFileSync(LOCALES_FILE, 'utf8'));
    var codes = [registry.defaultLocale];
    for (var i = 0; i < registry.locales.length; i++) {
        if (registry.locales[i].code !== registry.defaultLocale) codes.push(registry.locales[i].code);
    }
    return codes;
}

// Audience slugs by persona: a copy of PERSONA_LANDING_SLUGS in site/lib/attribution-persona.ts
// (keep in step). `/?for=creator` links exist in the wild and must reach /for/creators.
var PERSONA_SLUGS = {
    creator: 'creators', business: 'small-business', educator: 'educators',
    nonprofit: 'nonprofits', software: 'software-teams', agency: 'agencies'
};

// The persona slug of the `for` query parameter (trimmed, lowercased; `other`/unknown ignored), or ''.
function personaSlug(args) {
    var parts = (args || '').split('&');
    for (var i = 0; i < parts.length; i++) {
        var eq = parts[i].indexOf('=');
        if (eq < 0 || parts[i].slice(0, eq) !== 'for') continue;
        var v;
        try { v = decodeURIComponent(parts[i].slice(eq + 1).replace(/\+/g, ' ')); } catch (e) { return ''; }
        v = v.replace(/^\s+|\s+$/g, '').toLowerCase();
        return Object.prototype.hasOwnProperty.call(PERSONA_SLUGS, v) ? PERSONA_SLUGS[v] : '';
    }
    return '';
}

// '' (stay on the English home page) or '[/<code>][/for/<slug>]' plus the original query string (campaign
// parameters must survive the redirect). A pure function so the tests can drive it.
function targetFor(cookie, acceptLanguage, args, locales) {
    var picked = pick.pickLocale(cookie, acceptLanguage, locales);
    var slug = personaSlug(args);
    if (picked === locales[0] && !slug) return '';
    return (picked === locales[0] ? '' : '/' + picked) + (slug ? '/for/' + slug : '') + (args ? '?' + args : '');
}

// js_set $i18n_home_target i18n.homeTarget;
function homeTarget(r) {
    return targetFor(r.headersIn['Cookie'], r.headersIn['Accept-Language'], r.variables.args, registryCodes());
}

export default { homeTarget: homeTarget, targetFor: targetFor };
