// Package internalmail is the Go API's private mail surface. It is deliberately NOT part of
// /api/v1: nginx forwards /api/v1/*, /health and the site paths, so /internal/* is unreachable from
// outside the compose network, the same way the sandbox executor is kept private.
package internalmail

import (
	"crypto/subtle"
	"encoding/json"
	"log"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/gorilla/mux"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/services"
	"gorm.io/gorm"
)

// RegisterRoutes mounts the internal mail endpoint on the root router. The limiter is rebuilt here so
// the MARKETING_RELAY_* caps are read once configuration has been loaded.
func RegisterRoutes(router *mux.Router) {
	d.limiter = defaultConfirmLimiter()
	router.HandleFunc("/internal/mail/send", handleSend).Methods("POST")
}

// deps are the endpoint's collaborators, replaceable in tests.
type deps struct {
	// token is the shared secret the caller must present. Configuring it is what turns the endpoint
	// on at all: unset means "not configured", which answers 404 rather than accepting anything.
	token func() string
	// userExists stops a compromised caller from using this endpoint as an open relay (C0): only
	// addresses that belong to an account are mailed.
	userExists func(email string) (bool, error)
	// userLocale returns the saved locale of the account behind an address ("" when none); nil in tests.
	userLocale func(email string) string
	sendMail   func(to string, tmpl services.TemplateName, data services.MailData) error
	// runAsync detaches delivery from the response; the caller gets 202 immediately.
	runAsync func(func())
	// linkOrigins returns PublicBaseURL and the extra MAIL_LINK_HOSTS origins a link may name.
	linkOrigins func() (string, []string)
	// operatorEmail is the one address the operator_digest mail may go to: this process's own
	// OPERATOR_DIGEST_EMAIL, never anything the request says. Nil or empty means the template is
	// refused for every recipient.
	operatorEmail func() string
	// limiter caps marketing-confirm mail per mailbox, per domain and overall.
	limiter *confirmLimiter
}

// defaultConfirmLimiter is the production limiter: env-configured caps over mail_rate_counters.
func defaultConfirmLimiter() *confirmLimiter {
	store := sqlCounterStore{db: func() *gorm.DB { return database.DB }}
	return newConfirmLimiter(time.Now, store, relayLimitsFromEnv(os.Getenv))
}

var d = deps{
	token:      func() string { return config.Cfg.InternalAPIToken },
	userExists: services.GormAccountDeletionStore{}.UserExists,
	userLocale: services.UserLocaleByEmail,
	sendMail: func(to string, tmpl services.TemplateName, data services.MailData) error {
		return services.DefaultMailer().SendTemplate(to, tmpl, data)
	},
	runAsync:      func(f func()) { go f() },
	linkOrigins:   func() (string, []string) { return config.Cfg.PublicBaseURL, config.Cfg.MailLinkHosts },
	limiter:       defaultConfirmLimiter(),
	operatorEmail: func() string { return os.Getenv("OPERATOR_DIGEST_EMAIL") },
}

// sendRequest is the wire shape the Inference API posts: the Go API maps it onto MailData, and the
// template name must be one of the C2 templates — that closed list is what keeps this endpoint from
// being able to send arbitrary content.
type sendRequest struct {
	To string `json:"to"`
	// Locale is optional (allowlisted): the language for a recipient with no saved locale.
	Locale   string   `json:"locale,omitempty"`
	Template string   `json:"template"`
	Data     mailData `json:"data"`
}

type mailData struct {
	Name         string `json:"name"`
	Link         string `json:"link"`
	ExpiresIn    string `json:"expiresIn"`
	OrgName      string `json:"orgName"`
	InviterName  string `json:"inviterName"`
	Role         string `json:"role"`
	DeletionDate string `json:"deletionDate"`
	Balance      string `json:"balance"`
	Reason       string `json:"reason"`
	// Digest is the structured body of an operator_digest mail; ignored for every other template.
	Digest *services.OperatorDigest `json:"digest,omitempty"`
}

// templateAllowed reports whether name is one of the templates this build ships.
func templateAllowed(name string) bool {
	for _, t := range services.AllTemplates {
		if string(t) == name {
			return true
		}
	}
	return false
}

// tmplAddressesNonAccounts reports whether a template may be sent to an address with no account.
func tmplAddressesNonAccounts(name string) bool {
	return name == string(services.TemplateMarketingConfirm)
}

// operatorDigestRecipient returns the configured operator address when to is that address: an exact match
// except that ASCII letters compare case-insensitively (never Unicode folding, under which a look-alike such as
// a long s would match an s). The caller mails the returned address, the Go API's own, not the request's text.
// An unset or invalid configured address matches nothing.
func operatorDigestRecipient(to string) (string, bool) {
	if d.operatorEmail == nil {
		return "", false
	}
	op, err := services.ValidateRecipient(d.operatorEmail())
	if err != nil || !asciiEqualFold(op, to) {
		return "", false
	}
	return op, true
}

func asciiEqualFold(a, b string) bool {
	if len(a) != len(b) {
		return false
	}
	for i := 0; i < len(a); i++ {
		x, y := a[i], b[i]
		if 'A' <= x && x <= 'Z' {
			x += 'a' - 'A'
		}
		if 'A' <= y && y <= 'Z' {
			y += 'a' - 'A'
		}
		if x != y {
			return false
		}
	}
	return true
}

