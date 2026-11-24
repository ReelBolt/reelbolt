package models

import (
	"os"
	"path/filepath"
	"reflect"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"

	"gorm.io/gorm/schema"
)

// The OAuth tables are owned by the Inference API's EF migration and written by this service through
// GORM. Column names and types are therefore an interface between two languages and two teams: nothing
// in the compiler checks it, and a rename on one side is silent data loss on the other. These tests are
// what checks it — they compare the tables GORM will actually generate SQL for against the DDL in the
// migration that creates them.

// ---------------------------------------------------------------------------
// StringList: the jsonb array type
// ---------------------------------------------------------------------------

func TestStringListValueIsAJSONArray(t *testing.T) {
	tests := []struct {
		name string
		in   StringList
		want string
	}{
		{name: "two entries", in: StringList{"a", "b"}, want: `["a","b"]`},
		{name: "one entry", in: StringList{"https://x/cb"}, want: `["https://x/cb"]`},
		{name: "nil encodes as an empty array, never NULL", in: nil, want: `[]`},
		{name: "empty encodes as an empty array", in: StringList{}, want: `[]`},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			v, err := tc.in.Value()
			if err != nil {
				t.Fatalf("Value: %v", err)
			}
			got, ok := v.(string)
			if !ok {
				t.Fatalf("Value returned %T, want string", v)
			}
			if got != tc.want {
				t.Fatalf("Value = %s, want %s", got, tc.want)
			}
		})
	}
}

func TestStringListScan(t *testing.T) {
	tests := []struct {
		name string
		src  any
		want []string
	}{
		{name: "jsonb array", src: []byte(`["authorization_code","refresh_token"]`), want: []string{"authorization_code", "refresh_token"}},
		{name: "jsonb array as string", src: `["code"]`, want: []string{"code"}},
		{name: "empty jsonb array", src: []byte(`[]`), want: nil},
		{name: "null", src: nil, want: nil},
		// A column that ends up as text[] still reads, so a migration choice does not break the read
		// path even though the write path depends on the declared jsonb type.
		{name: "postgres array literal", src: []byte(`{code,refresh_token}`), want: []string{"code", "refresh_token"}},
		{name: "postgres quoted array literal", src: []byte(`{"a,b","c"}`), want: []string{"a,b", "c"}},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			var got StringList
			if err := got.Scan(tc.src); err != nil {
				t.Fatalf("Scan(%v): %v", tc.src, err)
			}
			if len(got) != len(tc.want) {
				t.Fatalf("Scan = %#v, want %#v", got, tc.want)
			}
			for i := range got {
				if got[i] != tc.want[i] {
					t.Fatalf("Scan = %#v, want %#v", got, tc.want)
				}
			}
		})
	}
}

func TestStringListScanRefusesNonsense(t *testing.T) {
	var got StringList
	if err := got.Scan(42); err == nil {
		t.Fatal("Scan must refuse a value it cannot interpret")
	}
	if err := got.Scan([]byte("not-json-or-an-array")); err == nil {
		t.Fatal("Scan must refuse text that is neither a JSON array nor an array literal")
	}
}

func TestStringListRoundTrip(t *testing.T) {
	in := StringList{"https://claude.example/callback", "http://127.0.0.1:7654/cb"}
	v, err := in.Value()
	if err != nil {
		t.Fatalf("Value: %v", err)
	}
	var out StringList
	if err := out.Scan([]byte(v.(string))); err != nil {
		t.Fatalf("Scan: %v", err)
	}
	if len(out) != len(in) {
		t.Fatalf("round trip changed length: %#v", out)
	}
	for i := range in {
		if in[i] != out[i] {
			t.Fatalf("round trip changed element %d: %q != %q", i, in[i], out[i])
		}
	}
}

// ---------------------------------------------------------------------------
// The models' own schema
// ---------------------------------------------------------------------------

func parseSchema(t *testing.T, model any) *schema.Schema {
	t.Helper()
	s, err := schema.Parse(model, &sync.Map{}, schema.NamingStrategy{})
	if err != nil {
		t.Fatalf("parse schema for %T: %v", model, err)
	}
	return s
}

func columnNames(s *schema.Schema) []string {
	out := make([]string, 0, len(s.Fields))
	for _, f := range s.Fields {
		out = append(out, f.DBName)
	}
	sort.Strings(out)
	return out
}

