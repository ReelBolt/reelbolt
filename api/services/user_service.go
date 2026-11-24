package services

import (
	"crypto/rand"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"math/big"
	"os"
	"path/filepath"
	"time"

	"github.com/google/uuid"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"golang.org/x/crypto/bcrypt"
	"gorm.io/gorm"
)

const otpChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%"

func generateOTP(length int) (string, error) {
	b := make([]byte, length)
	for i := range b {
		n, err := rand.Int(rand.Reader, big.NewInt(int64(len(otpChars))))
		if err != nil {
			return "", err
		}
		b[i] = otpChars[n.Int64()]
	}
	return string(b), nil
}

func CreateUser(email, displayName string, isAdmin bool, actor AdminActor) (*models.ApplicationUser, string, error) {
	otp, err := generateOTP(16)
	if err != nil {
		return nil, "", fmt.Errorf("generate OTP: %w", err)
	}

	hash, err := bcrypt.GenerateFromPassword([]byte(otp), bcrypt.DefaultCost)
	if err != nil {
		return nil, "", fmt.Errorf("hash password: %w", err)
	}

	// The admin vouches for the address, so the account starts verified.
	now := time.Now().UTC()
	user := models.ApplicationUser{
		ID:                 uuid.New(),
		Email:              email,
		DisplayName:        displayName,
		CreatedAt:          now,
		PasswordHash:       string(hash),
		IsAdmin:            isAdmin,
		MustChangePassword: true,
		EmailVerifiedAt:    &now,
	}

	// User, Personal org and memberships commit together or not at all.
	err = database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(&user).Error; err != nil {
			return fmt.Errorf("create user: %w", err)
		}
		if err := ProvisionUserTenancy(tx, &user); err != nil {
			return err
		}
		return insertAdminAudit(tx, actor, user.ID, AdminActionUserCreate, map[string]FieldChange{
			"email":        {Old: nil, New: email},
			"display_name": {Old: nil, New: displayName},
			"is_admin":     {Old: nil, New: isAdmin},
		})
	})
	if err != nil {
		return nil, "", err
	}
	// After the transaction: the admin-created account has its Personal workspace.
	recordPersonalWorkspaceCreatedBestEffort(&user)
	return &user, otp, nil
}

func GetUserByID(id uuid.UUID) (*models.ApplicationUser, error) {
	var user models.ApplicationUser
	if err := database.DB.First(&user, "id = ? AND is_service_account = ?", id, false).Error; err != nil {
		return nil, err
	}
	return &user, nil
}

func GetUserByEmail(email string) (*models.ApplicationUser, error) {
	var user models.ApplicationUser
	if err := database.DB.First(&user, "email = ? AND is_service_account = ?", email, false).Error; err != nil {
		return nil, err
	}
	return &user, nil
}

func ListUsers() ([]models.ApplicationUser, error) {
	var users []models.ApplicationUser
	if err := database.DB.Where("is_service_account = ?", false).Find(&users).Error; err != nil {
		return nil, err
	}
	return users, nil
}

// AdminActor identifies who made a platform-admin write and from where, for admin_audit_events.
type AdminActor struct {
	UserID    *uuid.UUID
	ClientIP  string
	UserAgent string
}

// Admin audit actions.
const (
	AdminActionUserCreate        = "user.create"
	AdminActionUserUpdate        = "user.update"
	AdminActionUserDelete        = "user.delete"
	AdminActionUserResetPassword = "user.reset_password"

	AdminActionUserEmailChangeRequest = "user.email_change.request"
	AdminActionUserEmailChangeConfirm = "user.email_change.confirm"
	AdminActionUserEmailChangeCancel  = "user.email_change.cancel"
)

// FieldChange is one old/new pair in admin_audit_events.changed_fields.
type FieldChange struct {
	Old any `json:"old"`
	New any `json:"new"`
}

func truncateBytes(s string, n int) string {
	if len(s) > n {
		return s[:n]
	}
	return s
}

