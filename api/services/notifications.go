package services

import (
	"context"
	"errors"
	"log"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/vecchiotom/reelbolt/config"
	"github.com/vecchiotom/reelbolt/database"
	"github.com/vecchiotom/reelbolt/models"
	"gorm.io/gorm"
	"gorm.io/gorm/clause"
)

// NotificationPreferences is one user's four switches. A user with no stored
// row runs on the defaults: everything on.
type NotificationPreferences struct {
	JobSucceeded     bool `json:"jobSucceeded"`
	JobFailed        bool `json:"jobFailed"`
	FileCreated      bool `json:"fileCreated"`
	GraphicsEdited   bool `json:"graphicsEdited"`
	ReviewActivity   bool `json:"reviewActivity"`
	AvatarRetention  bool `json:"avatarRetention"`
	SocialPublishing bool `json:"socialPublishing"`
}

// DefaultNotificationPreferences is what an absent preferences row means.
func DefaultNotificationPreferences() NotificationPreferences {
	return NotificationPreferences{JobSucceeded: true, JobFailed: true, FileCreated: true, GraphicsEdited: true, ReviewActivity: true, AvatarRetention: true, SocialPublishing: true}
}

// Enabled reports whether the category's switch is on. An unknown category is
// off: a misspelled event type must not silently mail anyone.
func (p NotificationPreferences) Enabled(category string) bool {
	switch category {
	case models.NotificationJobSucceeded:
		return p.JobSucceeded
	case models.NotificationJobFailed:
		return p.JobFailed
	case models.NotificationFileCreated:
		return p.FileCreated
	case models.NotificationGraphicsEdited:
		return p.GraphicsEdited
	case models.NotificationReviewActivity:
		return p.ReviewActivity
	case models.NotificationAvatarRetention:
		return p.AvatarRetention
	case models.NotificationSocialPublishing:
		return p.SocialPublishing
	case models.NotificationAPIKeyExpiring, models.NotificationWebhookDisabled:
		return true
	default:
		return false
	}
}

// NotificationRecord is one row of the per-user delivery log.
type NotificationRecord struct {
	ID             uuid.UUID `json:"id"`
	UserID         uuid.UUID `json:"userId"`
	OrganizationID uuid.UUID `json:"organizationId"`
	Category       string    `json:"category"`
	Title          string    `json:"title"`
	Delivered      bool      `json:"delivered"`
	Error          *string   `json:"error"`
	CreatedAt      time.Time `json:"createdAt"`
}

// UserActivityEvent is the wire shape of the Inference API's
// UserActivityNotificationRequested message. Go's encoding/json matches keys
// case-insensitively, so the MassTransit payload decodes whatever casing the
// .NET serializer emits.
type UserActivityEvent struct {
	OrganizationID string `json:"organizationId"`
	ActorUserID    string `json:"actorUserId"`
	ProjectID      string `json:"projectId"`
	Type           string `json:"type"`
	Subject        string `json:"subject"`
	Details        string `json:"details"`
	// RecipientUserID is set on review_activity only: the single user the mail
	// goes to (the review link's creator).
	RecipientUserID *string `json:"recipientUserId"`
}

// NotificationStore persists the switches and the delivery log and resolves the
// people a notification goes to.
type NotificationStore interface {
	GetPreferences(ctx context.Context, userID uuid.UUID) (NotificationPreferences, error)
	SavePreferences(ctx context.Context, userID uuid.UUID, prefs NotificationPreferences) error

	RecordDelivery(ctx context.Context, rec NotificationRecord) error
	ListRecent(ctx context.Context, userID uuid.UUID, limit int) ([]NotificationRecord, error)

	// LookupUser returns the account's mail address and display name.
	LookupUser(ctx context.Context, userID uuid.UUID) (email, displayName string, ok bool, err error)
	OrgName(ctx context.Context, orgID uuid.UUID) (string, error)
	// OrgMembers returns every non-deleted member of the org, including the Owner.
	OrgMembers(ctx context.Context, orgID uuid.UUID) ([]uuid.UUID, error)
	// OrgOwner is the fallback recipient for a job nobody recorded starting.
	OrgOwner(ctx context.Context, orgID uuid.UUID) (uuid.UUID, error)
	WorkflowExecution(ctx context.Context, id uuid.UUID) (*models.WorkflowExecution, error)
	ProjectName(ctx context.Context, id uuid.UUID) (string, error)
}