func fieldByName(s *schema.Schema, dbName string) *schema.Field {
	for _, f := range s.Fields {
		if f.DBName == dbName {
			return f
		}
	}
	return nil
}

func TestOAuthModelTableNames(t *testing.T) {
	tests := []struct {
		model any
		want  string
	}{
		{&OAuthClient{}, "oauth_clients"},
		{&OAuthAuthorizationCode{}, "oauth_authorization_codes"},
		{&OAuthRefreshToken{}, "oauth_refresh_tokens"},
		{&ApplicationUser{}, "application_users"},
	}

	for _, tc := range tests {
		s := parseSchema(t, tc.model)
		if s.Table != tc.want {
			t.Errorf("%T maps to table %q, want %q", tc.model, s.Table, tc.want)
		}
	}
}

func TestApplicationUserHasTheMcpAdminOptIn(t *testing.T) {
	s := parseSchema(t, &ApplicationUser{})
	f := fieldByName(s, "mcp_admin_tools_enabled")
	if f == nil {
		t.Fatal("application_users.mcp_admin_tools_enabled is missing from the model: the consent flow reads it to decide whether assistant.admin may be granted")
	}
	if f.FieldType != reflect.TypeOf(true) {
		t.Fatalf("mcp_admin_tools_enabled has Go type %v, want bool", f.FieldType)
	}
}

// ---------------------------------------------------------------------------
// The migration cross-check
// ---------------------------------------------------------------------------

// migrationTable is one CREATE TABLE parsed out of the EF migration.
type migrationTable struct {
	name    string
	columns map[string]migrationColumn
}

type migrationColumn struct {
	sqlType   string
	nullable  bool
	maxLength int
}

var (
	reCreateTable = regexp.MustCompile(`CreateTable\(\s*name: "([a-z_]+)"`)
	// The options are captured greedily to the LAST ")" on the line: a column type may itself contain
	// parentheses ("character varying(128)"), so a non-greedy [^)]* would stop inside the type and
	// silently drop every string column from the parsed schema.
	reColumnLine = regexp.MustCompile(`(?m)^\s*([a-z_][a-z0-9_]*) = table\.Column<[^>]+>\((.*)\),?\s*$`)
	reSqlType    = regexp.MustCompile(`type: "([^"]+)"`)
	reNullable   = regexp.MustCompile(`nullable: (true|false)`)
	reMaxLength  = regexp.MustCompile(`maxLength: (\d+)`)
	reAddColumn  = regexp.MustCompile(`AddColumn<[^>]+>\(\s*name: "([a-z_]+)",\s*table: "([a-z_]+)",\s*type: "([^"]+)",\s*nullable: (true|false)`)
)

// loadMcpOAuthMigration finds and reads the EF migration that creates these tables. It skips when the
// inference tree is not present (a checkout that only has api/), and fails when the tree is there but
// the migration is not — that is a real regression, not a missing environment.
func loadMcpOAuthMigration(t *testing.T) string {
	t.Helper()

	dir := filepath.Join("..", "..", "inference", "src", "ReelBolt.Inference.Api", "Migrations")
	if _, err := os.Stat(dir); err != nil {
		t.Skipf("the inference tree is not present at %s; skipping the models/migration cross-check", dir)
	}

	matches, err := filepath.Glob(filepath.Join(dir, "*_AddMcpOAuth.cs"))
	if err != nil {
		t.Fatalf("glob migrations: %v", err)
	}
	if len(matches) == 0 {
		t.Fatalf("no *_AddMcpOAuth.cs in %s: the migration that creates the OAuth tables is missing", dir)
	}

	raw, err := os.ReadFile(matches[0])
	if err != nil {
		t.Fatalf("read %s: %v", matches[0], err)
	}
	return string(raw)
}