// insertAdminAudit appends one admin_audit_events row inside tx. changed must never carry a
// secret (password, hash, token). An error here must abort the admin write.
func insertAdminAudit(tx *gorm.DB, actor AdminActor, target uuid.UUID, action string, changed map[string]FieldChange) error {
	if len(changed) == 0 {
		return insertAdminAuditPayload(tx, actor, target, action, nil)
	}
	return insertAdminAuditPayload(tx, actor, target, action, changed)
}

// insertAdminAuditPayload is insertAdminAudit with any JSON-able changed_fields payload (nil for
// none), for rows whose detail is not an old/new pair (an email-change cancel records {via}).
func insertAdminAuditPayload(tx *gorm.DB, actor AdminActor, target uuid.UUID, action string, payload any) error {
	var cf *string
	if payload != nil {
		b, err := json.Marshal(payload)
		if err != nil {
			return fmt.Errorf("audit changed_fields: %w", err)
		}
		s := string(b)
		cf = &s
	}
	var ip *string
	if actor.ClientIP != "" {
		v := truncateBytes(actor.ClientIP, 64)
		ip = &v
	}
	var ua *string
	if actor.UserAgent != "" {
		v := truncateBytes(actor.UserAgent, 512)
		ua = &v
	}
	err := tx.Exec(`INSERT INTO admin_audit_events (actor_user_id, target_user_id, action, changed_fields, client_ip, user_agent)
VALUES (?, ?, ?, CAST(? AS jsonb), ?, ?)`, actor.UserID, target, action, cf, ip, ua).Error
	if err != nil {
		return fmt.Errorf("admin audit insert: %w", err)
	}
	return nil
}

// ErrEmailChangeRequiresUser: an admin never changes a user's email; only the user can.
var ErrEmailChangeRequiresUser = errors.New("email can only be changed by the user")

// UpdateUserResult reports what an admin update did beyond the row itself.
type UpdateUserResult struct {
	User *models.ApplicationUser
	// SessionsEnded is true when is_admin changed, so tokens_invalidated_at was set.
	SessionsEnded bool
}

// UpdateUser applies an admin edit (display_name, is_admin). An email different from the stored
// one is refused with ErrEmailChangeRequiresUser before anything is written: an admin-chosen
// address plus the public verify (which signs in) or forgot-password flow would take the account
// over. An unchanged email is accepted (the admin form always sends it). An is_admin change ends
// the user's sessions (tokens_invalidated_at). One audit row is written in the same transaction.
func UpdateUser(id uuid.UUID, fields map[string]interface{}, actor AdminActor) (*UpdateUserResult, error) {
	var user models.ApplicationUser
	if err := database.DB.First(&user, "id = ? AND is_service_account = ?", id, false).Error; err != nil {
		return nil, err
	}
	if v, ok := fields["email"].(string); ok && v != user.Email {
		return nil, ErrEmailChangeRequiresUser
	}
	changed := map[string]FieldChange{}
	updates := map[string]interface{}{}
	if v, ok := fields["display_name"].(string); ok && v != user.DisplayName {
		changed["display_name"] = FieldChange{Old: user.DisplayName, New: v}
		updates["display_name"] = v
	}
	adminChanged := false
	if v, ok := fields["is_admin"].(bool); ok && v != user.IsAdmin {
		changed["is_admin"] = FieldChange{Old: user.IsAdmin, New: v}
		updates["is_admin"] = v
		adminChanged = true
	}
	res := &UpdateUserResult{User: &user}
	if len(updates) == 0 {
		return res, nil
	}
	if adminChanged {
		updates["tokens_invalidated_at"] = time.Now().UTC()
		res.SessionsEnded = true
	}
	err := database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Model(&user).Updates(updates).Error; err != nil {
			return err
		}
		// is_admin is platform authority; mirror it in the Platform org.
		if adminChanged {
			if err := SyncPlatformMembership(tx, id, user.IsAdmin); err != nil {
				return err
			}
		}
		return insertAdminAudit(tx, actor, id, AdminActionUserUpdate, changed)
	})
	if err != nil {
		return nil, err
	}
	return res, nil
}

