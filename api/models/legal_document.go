package models

import "time"

// LegalDocumentVersion is the version of one legal document this deployment
// currently serves (F-7). The table is owned and migrated by the Inference API
// (`legal_documents`, migration AddLegalDocuments); the Go API reads it and
// never migrates it, exactly like organizations and platform_settings.
//
// It exists so that a recorded acceptance names a version the SERVER published
// rather than one the caller asserted: signup and accept-terms used to take the
// version from the request body, which made legal_acceptances evidence of
// nothing. The current version, when it became current, and who published it all
// live here; the acceptances are the history of who accepted what, and when.
type LegalDocumentVersion struct {
	// Document is the document's name ("Terms" or "Privacy"), stored by name to
	// match the Inference API's LegalDocument enum. It is the primary key.
	Document string `gorm:"column:document;primaryKey"`
	// Version is the current version label of that document.
	Version string `gorm:"column:version"`
	// EffectiveAt is when this version became the current one, which is the
	// instant acceptances of the previous version stopped being current.
	EffectiveAt time.Time `gorm:"column:effective_at"`
	// UpdatedAt is when the row was last written.
	UpdatedAt time.Time `gorm:"column:updated_at"`
	// UpdatedBy names who last wrote it: "config" for the first-boot seed,
	// otherwise the platform admin's email.
	UpdatedBy *string `gorm:"column:updated_by"`
}

func (LegalDocumentVersion) TableName() string {
	return "legal_documents"
}