func parseMigrationTables(t *testing.T, migration string) map[string]migrationTable {
	t.Helper()

	locs := reCreateTable.FindAllStringSubmatchIndex(migration, -1)
	if len(locs) == 0 {
		t.Fatal("the migration contains no CreateTable block; the parser or the migration changed")
	}

	out := make(map[string]migrationTable, len(locs))
	for i, loc := range locs {
		name := migration[loc[2]:loc[3]]

		end := len(migration)
		if i+1 < len(locs) {
			end = locs[i+1][0]
		}
		block := migration[loc[1]:end]

		table := migrationTable{name: name, columns: map[string]migrationColumn{}}
		for _, col := range reColumnLine.FindAllStringSubmatch(block, -1) {
			colName, options := col[1], col[2]
			c := migrationColumn{}
			if m := reSqlType.FindStringSubmatch(options); m != nil {
				c.sqlType = m[1]
			}
			if m := reNullable.FindStringSubmatch(options); m != nil {
				c.nullable = m[1] == "true"
			}
			if m := reMaxLength.FindStringSubmatch(options); m != nil {
				if n, err := strconv.Atoi(m[1]); err == nil {
					c.maxLength = n
				}
			}
			table.columns[colName] = c
		}
		if len(table.columns) == 0 {
			t.Fatalf("parsed no columns for table %q out of the migration", name)
		}
		out[name] = table
	}
	return out
}

func TestOAuthModelsMatchTheMigrationColumns(t *testing.T) {
	migration := loadMcpOAuthMigration(t)
	tables := parseMigrationTables(t, migration)

	models := []struct {
		model any
		table string
	}{
		{&OAuthClient{}, "oauth_clients"},
		{&OAuthAuthorizationCode{}, "oauth_authorization_codes"},
		{&OAuthRefreshToken{}, "oauth_refresh_tokens"},
	}

	for _, m := range models {
		t.Run(m.table, func(t *testing.T) {
			mig, ok := tables[m.table]
			if !ok {
				t.Fatalf("the migration has no %s table", m.table)
			}
			s := parseSchema(t, m.model)

			// Both directions: a column the model writes but the table lacks is a broken INSERT; a
			// column the table has but the model ignores is a value silently never persisted.
			got := columnNames(s)
			want := make([]string, 0, len(mig.columns))
			for name := range mig.columns {
				want = append(want, name)
			}
			sort.Strings(want)

			if strings.Join(got, ",") != strings.Join(want, ",") {
				t.Fatalf("GORM columns do not match %s in the migration\n GORM: %v\n  DDL: %v", m.table, got, want)
			}

			// Types: the three list columns are jsonb, so StringList must be what maps them.
			for _, col := range []string{"redirect_uris", "grant_types", "response_types"} {
				c, present := mig.columns[col]
				if !present {
					continue
				}
				if c.sqlType != "jsonb" {
					t.Errorf("%s.%s is %q in the migration, expected jsonb", m.table, col, c.sqlType)
				}
				f := fieldByName(s, col)
				if f == nil {
					t.Fatalf("%s.%s missing from the model", m.table, col)
				}
				if f.FieldType.String() != "models.StringList" {
					t.Errorf("%s.%s maps to %v, but a jsonb array must map to models.StringList", m.table, col, f.FieldType)
				}
			}

			// Nullability: every pointer/pointer-like field must be nullable and every plain one must
			// not be, or an INSERT with a zero value silently violates NOT NULL.
			for name, c := range mig.columns {
				f := fieldByName(s, name)
				if f == nil {
					continue
				}
				isPointer := f.FieldType.Kind().String() == "ptr"
				if isPointer && !c.nullable {
					t.Errorf("%s.%s is NOT NULL in the migration but a pointer in the model", m.table, name)
				}
				if !isPointer && c.nullable {
					t.Errorf("%s.%s is nullable in the migration but not a pointer in the model", m.table, name)
				}
			}

			// Length limits the migration enforces, so the handlers know what they must refuse.
			limits := map[string]struct {
				max    int
				column string
			}{
				"client_id":      {128, "client_id"},
				"redirect_uri":   {2048, "redirect_uri"},
				"code_challenge": {128, "code_challenge"},
				"scope":          {200, "scope"},
			}
			for _, want := range limits {
				c, present := mig.columns[want.column]
				if !present {
					continue
				}
				if c.maxLength != want.max {
					t.Errorf("%s.%s maxLength = %d in the migration, expected %d", m.table, want.column, c.maxLength, want.max)
				}
			}
		})
	}
}