// DeleteUser removes the user and their Personal workspace. The schema only
// SET NULLs organizations.created_by_user_id, so without the explicit cleanup
// the Personal org (and its custom agents, threads, providers) would be orphaned.
func DeleteUser(id uuid.UUID, actor AdminActor) error {
	return database.DB.Transaction(func(tx *gorm.DB) error {
		// A service account is removed with its key's org, never through the user routes.
		if svc, err := isServiceAccountUser(tx, id); err != nil {
			return err
		} else if svc {
			return gorm.ErrRecordNotFound
		}
		var personalOrgIDs []uuid.UUID
		if err := tx.Model(&models.Organization{}).
			Where("kind = ? AND created_by_user_id = ?", models.OrgKindPersonal, id).
			Pluck("id", &personalOrgIDs).Error; err != nil {
			return err
		}
		if err := refuseIfProjectsLocked(tx, id, personalOrgIDs); err != nil {
			return err
		}
		if err := tx.Delete(&models.ApplicationUser{}, "id = ?", id).Error; err != nil {
			return err
		}
		// No FK on admin_audit_events, so the row outlives the user it names.
		if err := insertAdminAudit(tx, actor, id, AdminActionUserDelete, nil); err != nil {
			return err
		}
		if len(personalOrgIDs) == 0 {
			return nil
		}
		// agent_definitions.organization_id is RESTRICT; remove the user's custom agents first.
		if err := tx.Exec("DELETE FROM agent_definitions WHERE organization_id IN ? AND NOT is_built_in", personalOrgIDs).Error; err != nil {
			return err
		}
		return tx.Where("id IN ?", personalOrgIDs).Delete(&models.Organization{}).Error
	})
}

// ErrAccountUnavailable is returned when the account cannot be removed right now.
var ErrAccountUnavailable = errors.New("account unavailable")

// refuseIfProjectsLocked refuses the delete while any project it would remove carries the
// output marker or a file row flagged by the pipeline. A failed read refuses as well.
// The columns live in the Inference-owned schema, hence raw SQL.
func refuseIfProjectsLocked(tx *gorm.DB, userID uuid.UUID, orgIDs []uuid.UUID) error {
	if orgIDs == nil {
		orgIDs = []uuid.UUID{}
	}
	var n int64
	err := tx.Raw(`SELECT count(*) FROM projects p
WHERE (p.owner_id = ? OR p.organization_id IN ?)
  AND (p.csam_output_hold_at IS NOT NULL
    OR EXISTS (SELECT 1 FROM project_files f WHERE f.project_id = p.id AND f.csam_status = 'Quarantined'))`,
		userID, orgIDs).Scan(&n).Error
	if err != nil || n > 0 {
		return ErrAccountUnavailable
	}
	return nil
}

func ValidatePassword(user *models.ApplicationUser, password string) bool {
	return bcrypt.CompareHashAndPassword([]byte(user.PasswordHash), []byte(password)) == nil
}

func ChangePassword(userID uuid.UUID, newPassword string) error {
	hash, err := bcrypt.GenerateFromPassword([]byte(newPassword), bcrypt.DefaultCost)
	if err != nil {
		return fmt.Errorf("hash password: %w", err)
	}
	return database.DB.Model(&models.ApplicationUser{}).
		Where("id = ?", userID).
		Updates(map[string]interface{}{
			"password_hash":         string(hash),
			"must_change_password":  false,
			"tokens_invalidated_at": time.Now().UTC(),
		}).Error
}

