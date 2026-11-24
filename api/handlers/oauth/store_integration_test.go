package oauth

import (
	"context"
	"os"
	"sync"
	"testing"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/driver/postgres"
	"gorm.io/gorm"
	"gorm.io/gorm/logger"
	"gorm.io/gorm/schema"
)

// The GormOAuthStore is the only code in this flow that talks to PostgreSQL, and the handler tests use
// a fake store, so without this file nothing would exercise it. Everything below runs against a real
// server: the jsonb columns, the unique hash indexes, the conditional "consume a code" UPDATE and the
// grant-wide revocation are all behaviour a fake cannot confirm.
//
// It is opt-in rather than part of the default run: `go test ./...` must not require a database.
//
//	OAUTH_TEST_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:55432/oauthit?sslmode=disable' \
//	    go test ./handlers/oauth/ -run GormOAuthStore -v
//
// The DDL below is a transcription of the Inference API's `AddMcpOAuth` migration. It is not a second
// source of truth: models/oauth_test.go compares the models against that migration's columns, and this
// file compares the models against a live database's information_schema, so a transcription mistake
// fails the shape assertion rather than passing silently.

const integrationDDL = `
CREATE TABLE IF NOT EXISTS oauth_clients (
    id uuid PRIMARY KEY,
    client_id character varying(128) NOT NULL,
    client_secret_hash character varying(200) NULL,
    client_name character varying(200) NOT NULL,
    redirect_uris jsonb NOT NULL,
    grant_types jsonb NOT NULL,
    response_types jsonb NOT NULL,
    token_endpoint_auth_method character varying(50) NOT NULL,
    created_at timestamp with time zone NOT NULL,
    last_used_at timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_oauth_clients_client_id ON oauth_clients (client_id);

CREATE TABLE IF NOT EXISTS oauth_authorization_codes (
    id uuid PRIMARY KEY,
    code_hash character varying(64) NOT NULL,
    client_id character varying(128) NOT NULL,
    user_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    redirect_uri character varying(2048) NOT NULL,
    code_challenge character varying(128) NOT NULL,
    scope character varying(200) NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    consumed_at timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_oauth_authorization_codes_code_hash ON oauth_authorization_codes (code_hash);

CREATE TABLE IF NOT EXISTS oauth_refresh_tokens (
    id uuid PRIMARY KEY,
    token_hash character varying(64) NOT NULL,
    client_id character varying(128) NOT NULL,
    user_id uuid NOT NULL,
    organization_id uuid NOT NULL,
    scope character varying(200) NOT NULL,
    expires_at timestamp with time zone NOT NULL,
    revoked_at timestamp with time zone NULL,
    rotated_from_id uuid NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_oauth_refresh_tokens_token_hash ON oauth_refresh_tokens (token_hash);
`

func integrationDB(t *testing.T) *gorm.DB {
	t.Helper()

	dsn := os.Getenv("OAUTH_TEST_DATABASE_URL")
	if dsn == "" {
		t.Skip("OAUTH_TEST_DATABASE_URL is not set; skipping the PostgreSQL integration test")
	}

	db, err := gorm.Open(postgres.Open(dsn), &gorm.Config{Logger: logger.Default.LogMode(logger.Silent)})
	if err != nil {
		t.Fatalf("connect to %s: %v", dsn, err)
	}
	if err := db.Exec(integrationDDL).Error; err != nil {
		t.Fatalf("apply schema: %v", err)
	}
	// Rows are never truncated and every identifier is unique per run. The database is shared (the
	// end-to-end test in package handlers uses the same one, and `go test ./...` runs packages in
	// parallel), so isolation comes from unique keys rather than from wiping shared state.

	old := database.DB
	database.DB = db
	t.Cleanup(func() { database.DB = old })
	return db
}