// GormNotificationStore is the production NotificationStore over database.DB.
// The two notification tables' schema is owned by the Inference API (EF Core,
// same convention as the tenancy tables); this store reads and writes them and
// never migrates.
type GormNotificationStore struct{}

func (GormNotificationStore) GetPreferences(ctx context.Context, userID uuid.UUID) (NotificationPreferences, error) {
	var row models.UserNotificationPreferences
	err := database.DB.WithContext(ctx).Where("user_id = ?", userID).First(&row).Error
	if err != nil {
		// An absent row AND an absent table both mean "no stored choice yet":
		// the table is created by the Inference API's EF migration, so a
		// deployment where the Go API is running before that migration has
		// applied reads the defaults instead of failing the settings page.
		if errorsIsNotFound(err) || errorsIsMissingTable(err) {
			return DefaultNotificationPreferences(), nil
		}
		return NotificationPreferences{}, err
	}
	return NotificationPreferences{
		JobSucceeded:     row.JobSucceeded,
		JobFailed:        row.JobFailed,
		FileCreated:      row.FileCreated,
		GraphicsEdited:   row.GraphicsEdited,
		ReviewActivity:   row.ReviewActivity,
		AvatarRetention:  row.AvatarRetention,
		SocialPublishing: row.SocialPublishing,
	}, nil
}

func (GormNotificationStore) SavePreferences(ctx context.Context, userID uuid.UUID, prefs NotificationPreferences) error {
	now := time.Now().UTC()
	row := models.UserNotificationPreferences{
		UserID:           userID,
		JobSucceeded:     prefs.JobSucceeded,
		JobFailed:        prefs.JobFailed,
		FileCreated:      prefs.FileCreated,
		GraphicsEdited:   prefs.GraphicsEdited,
		ReviewActivity:   prefs.ReviewActivity,
		AvatarRetention:  prefs.AvatarRetention,
		SocialPublishing: prefs.SocialPublishing,
		CreatedAt:        now,
		UpdatedAt:        now,
	}
	// Upsert: the first write creates the row, later ones update only the four
	// switches and updated_at so created_at is preserved. The caller always
	// sends the fully-merged struct (the handler folds the partial patch over the
	// stored row first), so a plain column list is safe here.
	return database.DB.WithContext(ctx).
		Clauses(clause.OnConflict{
			Columns:   []clause.Column{{Name: "user_id"}},
			DoUpdates: clause.AssignmentColumns([]string{"job_succeeded", "job_failed", "file_created", "graphics_edited", "review_activity", "avatar_retention", "social_publishing", "updated_at"}),
		}).
		Create(&row).Error
}

func (GormNotificationStore) RecordDelivery(ctx context.Context, rec NotificationRecord) error {
	if rec.ID == uuid.Nil {
		rec.ID = uuid.New()
	}
	if rec.CreatedAt.IsZero() {
		rec.CreatedAt = time.Now().UTC()
	}
	err := database.DB.WithContext(ctx).Create(&models.UserNotification{
		ID:             rec.ID,
		UserID:         rec.UserID,
		OrganizationID: rec.OrganizationID,
		Category:       rec.Category,
		Title:          rec.Title,
		Delivered:      rec.Delivered,
		Error:          rec.Error,
		CreatedAt:      rec.CreatedAt,
	}).Error
	if err != nil {
		// The delivery log is a side record: if the table does not exist yet
		// (Inference's migration has not run), the mail is still out — the
		// record is skipped with a log line, not turned into an error.
		if errorsIsMissingTable(err) {
			log.Printf("notification: delivery log table not present yet (Inference API migration pending); skipped recording %s to %s", rec.Category, rec.UserID)
			return nil
		}
		return err
	}
	return nil
}

func (GormNotificationStore) ListRecent(ctx context.Context, userID uuid.UUID, limit int) ([]NotificationRecord, error) {
	if limit <= 0 || limit > 100 {
		limit = 30
	}
	var rows []models.UserNotification
	if err := database.DB.WithContext(ctx).
		Where("user_id = ?", userID).
		Order("created_at DESC, id DESC").
		Limit(limit).
		Find(&rows).Error; err != nil {
		// An absent table is "no deliveries recorded yet", the same state as
		// an empty log: the recent-activity list renders empty.
		if errorsIsMissingTable(err) {
			return []NotificationRecord{}, nil
		}
		return nil, err
	}
	out := make([]NotificationRecord, len(rows))
	for i, r := range rows {
		out[i] = NotificationRecord{
			ID: r.ID, UserID: r.UserID, OrganizationID: r.OrganizationID, Category: r.Category,
			Title: r.Title, Delivered: r.Delivered, Error: r.Error, CreatedAt: r.CreatedAt,
		}
	}
	return out, nil
}