// SetMcpAdminToolsEnabled writes the per-user MCP admin-tools opt-in
// (docs/design/mcp-oauth.md §11).
//
// It touches exactly one column and nothing else. In particular it does NOT set
// tokens_invalidated_at: the setting is read live by the MCP bridge on every call and is only
// mirrored into a token's `scope` at consent time, so changing it must not sign the user out or
// rewrite a credential they already hold (§11: "changing it does not retroactively alter tokens
// already issued").
func SetMcpAdminToolsEnabled(userID uuid.UUID, enabled bool) error {
	return database.DB.Model(&models.ApplicationUser{}).
		Where("id = ?", userID).
		Updates(map[string]interface{}{
			"mcp_admin_tools_enabled": enabled,
		}).Error
}

// SetMcpMediaEgressEnabled writes the per-user opt-in that lets connected tools (MCP) allow sending
// the user's media to AI services for a run. One column, nothing else: the Inference API reads it
// live on every call, so changing it neither signs the user out nor rewrites a token.
func SetMcpMediaEgressEnabled(userID uuid.UUID, enabled bool) error {
	return database.DB.Model(&models.ApplicationUser{}).
		Where("id = ?", userID).
		Updates(map[string]interface{}{
			"mcp_media_egress_enabled": enabled,
		}).Error
}

// SetDisplayName writes the user's display name (already validated by the caller). Only that
// column is touched; tokens_invalidated_at is left alone so the user stays signed in.
func SetDisplayName(userID uuid.UUID, name string) error {
	return database.DB.Model(&models.ApplicationUser{}).
		Where("id = ?", userID).
		Updates(map[string]interface{}{"display_name": name}).Error
}

// SetLocale stores the user's language; nil clears it (default). The caller validates the code.
func SetLocale(userID uuid.UUID, locale *string) error {
	return database.DB.Model(&models.ApplicationUser{}).
		Where("id = ?", userID).
		Updates(map[string]interface{}{"locale": locale}).Error
}

// ErrResetPreconditionChanged means the reset's guarded UPDATE matched no row: the address
// changed or became unverified after the caller checked it.
var ErrResetPreconditionChanged = errors.New("reset precondition changed")

// ResetPassword sets a one-time password (must change on next sign-in) and ends the user's
// sessions, only if the user's address is still verifiedEmail (the address the caller checked and
// will mail). Exactly one row must match, else ErrResetPreconditionChanged. The audit row (never
// the password) is written in the same transaction, only when the update matched.
func ResetPassword(id uuid.UUID, verifiedEmail string, actor AdminActor) (string, error) {
	otp, err := generateOTP(16)
	if err != nil {
		return "", fmt.Errorf("generate OTP: %w", err)
	}

	hash, err := bcrypt.GenerateFromPassword([]byte(otp), bcrypt.DefaultCost)
	if err != nil {
		return "", fmt.Errorf("hash password: %w", err)
	}

	err = database.DB.Transaction(func(tx *gorm.DB) error {
		res := tx.Model(&models.ApplicationUser{}).
			Where("id = ? AND is_service_account = ? AND email = ? AND email_verified_at IS NOT NULL", id, false, verifiedEmail).
			Updates(map[string]interface{}{
				"password_hash":         string(hash),
				"must_change_password":  true,
				"tokens_invalidated_at": time.Now().UTC(),
			})
		if res.Error != nil {
			return res.Error
		}
		if res.RowsAffected != 1 {
			return ErrResetPreconditionChanged
		}
		return insertAdminAudit(tx, actor, id, AdminActionUserResetPassword, nil)
	})
	if err != nil {
		return "", err
	}

	return otp, nil
}

