package auth

import (
	"encoding/json"
	"log"
	"net/http"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/middleware"
	"github.com/vecchiotom/reelbolt/models"
	"github.com/vecchiotom/reelbolt/services"
)

// servedLegalVersions is the version of each legal document this deployment
// currently serves, read from legal_documents — the table the Inference API owns
// and publishes through PUT /api/v1/legal/documents/{document} (F-7).
//
// It answers the request itself and reports false when the deployment cannot
// state a version (no row, or a failed read). That is a 503 on purpose: an
// acceptance whose version the server cannot name must not be written at all,
// because the row is supposed to be evidence.
func servedLegalVersions(w http.ResponseWriter) (services.LegalVersions, bool) {
	current, err := acct.store.CurrentLegalVersions()
	if err != nil {
		log.Printf("read the deployed legal versions: %v", err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return services.LegalVersions{}, false
	}
	return current, true
}

// matchesServedVersions reports whether the caller's submission names exactly
// the versions the server serves. There is no other accepted value: the 32
// characters of anything at all that signup and accept-terms used to accept is
// what made a recorded acceptance worthless.
func matchesServedVersions(submittedTerms, submittedPrivacy string, current services.LegalVersions) bool {
	return strings.TrimSpace(submittedTerms) == current.Terms &&
		strings.TrimSpace(submittedPrivacy) == current.Privacy
}

// writeLegalVersionMismatch refuses a submission that names a version the
// deployment is not serving — a stale page, a forged value, or a version that
// never existed. 409 rather than 400: the request is well-formed and the caller
// is not wrong about anything it controls, the documents simply moved underneath
// it. The current versions ride in the body so the page can re-render its
// checkbox against the real ones instead of asking the visitor to guess.
func writeLegalVersionMismatch(w http.ResponseWriter, current services.LegalVersions) {
	writeJSON(w, http.StatusConflict, map[string]string{
		"error":                 "the terms have been updated since this page was loaded; reload and accept the current version",
		"currentTermsVersion":   current.Terms,
		"currentPrivacyVersion": current.Privacy,
	})
}

// legalAcceptances builds the two rows a signup or an accept-terms request
// writes, pinned to the versions the SERVER serves. It does not take the
// submission: whoever calls it has already established that the two agree.
func legalAcceptances(userID uuid.UUID, now time.Time, ip *string, current services.LegalVersions) []models.LegalAcceptance {
	return []models.LegalAcceptance{
		{ID: uuid.New(), UserID: userID, Document: models.LegalDocumentTerms, Version: current.Terms, AcceptedAt: now, IP: ip},
		{ID: uuid.New(), UserID: userID, Document: models.LegalDocumentPrivacy, Version: current.Privacy, AcceptedAt: now, IP: ip},
	}
}

// privacyOnly keeps the Privacy rows of an acceptance: what a privacy acknowledgement records.
func privacyOnly(rows []models.LegalAcceptance) []models.LegalAcceptance {
	out := make([]models.LegalAcceptance, 0, 1)
	for _, r := range rows {
		if r.Document == models.LegalDocumentPrivacy {
			out = append(out, r)
		}
	}
	return out
}

// handleAcceptTerms records a legal acceptance for the signed-in caller and
// re-issues the session so its claims carry the new version (C10).
//
// This is the endpoint C7-UI's /app/accept-terms page was missing: before it,
// legal_acceptances was written only by signup and invite signup, so an
// already-signed-in user (an OAuth first login, or anyone re-accepting a new
// version the proxy gate sent them to) could only record the acceptance in the
// readable cookie, which proves nothing. The rows are written here, and only
// here, for that case.
//
// F-7: the version recorded is the one the deployment serves, never the one in
// the request body. The body has to say which version it is accepting — consent
// to a version nobody showed you is not consent — and a body naming anything
// else is refused with 409 and writes nothing.
func handleAcceptTerms(w http.ResponseWriter, r *http.Request) {
	uc, ok := middleware.GetUserContext(r)
	if !ok {
		http.Error(w, `{"error":"unauthorized"}`, http.StatusUnauthorized)
		return
	}
	userID, err := uuid.Parse(uc.UserID)
	if err != nil {
		http.Error(w, `{"error":"invalid user ID"}`, http.StatusInternalServerError)
		return
	}

	var req AcceptTermsRequest
	if err := json.NewDecoder(http.MaxBytesReader(w, r.Body, 4<<10)).Decode(&req); err != nil {
		writeJSONError(w, http.StatusBadRequest, "invalid request body")
		return
	}
	if !req.AcceptTerms && !req.PrivacyAcknowledgement {
		writeJSONError(w, http.StatusBadRequest, "you must accept the terms and privacy policy")
		return
	}

	// The server's own versions, before anything is written.
	current, ok := servedLegalVersions(w)
	if !ok {
		return
	}
	if req.PrivacyAcknowledgement {
		// Only the Privacy version is submitted; the Terms are checked against what is stored below.
		if strings.TrimSpace(req.PrivacyVersion) != current.Privacy {
			writeLegalVersionMismatch(w, current)
			return
		}
	} else if !matchesServedVersions(req.TermsVersion, req.PrivacyVersion, current) {
		writeLegalVersionMismatch(w, current)
		return
	}

	user, err := acct.store.FindUserByID(userID)
	if err != nil {
		log.Printf("accept-terms lookup %s: %v", userID, err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return
	}
	if user == nil {
		writeJSONError(w, http.StatusUnauthorized, "unauthorized")
		return
	}

	now := acct.now().UTC()
	ip := clientIP(r)
	rows := legalAcceptances(userID, now, ip, current)
	if req.PrivacyAcknowledgement {
		// S-23: an acknowledgement of a new Privacy Policy is not an acceptance of the Terms. It is
		// only valid for a user whose latest accepted Terms already is the served one; anyone else has
		// to go through the full acceptance, so the same 409 sends the page back to it.
		accepted, err := acct.store.LatestAcceptances(userID)
		if err != nil {
			log.Printf("accept-terms read acceptances %s: %v", userID, err)
			writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
			return
		}
		if accepted[models.LegalDocumentTerms] != current.Terms {
			writeLegalVersionMismatch(w, current)
			return
		}
		rows = privacyOnly(rows)
	}
	if err := acct.store.RecordAcceptances(rows); err != nil {
		log.Printf("record legal acceptance for %s: %v", userID, err)
		writeJSONError(w, http.StatusServiceUnavailable, "service unavailable")
		return
	}

	// Re-issue: the token and the readable cookie have to carry the version this
	// request just recorded, or the proxy gate would send the caller straight
	// back here on the next navigation.
	m, err := acct.resolveMembership(r.Context(), user)
	if err != nil {
		log.Printf("resolve active organization for %s: %v", userID, err)
		writeJSONError(w, http.StatusForbidden, "no workspace available for this account")
		return
	}
	writeSession(w, user, m)
}