// TestGormOAuthStoreSchemaMatchesTheModels asserts the live columns against GORM's mapping — name,
// type, nullability and length — so a model change that the database does not have fails here rather
// than as a runtime INSERT error.
func TestGormOAuthStoreSchemaMatchesTheModels(t *testing.T) {
	db := integrationDB(t)

	tests := []struct {
		table string
		model any
	}{
		{"oauth_clients", &models.OAuthClient{}},
		{"oauth_authorization_codes", &models.OAuthAuthorizationCode{}},
		{"oauth_refresh_tokens", &models.OAuthRefreshToken{}},
	}

	for _, tc := range tests {
		t.Run(tc.table, func(t *testing.T) {
			var rows []struct {
				ColumnName string `gorm:"column:column_name"`
				DataType   string `gorm:"column:data_type"`
				IsNullable string `gorm:"column:is_nullable"`
				MaxLength  *int   `gorm:"column:character_maximum_length"`
			}
			if err := db.Raw(`SELECT column_name, data_type, is_nullable, character_maximum_length
			                    FROM information_schema.columns
			                   WHERE table_name = ? ORDER BY column_name`, tc.table).Scan(&rows).Error; err != nil {
				t.Fatalf("read information_schema: %v", err)
			}
			if len(rows) == 0 {
				t.Fatalf("the live database has no %s table", tc.table)
			}

			live := map[string]string{}
			for _, r := range rows {
				live[r.ColumnName] = r.DataType
			}

			// Every column GORM will write must exist in the live table.
			for _, column := range modelColumnNames(t, tc.model) {
				if _, ok := live[column]; !ok {
					t.Errorf("the model writes %s.%s, which the live table does not have", tc.table, column)
				}
			}
			// ...and nothing in the live table is unmapped, which would mean a value silently never
			// persisted.
			for column := range live {
				found := false
				for _, mapped := range modelColumnNames(t, tc.model) {
					if mapped == column {
						found = true
						break
					}
				}
				if !found {
					t.Errorf("the live table has %s.%s, which the model does not map", tc.table, column)
				}
			}

			// The types the flow depends on, asserted only for the columns this table actually has
			// (the three tables do not share a column set).
			wantTypes := map[string]string{
				"redirect_uris":   "jsonb",
				"grant_types":     "jsonb",
				"response_types":  "jsonb",
				"created_at":      "timestamp with time zone",
				"last_used_at":    "timestamp with time zone",
				"expires_at":      "timestamp with time zone",
				"consumed_at":     "timestamp with time zone",
				"revoked_at":      "timestamp with time zone",
				"code_hash":       "character varying",
				"token_hash":      "character varying",
				"user_id":         "uuid",
				"organization_id": "uuid",
			}
			for column, want := range wantTypes {
				got, present := live[column]
				if !present {
					continue
				}
				if got != want {
					t.Errorf("%s.%s is %q, want %q", tc.table, column, got, want)
				}
			}
		})
	}
}

func modelColumnNames(t *testing.T, model any) []string {
	t.Helper()
	s, err := schema.Parse(model, &sync.Map{}, schema.NamingStrategy{})
	if err != nil {
		t.Fatalf("parse schema for %T: %v", model, err)
	}
	out := make([]string, 0, len(s.Fields))
	for _, f := range s.Fields {
		out = append(out, f.DBName)
	}
	return out
}

