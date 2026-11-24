package services

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"log"
	"regexp"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

// FirstTouchCookieName is the first-touch cookie the marketing site and web app write.
const FirstTouchCookieName = "rb_ft"

const maxFirstTouchCookieBytes = 512

var (
	utmPattern      = regexp.MustCompile(`^[a-z0-9][a-z0-9._~+-]{0,63}$`)
	hostPattern     = regexp.MustCompile(`^[a-z0-9][a-z0-9.-]{0,99}$`)
	landingPattern  = regexp.MustCompile(`^/[a-z0-9/_~.-]{0,119}$`)
	ownHostSuffixes = []string{"reelbolt.ai"}
)

// How a user_attribution row was learned (user_attribution.capture, docs/design/new-user-funnel.md
// section 5). The cookie is the consented first touch the site wrote; "url" is campaign parameters
// the signup request or the OAuth state carried, with nothing read from or stored on the device.
const (
	AttributionCaptureCookie = "cookie"
	AttributionCaptureURL    = "url"
)

// FirstTouch is the sanitised first touch of a new account: the content of an rb_ft cookie, or the
// campaign parameters a signup carried in its own request (URLAttribution). Every field is
// optional. Capture names which of the two it is and becomes user_attribution.capture.
type FirstTouch struct {
	Source, Medium, Campaign, Content, Term *string
	ReferrerHost, LandingPath               *string
	VisitorID                               *uuid.UUID
	At                                      *time.Time
	Capture                                 string
}

// maxSourceLabelLength is the width of user_attribution.source_label (varchar(64)), and the
// Inference API's AttributionSanitizer.MaxLabelLength: the two services must label alike.
const maxSourceLabelLength = 64

// SourceLabel is utm_source, else the referrer host, else "direct".
//
// A referrer host may be up to 100 characters (referrer_host is that wide), which is wider than a
// label can be. Such a host is not a label: the touch is "direct", exactly as the Inference API
// labels the same visitor (AttributionSanitizer.SourceLabel), and the host itself is still stored
// in referrer_host. Returning it would make the insert fail on the column width and lose the row.
// A utm_source needs no such check: cleanUTM bounds it at 64 characters.
func (f *FirstTouch) SourceLabel() string {
	if f != nil && f.Source != nil {
		return *f.Source
	}
	if f != nil && f.ReferrerHost != nil && len(*f.ReferrerHost) <= maxSourceLabelLength {
		return *f.ReferrerHost
	}
	return "direct"
}

func cleanUTM(s string) *string {
	s = strings.ToLower(strings.TrimSpace(s))
	if !utmPattern.MatchString(s) {
		return nil
	}
	return &s
}

// cleanSource is cleanUTM that also refuses our own host as a campaign source label.
func cleanSource(s string) *string {
	v := cleanUTM(s)
	if v != nil && isOwnHost(*v) {
		return nil
	}
	return v
}

func isOwnHost(h string) bool {
	for _, o := range ownHostSuffixes {
		if h == o || strings.HasSuffix(h, "."+o) {
			return true
		}
	}
	return false
}

func cleanHost(s string) *string {
	s = strings.ToLower(strings.TrimSpace(s))
	if !hostPattern.MatchString(s) || isOwnHost(s) {
		return nil
	}
	return &s
}

func cleanLanding(s string) *string {
	s = strings.ToLower(strings.TrimSpace(s))
	if i := strings.IndexAny(s, "?#"); i >= 0 {
		s = s[:i]
	}
	if strings.HasPrefix(s, "//") || !landingPattern.MatchString(s) {
		return nil
	}
	return &s
}

// ParseFirstTouchCookie strictly decodes an rb_ft value: base64url(JSON), at most 512 bytes, v==1.
// Invalid fields are dropped; it returns nil when nothing valid remains. It never panics.
func ParseFirstTouchCookie(value string) *FirstTouch {
	if value == "" || len(value) > maxFirstTouchCookieBytes {
		return nil
	}
	raw, err := base64.RawURLEncoding.DecodeString(strings.TrimRight(value, "="))
	if err != nil {
		return nil
	}
	var p struct {
		V   int    `json:"v"`
		S   string `json:"s"`
		M   string `json:"m"`
		C   string `json:"c"`
		N   string `json:"n"`
		T   string `json:"t"`
		R   string `json:"r"`
		L   string `json:"l"`
		Vid string `json:"vid"`
		Ts  int64  `json:"ts"`
	}
	if json.Unmarshal(raw, &p) != nil || p.V != 1 {
		return nil
	}
	ft := &FirstTouch{
		Source: cleanSource(p.S), Medium: cleanUTM(p.M), Campaign: cleanUTM(p.C),
		Content: cleanUTM(p.N), Term: cleanUTM(p.T),
		ReferrerHost: cleanHost(p.R), LandingPath: cleanLanding(p.L),
		Capture: AttributionCaptureCookie,
	}
	if id, err := uuid.Parse(strings.TrimSpace(p.Vid)); err == nil && id.Version() == 4 {
		ft.VisitorID = &id
	}
	// A timestamp is only meaningful within a sane window (2020 .. a day from now).
	if p.Ts > 1577836800 && p.Ts < time.Now().Add(24*time.Hour).Unix() {
		t := time.Unix(p.Ts, 0).UTC()
		ft.At = &t
	}
	if ft.Source == nil && ft.Medium == nil && ft.Campaign == nil && ft.Content == nil &&
		ft.Term == nil && ft.ReferrerHost == nil && ft.LandingPath == nil && ft.VisitorID == nil {
		return nil
	}
	return ft
}

