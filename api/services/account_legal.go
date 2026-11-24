package services

import (
	"errors"
	"fmt"
	"strings"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
)

// Legal acceptances (C10). Signup and invite-signup write the first rows inside
// their own transaction (GormAccountStore.RegisterUser/RegisterInvitedUser);
// these are the reads and the later writes: what a session has accepted (so the
// readable cookie can carry it) and the re-acceptance the /accept-terms page
// posts for an already-signed-in user, which is the endpoint C7-UI's KNOWN GAP
// was waiting for.

// LegalVersions are the versions of the two legal documents this deployment
// currently serves, as the server itself states them.
type LegalVersions struct {
	Terms   string
	Privacy string
}

// ErrLegalDocumentsUnavailable means the deployment cannot state which version
// of a legal document it is serving: legal_documents has no row for it, or the
// read failed. It is deliberately an error rather than an empty default — a
// caller that cannot learn the server's version must refuse to record an
// acceptance (503) rather than invent one, because the whole point of the row is
// that the version in it is a fact the server established.
var ErrLegalDocumentsUnavailable = errors.New("the legal documents this deployment serves are not configured")

// CurrentLegalVersions reads the versions the deployment currently serves from
// legal_documents. It is read on every acceptance rather than cached: the table
// is two rows read by primary key, and a publication has to take effect on the
// very next signup, not after a TTL.
//
// The table is Inference-API-owned; this is the same read-by-SQL arrangement the
// Go API uses for organizations, memberships and platform_settings.
func (GormAccountStore) CurrentLegalVersions() (LegalVersions, error) {
	var rows []models.LegalDocumentVersion
	if err := database.DB.Find(&rows).Error; err != nil {
		return LegalVersions{}, fmt.Errorf("read legal documents: %w", err)
	}
	out := LegalVersions{}
	for _, r := range rows {
		switch r.Document {
		case models.LegalDocumentTerms:
			out.Terms = strings.TrimSpace(r.Version)
		case models.LegalDocumentPrivacy:
			out.Privacy = strings.TrimSpace(r.Version)
		}
	}
	if out.Terms == "" || out.Privacy == "" {
		return LegalVersions{}, ErrLegalDocumentsUnavailable
	}
	return out, nil
}

// LatestAcceptances returns document -> version for the newest acceptance of
// each document, newest by accepted_at (ties broken by insertion id order, so
// two rows written in the same millisecond still answer deterministically).
func (GormAccountStore) LatestAcceptances(userID uuid.UUID) (map[string]string, error) {
	var rows []models.LegalAcceptance
	if err := database.DB.
		Where("user_id = ?", userID).
		Order("accepted_at DESC, id DESC").
		Find(&rows).Error; err != nil {
		return nil, fmt.Errorf("read legal acceptances: %w", err)
	}
	out := make(map[string]string, len(rows))
	for _, r := range rows {
		if _, seen := out[r.Document]; !seen {
			out[r.Document] = r.Version
		}
	}
	return out, nil
}

// LatestTermsVersion is the version of the Terms the user has accepted, or ""
// when they have accepted none. It is what the session cookies and the JWT
// carry: an empty value means "never accepted", which web/proxy.ts treats as a
// session that predates the gate and the OAuth callback treats as a first login.
func LatestTermsVersion(userID uuid.UUID) (string, error) {
	versions, err := GormAccountStore{}.LatestAcceptances(userID)
	if err != nil {
		return "", err
	}
	return versions[models.LegalDocumentTerms], nil
}

// LatestPrivacyVersion is LatestTermsVersion for the Privacy Policy: the
// version the user has ACCEPTED (never the one currently served), or "" when
// they have accepted none. web/proxy.ts compares it with the served version to
// send a user whose acceptance is stale back through accept-terms.
func LatestPrivacyVersion(userID uuid.UUID) (string, error) {
	versions, err := GormAccountStore{}.LatestAcceptances(userID)
	if err != nil {
		return "", err
	}
	return versions[models.LegalDocumentPrivacy], nil
}

// RecordAcceptances inserts the acceptances whose document is not already
// recorded at that version. The read and the insert share one transaction so
// two concurrent posts of the same form cannot both see "not recorded".
func (GormAccountStore) RecordAcceptances(acceptances []models.LegalAcceptance) error {
	if len(acceptances) == 0 {
		return nil
	}
	userID := acceptances[0].UserID
	return database.DB.Transaction(func(tx *gorm.DB) error {
		var rows []models.LegalAcceptance
		if err := tx.Where("user_id = ?", userID).
			Order("accepted_at DESC, id DESC").
			Find(&rows).Error; err != nil {
			return fmt.Errorf("read legal acceptances: %w", err)
		}
		current := make(map[string]string, len(rows))
		for _, r := range rows {
			if _, seen := current[r.Document]; !seen {
				current[r.Document] = r.Version
			}
		}
		for i := range acceptances {
			if current[acceptances[i].Document] == acceptances[i].Version {
				continue
			}
			if err := tx.Create(&acceptances[i]).Error; err != nil {
				return fmt.Errorf("record legal acceptance: %w", err)
			}
		}
		return nil
	})
}