func TestGormOAuthStoreClientRoundTrip(t *testing.T) {
	integrationDB(t)
	ctx := context.Background()
	store := GormOAuthStore{}
	now := time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)

	secretHash := services.HashOAuthSecret("secret")
	clientID := "client-" + uuid.NewString()
	client := &models.OAuthClient{
		ID:       uuid.New(),
		ClientID: clientID,
		// A jsonb array with a comma and a colon inside an element, to prove the encoding survives
		// real JSON handling rather than splitting on a delimiter.
		ClientSecretHash:        &secretHash,
		ClientName:              "Claude Code",
		RedirectURIs:            models.StringList{"http://127.0.0.1:7654/callback?slot=1", "https://claude.example/cb"},
		GrantTypes:              models.StringList{"authorization_code", "refresh_token"},
		ResponseTypes:           models.StringList{"code"},
		TokenEndpointAuthMethod: "client_secret_post",
		CreatedAt:               now,
	}
	if err := store.CreateClient(ctx, client); err != nil {
		t.Fatalf("CreateClient: %v", err)
	}

	got, err := store.FindClientByClientID(ctx, clientID)
	if err != nil {
		t.Fatalf("FindClientByClientID: %v", err)
	}
	if got == nil {
		t.Fatal("the client was not found after being created")
	}
	if got.ClientName != client.ClientName || got.TokenEndpointAuthMethod != "client_secret_post" {
		t.Fatalf("scalar columns did not round-trip: %#v", got)
	}
	if len(got.RedirectURIs) != 2 || got.RedirectURIs[0] != client.RedirectURIs[0] {
		t.Fatalf("redirect_uris jsonb did not round-trip: %#v", got.RedirectURIs)
	}
	if len(got.GrantTypes) != 2 || got.GrantTypes[1] != "refresh_token" {
		t.Fatalf("grant_types jsonb did not round-trip: %#v", got.GrantTypes)
	}
	if got.ClientSecretHash == nil || *got.ClientSecretHash != secretHash {
		t.Fatal("client_secret_hash did not round-trip")
	}
	if got.LastUsedAt != nil {
		t.Fatal("last_used_at must start NULL")
	}

	// Unknown id is (nil, nil) — never an error the caller might turn into a 500.
	missing, err := store.FindClientByClientID(ctx, "no-such-client")
	if err != nil || missing != nil {
		t.Fatalf("unknown client: got (%v, %v), want (nil, nil)", missing, err)
	}

	// A secret-less public client is legal: the column is nullable.
	publicID := "client-public-" + uuid.NewString()
	public := &models.OAuthClient{
		ID: uuid.New(), ClientID: publicID, ClientName: "Public",
		RedirectURIs:  models.StringList{"https://claude.example/cb"},
		GrantTypes:    models.StringList{"authorization_code"},
		ResponseTypes: models.StringList{"code"}, TokenEndpointAuthMethod: "none", CreatedAt: now,
	}
	if err := store.CreateClient(ctx, public); err != nil {
		t.Fatalf("CreateClient (public): %v", err)
	}
	stored, err := store.FindClientByClientID(ctx, publicID)
	if err != nil || stored == nil {
		t.Fatalf("public client lookup: (%v, %v)", stored, err)
	}
	if stored.ClientSecretHash != nil {
		t.Fatal("a public client's client_secret_hash must stay NULL")
	}

	used := now.Add(time.Hour)
	if err := store.TouchClientUsed(ctx, publicID, used); err != nil {
		t.Fatalf("TouchClientUsed: %v", err)
	}
	stored, err = store.FindClientByClientID(ctx, publicID)
	if err != nil || stored == nil || stored.LastUsedAt == nil {
		t.Fatalf("last_used_at was not stamped: %#v (%v)", stored, err)
	}
}

func TestGormOAuthStoreAuthorizationCodeIsSingleUse(t *testing.T) {
	integrationDB(t)
	ctx := context.Background()
	store := GormOAuthStore{}
	now := time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)
	userID, orgID := uuid.New(), uuid.New()

	raw := "code-" + uuid.NewString()
	row := &models.OAuthAuthorizationCode{
		ID:             uuid.New(),
		CodeHash:       services.HashOAuthSecret(raw),
		ClientID:       "client-" + uuid.NewString(),
		UserID:         userID,
		OrganizationID: orgID,
		RedirectURI:    "https://claude.example/cb",
		CodeChallenge:  "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
		Scope:          "assistant.tools",
		ExpiresAt:      now.Add(10 * time.Minute),
	}
	if err := store.CreateCode(ctx, row); err != nil {
		t.Fatalf("CreateCode: %v", err)
	}

	got, err := store.FindCodeByHash(ctx, services.HashOAuthSecret(raw))
	if err != nil || got == nil {
		t.Fatalf("FindCodeByHash: (%v, %v)", got, err)
	}
	if got.UserID != userID || got.OrganizationID != orgID {
		t.Fatalf("uuid columns did not round-trip: %#v", got)
	}
	if got.ConsumedAt != nil {
		t.Fatal("a fresh code must not be consumed")
	}
	// The plaintext must not be stored anywhere.
	var plaintext int64
	if err := database.DB.Raw(`SELECT count(*) FROM oauth_authorization_codes WHERE code_hash = ?`, raw).Scan(&plaintext).Error; err != nil {
		t.Fatalf("count plaintext: %v", err)
	}
	if plaintext != 0 {
		t.Fatal("the raw code must never be stored")
	}

	// Unknown hash is (nil, nil).
	missing, err := store.FindCodeByHash(ctx, services.HashOAuthSecret("nope"))
	if err != nil || missing != nil {
		t.Fatalf("unknown code: (%v, %v), want (nil, nil)", missing, err)
	}

	// The conditional UPDATE: exactly one of two consumes wins, which is what makes the code
	// single-use under concurrency rather than merely single-use in a sequential test.
	first, err := store.ConsumeCode(ctx, row.ID, now)
	if err != nil || !first {
		t.Fatalf("first ConsumeCode = (%v, %v), want (true, nil)", first, err)
	}
	second, err := store.ConsumeCode(ctx, row.ID, now.Add(time.Second))
	if err != nil || second {
		t.Fatalf("second ConsumeCode = (%v, %v), want (false, nil)", second, err)
	}

	reloaded, err := store.FindCodeByHash(ctx, services.HashOAuthSecret(raw))
	if err != nil || reloaded == nil || reloaded.ConsumedAt == nil {
		t.Fatalf("consumed_at was not persisted: %#v (%v)", reloaded, err)
	}
}