func (GormNotificationStore) LookupUser(ctx context.Context, userID uuid.UUID) (string, string, bool, error) {
	var u models.ApplicationUser
	if err := database.DB.WithContext(ctx).Where("id = ?", userID).First(&u).Error; err != nil {
		if errorsIsNotFound(err) {
			return "", "", false, nil
		}
		return "", "", false, err
	}
	if u.IsServiceAccount {
		// Second line of defence: an API-key service account is never mailed.
		return "", "", false, nil
	}
	return u.Email, u.DisplayName, true, nil
}

func (GormNotificationStore) OrgName(ctx context.Context, orgID uuid.UUID) (string, error) {
	var o models.Organization
	if err := database.DB.WithContext(ctx).
		Where("id = ? AND deleted_at IS NULL", orgID).First(&o).Error; err != nil {
		if errorsIsNotFound(err) {
			return "", nil
		}
		return "", err
	}
	return o.Name, nil
}

func (GormNotificationStore) OrgMembers(ctx context.Context, orgID uuid.UUID) ([]uuid.UUID, error) {
	var rows []models.OrganizationMembership
	if err := database.DB.WithContext(ctx).
		Joins("JOIN application_users u ON u.id = organization_memberships.user_id").
		Where("organization_memberships.organization_id = ? AND u.is_service_account = ?", orgID, false).
		Find(&rows).Error; err != nil {
		return nil, err
	}
	out := make([]uuid.UUID, 0, len(rows))
	for _, r := range rows {
		out = append(out, r.UserID)
	}
	return out, nil
}

func (GormNotificationStore) OrgOwner(ctx context.Context, orgID uuid.UUID) (uuid.UUID, error) {
	var m models.OrganizationMembership
	// A service account is not a person and must never be mailed an owner notification.
	err := database.DB.WithContext(ctx).
		Joins("JOIN application_users u ON u.id = organization_memberships.user_id").
		Where("organization_memberships.organization_id = ? AND organization_memberships.role = ? AND u.is_service_account = ?", orgID, models.OrgRoleOwner, false).
		First(&m).Error
	if err != nil {
		if errorsIsNotFound(err) {
			return uuid.Nil, nil
		}
		return uuid.Nil, err
	}
	return m.UserID, nil
}

func (GormNotificationStore) WorkflowExecution(ctx context.Context, id uuid.UUID) (*models.WorkflowExecution, error) {
	var e models.WorkflowExecution
	if err := database.DB.WithContext(ctx).Where("id = ?", id).First(&e).Error; err != nil {
		if errorsIsNotFound(err) {
			return nil, nil
		}
		return nil, err
	}
	return &e, nil
}

func (GormNotificationStore) ProjectName(ctx context.Context, id uuid.UUID) (string, error) {
	var p models.Project
	if err := database.DB.WithContext(ctx).Where("id = ?", id).First(&p).Error; err != nil {
		if errorsIsNotFound(err) {
			return "", nil
		}
		return "", err
	}
	return p.Name, nil
}

// errorsIsNotFound folds GORM's not-found sentinel into one check so a missing
// row is a clean "absent", not an error.
func errorsIsNotFound(err error) bool {
	return errors.Is(err, gorm.ErrRecordNotFound)
}

// errorsIsMissingTable recognizes Postgres's undefined_table error (42P01).
// The notification tables' schema is owned by the Inference API's EF migration,
// so a Go API that is running before that migration has applied gets 42P01 on
// its first query; the reads degrade to "absent" (defaults / empty) and the
// delivery-log write is skipped with a log line instead of all of them
// surfacing as 503s. SavePreferences deliberately does NOT use this check: a
// switch change that cannot be persisted must fail so the page rolls it back,
// not pretend it saved.
func errorsIsMissingTable(err error) bool {
	var pe *pgconn.PgError
	return errors.As(err, &pe) && pe.Code == "42P01"
}

