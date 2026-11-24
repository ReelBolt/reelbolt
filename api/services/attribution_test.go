package services

import (
	"database/sql"
	"encoding/base64"
	"errors"
	"strings"
	"testing"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
)

func enc(s string) string { return base64.RawURLEncoding.EncodeToString([]byte(s)) }

const goodVid = "3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b"

func TestParseFirstTouchCookie(t *testing.T) {
	cases := []struct {
		name, in  string
		wantNil   bool
		source    string
		label     string
		hasRef    bool
		hasLand   bool
		hasMedium bool
	}{
		{name: "valid", in: enc(`{"v":1,"s":"newsletter","m":"email","r":"news.ycombinator.com","l":"/pricing","vid":"` + goodVid + `","ts":1790000000}`),
			source: "newsletter", label: "newsletter", hasRef: true, hasLand: true, hasMedium: true},
		{name: "uppercase normalised", in: enc(`{"v":1,"s":" NewsLetter ","r":"Example.COM","l":"/Pricing?x=1#y","vid":"` + goodVid + `"}`),
			source: "newsletter", label: "newsletter", hasRef: true, hasLand: true},
		{name: "referrer only gives host label", in: enc(`{"v":1,"r":"example.com"}`), label: "example.com", hasRef: true},
		{name: "visitor only gives direct", in: enc(`{"v":1,"vid":"` + goodVid + `"}`), label: "direct"},
		{name: "wrong version", in: enc(`{"v":2,"s":"x"}`), wantNil: true},
		{name: "missing version", in: enc(`{"s":"x"}`), wantNil: true},
		{name: "bad base64", in: "%%%not-base64%%%", wantNil: true},
		{name: "not json", in: enc("hello"), wantNil: true},
		{name: "empty", in: "", wantNil: true},
		{name: "oversize", in: enc(`{"v":1,"s":"x","pad":"` + strings.Repeat("a", 600) + `"}`), wantNil: true},
		{name: "hostile utm dropped", in: enc(`{"v":1,"s":"<script>alert(1)</script>","m":"email"}`), label: "direct", hasMedium: true},
		{name: "all hostile is nil", in: enc(`{"v":1,"s":"<script>","m":"a b","r":"evil.com/x","l":"//evil.com","vid":"nope"}`), wantNil: true},
		{name: "own host referrer dropped", in: enc(`{"v":1,"r":"www.reelbolt.ai"}`), wantNil: true},
		{name: "own host as source dropped", in: enc(`{"v":1,"s":"app.reelbolt.ai"}`), wantNil: true},
		{name: "own host referrer dropped but utm kept", in: enc(`{"v":1,"s":"ads","r":"reelbolt.ai"}`), source: "ads", label: "ads"},
		{name: "referrer with port dropped", in: enc(`{"v":1,"r":"example.com:8080"}`), wantNil: true},
		{name: "landing without slash dropped", in: enc(`{"v":1,"s":"a","l":"pricing"}`), source: "a", label: "a"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			ft := ParseFirstTouchCookie(c.in)
			if c.wantNil {
				if ft != nil {
					t.Fatalf("want nil, got %+v", ft)
				}
				return
			}
			if ft == nil {
				t.Fatal("want a first touch, got nil")
			}
			if got := ft.SourceLabel(); got != c.label {
				t.Fatalf("label = %q, want %q", got, c.label)
			}
			if (ft.Source != nil) != (c.source != "") || (ft.Source != nil && *ft.Source != c.source) {
				t.Fatalf("source = %v, want %q", ft.Source, c.source)
			}
			if (ft.ReferrerHost != nil) != c.hasRef || (ft.LandingPath != nil) != c.hasLand || (ft.Medium != nil) != c.hasMedium {
				t.Fatalf("fields = %+v", ft)
			}
			if ft.LandingPath != nil && *ft.LandingPath != "/pricing" {
				t.Fatalf("landing = %q", *ft.LandingPath)
			}
		})
	}
}

func deadDB(t *testing.T, dryRun bool) *gorm.DB {
	t.Helper()
	sqlDB, err := sql.Open("pgx", "postgres://u:p@127.0.0.1:1/x?sslmode=disable&connect_timeout=1")
	if err != nil {
		t.Fatal(err)
	}
	db, err := gorm.Open(postgres.New(postgres.Config{Conn: sqlDB}), &gorm.Config{DryRun: dryRun, SkipDefaultTransaction: true, DisableAutomaticPing: true})
	if err != nil {
		t.Fatal(err)
	}
	return db
}