// URLAttribution is the campaign parameters an account-creating request carried in itself: the
// `attribution` object of POST /api/v1/auth/signup, or the utm_* query of the OAuth start endpoint
// after its round trip through the OAuth state. The values are raw: nothing here is trusted until
// FirstTouch has run each one through the same sanitisers the rb_ft cookie goes through.
type URLAttribution struct {
	Source, Medium, Campaign, Content, Term string
	LandingPath                             string
}

// FirstTouch sanitises the carried values exactly like the cookie's (same patterns, lowercased and
// trimmed, an invalid value dropped and never rejected) and stamps the touch with now: a request
// carries no time of its own, and the request that creates the account is the touch. It returns nil
// when no value survives, so a signup that carried nothing writes no row rather than a "direct" one
// the server has no evidence for. There is no referrer host and no visitor id on this path.
func (a URLAttribution) FirstTouch(now time.Time) *FirstTouch {
	ft := &FirstTouch{
		Source: cleanSource(a.Source), Medium: cleanUTM(a.Medium), Campaign: cleanUTM(a.Campaign),
		Content: cleanUTM(a.Content), Term: cleanUTM(a.Term),
		LandingPath: cleanLanding(a.LandingPath),
		Capture:     AttributionCaptureURL,
	}
	if ft.Source == nil && ft.Medium == nil && ft.Campaign == nil && ft.Content == nil &&
		ft.Term == nil && ft.LandingPath == nil {
		return nil
	}
	at := now.UTC()
	ft.At = &at
	return ft
}

// Sanitised returns the carried values after sanitising, with every dropped value empty. It is what
// the OAuth start endpoint packs into the state parameter, so only closed tokens ever leave for the
// provider; the callback sanitises again on the way back in.
func (a URLAttribution) Sanitised() URLAttribution {
	str := func(p *string) string {
		if p == nil {
			return ""
		}
		return *p
	}
	return URLAttribution{
		Source: str(cleanSource(a.Source)), Medium: str(cleanUTM(a.Medium)),
		Campaign: str(cleanUTM(a.Campaign)), Content: str(cleanUTM(a.Content)),
		Term: str(cleanUTM(a.Term)), LandingPath: str(cleanLanding(a.LandingPath)),
	}
}

// IsZero reports whether no value is carried at all.
func (a URLAttribution) IsZero() bool { return a == URLAttribution{} }

var onConflictDoNothing = clause.OnConflict{DoNothing: true}

// pgUndefinedColumn is PostgreSQL's SQLSTATE for a column the table does not have.
const pgUndefinedColumn = "42703"

// attributionWriter is the insert behind a package var so a test can force a failure.
//
// user_attribution.capture is added by the Inference API's AddAcquisitionFunnel migration, and the
// Go API can be serving before that migration has run (it never migrates, and it starts first). An
// insert that names a column the table does not have yet fails as a whole, which would lose the
// row. So that one failure, and only that one, is retried without the column: the first touch is
// kept and capture stays NULL, exactly as on every row written before the column existed.
var attributionWriter = func(db *gorm.DB, row *models.UserAttribution) error {
	err := db.Clauses(onConflictDoNothing).Create(row).Error
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && pgErr.Code == pgUndefinedColumn && strings.Contains(pgErr.Message, `"capture"`) {
		return db.Omit("Capture").Clauses(onConflictDoNothing).Create(row).Error
	}
	return err
}

// RecordAttributionBestEffort stores the first touch for a user (first write wins) and never fails
// the flow: a nil handle or nil touch is a no-op, a failed insert is logged without payload.
func RecordAttributionBestEffort(db *gorm.DB, userID uuid.UUID, ft *FirstTouch) {
	if db == nil || ft == nil {
		return
	}
	row := &models.UserAttribution{
		UserID: userID, SourceLabel: ft.SourceLabel(),
		UTMSource: ft.Source, UTMMedium: ft.Medium, UTMCampaign: ft.Campaign,
		UTMContent: ft.Content, UTMTerm: ft.Term,
		ReferrerHost: ft.ReferrerHost, LandingPath: ft.LandingPath,
		VisitorID: ft.VisitorID, FirstTouchAt: ft.At, CreatedAt: time.Now().UTC(),
	}
	if capture := ft.Capture; capture != "" {
		row.Capture = &capture
	}
	if err := attributionWriter(db, row); err != nil {
		log.Printf("WARN user_attribution: not recorded for user %s: %v", userID, err)
	}
}

// ObserveAttributionWrites replaces the insert with fn until the returned restore func runs. Test
// seam for handler tests that must prove the signup paths record attribution.
func ObserveAttributionWrites(fn func(row *models.UserAttribution)) (restore func()) {
	old := attributionWriter
	attributionWriter = func(_ *gorm.DB, row *models.UserAttribution) error { fn(row); return nil }
	return func() { attributionWriter = old }
}