// NotificationDispatcher delivers the mail a notification event calls for.
type NotificationDispatcher interface {
	// HandleWorkflowEvent acts on one completed/failed run. It never fails the
	// caller: a notification that cannot be delivered is logged, and the run's
	// own state is never touched by it.
	HandleWorkflowEvent(ctx context.Context, eventType string, executionID uuid.UUID)
	// HandleActivityEvent acts on one file-created / graphics-edited message.
	HandleActivityEvent(ctx context.Context, evt UserActivityEvent)
}

// NotificationDispatcherImpl is the production dispatcher.
type NotificationDispatcherImpl struct {
	store  NotificationStore
	send   func(to string, tmpl TemplateName, data MailData) error
	base   func() string
	record func(ctx context.Context, rec NotificationRecord)
}

// NewNotificationDispatcher builds the process-wide dispatcher over the real
// store and the real mailer.
func NewNotificationDispatcher() *NotificationDispatcherImpl {
	return &NotificationDispatcherImpl{
		store: GormNotificationStore{},
		send: func(to string, tmpl TemplateName, data MailData) error {
			return DefaultMailer().SendTemplate(to, tmpl, data)
		},
		base: func() string { return config.Cfg.LinkBaseURL },
		record: func(ctx context.Context, rec NotificationRecord) {
			store := GormNotificationStore{}
			if err := store.RecordDelivery(ctx, rec); err != nil {
				log.Printf("notification log write failed (user %s, %s): %v", rec.UserID, rec.Category, err)
			}
		},
	}
}

// Notifications is the process-wide dispatcher the RabbitMQ consumer calls.
var Notifications NotificationDispatcher = NewNotificationDispatcher()

const errorDetailLimit = 300

// truncate keeps an error line to a sentence the mail can carry.
func truncate(s string, limit int) string {
	s = strings.TrimSpace(s)
	if len(s) <= limit {
		return s
	}
	return s[:limit] + "…"
}

// sendToUser resolves one recipient's switch and mail, and records the attempt.
// It returns whether a mail went out.
func (n *NotificationDispatcherImpl) sendToUser(
	ctx context.Context, category string, recipient, orgID, projectID uuid.UUID,
	tmpl TemplateName, title string, extra func() MailData,
) bool {
	prefs, err := n.store.GetPreferences(ctx, recipient)
	if err != nil {
		log.Printf("notification: reading preferences for %s failed: %v", recipient, err)
		return false
	}
	if !prefs.Enabled(category) {
		return false
	}
	email, name, ok, err := n.store.LookupUser(ctx, recipient)
	if err != nil {
		log.Printf("notification: looking up user %s failed: %v", recipient, err)
		return false
	}
	if !ok {
		return false
	}

	data := extra()
	data.Name = name
	data.Link = n.base() + data.Link
	if ll, ok := n.store.(localeLookup); ok {
		data.Locale = ll.UserLocaleByID(ctx, recipient)
	}
	if err := n.send(email, tmpl, data); err != nil {
		errStr := err.Error()
		n.record(ctx, NotificationRecord{
			UserID: recipient, OrganizationID: orgID, Category: category,
			Title: title, Delivered: false, Error: &errStr,
		})
		log.Printf("notification: %s mail to %s failed: %s", category, MailLogRef(email), SanitizeMailError(err))
		return false
	}
	n.record(ctx, NotificationRecord{
		UserID: recipient, OrganizationID: orgID, Category: category,
		Title: title, Delivered: true,
	})
	return true
}