// authorized compares the presented bearer token with the configured secret in constant time, and
// requires the secret to be configured at all.
func authorized(header string) bool {
	secret := strings.TrimSpace(d.token())
	if secret == "" {
		return false
	}
	const prefix = "Bearer "
	if !strings.HasPrefix(header, prefix) {
		return false
	}
	presented := strings.TrimSpace(strings.TrimPrefix(header, prefix))
	return subtle.ConstantTimeCompare([]byte(presented), []byte(secret)) == 1
}

// recipientRef is how a recipient appears in logs: never the address, only the keyed C4 reference
// (HMAC-SHA256 under MAIL_LOG_REF_KEY of the canonical mailbox), enough to correlate lines for one
// mailbox across both services without being reversible by dictionary (S-20, F-4).
func recipientRef(addr string) string {
	return services.MailLogRef(addr)
}

func handleSend(w http.ResponseWriter, r *http.Request) {
	// An unconfigured deployment does not expose the endpoint at all.
	if strings.TrimSpace(d.token()) == "" {
		http.Error(w, `{"error":"not found"}`, http.StatusNotFound)
		return
	}
	if !authorized(r.Header.Get("Authorization")) {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}

	var req sendRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 64*1024)).Decode(&req); err != nil {
		http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
		return
	}

	to, err := services.ValidateRecipient(req.To)
	if err != nil {
		http.Error(w, `{"error":"invalid recipient"}`, http.StatusBadRequest)
		return
	}
	if !templateAllowed(req.Template) {
		http.Error(w, `{"error":"unknown template"}`, http.StatusBadRequest)
		return
	}

	// The marketing double opt-in mail is the one template whose recipient is, by design, often not
	// an account (a site subscriber). It stays a closed relay: the wording is fixed and the mailer
	// pins its only link to the confirm page on a configured origin, so all it can ever say is
	// "confirm on ReelBolt" — and only a few times per address and a bounded number per hour.
	exists := tmplAddressesNonAccounts(req.Template)
	if req.Template == string(services.TemplateOperatorDigest) {
		// The operator digest carries platform error text, so it goes to exactly one address: the
		// one this process is configured with. Checked here from the Go API's own environment (the
		// caller cannot widen it) and before any account lookup, so it is no oracle either.
		op, ok := operatorDigestRecipient(to)
		if !ok {
			log.Printf("internal mail: refusing the %s mail to %s: not the configured operator address", req.Template, recipientRef(to))
			http.Error(w, `{"error":"recipient not permitted"}`, http.StatusForbidden)
			return
		}
		if req.Data.Digest == nil {
			http.Error(w, `{"error":"invalid request body"}`, http.StatusBadRequest)
			return
		}
		to = op
		exists = true
	}
	if !exists {
		exists, err = d.userExists(to)
		if err != nil {
			log.Printf("internal mail: recipient lookup for %s failed: %s", recipientRef(to), services.SanitizeMailError(err))
			http.Error(w, `{"error":"service unavailable"}`, http.StatusServiceUnavailable)
			return
		}
	}
	if !exists {
		// Also the answer for an address that never had an account: the endpoint is not an oracle.
		http.Error(w, `{"error":"unknown recipient"}`, http.StatusNotFound)
		return
	}

	data := services.MailData{
		Name:         req.Data.Name,
		Link:         req.Data.Link,
		ExpiresIn:    req.Data.ExpiresIn,
		OrgName:      req.Data.OrgName,
		InviterName:  req.Data.InviterName,
		Role:         req.Data.Role,
		DeletionDate: req.Data.DeletionDate,
		Balance:      req.Data.Balance,
		Reason:       req.Data.Reason,
	}
	tmpl := services.TemplateName(req.Template)
	if tmpl == services.TemplateOperatorDigest {
		data.Digest = req.Data.Digest
	}
	// Language: the account's saved locale, else the request body, else Accept-Language, else en.
	data.Locale = services.LocaleFromRequest(r, req.Locale)
	if tmpl != services.TemplateOperatorDigest && !tmplAddressesNonAccounts(req.Template) && d.userLocale != nil {
		if loc := d.userLocale(to); loc != "" {
			data.Locale = loc
		}
	}
	if tmpl == services.TemplateMarketingConfirm {
		// S-07: the link is pinned to the confirm page with a bare token, refused here (not only at
		// delivery) so the caller learns of it, then the mailbox, domain and global caps apply (F-3;
		// a counter-store failure refuses too). The
		// template renders nothing but the link, so nothing else the caller sent is forwarded.
		base, extra := d.linkOrigins()
		if err := services.ValidateMarketingConfirmLink(req.Data.Link, base, extra); err != nil {
			http.Error(w, `{"error":"invalid link"}`, http.StatusBadRequest)
			return
		}
		if !d.limiter.allow(r.Context(), to) {
			http.Error(w, `{"error":"too many requests"}`, http.StatusTooManyRequests)
			return
		}
		data = services.MailData{Link: req.Data.Link, Locale: data.Locale}
	}

	d.runAsync(func() {
		if err := d.sendMail(to, tmpl, data); err != nil {
			// Delivery failures are logged and never block the caller (C0). SMTP replies echo the
			// recipient, so the error is reduced to status, enhanced code and class (C4).
			log.Printf("internal mail: sending %s to %s failed: %s", tmpl, recipientRef(to), services.SanitizeMailError(err))
		}
	})

	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(http.StatusAccepted)
	_ = json.NewEncoder(w).Encode(map[string]string{"status": "accepted"})
}