func EnsureAdminExists() {
	var count int64
	// A failed count must stop startup: treating it as zero would seed a second admin.
	if err := database.DB.Model(&models.ApplicationUser{}).Where("is_service_account = ?", false).Count(&count).Error; err != nil {
		log.Fatalf("Failed to count users before admin bootstrap: %v", err)
	}
	if count > 0 {
		return
	}

	email := config.Cfg.AdminEmail
	password := config.Cfg.AdminPassword
	generated := false
	passwordFile := ""

	if password == "" {
		otp, err := generateOTP(16)
		if err != nil {
			log.Fatalf("Failed to generate admin password: %v", err)
		}
		password = otp
		generated = true
		// Written before the admin exists, so a failure leaves no admin with an unknowable password.
		passwordFile = bootstrapPasswordFile()
		if err := writeBootstrapPassword(passwordFile, password); err != nil {
			log.Fatalf("Failed to write the generated admin password to %s: %v", passwordFile, err)
		}
	}

	hash, err := bcrypt.GenerateFromPassword([]byte(password), bcrypt.DefaultCost)
	if err != nil {
		log.Fatalf("Failed to hash admin password: %v", err)
	}

	adminNow := time.Now().UTC()
	admin := models.ApplicationUser{
		ID:                 uuid.New(),
		Email:              email,
		DisplayName:        "Admin",
		CreatedAt:          adminNow,
		EmailVerifiedAt:    &adminNow,
		PasswordHash:       string(hash),
		IsAdmin:            true,
		MustChangePassword: true,
	}

	// IsAdmin makes ProvisionUserTenancy add the Platform Owner membership too.
	err = database.DB.Transaction(func(tx *gorm.DB) error {
		if err := tx.Create(&admin).Error; err != nil {
			return err
		}
		return ProvisionUserTenancy(tx, &admin)
	})
	if err != nil {
		if passwordFile != "" {
			_ = os.Remove(passwordFile)
		}
		log.Fatalf("Failed to create admin user: %v", err)
	}
	// After the transaction: the bootstrap admin's Personal workspace exists. A lost funnel row
	// here is only a lost metric; startup carries on.
	recordPersonalWorkspaceCreatedBestEffort(&admin)

	logAdminBootstrap(email, passwordFile, generated, admin.MustChangePassword)
}

// defaultBootstrapPasswordFile is where a generated bootstrap admin password lands when
// ADMIN_PASSWORD_FILE is unset: on the go-api-secrets named volume (docker-compose.yml), so it
// survives a container recreate; read it with `docker compose exec go-api cat <path>`.
const defaultBootstrapPasswordFile = "/var/lib/reelbolt/bootstrap-admin-password"

func bootstrapPasswordFile() string {
	if p := os.Getenv("ADMIN_PASSWORD_FILE"); p != "" {
		return p
	}
	return defaultBootstrapPasswordFile
}

// writeBootstrapPassword writes the generated password to a fresh 0600 file at path, creating
// the parent directory 0700 if needed. A symlink or non-regular file at path is refused (never
// followed); a stale regular file is removed first, then the file is created with O_EXCL so
// nothing can be swapped in between.
func writeBootstrapPassword(path, password string) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	if info, err := os.Lstat(path); err == nil {
		if !info.Mode().IsRegular() {
			return fmt.Errorf("refusing to write the admin password: %s is not a regular file", path)
		}
		if err := os.Remove(path); err != nil {
			return err
		}
	} else if !errors.Is(err, os.ErrNotExist) {
		return err
	}
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return err
	}
	if err := f.Chmod(0o600); err != nil { // umask-proof
		_ = f.Close()
		return err
	}
	if _, err := f.WriteString(password + "\n"); err != nil {
		_ = f.Close()
		return err
	}
	return f.Close()
}

// logAdminBootstrap announces the bootstrap admin. No password is ever logged: a generated one is
// in passwordFile (named here), and an operator-supplied ADMIN_PASSWORD is already known.
func logAdminBootstrap(email, passwordFile string, generated, mustChange bool) {
	log.Printf("=== Initial admin user created ===")
	log.Printf("Email:    %s", email)
	if generated {
		log.Printf("Password: generated; written to %s (mode 0600). Read it, sign in, then delete the file.", passwordFile)
	} else {
		log.Printf("Password: set from ADMIN_PASSWORD (not logged)")
	}
	if mustChange {
		log.Printf("(Password change required on first login)")
	}
	log.Printf("==================================")
}