// HandleWorkflowEvent delivers the job-succeeded / job-failed mail for one run.
func (n *NotificationDispatcherImpl) HandleWorkflowEvent(ctx context.Context, eventType string, executionID uuid.UUID) {
	var (
		category string
		tmpl     TemplateName
	)
	switch eventType {
	case "execution.completed":
		category, tmpl = models.NotificationJobSucceeded, TemplateJobSucceeded
	case "execution.failed":
		category, tmpl = models.NotificationJobFailed, TemplateJobFailed
	default:
		return
	}

	exec, err := n.store.WorkflowExecution(ctx, executionID)
	if err != nil {
		log.Printf("notification: execution lookup for %s failed: %v", executionID, err)
		return
	}
	if exec == nil || exec.OrganizationID == uuid.Nil {
		return
	}

	projectName, _ := n.store.ProjectName(ctx, exec.ProjectID)
	orgName, _ := n.store.OrgName(ctx, exec.OrganizationID)

	var recipient uuid.UUID
	if exec.InitiatedByUserID != nil {
		recipient = *exec.InitiatedByUserID
		// A run started with an API key is initiated by that key's service account, which is
		// not a person and has no mailbox. It gets NO job mail; integrations learn the outcome
		// from their webhooks instead. LookupUser reports such an account as absent.
		if _, _, ok, err := n.store.LookupUser(ctx, recipient); err != nil || !ok {
			return
		}
	} else {
		// A run with no recorded initiator (a system trigger) goes to the
		// workspace owner, the person responsible for the workspace.
		owner, err := n.store.OrgOwner(ctx, exec.OrganizationID)
		if err != nil {
			log.Printf("notification: owner lookup for org %s failed: %v", exec.OrganizationID, err)
			return
		}
		recipient = owner
	}
	if recipient == uuid.Nil {
		return
	}

	errorDetail := ""
	if exec.ErrorMessage != nil {
		errorDetail = truncate(*exec.ErrorMessage, errorDetailLimit)
	}

	title := "Video job finished"
	if eventType == "execution.failed" {
		title = "Video job failed"
	}
	if projectName != "" {
		title += ": " + projectName
	}

	n.sendToUser(ctx, category, recipient, exec.OrganizationID, exec.ProjectID, tmpl, title, func() MailData {
		return MailData{
			OrgName:     orgName,
			ProjectName: projectName,
			ErrorDetail: errorDetail,
			Link: "/app/projects/" + exec.ProjectID.String() + "/workflows/" +
				exec.WorkflowDefinitionID.String() + "/executions/" + exec.ID.String(),
		}
	})
}

// HandleActivityEvent delivers the file-created / graphics-edited mail for one
// activity message: every member of the workspace gets it, the actor excluded
// (a system actor with no recorded user reaches everyone).
func (n *NotificationDispatcherImpl) HandleActivityEvent(ctx context.Context, evt UserActivityEvent) {
	var (
		category string
		tmpl     TemplateName
	)
	switch evt.Type {
	case models.NotificationReviewActivity:
		n.handleReviewActivity(ctx, evt)
		return
	case models.NotificationAvatarRetention:
		n.handleAvatarRetention(ctx, evt)
		return
	case models.NotificationSocialPublishing:
		n.handleSocialPublishing(ctx, evt)
		return
	case models.NotificationAPIKeyExpiring, models.NotificationWebhookDisabled:
		n.handleSecurityMail(ctx, evt)
		return
	case models.NotificationFileCreated:
		category, tmpl = models.NotificationFileCreated, TemplateFileCreated
	case models.NotificationGraphicsEdited:
		category, tmpl = models.NotificationGraphicsEdited, TemplateGraphicsEdited
	default:
		return
	}
	orgID, err := uuid.Parse(evt.OrganizationID)
	if err != nil {
		log.Printf("notification: activity event with unparsable organization id %q: %v", evt.OrganizationID, err)
		return
	}
	projectID, err := uuid.Parse(evt.ProjectID)
	if err != nil {
		log.Printf("notification: activity event with unparsable project id %q: %v", evt.ProjectID, err)
		return
	}
	var actor uuid.UUID
	if evt.ActorUserID != "" {
		if a, err := uuid.Parse(evt.ActorUserID); err == nil {
			actor = a
		}
	}

	members, err := n.store.OrgMembers(ctx, orgID)
	if err != nil {
		log.Printf("notification: member lookup for org %s failed: %v", orgID, err)
		return
	}

	projectName, _ := n.store.ProjectName(ctx, projectID)
	orgName, _ := n.store.OrgName(ctx, orgID)
	actorName := ""
	if actor != uuid.Nil {
		if _, name, ok, err := n.store.LookupUser(ctx, actor); err == nil && ok {
			actorName = name
		}
	}

	title := "File added"
	if evt.Type == models.NotificationGraphicsEdited {
		title = "Thumbnail graphic edited"
	}
	if projectName != "" {
		title += " in " + projectName
	}

	for _, member := range members {
		if actor != uuid.Nil && member == actor {
			continue
		}
		n.sendToUser(ctx, category, member, orgID, projectID, tmpl, title, func() MailData {
			d := MailData{
				OrgName:     orgName,
				ProjectName: projectName,
				ActorName:   actorName,
				Link:        "/app/projects/" + projectID.String() + "/files",
			}
			d.FileName = evt.Subject
			if evt.Details != "" {
				d.ErrorDetail = truncate(evt.Details, errorDetailLimit)
			}
			return d
		})
	}
}