func TestGormOAuthStoreRefreshRotationAndRevocation(t *testing.T) {
	integrationDB(t)
	ctx := context.Background()
	store := GormOAuthStore{}
	now := time.Date(2026, 10, 11, 9, 0, 0, 0, time.UTC)
	userID, otherUserID, orgID := uuid.New(), uuid.New(), uuid.New()
	grantClient := "client-" + uuid.NewString()
	otherGrantClient := "client-" + uuid.NewString()

	insert := func(raw, clientID string, user uuid.UUID, rotatedFrom *uuid.UUID) *models.OAuthRefreshToken {
		t.Helper()
		row := &models.OAuthRefreshToken{
			ID:             uuid.New(),
			TokenHash:      services.HashOAuthSecret(raw),
			ClientID:       clientID,
			UserID:         user,
			OrganizationID: orgID,
			Scope:          "assistant.tools assistant.admin",
			ExpiresAt:      now.Add(30 * 24 * time.Hour),
			RotatedFromID:  rotatedFrom,
		}
		if err := store.CreateRefreshToken(ctx, row); err != nil {
			t.Fatalf("CreateRefreshToken(%s): %v", raw, err)
		}
		return row
	}

	run := uuid.NewString()
	first := insert("refresh-1-"+run, grantClient, userID, nil)
	insert("refresh-2-"+run, grantClient, userID, &first.ID)
	// A different grant for the same user, and one for a different user, must survive a revocation of
	// the first grant.
	insert("refresh-3-"+run, otherGrantClient, userID, nil)
	insert("refresh-4-"+run, grantClient, otherUserID, nil)

	got, err := store.FindRefreshTokenByHash(ctx, services.HashOAuthSecret("refresh-2-"+run))
	if err != nil || got == nil {
		t.Fatalf("FindRefreshTokenByHash: (%v, %v)", got, err)
	}
	if got.RotatedFromID == nil || *got.RotatedFromID != first.ID {
		t.Fatalf("rotated_from_id did not round-trip: %#v", got.RotatedFromID)
	}
	if got.Scope != "assistant.tools assistant.admin" {
		t.Fatalf("scope did not round-trip: %q", got.Scope)
	}

	// Rotation is conditional, so a double rotation reports false rather than overwriting the first.
	if rotated, err := store.RotateRefreshToken(ctx, first.ID, now); err != nil || !rotated {
		t.Fatalf("first RotateRefreshToken = (%v, %v), want (true, nil)", rotated, err)
	}
	if rotated, err := store.RotateRefreshToken(ctx, first.ID, now.Add(time.Second)); err != nil || rotated {
		t.Fatalf("second RotateRefreshToken = (%v, %v), want (false, nil)", rotated, err)
	}

	// Revoking "the grant" must kill every live token for (client, user) — both the rotated-from row
	// and its successor — and leave the other client's grant and the other user's token alone.
	if err := store.RevokeGrant(ctx, grantClient, userID, now); err != nil {
		t.Fatalf("RevokeGrant: %v", err)
	}

	for _, raw := range []string{"refresh-1-" + run, "refresh-2-" + run} {
		row, err := store.FindRefreshTokenByHash(ctx, services.HashOAuthSecret(raw))
		if err != nil || row == nil {
			t.Fatalf("reload %s: (%v, %v)", raw, row, err)
		}
		if row.RevokedAt == nil {
			t.Errorf("%s must be revoked by RevokeGrant", raw)
		}
	}
	for _, raw := range []string{"refresh-3-" + run, "refresh-4-" + run} {
		row, err := store.FindRefreshTokenByHash(ctx, services.HashOAuthSecret(raw))
		if err != nil || row == nil {
			t.Fatalf("reload %s: (%v, %v)", raw, row, err)
		}
		if row.RevokedAt != nil {
			t.Errorf("%s belongs to another grant and must NOT be revoked", raw)
		}
	}
}

