package services

import (
	_ "embed"
	"encoding/json"
	"net/http"
	"sort"
	"strconv"
	"strings"
)

// localesJSON is the vendored copy of i18n/locales.json (the registry). It is the ONLY allowlist
// of locale codes; a code must pass it before it is stored or touches any template path.
//
//go:embed locales.json
var localesJSON []byte

// DefaultLocale is the fallback language.
var DefaultLocale = "en"

var supportedLocales = map[string]bool{}

// SupportedLocales lists the registered codes (sorted).
func SupportedLocales() []string {
	out := make([]string, 0, len(supportedLocales))
	for c := range supportedLocales {
		out = append(out, c)
	}
	sort.Strings(out)
	return out
}

func init() {
	var reg struct {
		DefaultLocale string `json:"defaultLocale"`
		Locales       []struct {
			Code string `json:"code"`
		} `json:"locales"`
	}
	if err := json.Unmarshal(localesJSON, &reg); err != nil {
		panic("locales.json: " + err.Error())
	}
	for _, l := range reg.Locales {
		supportedLocales[l.Code] = true
	}
	if reg.DefaultLocale != "" && supportedLocales[reg.DefaultLocale] {
		DefaultLocale = reg.DefaultLocale
	}
}

// IsSupportedLocale reports whether code is exactly a registered code.
func IsSupportedLocale(code string) bool { return supportedLocales[code] }

// NormalizeLocale lowercases, strips the region/script ("it-IT" -> "it") and checks the allowlist.
func NormalizeLocale(s string) (string, bool) {
	s = strings.ToLower(strings.TrimSpace(s))
	if s == "" || len(s) > 35 {
		return "", false
	}
	if i := strings.IndexAny(s, "-_"); i >= 0 {
		s = s[:i]
	}
	if !IsSupportedLocale(s) {
		return "", false
	}
	return s, true
}

const maxLocaleHeader = 512

// LocaleFromAcceptLanguage picks the best supported locale from an Accept-Language value.
func LocaleFromAcceptLanguage(h string) (string, bool) {
	if len(h) > maxLocaleHeader {
		h = h[:maxLocaleHeader]
	}
	best, bestQ := "", -1.0
	for i, part := range strings.Split(h, ",") {
		if i >= 20 {
			break
		}
		fields := strings.Split(part, ";")
		tag := strings.TrimSpace(fields[0])
		q := 1.0
		for _, p := range fields[1:] {
			p = strings.TrimSpace(p)
			if strings.HasPrefix(p, "q=") {
				v, err := strconv.ParseFloat(p[2:], 64)
				if err != nil || v < 0 || v > 1 {
					q = 0
				} else {
					q = v
				}
			}
		}
		if q <= 0 {
			continue
		}
		if loc, ok := NormalizeLocale(tag); ok && q > bestQ {
			best, bestQ = loc, q
		}
	}
	return best, best != ""
}

// LocaleFromRequest resolves the request locale: explicit (already-parsed body field), then the
// rb_locale cookie, then Accept-Language, then the default. Nothing unvalidated is returned.
func LocaleFromRequest(r *http.Request, explicit ...string) string {
	if loc, ok := LocaleSignalFromRequest(r, explicit...); ok {
		return loc
	}
	return DefaultLocale
}

// LocaleSignalFromRequest is LocaleFromRequest without the default: ok is false when the request
// carried no usable language signal at all (no explicit value, no cookie, no supported
// Accept-Language), so a caller can store "unset" instead of inventing a preference.
func LocaleSignalFromRequest(r *http.Request, explicit ...string) (string, bool) {
	for _, e := range explicit {
		if loc, ok := NormalizeLocale(e); ok {
			return loc, true
		}
	}
	if r == nil {
		return "", false
	}
	if c, err := r.Cookie("rb_locale"); err == nil && len(c.Value) <= 35 {
		if loc, ok := NormalizeLocale(c.Value); ok {
			return loc, true
		}
	}
	return LocaleFromAcceptLanguage(r.Header.Get("Accept-Language"))
}

// UserLocale returns the saved locale of a user pointer, or "" when unset/invalid.
func UserLocale(p *string) string {
	if p == nil {
		return ""
	}
	if loc, ok := NormalizeLocale(*p); ok {
		return loc
	}
	return ""
}