// handleReviewActivity mails the single recipient of a review_activity event.
// It skips silently when the recipient is missing or unparsable, is not a member
// of the event's organization, or is the actor.
func (n *NotificationDispatcherImpl) handleReviewActivity(ctx context.Context, evt UserActivityEvent) {
	if evt.RecipientUserID == nil || *evt.RecipientUserID == "" {
		return
	}
	recipient, err := uuid.Parse(*evt.RecipientUserID)
	if err != nil {
		log.Printf("notification: review activity with unparsable recipient id: %v", err)
		return
	}
	orgID, err := uuid.Parse(evt.OrganizationID)
	if err != nil {
		log.Printf("notification: activity event with unparsable organization id %q: %v", evt.OrganizationID, err)
		return
	}
	projectID, err := uuid.Parse(evt.ProjectID)
	if err != nil {
		log.Printf("notification: activity event with unparsable project id %q: %v", evt.ProjectID, err)
		return
	}
	if evt.ActorUserID != "" {
		if a, err := uuid.Parse(evt.ActorUserID); err == nil && a == recipient {
			return
		}
	}
	members, err := n.store.OrgMembers(ctx, orgID)
	if err != nil {
		log.Printf("notification: member lookup for org %s failed: %v", orgID, err)
		return
	}
	isMember := false
	for _, m := range members {
		if m == recipient {
			isMember = true
			break
		}
	}
	if !isMember {
		return
	}
	projectName, _ := n.store.ProjectName(ctx, projectID)
	orgName, _ := n.store.OrgName(ctx, orgID)
	title := "Review activity"
	if evt.Subject != "" {
		title += ": " + evt.Subject
	}
	n.sendToUser(ctx, models.NotificationReviewActivity, recipient, orgID, projectID, TemplateReviewActivity, title, func() MailData {
		return MailData{
			OrgName:     orgName,
			ProjectName: projectName,
			FileName:    evt.Subject,
			ErrorDetail: truncate(evt.Details, errorDetailLimit),
			Link:        "/app/projects/" + projectID.String(),
		}
	})
}

// handleAvatarRetention mails the single recipient of an avatar_retention event:
// the member who attested the avatar's consent, seven days before the avatar is
// deleted by its retention date. Subject is the avatar's display name, Details
// the plain "Deleted on <date>" line. It skips silently when the recipient is
// missing, unparsable or no longer a member of the event's organization. The
// subject's own name is never in the event.
//
// An event with the nil project id is about an item of the workspace avatar
// library (one event per Owner or Admin of the organization): the mail names no
// project and links to the workspace's avatar library page.
func (n *NotificationDispatcherImpl) handleAvatarRetention(ctx context.Context, evt UserActivityEvent) {
	if evt.RecipientUserID == nil || *evt.RecipientUserID == "" {
		return
	}
	recipient, err := uuid.Parse(*evt.RecipientUserID)
	if err != nil {
		log.Printf("notification: avatar retention with unparsable recipient id: %v", err)
		return
	}
	orgID, err := uuid.Parse(evt.OrganizationID)
	if err != nil {
		log.Printf("notification: activity event with unparsable organization id %q: %v", evt.OrganizationID, err)
		return
	}
	projectID, err := uuid.Parse(evt.ProjectID)
	if err != nil {
		log.Printf("notification: activity event with unparsable project id %q: %v", evt.ProjectID, err)
		return
	}
	members, err := n.store.OrgMembers(ctx, orgID)
	if err != nil {
		log.Printf("notification: member lookup for org %s failed: %v", orgID, err)
		return
	}
	isMember := false
	for _, m := range members {
		if m == recipient {
			isMember = true
			break
		}
	}
	if !isMember {
		return
	}
	projectName := ""
	link := "/app/settings/avatar-library"
	if projectID != uuid.Nil {
		projectName, _ = n.store.ProjectName(ctx, projectID)
		link = "/app/projects/" + projectID.String() + "?tab=avatars"
	}
	orgName, _ := n.store.OrgName(ctx, orgID)
	title := "Avatar expires soon"
	if evt.Subject != "" {
		title += ": " + evt.Subject
	}
	n.sendToUser(ctx, models.NotificationAvatarRetention, recipient, orgID, projectID, TemplateAvatarRetention, title, func() MailData {
		return MailData{
			OrgName:     orgName,
			ProjectName: projectName,
			FileName:    evt.Subject,
			ErrorDetail: truncate(evt.Details, errorDetailLimit),
			Link:        link,
		}
	})
}