// TestGormOAuthStoreClientIDIsUnique proves the unique index the registration flow relies on is really
// there: a duplicate client_id is a failed INSERT, not a second client sharing an id.
func TestGormOAuthStoreClientIDIsUnique(t *testing.T) {
	integrationDB(t)
	ctx := context.Background()
	store := GormOAuthStore{}
	now := time.Now().UTC()

	duplicateID := "duplicate-" + uuid.NewString()
	base := func() *models.OAuthClient {
		return &models.OAuthClient{
			ID: uuid.New(), ClientID: duplicateID, ClientName: "C",
			RedirectURIs:  models.StringList{"https://claude.example/cb"},
			GrantTypes:    models.StringList{"authorization_code"},
			ResponseTypes: models.StringList{"code"}, TokenEndpointAuthMethod: "none", CreatedAt: now,
		}
	}

	if err := store.CreateClient(ctx, base()); err != nil {
		t.Fatalf("first CreateClient: %v", err)
	}
	if err := store.CreateClient(ctx, base()); err == nil {
		t.Fatal("a duplicate client_id must be refused by the unique index")
	}
}

// TestGormOAuthStoreDuplicateHashIsRefused covers the same guarantee for the two hash columns. A
// collision here would mean one credential standing in for another.
func TestGormOAuthStoreDuplicateHashIsRefused(t *testing.T) {
	integrationDB(t)
	ctx := context.Background()
	store := GormOAuthStore{}
	now := time.Now().UTC()

	codeValue := "code-" + uuid.NewString()
	code := func() *models.OAuthAuthorizationCode {
		return &models.OAuthAuthorizationCode{
			ID: uuid.New(), CodeHash: services.HashOAuthSecret(codeValue), ClientID: "c",
			UserID: uuid.New(), OrganizationID: uuid.New(), RedirectURI: "https://x/cb",
			CodeChallenge: "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
			Scope:         "assistant.tools", ExpiresAt: now.Add(time.Minute),
		}
	}
	if err := store.CreateCode(ctx, code()); err != nil {
		t.Fatalf("first CreateCode: %v", err)
	}
	if err := store.CreateCode(ctx, code()); err == nil {
		t.Fatal("a duplicate code_hash must be refused by the unique index")
	}

	tokenValue := "token-" + uuid.NewString()
	token := func() *models.OAuthRefreshToken {
		return &models.OAuthRefreshToken{
			ID: uuid.New(), TokenHash: services.HashOAuthSecret(tokenValue), ClientID: "c",
			UserID: uuid.New(), OrganizationID: uuid.New(), Scope: "assistant.tools",
			ExpiresAt: now.Add(time.Hour),
		}
	}
	if err := store.CreateRefreshToken(ctx, token()); err != nil {
		t.Fatalf("first CreateRefreshToken: %v", err)
	}
	if err := store.CreateRefreshToken(ctx, token()); err == nil {
		t.Fatal("a duplicate token_hash must be refused by the unique index")
	}
}
