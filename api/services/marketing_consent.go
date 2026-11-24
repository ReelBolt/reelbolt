package services

import (
	"fmt"
	"strings"
	"time"
	"unicode/utf8"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

// MarketingWordingVersion is the current entry of the marketing consent wording
// registry (inference/src/ReelBolt.Inference.Api/Marketing/marketing-consent.wording.json).
// Signup refuses an opt-in that names any other version; a test pins the two
// together, so bumping the registry without this constant fails the build.
const MarketingWordingVersion = "2026-10-marketing-v1"

// maxConsentUserAgent is the user_agent column width (≤256 characters).
const maxConsentUserAgent = 256

// MarketingOptIn is the marketing checkbox a signup ticked, with the request
// evidence the consent row records. A nil *MarketingOptIn means "not ticked".
type MarketingOptIn struct {
	WordingVersion string
	IP             *string
	UserAgent      string
	At             time.Time
}

func normalizeConsentEmail(e string) string { return strings.ToLower(strings.TrimSpace(e)) }

func truncateRunes(s string, n int) string {
	if utf8.RuneCountInString(s) <= n {
		return s
	}
	return string([]rune(s)[:n])
}

// SignupMarketingConsent is the pure upsert behind a signup opt-in: given the
// row already stored for the address (nil when none) it returns the row to
// save and the events to append.
//
//   - No row yet: a new OptedIn row with Source=Signup, the request evidence and
//     the signup's user; ConfirmedAt stays null until the address is verified.
//   - A verified user (an invite proved the address) may overwrite whatever is
//     stored, and the opt-in is confirmed at once (Granted then Confirmed).
//   - S-18: an unverified signup proves nothing about the address, so it never
//     rewrites or claims an existing row. The row keeps its status, evidence and
//     owner; a Requested event naming the signup's user records the request, and
//     a Withdrawn row also gets resubscribe_requested_at (as the Inference API
//     records an anonymous re-subscribe). Verifying the address is what opts it
//     in (ConfirmSignupOptInOnVerify).
//   - A row that is OptedIn and confirmed is returned unchanged with no events.
func SignupMarketingConsent(existing *models.MarketingConsent, user *models.ApplicationUser, in MarketingOptIn) (models.MarketingConsent, []models.MarketingConsentEvent) {
	at := in.At
	var ua *string
	if v := truncateRunes(strings.TrimSpace(in.UserAgent), maxConsentUserAgent); v != "" {
		ua = &v
	}
	var row models.MarketingConsent
	if existing != nil {
		if existing.Status == models.MarketingConsentStatusOptedIn && existing.ConfirmedAt != nil {
			return *existing, nil
		}
		row = *existing
		if user.EmailVerifiedAt == nil {
			userID := user.ID
			if row.Status == models.MarketingConsentStatusWithdrawn {
				row.ResubscribeRequestedAt = &at
				row.UpdatedAt = at
			}
			ev := models.MarketingConsentEvent{
				ID: uuid.New(), ConsentID: row.ID, Email: row.Email, UserID: &userID,
				Action: models.MarketingConsentActionRequested, Source: models.MarketingConsentSourceSignup,
				WordingVersion: in.WordingVersion, IP: in.IP, UserAgent: ua, OccurredAt: at,
			}
			return row, []models.MarketingConsentEvent{ev}
		}
	} else {
		row = models.MarketingConsent{ID: uuid.New()}
	}
	userID := user.ID
	row.Email = normalizeConsentEmail(user.Email)
	if row.UserID == nil {
		row.UserID = &userID
	}
	row.Status = models.MarketingConsentStatusOptedIn
	row.Source = models.MarketingConsentSourceSignup
	row.WordingVersion = in.WordingVersion
	row.ConsentedAt = &at
	row.ConfirmedAt = nil
	row.WithdrawnAt = nil
	row.ResubscribeRequestedAt = nil
	row.IP = in.IP
	row.UserAgent = ua
	row.UpdatedAt = at

	events := []models.MarketingConsentEvent{consentEvent(&row, models.MarketingConsentActionGranted, at)}
	if user.EmailVerifiedAt != nil {
		row.ConfirmedAt = &at
		events = append(events, consentEvent(&row, models.MarketingConsentActionConfirmed, at))
	}
	return row, events
}

// signupRequestOpen reports whether request is a still-open signup request by userID for row: a
// Requested event from a Signup by that user, made after any withdrawal, on a row that is not
// already a confirmed opt-in; for a Withdrawn row the request must still be recorded
// (resubscribe_requested_at, cleared when the address says no again or the sweeper expires it).
func signupRequestOpen(row *models.MarketingConsent, request *models.MarketingConsentEvent, userID uuid.UUID) bool {
	if row == nil || request == nil || request.ConsentID != row.ID ||
		request.Action != models.MarketingConsentActionRequested || request.Source != models.MarketingConsentSourceSignup ||
		request.UserID == nil || *request.UserID != userID {
		return false
	}
	if row.Status == models.MarketingConsentStatusOptedIn && row.ConfirmedAt != nil {
		return false
	}
	if row.WithdrawnAt != nil && !request.OccurredAt.After(*row.WithdrawnAt) {
		return false
	}
	if row.Status == models.MarketingConsentStatusWithdrawn && row.ResubscribeRequestedAt == nil {
		return false
	}
	return true
}

// ownUnconfirmedOptIn reports whether row is userID's own OptedIn row awaiting confirmation (the row
// a first signup creates).
func ownUnconfirmedOptIn(row *models.MarketingConsent, userID uuid.UUID) bool {
	return row != nil && row.UserID != nil && *row.UserID == userID &&
		row.Status == models.MarketingConsentStatusOptedIn && row.ConfirmedAt == nil
}

// SignupOptInPending reports whether verifying the address would confirm a marketing opt-in for
// userID — what the verify mail's marketing notice depends on (S-32). request is the latest signup
// Requested event by userID on row (nil when none).
func SignupOptInPending(row *models.MarketingConsent, request *models.MarketingConsentEvent, userID uuid.UUID) bool {
	return ownUnconfirmedOptIn(row, userID) || signupRequestOpen(row, request, userID)
}

// ConfirmSignupOptInOnVerify applies a verified address to the row stored for it and returns the
// events to append (nil when nothing changes). The user's own unconfirmed opt-in is confirmed; a row
// carrying the user's open signup request (S-18) becomes OptedIn and confirmed under the request's
// wording, like the Inference API's confirm click, and is bound to the user if it had no owner.
func ConfirmSignupOptInOnVerify(row *models.MarketingConsent, request *models.MarketingConsentEvent, userID uuid.UUID, at time.Time) []models.MarketingConsentEvent {
	if ownUnconfirmedOptIn(row, userID) {
		if ev := ConfirmMarketingConsentOnVerify(row, at); ev != nil {
			return []models.MarketingConsentEvent{*ev}
		}
		return nil
	}
	if !signupRequestOpen(row, request, userID) {
		return nil
	}
	if row.UserID == nil {
		uid := userID
		row.UserID = &uid
	}
	row.Status = models.MarketingConsentStatusOptedIn
	row.Source = request.Source
	row.WordingVersion = request.WordingVersion
	row.ConsentedAt = &at
	row.ConfirmedAt = &at
	row.WithdrawnAt = nil
	row.ResubscribeRequestedAt = nil
	row.IP = request.IP
	row.UserAgent = request.UserAgent
	row.UpdatedAt = at
	ev := consentEvent(row, models.MarketingConsentActionConfirmed, at)
	ev.IP, ev.UserAgent = nil, nil
	return []models.MarketingConsentEvent{ev}
}

// ConfirmMarketingConsentOnVerify stamps ConfirmedAt on an OptedIn row whose
// address was just verified, returning the Confirmed event to append. Any
// other row (not OptedIn, or already confirmed) is left alone and nil returned.
func ConfirmMarketingConsentOnVerify(c *models.MarketingConsent, at time.Time) *models.MarketingConsentEvent {
	if c == nil || c.Status != models.MarketingConsentStatusOptedIn || c.ConfirmedAt != nil {
		return nil
	}
	c.ConfirmedAt = &at
	c.UpdatedAt = at
	ev := consentEvent(c, models.MarketingConsentActionConfirmed, at)
	// The click proving the address is not the request that granted consent.
	ev.IP, ev.UserAgent = nil, nil
	return &ev
}

func consentEvent(c *models.MarketingConsent, action string, at time.Time) models.MarketingConsentEvent {
	return models.MarketingConsentEvent{
		ID: uuid.New(), ConsentID: c.ID, Email: c.Email, UserID: c.UserID,
		Action: action, Source: c.Source, WordingVersion: c.WordingVersion,
		IP: c.IP, UserAgent: c.UserAgent, OccurredAt: at,
	}
}

// writeSignupMarketingConsentTx upserts the address's consent row and appends
// its events inside the signup's transaction, so a failed signup leaves no
// consent behind. A nil opt-in writes nothing.
func writeSignupMarketingConsentTx(tx *gorm.DB, user *models.ApplicationUser, in *MarketingOptIn) error {
	if in == nil || models.IsServiceAccount(user) || isServiceAccountEmail(user.Email) {
		return nil
	}
	// Find, not First: "no row yet" is the common case and must not log as an error.
	var found []models.MarketingConsent
	if err := tx.Clauses(clause.Locking{Strength: "UPDATE"}).
		Where("email = ?", normalizeConsentEmail(user.Email)).Limit(1).
		Find(&found).Error; err != nil {
		return fmt.Errorf("read marketing consent: %w", err)
	}
	var prev *models.MarketingConsent
	if len(found) == 1 {
		prev = &found[0]
	}

	row, events := SignupMarketingConsent(prev, user, *in)
	if len(events) == 0 {
		// An already confirmed opt-in: nothing to record (S-18).
		return nil
	}
	var err error
	switch {
	case prev == nil:
		err = tx.Create(&row).Error
	case row != *prev:
		err = tx.Save(&row).Error
	}
	if err != nil {
		return fmt.Errorf("record marketing consent: %w", err)
	}
	for i := range events {
		if err := tx.Create(&events[i]).Error; err != nil {
			return fmt.Errorf("record marketing consent event: %w", err)
		}
	}
	return nil
}

// latestSignupRequestTx is the newest Requested event a Signup by userID recorded on the consent row.
func latestSignupRequestTx(tx *gorm.DB, consentID, userID uuid.UUID) (*models.MarketingConsentEvent, error) {
	var found []models.MarketingConsentEvent
	if err := tx.Where("consent_id = ? AND user_id = ? AND action = ? AND source = ?",
		consentID, userID, models.MarketingConsentActionRequested, models.MarketingConsentSourceSignup).
		Order("occurred_at DESC").Limit(1).Find(&found).Error; err != nil {
		return nil, fmt.Errorf("read marketing consent request: %w", err)
	}
	if len(found) == 0 {
		return nil, nil
	}
	return &found[0], nil
}

// signupOptInTx loads the consent row for email and userID's latest signup request on it. lock takes
// the row FOR UPDATE (the verify transaction). Nil row means no consent for the address.
func signupOptInTx(tx *gorm.DB, userID uuid.UUID, email string, lock bool) (*models.MarketingConsent, *models.MarketingConsentEvent, error) {
	if isServiceAccountEmail(email) {
		// An API-key service account has no marketing consent, ever.
		return nil, nil, nil
	}
	q := tx
	if lock {
		q = q.Clauses(clause.Locking{Strength: "UPDATE"})
	}
	// Find, not First: most verifications have no opt-in, which is not an error.
	var found []models.MarketingConsent
	if err := q.Where("email = ?", normalizeConsentEmail(email)).Limit(1).Find(&found).Error; err != nil {
		return nil, nil, fmt.Errorf("read marketing consent: %w", err)
	}
	if len(found) == 0 {
		return nil, nil, nil
	}
	row := &found[0]
	request, err := latestSignupRequestTx(tx, row.ID, userID)
	if err != nil {
		return nil, nil, err
	}
	return row, request, nil
}

// confirmMarketingConsentTx applies the verified address to its consent row (ConfirmSignupOptInOnVerify)
// inside the verify transaction, the row held FOR UPDATE so a concurrent verification cannot append
// a second Confirmed event.
//
// S-19: the row is the one for the address just verified, and it is confirmed only when it is the
// user's own opt-in or carries the user's own signup request. Verifying one address proves nothing
// about a row the account holds for another one (an address it has since changed away from).
func confirmMarketingConsentTx(tx *gorm.DB, userID uuid.UUID, email string, at time.Time) error {
	row, request, err := signupOptInTx(tx, userID, email, true)
	if err != nil || row == nil {
		return err
	}
	events := ConfirmSignupOptInOnVerify(row, request, userID, at)
	if len(events) == 0 {
		return nil
	}
	if err := tx.Save(row).Error; err != nil {
		return fmt.Errorf("confirm marketing consent: %w", err)
	}
	for i := range events {
		if err := tx.Create(&events[i]).Error; err != nil {
			return fmt.Errorf("record marketing consent event: %w", err)
		}
	}
	return nil
}

// MarketingOptInPending reports whether verifying email would confirm a marketing opt-in for userID,
// so a resent or duplicate-signup verify mail carries the marketing notice (S-32).
func (GormAccountStore) MarketingOptInPending(userID uuid.UUID, email string) (bool, error) {
	row, request, err := signupOptInTx(database.DB, userID, email, false)
	if err != nil {
		return false, err
	}
	return SignupOptInPending(row, request, userID), nil
}

// isServiceAccountEmail reports whether email is in the reserved service-account domain.
func isServiceAccountEmail(email string) bool {
	return strings.HasSuffix(strings.ToLower(strings.TrimSpace(email)), models.ServiceAccountEmailDomain)
}