// handleSocialPublishing mails the single recipient of a social_publishing event:
// the member who made a post that was published or failed, or who connected an
// account that needs reconnecting. Subject is the one-line headline (for example
// "Published to YouTube Shorts"), Details a neutral line with no token or platform
// body. The project id is optional (a reconnect has none); the link then goes to
// the Publishing page. It skips silently when the recipient is missing,
// unparsable or no longer a member of the event's organization.
func (n *NotificationDispatcherImpl) handleSocialPublishing(ctx context.Context, evt UserActivityEvent) {
	if evt.RecipientUserID == nil || *evt.RecipientUserID == "" {
		return
	}
	recipient, err := uuid.Parse(*evt.RecipientUserID)
	if err != nil {
		log.Printf("notification: social publishing with unparsable recipient id: %v", err)
		return
	}
	orgID, err := uuid.Parse(evt.OrganizationID)
	if err != nil {
		log.Printf("notification: activity event with unparsable organization id %q: %v", evt.OrganizationID, err)
		return
	}
	projectID, _ := uuid.Parse(evt.ProjectID)
	members, err := n.store.OrgMembers(ctx, orgID)
	if err != nil {
		log.Printf("notification: member lookup for org %s failed: %v", orgID, err)
		return
	}
	isMember := false
	for _, m := range members {
		if m == recipient {
			isMember = true
			break
		}
	}
	if !isMember {
		return
	}
	var projectName string
	if projectID != uuid.Nil {
		projectName, _ = n.store.ProjectName(ctx, projectID)
	}
	orgName, _ := n.store.OrgName(ctx, orgID)
	title := evt.Subject
	if title == "" {
		title = "Social publishing update"
	}
	n.sendToUser(ctx, models.NotificationSocialPublishing, recipient, orgID, projectID, TemplateSocialPublishing, title, func() MailData {
		return MailData{
			OrgName:     orgName,
			ProjectName: projectName,
			FileName:    evt.Subject,
			ErrorDetail: truncate(evt.Details, errorDetailLimit),
			Link:        "/app/publishing",
		}
	})
}

// handleSecurityMail mails the single recipient of an api_key_expiring or
// webhook_disabled event. It ignores notification preferences, needs no
// project id, and skips a missing, unparsable or non-member recipient.
func (n *NotificationDispatcherImpl) handleSecurityMail(ctx context.Context, evt UserActivityEvent) {
	if evt.RecipientUserID == nil || *evt.RecipientUserID == "" {
		return
	}
	recipient, err := uuid.Parse(*evt.RecipientUserID)
	if err != nil {
		log.Printf("notification: %s with unparsable recipient id: %v", evt.Type, err)
		return
	}
	orgID, err := uuid.Parse(evt.OrganizationID)
	if err != nil {
		log.Printf("notification: %s with unparsable organization id %q: %v", evt.Type, evt.OrganizationID, err)
		return
	}
	members, err := n.store.OrgMembers(ctx, orgID)
	if err != nil {
		log.Printf("notification: member lookup for org %s failed: %v", orgID, err)
		return
	}
	isMember := false
	for _, m := range members {
		if m == recipient {
			isMember = true
			break
		}
	}
	if !isMember {
		return
	}
	orgName, _ := n.store.OrgName(ctx, orgID)
	tmpl, title, link := TemplateApiKeyExpiring, "API key expiring", "/app/org/api-keys"
	expiry := ""
	if evt.Type == models.NotificationWebhookDisabled {
		tmpl, title, link = TemplateWebhookDisabled, "Webhook disabled", "/app/org/api-keys?tab=webhooks"
	} else {
		expiry = evt.Details
		if t, perr := time.Parse(time.RFC3339, evt.Details); perr == nil {
			expiry = t.UTC().Format("2006-01-02 15:04") + " UTC"
		}
	}
	if evt.Subject != "" {
		title += ": " + evt.Subject
	}
	n.sendToUser(ctx, evt.Type, recipient, orgID, uuid.Nil, tmpl, title, func() MailData {
		d := MailData{OrgName: orgName, FileName: evt.Subject, Link: link, ExpiryDate: expiry}
		if evt.Type == models.NotificationWebhookDisabled {
			d.ErrorDetail = truncate(evt.Details, errorDetailLimit)
		}
		return d
	})
}