func TestOAuthHashesFitTheirColumns(t *testing.T) {
	migration := loadMcpOAuthMigration(t)
	tables := parseMigrationTables(t, migration)

	// code_hash and token_hash are varchar(64): hex SHA-256 exactly. A base64 digest (44 chars) would
	// fit the column but not the intent, and a longer encoding would be truncated or rejected.
	for _, tc := range []struct{ table, column string }{
		{"oauth_authorization_codes", "code_hash"},
		{"oauth_refresh_tokens", "token_hash"},
		{"oauth_clients", "client_secret_hash"},
	} {
		c, ok := tables[tc.table].columns[tc.column]
		if !ok {
			t.Fatalf("%s.%s is missing from the migration", tc.table, tc.column)
		}
		if tc.column != "client_secret_hash" && c.maxLength != 64 {
			t.Errorf("%s.%s maxLength = %d, want 64 (hex SHA-256)", tc.table, tc.column, c.maxLength)
		}
	}
}

func TestOAuthMigrationIsAdditiveAndHasNoForeignKeys(t *testing.T) {
	migration := loadMcpOAuthMigration(t)

	// The contract is explicit: these are plain indexed columns, because both services write both
	// sides. A foreign key would make the Go API's writes depend on the Inference API's tables in an
	// order it cannot control.
	if strings.Contains(migration, "ForeignKey(") || strings.Contains(migration, "AddForeignKey") {
		t.Fatal("the OAuth tables must have no foreign keys (docs/design/mcp-oauth.md §5)")
	}

	// application_users.mcp_admin_tools_enabled: NOT NULL with a default, so every existing row and
	// every new one is off until the portal turns it on.
	m := reAddColumn.FindStringSubmatch(migration)
	if m == nil {
		t.Fatal("the migration does not add mcp_admin_tools_enabled")
	}
	if m[1] != "mcp_admin_tools_enabled" || m[2] != "application_users" {
		t.Fatalf("unexpected AddColumn target: %s on %s", m[1], m[2])
	}
	if m[3] != "boolean" || m[4] != "false" {
		t.Fatalf("mcp_admin_tools_enabled must be boolean NOT NULL (nullable=%s, type=%s)", m[4], m[3])
	}
}

// TestOAuthTimestampsAreTimestamptz guards the column type every expiry comparison depends on. The Go
// side sends UTC; a `timestamp without time zone` column would still accept it and silently drop the
// offset, so the migration must say `timestamp with time zone`.
func TestOAuthTimestampsAreTimestamptz(t *testing.T) {
	migration := loadMcpOAuthMigration(t)
	tables := parseMigrationTables(t, migration)

	for _, tc := range []struct{ table, column string }{
		{"oauth_clients", "created_at"},
		{"oauth_clients", "last_used_at"},
		{"oauth_authorization_codes", "expires_at"},
		{"oauth_authorization_codes", "consumed_at"},
		{"oauth_refresh_tokens", "expires_at"},
		{"oauth_refresh_tokens", "revoked_at"},
	} {
		c, ok := tables[tc.table].columns[tc.column]
		if !ok {
			t.Fatalf("%s.%s missing from the migration", tc.table, tc.column)
		}
		if c.sqlType != "timestamp with time zone" {
			t.Errorf("%s.%s is %q, want timestamp with time zone", tc.table, tc.column, c.sqlType)
		}
	}
}

// TestTimeFieldsArePointersWhenTheColumnIsNullable is the narrow version of the nullability rule for
// the timestamps, where a zero time.Time would be written as year 1 instead of NULL.
func TestTimeFieldsArePointersWhenTheColumnIsNullable(t *testing.T) {
	nullable := map[string]bool{
		"last_used_at": true,
		"consumed_at":  true,
		"revoked_at":   true,
		"created_at":   false,
		"expires_at":   false,
	}

	for _, model := range []any{&OAuthClient{}, &OAuthAuthorizationCode{}, &OAuthRefreshToken{}} {
		s := parseSchema(t, model)
		for name, isNullable := range nullable {
			f := fieldByName(s, name)
			if f == nil || f.FieldType != reflect.TypeOf(time.Time{}) && f.FieldType != reflect.TypeOf(&time.Time{}) {
				continue
			}
			isPtr := f.FieldType.Kind().String() == "ptr"
			if isNullable != isPtr {
				t.Errorf("%s.%s: nullable=%v but pointer=%v", s.Table, name, isNullable, isPtr)
			}
		}
	}
}
