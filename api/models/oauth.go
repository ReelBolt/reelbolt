package models

import (
	"database/sql/driver"
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/google/uuid"
)

// OAuth tables (docs/design/mcp-oauth.md §5). The Go API is the authorization
// server and owns the behaviour, but it NEVER migrates: the Inference API owns
// these tables through EF Core (`ReelBolt.Shared/Data/Models/McpOAuth.cs` and
// the `oauth_clients` configuration in `InferenceApiDbContext`). Column names
// and types are therefore an interface with that side and must match it
// exactly — `redirect_uris`, `grant_types` and `response_types` are all
// **jsonb**, `client_id`/`code_hash`/`token_hash` are bounded strings.

// StringList is a list of strings persisted into a Postgres jsonb column. It
// has to exist because GORM cannot map `[]string` to jsonb on its own, and the
// three columns that need it are an interface with the EF side.
//
// Writing always emits a JSON array — the encoding the EF configuration
// specifies (`HasColumnType("jsonb")`). Reading also accepts a Postgres array
// literal, so a column that ends up as text[] still reads rather than failing
// at the first SELECT; the write path is the one that depends on the declared
// type.
type StringList []string

func (l StringList) Value() (driver.Value, error) {
	out := []string(l)
	if out == nil {
		out = []string{}
	}
	b, err := json.Marshal(out)
	if err != nil {
		return nil, fmt.Errorf("models.StringList: encode: %w", err)
	}
	return string(b), nil
}

func (l *StringList) Scan(src any) error {
	if src == nil {
		*l = nil
		return nil
	}

	var raw string
	switch v := src.(type) {
	case []byte:
		raw = string(v)
	case string:
		raw = v
	default:
		return fmt.Errorf("models.StringList: cannot scan %T", src)
	}

	trimmed := strings.TrimSpace(raw)
	if trimmed == "" || trimmed == "null" {
		*l = nil
		return nil
	}

	if strings.HasPrefix(trimmed, "[") {
		var out []string
		if err := json.Unmarshal([]byte(trimmed), &out); err != nil {
			return fmt.Errorf("models.StringList: decode json: %w", err)
		}
		*l = out
		return nil
	}

	if strings.HasPrefix(trimmed, "{") && strings.HasSuffix(trimmed, "}") {
		out, err := parsePostgresArrayLiteral(trimmed)
		if err != nil {
			return fmt.Errorf("models.StringList: %w", err)
		}
		*l = out
		return nil
	}

	return fmt.Errorf("models.StringList: unrecognised encoding %q", trimmed)
}

// parsePostgresArrayLiteral reads the `{a,b,"c,d"}` form Postgres uses for text[]. It exists only as a
// defensive read path: the columns this type maps are jsonb, so the JSON branch above is the one that
// runs. It is implemented properly rather than by splitting on commas because a quoted element may
// itself contain one, and a fallback that silently corrupted a redirect-URI list would be worse than
// having none.
func parsePostgresArrayLiteral(s string) ([]string, error) {
	body := strings.TrimSuffix(strings.TrimPrefix(s, "{"), "}")

	var out []string
	i := 0
	for i < len(body) {
		for i < len(body) && (body[i] == ' ' || body[i] == '\t') {
			i++
		}
		if i >= len(body) {
			break
		}

		var element strings.Builder
		quoted := body[i] == '"'
		if quoted {
			i++
			closed := false
			for i < len(body) {
				c := body[i]
				if c == '\\' && i+1 < len(body) {
					element.WriteByte(body[i+1])
					i += 2
					continue
				}
				if c == '"' {
					i++
					closed = true
					break
				}
				element.WriteByte(c)
				i++
			}
			if !closed {
				return nil, fmt.Errorf("unterminated quoted element in %q", s)
			}
		} else {
			for i < len(body) && body[i] != ',' {
				element.WriteByte(body[i])
				i++
			}
		}

		value := element.String()
		// An unquoted NULL is a SQL NULL element, not the four-character string.
		if !quoted && value == "NULL" {
			value = ""
		}
		if value != "" {
			out = append(out, value)
		}

		for i < len(body) && (body[i] == ' ' || body[i] == '\t') {
			i++
		}
		if i < len(body) && body[i] == ',' {
			i++
		}
	}
	return out, nil
}

// OAuthClient is one dynamically registered client (oauth_clients).
type OAuthClient struct {
	ID       uuid.UUID `gorm:"type:uuid;primaryKey;column:id"`
	ClientID string    `gorm:"column:client_id;uniqueIndex"`
	// ClientSecretHash is hex SHA-256 of the issued secret, or nil for a public
	// (PKCE-only) client. The plaintext is shown once, at registration.
	ClientSecretHash        *string    `gorm:"column:client_secret_hash"`
	ClientName              string     `gorm:"column:client_name"`
	RedirectURIs            StringList `gorm:"type:jsonb;column:redirect_uris"`
	GrantTypes              StringList `gorm:"type:jsonb;column:grant_types"`
	ResponseTypes           StringList `gorm:"type:jsonb;column:response_types"`
	TokenEndpointAuthMethod string     `gorm:"column:token_endpoint_auth_method"`
	CreatedAt               time.Time  `gorm:"column:created_at"`
	LastUsedAt              *time.Time `gorm:"column:last_used_at"`
}

func (OAuthClient) TableName() string { return "oauth_clients" }

// OAuthAuthorizationCode is one issued, single-use authorization code. Only the
// hash is stored; the code itself is handed to the client once.
type OAuthAuthorizationCode struct {
	ID             uuid.UUID  `gorm:"type:uuid;primaryKey;column:id"`
	CodeHash       string     `gorm:"column:code_hash;uniqueIndex"`
	ClientID       string     `gorm:"column:client_id"`
	UserID         uuid.UUID  `gorm:"type:uuid;column:user_id"`
	OrganizationID uuid.UUID  `gorm:"type:uuid;column:organization_id"`
	RedirectURI    string     `gorm:"column:redirect_uri"`
	CodeChallenge  string     `gorm:"column:code_challenge"`
	Scope          string     `gorm:"column:scope"`
	ExpiresAt      time.Time  `gorm:"column:expires_at"`
	ConsumedAt     *time.Time `gorm:"column:consumed_at"`
}

func (OAuthAuthorizationCode) TableName() string { return "oauth_authorization_codes" }

// OAuthRefreshToken is one issued refresh token. Only the hash is stored.
// Rotation revokes the presented row and writes a new one whose RotatedFromID
// points back at it, so a reused (already rotated) token is recognisable and
// revokes the whole grant.
type OAuthRefreshToken struct {
	ID             uuid.UUID  `gorm:"type:uuid;primaryKey;column:id"`
	TokenHash      string     `gorm:"column:token_hash;uniqueIndex"`
	ClientID       string     `gorm:"column:client_id"`
	UserID         uuid.UUID  `gorm:"type:uuid;column:user_id"`
	OrganizationID uuid.UUID  `gorm:"type:uuid;column:organization_id"`
	Scope          string     `gorm:"column:scope"`
	ExpiresAt      time.Time  `gorm:"column:expires_at"`
	RevokedAt      *time.Time `gorm:"column:revoked_at"`
	RotatedFromID  *uuid.UUID `gorm:"type:uuid;column:rotated_from_id"`
}

func (OAuthRefreshToken) TableName() string { return "oauth_refresh_tokens" }