func TestRecordAttribution_IsInsertOnConflictDoNothing(t *testing.T) {
	db := deadDB(t, true)
	var sqlText string
	old := attributionWriter
	t.Cleanup(func() { attributionWriter = old })
	attributionWriter = func(d *gorm.DB, row *models.UserAttribution) error {
		tx := old2(d, row)
		sqlText = tx
		return nil
	}
	ft := ParseFirstTouchCookie(enc(`{"v":1,"s":"ads"}`))
	RecordAttributionBestEffort(db, uuid.New(), ft)
	if !strings.Contains(sqlText, `INSERT INTO "user_attribution"`) || !strings.Contains(sqlText, "ON CONFLICT") || !strings.Contains(sqlText, "DO NOTHING") {
		t.Fatalf("sql = %q", sqlText)
	}
}

// old2 builds the production statement without executing it.
func old2(d *gorm.DB, row *models.UserAttribution) string {
	stmt := d.Session(&gorm.Session{DryRun: true}).Clauses(onConflictDoNothing).Create(row).Statement
	return stmt.SQL.String()
}

func TestRecordAttribution_NeverFailsTheFlow(t *testing.T) {
	ft := ParseFirstTouchCookie(enc(`{"v":1,"s":"ads"}`))
	RecordAttributionBestEffort(nil, uuid.New(), ft)
	RecordAttributionBestEffort(deadDB(t, false), uuid.New(), nil)
	old := attributionWriter
	t.Cleanup(func() { attributionWriter = old })
	attributionWriter = func(*gorm.DB, *models.UserAttribution) error { return errors.New("boom") }
	RecordAttributionBestEffort(deadDB(t, false), uuid.New(), ft) // must not panic
}

// A referrer host may be up to 100 characters, a source label at most 64 (the column width, and the
// Inference API's MaxLabelLength). A wider host is not a label: the touch is "direct", as the
// Inference API labels the same visitor, and the host stays in referrer_host. Before this rule the
// label was the wide host, the insert failed on varchar(64) and the whole row was lost.
func TestSourceLabel_ReferrerHostWiderThanALabelIsDirect(t *testing.T) {
	host := func(n int) string { return strings.Repeat("a", n-len(".example.com")) + ".example.com" }
	cases := []struct {
		name  string
		host  string
		label string
	}{
		{name: "64 characters is still a label", host: host(64), label: host(64)},
		{name: "65 characters is direct", host: host(65), label: "direct"},
		{name: "73 characters is direct", host: host(73), label: "direct"},
		{name: "100 characters (the widest host) is direct", host: host(100), label: "direct"},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			ft := ParseFirstTouchCookie(enc(`{"v":1,"r":"` + c.host + `","l":"/pricing"}`))
			if ft == nil || ft.ReferrerHost == nil || *ft.ReferrerHost != c.host {
				t.Fatalf("the referrer host must be kept whatever its label: %+v", ft)
			}
			if got := ft.SourceLabel(); got != c.label {
				t.Fatalf("label = %q, want %q", got, c.label)
			}

			// What reaches the table: a label that fits the column, and the host beside it.
			var row *models.UserAttribution
			t.Cleanup(ObserveAttributionWrites(func(r *models.UserAttribution) { row = r }))
			RecordAttributionBestEffort(deadDB(t, true), uuid.New(), ft)
			if row == nil {
				t.Fatal("no row written")
			}
			if row.SourceLabel != c.label || len(row.SourceLabel) > maxSourceLabelLength {
				t.Fatalf("source_label = %q (%d characters)", row.SourceLabel, len(row.SourceLabel))
			}
			if row.ReferrerHost == nil || *row.ReferrerHost != c.host {
				t.Fatalf("referrer_host = %v, want %q", row.ReferrerHost, c.host)
			}
		})
	}

	// A utm_source always wins, and is bounded by its own pattern.
	ft := ParseFirstTouchCookie(enc(`{"v":1,"s":"newsletter","r":"` + host(90) + `"}`))
	if ft == nil || ft.SourceLabel() != "newsletter" {
		t.Fatalf("utm_source must be the label: %+v", ft)
	}
	if (*FirstTouch)(nil).SourceLabel() != "direct" {
		t.Fatal("a nil touch is direct")
	}
}
