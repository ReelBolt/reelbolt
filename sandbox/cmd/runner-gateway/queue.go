package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"sort"
	"strings"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file is the durable half of D5b: the Postgres job queue over
// runner_jobs and runner_sessions.
//
// Postgres rather than RabbitMQ (the plan's own queue choice): matching filters
// on organisation, capabilities and target, leases and fencing need
// transactional updates, and queue depth is one SQL query. RabbitMQ cannot
// express "only this organisation's runner may take this message" without one
// queue per organisation.
//
// The tables are owned by the EF migrations (WorkflowEngine owns runner_jobs and
// runner_sessions; the Inference API owns runner_devices) and are written here
// by raw SQL. The gateway NEVER migrates them.

// Job statuses, matching RunnerJobStatus in ReelBolt.Shared/Data/Models/RunnerEnums.cs.
// Go stores these strings by hand, so runner_enum_drift_test.go in the Go API
// module keeps them in step with the C# enum.
const (
	jobQueued    = "Queued"
	jobAssigned  = "Assigned"
	jobRunning   = "Running"
	jobSucceeded = "Succeeded"
	jobFailed    = "Failed"
	jobCancelled = "Cancelled"
	jobExpired   = "Expired"
)

// Targets, matching RunnerJobTarget.
const (
	targetCloudPool = "CloudPool"
	targetOrgRunner = "OrgRunner"
)

// jobChannel is the LISTEN/NOTIFY channel a newly created job announces itself
// on, so a gateway instance holding idle runners claims without waiting for the
// runner's next heartbeat.
const jobChannel = "runner_jobs_new"

// claimBatch is how many candidate rows one claim round locks. The rows are
// filtered by capability in Go (capabilities are a self-reported document, not
// something SQL can compare against a jsonb requirement), so a round may skip
// every row it locked.
const claimBatch = 20

// claimRounds bounds the loop that walks past capability-mismatched candidates.
const claimRounds = 5

// ErrJobNotFound is returned when no runner_jobs row matches.
var ErrJobNotFound = errors.New("gateway: job not found")

// Job is the gateway's view of one runner_jobs row. Timestamps that the column
// allows to be null are the zero time.
type Job struct {
	ID                 string
	IdempotencyKey     string
	OrganizationID     string
	ExecutionID        string
	StepID             string
	JobType            string
	Target             string
	RequiredCapsJSON   string
	SpecJSON           string
	Status             string
	Priority           int
	Attempt            int
	MaxAttempts        int
	LeaseOwnerRunnerID string
	LeaseEpoch         int64
	LeaseExpiresAt     time.Time
	GatewayInstance    string
	ResultJSON         string
	ErrorClass         string
	CreatedAt          time.Time
	UpdatedAt          time.Time
	CompletedAt        time.Time

	// SessionID is the job's session (runner_sessions.id). It is not a column of
	// runner_jobs: it is minted when the job is assigned and rides the job token,
	// so a requeued job keeps one session identity for its whole life.
	SessionID string
}

// Terminal reports whether the job has reached its one final state.
func (j Job) Terminal() bool {
	switch j.Status {
	case jobSucceeded, jobFailed, jobCancelled, jobExpired:
		return true
	}
	return false
}

// Leased reports whether a runner currently holds the job.
func (j Job) Leased() bool {
	return j.Status == jobAssigned || j.Status == jobRunning
}

// RequiredCaps is runner_jobs.required_caps_json: the pins a runner must meet
// before the gateway may assign the job ("Capabilities" in
// docs/runner-protocol.md). Every zero value means "not required by this job".
//
// It is a separate document from jobType because jobType is one fact and these
// are several; the job type itself is always matched against
// capabilities.jobTypes.
type RequiredCaps struct {
	Container          string `json:"container,omitempty"`
	RuntimeImageDigest string `json:"runtimeImageDigest,omitempty"`
	FontsetVersion     string `json:"fontsetVersion,omitempty"`
	FfmpegMajor        int    `json:"ffmpegMajor,omitempty"`
	MinOutputBytes     int64  `json:"minOutputBytes,omitempty"`
}

// ParseRequiredCaps decodes a stored requirement document. An empty document is
// no requirement at all.
func ParseRequiredCaps(raw string) (RequiredCaps, error) {
	var caps RequiredCaps
	if strings.TrimSpace(raw) == "" {
		return caps, nil
	}
	if err := json.Unmarshal([]byte(raw), &caps); err != nil {
		return RequiredCaps{}, fmt.Errorf("required_caps_json: %w", err)
	}
	return caps, nil
}

// SatisfiedBy reports whether a runner's advertised capability document meets
// every pin, and whether the runner advertises this job type at all.
//
// Capabilities are self-reported and therefore untrusted: a runner that lies can
// only fail its own jobs or receive work it then rejects, and its output is
// verified regardless. Matching is still required, because handing a job to a
// runner that cannot run it costs a lease round trip.
//
// FontsetVersion is compared for equality rather than "at least": the protocol
// calls it a version *tag*, and nothing defines an ordering over tags. A
// deployment that needs an ordered comparison must put the ordered form in
// FfmpegMajor-style fields instead.
func (r RequiredCaps) SatisfiedBy(jobType string, caps protocol.Capabilities) bool {
	if !caps.Supports(jobType) {
		return false
	}
	if jobType == protocol.JobRemotionWorkspace && caps.Container == protocol.ContainerNone {
		// Belt and braces: hello validation already refuses this combination, and
		// a Remotion session without a container runtime has no containment at all.
		return false
	}
	if r.Container != "" && caps.Container != r.Container {
		return false
	}
	if r.RuntimeImageDigest != "" {
		if caps.RuntimeImageDigest == nil || *caps.RuntimeImageDigest != r.RuntimeImageDigest {
			return false
		}
	}
	if r.FontsetVersion != "" && caps.FontsetVersion != r.FontsetVersion {
		return false
	}
	if r.FfmpegMajor > 0 {
		major, ok := ffmpegMajor(caps.FfmpegVersion)
		if !ok || major < r.FfmpegMajor {
			return false
		}
	}
	if r.MinOutputBytes > 0 && caps.MaxOutputBytes < r.MinOutputBytes {
		return false
	}
	return true
}

// ffmpegMajor reads the leading integer of an ffmpeg version string ("7.1" -> 7).
// An unparseable version fails closed: the gateway must not assign a job whose
// ffmpeg requirement it cannot check.
func ffmpegMajor(version string) (int, bool) {
	version = strings.TrimSpace(version)
	if version == "" {
		return 0, false
	}
	digitEnd := 0
	for digitEnd < len(version) && version[digitEnd] >= '0' && version[digitEnd] <= '9' {
		digitEnd++
	}
	if digitEnd == 0 {
		return 0, false
	}
	major := 0
	for _, c := range version[:digitEnd] {
		major = major*10 + int(c-'0')
	}
	return major, true
}

// IdempotencyKey is the key the engine supplies on POST /internal/v1/jobs:
// sha256(executionId|stepId|iteration|purpose) ("Idempotency" in
// docs/runner-protocol.md). Creating a job with a key that already exists
// returns the existing job rather than a second one, so an engine retry after a
// timeout cannot double-enqueue.
//
// The framing is the specification's, verbatim, because the .NET client (D6)
// must compute the same digest for the same job. Its components are UUIDs plus a
// purpose string; a purpose containing "|" could in principle collide with
// another framing, which is why the engine may also supply its own key — the
// gateway only computes one when the request omits it.
func IdempotencyKey(executionID, stepID string, iteration int, purpose string) string {
	sum := sha256.Sum256([]byte(fmt.Sprintf("%s|%s|%d|%s", executionID, stepID, iteration, purpose)))
	return hex.EncodeToString(sum[:])
}

// CreateJobParams is one POST /internal/v1/jobs.
type CreateJobParams struct {
	IdempotencyKey string
	OrganizationID string
	ExecutionID    string
	StepID         string
	JobType        string
	Target         string
	Priority       int
	MaxAttempts    int
	RequiredCaps   RequiredCaps
	Spec           json.RawMessage
}

// ClaimRequest is one attempt to hand work to a runner.
type ClaimRequest struct {
	// RunnerID is runner_devices.id.
	RunnerID string
	// Kind is protocol.KindDesktop or protocol.KindCloudPool.
	Kind string
	// OrgID is the device's organisation; empty for a pool worker.
	OrgID string
	Caps  protocol.Capabilities

	// Want is how many jobs to claim at most (the runner's free slots).
	Want int
	// Now is the gateway's clock; every timestamp is passed in rather than read
	// from Postgres so a test can drive expiry without sleeping.
	Now time.Time
	// LeaseBy is how long the new lease is valid for (protocol.LeaseSec).
	LeaseBy time.Duration
	// Instance is this gateway replica, recorded on the job and its session.
	Instance string
	// ExcludeJobIDs are jobs this runner just rejected; the gateway remembers a
	// rejection so it does not immediately re-offer the same job to the same
	// runner ("Job lifecycle, leases and fencing").
	ExcludeJobIDs []string
	// PoolOrgCap is the per-organisation cap on concurrent CloudPool slots
	// ("Fair share"). Zero means unlimited. A per-organisation override in Gates
	// takes precedence over it for that organisation.
	PoolOrgCap int

	// Gates is the per-candidate half of the platform kill switches (F11): the
	// organisations the platform has disabled, and the CloudPool slot-cap
	// overrides platform_settings carries for them.
	//
	// It is applied per CANDIDATE and not per connection, because a CloudPool
	// worker serves every organisation: the same claim round can skip an org A job
	// and take an org B one, and a later round can take an org A job the moment
	// that organisation is re-enabled.
	//
	// An empty Gates means "nothing is disabled and nobody has an override". The
	// gateway fills it from a snapshot it actually read — the switch that decides
	// whether any claim happens at all is the caller's, and a store that could not
	// be read never reaches here.
	Gates ClaimGates
}

// LeaseOutcome is what a fenced lease transition did.
type LeaseOutcome int

const (
	// LeaseRejected means the epoch, the owner or the status did not match: the
	// message was stale and the runner must be told to stop.
	LeaseRejected LeaseOutcome = iota
	// LeaseApplied means the transition was accepted.
	LeaseApplied
)

// FinishRequest ends a job from a runner message.
type FinishRequest struct {
	JobID      string
	RunnerID   string
	Epoch      int64
	Status     string // jobSucceeded | jobFailed | jobCancelled
	Result     json.RawMessage
	ErrorClass string
	Now        time.Time
}

// ExpiredJob is one job the reaper took back, with what became of it.
type ExpiredJob struct {
	JobID    string
	RunnerID string
	JobType  string
	Target   string
	Status   string // the status it now has
	Attempt  int
}

// JobStore is the gateway's view of the queue. It is an interface so the
// gateway's job state machine can be tested against a real Postgres in the
// integration tests and, where a database adds nothing, against a fake.
type JobStore interface {
	CreateJob(ctx context.Context, p CreateJobParams) (Job, bool, error)
	GetJob(ctx context.Context, jobID string) (Job, error)
	ClaimJobs(ctx context.Context, req ClaimRequest) ([]Job, error)
	RenewLease(ctx context.Context, jobID, runnerID string, epoch int64, until, now time.Time) (LeaseOutcome, error)
	AcceptJob(ctx context.Context, jobID, runnerID string, epoch int64, now time.Time) (LeaseOutcome, error)
	RejectJob(ctx context.Context, jobID, runnerID string, epoch int64, now time.Time) (LeaseOutcome, error)
	FinishJob(ctx context.Context, req FinishRequest) (LeaseOutcome, error)
	CancelQueued(ctx context.Context, jobID string, now time.Time) (Job, bool, error)
	CancelLease(ctx context.Context, jobID string, epoch int64, now time.Time) (LeaseOutcome, error)
	ActiveJobsForRunner(ctx context.Context, runnerID string) ([]Job, error)
	ExpireLeases(ctx context.Context, now time.Time) ([]ExpiredJob, error)
	ReleaseRunnerLeases(ctx context.Context, runnerID string, now time.Time) ([]ExpiredJob, error)
	JobsForEndedExecutions(ctx context.Context, limit int) ([]Job, error)
	QueueDepth(ctx context.Context) ([]QueueDepth, error)
	// D5c: the session surface reads the row that pins a session to a replica,
	// records that an op ran, and counts the pool's backlog for the scaler.
	SessionByID(ctx context.Context, sessionID string) (RunnerSession, error)
	TouchSessionOp(ctx context.Context, sessionID string, now time.Time) error
	PoolWorkload(ctx context.Context, instance string, connectedRunnerIDs []string, staleBefore time.Time) (PoolWorkload, error)
}

// JobNotifier is the optional LISTEN side of a queue: a store that can hold a
// notification channel open and call back whenever a job is created. It is a
// separate interface because it is the one part of the queue that needs a
// dedicated connection, and a store that cannot do it is still a working queue
// (the assignment ticker is the backstop).
type JobNotifier interface {
	Listen(ctx context.Context, onJob func()) error
}

// Listen holds one connection open on the queue's notification channel and calls
// onJob for every announcement, until the context is cancelled. That connection
// is taken from the jobs pool and never returned while the listener runs, which
// is why that pool's cap may not be below minJobsPoolConns (pool.go). It returns when
// the connection fails, so the caller can reconnect with backoff.
func (s *PostgresJobStore) Listen(ctx context.Context, onJob func()) error {
	conn, err := s.pool.Acquire(ctx)
	if err != nil {
		return fmt.Errorf("acquire a listener connection: %w", err)
	}
	defer conn.Release()
	if _, err := conn.Exec(ctx, "LISTEN "+jobChannel); err != nil {
		return fmt.Errorf("listen on %s: %w", jobChannel, err)
	}
	for {
		notification, err := conn.Conn().WaitForNotification(ctx)
		if err != nil {
			return err
		}
		if notification.Channel == jobChannel {
			onJob()
		}
	}
}

// QueueDepth is one (target, jobType) queue-depth row.
type QueueDepth struct {
	Target  string
	JobType string
	Queued  int64
}

// jobColumns is the projection every read shares. uuid columns are cast to text
// so a row's identity travels as a plain string, and nullable text columns are
// coalesced so a scan never needs a pointer per column.
const jobColumns = `id::text, idempotency_key, organization_id::text, execution_id::text,
	coalesce(step_id::text, ''), type, target, coalesce(required_caps_json::text, ''),
	coalesce(spec_json::text, ''), status, priority, attempt, max_attempts,
	coalesce(lease_owner_runner_id::text, ''), lease_epoch, lease_expires_at,
	coalesce(gateway_instance, ''), coalesce(result_json::text, ''), coalesce(error_class, ''),
	created_at, updated_at, completed_at`

// jobColumnsWithSession is jobColumns plus the job's OPEN session id, which is
// not a column of runner_jobs: it is minted when the job is assigned and lives
// in runner_sessions (D5b). Reads that publish a job to the engine need it,
// because sessionId is the address the engine sends its ops to, and without it
// the op surface would have nothing to address — a job view that says "here is
// your session" while omitting its id is worse than useless.
//
// A closed session is excluded, so a finished job advertises no session: its ops
// have nowhere to go, and saying so is what lets the engine open a new one.
const jobColumnsWithSession = jobColumns + `,
	coalesce((SELECT s.id::text FROM runner_sessions s
	           WHERE s.job_id = runner_jobs.id AND s.closed_at IS NULL), '')`

// PostgresJobStore is the queue over runner_jobs/runner_sessions.
type PostgresJobStore struct {
	pool *pgxpool.Pool
}

// NewPostgresJobStore connects and verifies the connection. maxConns is
// DATABASE_MAX_CONNS (zero = unset; see pool.go for the precedence). This pool's
// floor is minJobsPoolConns, because Listen holds one of its connections for as
// long as the gateway runs.
func NewPostgresJobStore(ctx context.Context, databaseURL string, maxConns int32) (*PostgresJobStore, error) {
	pool, err := newPgxPool(ctx, databaseURL, maxConns, minJobsPoolConns, poolJobs)
	if err != nil {
		return nil, err
	}
	store := &PostgresJobStore{pool: pool}
	if err := pool.Ping(ctx); err != nil {
		pool.Close()
		return nil, fmt.Errorf("ping postgres: %w", err)
	}
	return store, nil
}

// Close releases the connection pool.
func (s *PostgresJobStore) Close() { s.pool.Close() }

// CreateJob inserts a job, or returns the one that already carries the key.
func (s *PostgresJobStore) CreateJob(ctx context.Context, p CreateJobParams) (Job, bool, error) {
	if p.IdempotencyKey == "" {
		p.IdempotencyKey = IdempotencyKey(p.ExecutionID, p.StepID, 0, p.JobType)
	}
	if p.MaxAttempts <= 0 {
		p.MaxAttempts = protocol.MaxAttempts
	}
	if p.Target != targetCloudPool && p.Target != targetOrgRunner {
		return Job{}, false, fmt.Errorf("target %q is not one of %s, %s", p.Target, targetCloudPool, targetOrgRunner)
	}
	required := "{}"
	if (p.RequiredCaps != RequiredCaps{}) {
		raw, err := json.Marshal(p.RequiredCaps)
		if err != nil {
			return Job{}, false, fmt.Errorf("marshal required caps: %w", err)
		}
		required = string(raw)
	}
	spec := "{}"
	if len(p.Spec) > 0 {
		spec = string(p.Spec)
	}
	now := time.Now().UTC()

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return Job{}, false, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	rows, err := tx.Query(ctx, `INSERT INTO runner_jobs
		(id, idempotency_key, organization_id, execution_id, step_id, type, target,
		 required_caps_json, spec_json, status, priority, attempt, max_attempts,
		 lease_epoch, created_at, updated_at)
		VALUES (gen_random_uuid(), $1, $2::uuid, $3::uuid, $4::uuid, $5, $6,
		        $7::jsonb, $8::jsonb, $9, $10, 0, $11, 0, $12, $12)
		ON CONFLICT (idempotency_key) DO NOTHING
		RETURNING `+jobColumns,
		p.IdempotencyKey, p.OrganizationID, p.ExecutionID, nullableUUID(p.StepID),
		p.JobType, p.Target, required, spec, jobQueued, p.Priority, p.MaxAttempts, now)
	if err != nil {
		return Job{}, false, fmt.Errorf("insert runner_jobs: %w", err)
	}
	job, err := scanJob(rows)
	rows.Close()
	if err != nil {
		if errors.Is(err, pgx.ErrNoRows) {
			// The key already exists: return the existing job, unchanged. This is
			// the whole point of the key — an engine retry after a timeout must not
			// enqueue a second job.
			existing, err := getJobTx(ctx, tx, "idempotency_key = $1", p.IdempotencyKey)
			if err != nil {
				return Job{}, false, err
			}
			// One exception to "unchanged": a job nobody has leased yet is only a
			// queue entry, and its target is a routing decision the engine may have
			// since revised. The key is sha256(execution|step|iteration|purpose) and
			// does not name the target, so without this a CloudPool job queued while
			// no pool worker existed stays CloudPool for good: the engine's retry,
			// now routed to a desktop runner that has just connected, got the old
			// job back, no runner could claim it, and the step waited out the whole
			// lease of every earlier attempt (ten minutes, in the run that found it).
			// The new target wins, atomically and only while the job is still Queued:
			// a claim flips it to Assigned under a row lock, so the two cannot both
			// succeed, and a job that is already running anywhere is left exactly as
			// it is.
			if existing.Status == jobQueued && existing.Target != p.Target {
				rows, err := tx.Query(ctx, `UPDATE runner_jobs SET target = $2, updated_at = $3
					WHERE id = $1::uuid AND status = $4 AND target <> $2
					RETURNING `+jobColumns,
					existing.ID, p.Target, now, jobQueued)
				if err != nil {
					return Job{}, false, fmt.Errorf("retarget runner_jobs: %w", err)
				}
				retargeted, err := scanJob(rows)
				rows.Close()
				switch {
				case err == nil:
					existing = retargeted
					if _, err := tx.Exec(ctx, "SELECT pg_notify($1, $2)", jobChannel, existing.ID); err != nil {
						return Job{}, false, fmt.Errorf("notify %s: %w", jobChannel, err)
					}
				case errors.Is(err, pgx.ErrNoRows):
					// Claimed (or retargeted by a concurrent repeat) between the read and the
					// update: re-read, so the caller sees what the job is now.
					if existing, err = getJobTx(ctx, tx, "idempotency_key = $1", p.IdempotencyKey); err != nil {
						return Job{}, false, err
					}
				default:
					return Job{}, false, fmt.Errorf("retarget runner_jobs: %w", err)
				}
			}
			if err := tx.Commit(ctx); err != nil {
				return Job{}, false, fmt.Errorf("commit: %w", err)
			}
			return existing, false, nil
		}
		return Job{}, false, fmt.Errorf("insert runner_jobs: %w", err)
	}
	// Wake every instance's idle runners. NOTIFY is transactional: it is
	// delivered when this transaction commits.
	if _, err := tx.Exec(ctx, "SELECT pg_notify($1, $2)", jobChannel, job.ID); err != nil {
		return Job{}, false, fmt.Errorf("notify %s: %w", jobChannel, err)
	}
	if err := tx.Commit(ctx); err != nil {
		return Job{}, false, fmt.Errorf("commit: %w", err)
	}
	return job, true, nil
}

// GetJob reads one job by id.
func (s *PostgresJobStore) GetJob(ctx context.Context, jobID string) (Job, error) {
	if !validRunnerID(jobID) {
		return Job{}, ErrJobNotFound
	}
	rows, err := s.pool.Query(ctx, "SELECT "+jobColumnsWithSession+" FROM runner_jobs WHERE id = $1::uuid", jobID)
	if err != nil {
		return Job{}, fmt.Errorf("read runner_jobs: %w", err)
	}
	defer rows.Close()
	job, err := scanJobWithSession(rows)
	if errors.Is(err, pgx.ErrNoRows) {
		return Job{}, ErrJobNotFound
	}
	return job, err
}

// ClaimJobs transactionally takes up to Want eligible jobs for a runner.
//
// The row locks (FOR UPDATE SKIP LOCKED) are what make "two workers cannot hold
// one job" true: the second claimer skips a row the first has locked rather than
// blocking on it, and the status flip to Assigned is committed with the lock
// still held. Ordering is priority then creation time, and the filter is target
// + organisation + capability — the routing isolation in "Routing and targets":
// a Desktop runner sees only its own organisation's OrgRunner jobs, a pool
// worker sees only CloudPool jobs, and nothing ever falls through from one to
// the other inside the gateway.
//
// On top of that filter, req.Gates applies the platform kill switches to each
// candidate (F11): a job belonging to a disabled organisation is skipped, so a
// disabled organisation is not assigned work even through a pool worker that
// serves many organisations, and that organisation's own pause leaves every
// other organisation's jobs untouched.
func (s *PostgresJobStore) ClaimJobs(ctx context.Context, req ClaimRequest) ([]Job, error) {
	if req.Want <= 0 {
		return nil, nil
	}
	target, err := targetForKind(req.Kind)
	if err != nil {
		return nil, err
	}
	// A desktop runner with no organisation can never be handed work: the
	// routing rule is job.org_id == device.org_id and an empty id matches
	// nothing. Fail closed rather than fall through to the pool.
	if req.Kind != protocol.KindCloudPool && strings.TrimSpace(req.OrgID) == "" {
		return nil, nil
	}

	poolCounts, err := s.poolSlotCounts(ctx)
	if err != nil {
		return nil, err
	}

	exclude := req.ExcludeJobIDs
	if exclude == nil {
		exclude = []string{}
	}

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	until := req.Now.Add(req.LeaseBy)
	var claimed []Job
	// claimedPerOrg tracks the cap within this claim round: the rows this
	// transaction is about to take are not visible to the count query above.
	claimedPerOrg := map[string]int{}

	remaining := req.Want
	for round := 0; round < claimRounds && remaining > 0; round++ {
		args := []any{jobQueued, target, exclude}
		query := "SELECT " + jobColumns + " FROM runner_jobs WHERE status = $1 AND target = $2 AND NOT (id = ANY($3::uuid[]))"
		if req.Kind != protocol.KindCloudPool {
			args = append(args, req.OrgID)
			query += fmt.Sprintf(" AND organization_id = $%d::uuid", len(args))
		}
		args = append(args, claimBatch)
		// id breaks ties, so the order is total and a test (or an operator reading
		// two claims) sees the same sequence twice: created_at has microsecond
		// resolution and jobs created in the same transaction share it.
		query += fmt.Sprintf(" ORDER BY priority DESC, created_at ASC, id ASC LIMIT $%d FOR UPDATE SKIP LOCKED", len(args))

		rows, err := tx.Query(ctx, query, args...)
		if err != nil {
			return nil, fmt.Errorf("select claimable runner_jobs: %w", err)
		}
		var candidates []Job
		for rows.Next() {
			job, err := scanJobRow(rows)
			if err != nil {
				rows.Close()
				return nil, err
			}
			candidates = append(candidates, job)
		}
		rows.Close()
		if err := rows.Err(); err != nil {
			return nil, fmt.Errorf("select claimable runner_jobs: %w", err)
		}
		if len(candidates) == 0 {
			break
		}

		var ids []string
		for _, job := range candidates {
			if remaining == 0 {
				break
			}
			// The platform kill switch for this job's organisation (F11). It is
			// checked per candidate because a pool worker serves every
			// organisation, and it comes before the capability filters because it
			// is a platform decision rather than a mismatch between this job and
			// this runner.
			if req.Gates.Disabled(job.OrganizationID) {
				continue
			}
			if !req.Caps.Supports(job.JobType) {
				continue
			}
			caps, err := ParseRequiredCaps(job.RequiredCapsJSON)
			if err != nil {
				// A requirement document the gateway cannot read is a job it must
				// not assign, never a job it assigns blind.
				continue
			}
			if !caps.SatisfiedBy(job.JobType, req.Caps) {
				continue
			}
			// The organisation's own cap from platform_settings wins over the
			// deployment-wide one: it is the per-organisation fair share, and it
			// can tighten or loosen the config value for that one organisation.
			poolCap := req.PoolOrgCap
			if override, ok := req.Gates.PoolCap(job.OrganizationID); ok {
				poolCap = override
			}
			if poolCap > 0 && target == targetCloudPool {
				if poolCounts[job.OrganizationID]+claimedPerOrg[job.OrganizationID] >= poolCap {
					continue
				}
				claimedPerOrg[job.OrganizationID]++
			}
			ids = append(ids, job.ID)
			remaining--
		}
		if len(ids) == 0 {
			if len(candidates) < claimBatch {
				break
			}
			continue
		}

		updated, err := tx.Query(ctx, `UPDATE runner_jobs
			   SET status = $2, attempt = attempt + 1, lease_owner_runner_id = $3::uuid,
			       lease_epoch = lease_epoch + 1, lease_expires_at = $4, gateway_instance = $5,
			       updated_at = $6
			 WHERE id = ANY($1::uuid[]) AND status = $7
			 RETURNING `+jobColumns,
			ids, jobAssigned, req.RunnerID, until, req.Instance, req.Now, jobQueued)
		if err != nil {
			return nil, fmt.Errorf("claim runner_jobs: %w", err)
		}
		for updated.Next() {
			job, err := scanJobRow(updated)
			if err != nil {
				updated.Close()
				return nil, err
			}
			claimed = append(claimed, job)
		}
		updated.Close()
		if err := updated.Err(); err != nil {
			return nil, fmt.Errorf("claim runner_jobs: %w", err)
		}

		if len(candidates) < claimBatch {
			break
		}
	}

	// The UPDATE ... RETURNING order is unspecified, but the claim order is not:
	// it is what decides which of two queued jobs a runner gets first, so the
	// result is put back in the order the candidates were chosen in.
	sort.SliceStable(claimed, func(i, j int) bool {
		if claimed[i].Priority != claimed[j].Priority {
			return claimed[i].Priority > claimed[j].Priority
		}
		if !claimed[i].CreatedAt.Equal(claimed[j].CreatedAt) {
			return claimed[i].CreatedAt.Before(claimed[j].CreatedAt)
		}
		return claimed[i].ID < claimed[j].ID
	})

	for i := range claimed {
		sessionID := uuid.NewString()
		opened, err := openSessionTx(ctx, tx, claimed[i], sessionID, req.RunnerID, req.Instance, req.Now)
		if err != nil {
			return nil, err
		}
		// A job's session id is advertised to the engine as the address of its
		// session, so it is only set when a runner_sessions row actually exists. A
		// job that could not open one (the execution already has an open session:
		// ix_runner_sessions_execution_id allows only one) carries NO session id
		// rather than an id that addresses nothing. The gateway tolerates the
		// violation because the job is still runnable; making the engine serialise
		// sessions per execution is D6's.
		if opened {
			claimed[i].SessionID = sessionID
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit: %w", err)
	}
	return claimed, nil
}

// openSessionTx records the one session a job has ("Job types": the gateway
// creates one runner_sessions row per job; sessionId is the job's session). It
// is best effort: D5c routes ops with it, but a job whose session row could not
// be written must still be executable, so a conflict is reported rather than
// failing the assignment.
//
// The row is reused across reassignments because ix_runner_sessions_job_id is
// unique on job_id: a requeued job keeps one session identity for its whole
// life, repointed at the runner that holds it now.
//
// It reports whether the row exists after the call, because the session id is
// only addressable when it does.
func openSessionTx(ctx context.Context, tx pgx.Tx, job Job, sessionID, runnerID, instance string, now time.Time) (bool, error) {
	if job.ID == "" || runnerID == "" || sessionID == "" {
		return false, nil
	}
	// A savepoint, because ix_runner_sessions_execution_id allows only ONE open
	// session per execution and a failed statement would otherwise poison the
	// whole assignment transaction. A job whose execution already has an open
	// session is still assigned, just without a session row of its own: the
	// runner can execute it, and the constraint — which the schema owns, not the
	// gateway — is what says the engine must not keep two runner sessions open in
	// one execution.
	savepoint, err := tx.Begin(ctx)
	if err != nil {
		return false, fmt.Errorf("begin session savepoint: %w", err)
	}
	_, err = savepoint.Exec(ctx, `INSERT INTO runner_sessions
		(id, job_id, execution_id, runner_id, gateway_instance, opened_at)
		VALUES ($6::uuid, $1::uuid, $2::uuid, $3::uuid, $4, $5)
		ON CONFLICT (job_id) DO UPDATE
		   SET runner_id = EXCLUDED.runner_id,
		       gateway_instance = EXCLUDED.gateway_instance,
		       opened_at = EXCLUDED.opened_at,
		       last_op_at = NULL,
		       closed_at = NULL`,
		job.ID, job.ExecutionID, runnerID, instance, now, sessionID)
	if err != nil {
		if rollbackErr := savepoint.Rollback(ctx); rollbackErr != nil {
			return false, fmt.Errorf("roll back the session insert: %w", rollbackErr)
		}
		return false, nil
	}
	if err := savepoint.Commit(ctx); err != nil {
		return false, err
	}
	return true, nil
}

// closeJobTx marks the job's session closed; a terminal or requeued job has no
// live session.
func closeJobTx(ctx context.Context, tx pgx.Tx, jobID string, now time.Time) error {
	_, err := tx.Exec(ctx,
		"UPDATE runner_sessions SET closed_at = $2 WHERE job_id = $1::uuid AND closed_at IS NULL",
		jobID, now)
	return err
}

// RenewLease extends a lease from a heartbeat listing, and is also what resume
// uses. A lease that has already expired is NOT renewed: the 45-second lease is
// when the job is taken back, and reviving it from a heartbeat would race the
// reaper into two owners. The runner is told ok:false and stops the job; the
// reaper requeues it.
func (s *PostgresJobStore) RenewLease(ctx context.Context, jobID, runnerID string, epoch int64, until, now time.Time) (LeaseOutcome, error) {
	if !validRunnerID(jobID) || !validRunnerID(runnerID) {
		return LeaseRejected, nil
	}
	tag, err := s.pool.Exec(ctx, `UPDATE runner_jobs
		   SET lease_expires_at = $4, updated_at = $5
		 WHERE id = $1::uuid AND lease_owner_runner_id = $2::uuid AND lease_epoch = $3
		   AND status IN ($6, $7) AND lease_expires_at > $5`,
		jobID, runnerID, epoch, until, now, jobAssigned, jobRunning)
	if err != nil {
		return LeaseRejected, fmt.Errorf("renew lease: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return LeaseRejected, nil
	}
	return LeaseApplied, nil
}

// AcceptJob moves Assigned to Running.
func (s *PostgresJobStore) AcceptJob(ctx context.Context, jobID, runnerID string, epoch int64, now time.Time) (LeaseOutcome, error) {
	if !validRunnerID(jobID) || !validRunnerID(runnerID) {
		return LeaseRejected, nil
	}
	tag, err := s.pool.Exec(ctx, `UPDATE runner_jobs
		   SET status = $5, updated_at = $6
		 WHERE id = $1::uuid AND lease_owner_runner_id = $2::uuid AND lease_epoch = $3
		   AND status = $4`,
		jobID, runnerID, epoch, jobAssigned, jobRunning, now)
	if err != nil {
		return LeaseRejected, fmt.Errorf("accept job: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return LeaseRejected, nil
	}
	return LeaseApplied, nil
}

// RejectJob returns the job to Queued without consuming an attempt.
//
// attempt is incremented when a job is claimed (so a lease that is never
// answered still counts against the requeue budget), so a rejection gives that
// increment back: "the attempt count is not incremented".
func (s *PostgresJobStore) RejectJob(ctx context.Context, jobID, runnerID string, epoch int64, now time.Time) (LeaseOutcome, error) {
	if !validRunnerID(jobID) || !validRunnerID(runnerID) {
		return LeaseRejected, nil
	}
	tag, err := s.pool.Exec(ctx, `UPDATE runner_jobs
		   SET status = $5, attempt = greatest(attempt - 1, 0), lease_owner_runner_id = NULL,
		       lease_expires_at = NULL, gateway_instance = NULL, updated_at = $6
		 WHERE id = $1::uuid AND lease_owner_runner_id = $2::uuid AND lease_epoch = $3
		   AND status = $4`,
		jobID, runnerID, epoch, jobAssigned, jobQueued, now)
	if err != nil {
		return LeaseRejected, fmt.Errorf("reject job: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return LeaseRejected, nil
	}
	_, err = s.pool.Exec(ctx,
		"UPDATE runner_sessions SET closed_at = $2 WHERE job_id = $1::uuid AND closed_at IS NULL", jobID, now)
	if err != nil {
		return LeaseApplied, fmt.Errorf("close session: %w", err)
	}
	return LeaseApplied, nil
}

// FinishJob ends a job from complete or fail.
//
// ErrorClass on a runner-reported failure is the runner's CLAIM: the class that
// counts for billing is the one the platform derives itself, and the gateway
// writes "platform" only where it observed the cause (a lost lease). The whole
// fail body is kept in result_json so the claim stays auditable.
func (s *PostgresJobStore) FinishJob(ctx context.Context, req FinishRequest) (LeaseOutcome, error) {
	switch req.Status {
	case jobSucceeded, jobFailed, jobCancelled:
	default:
		return LeaseRejected, fmt.Errorf("finish status %q is not terminal", req.Status)
	}
	if !validRunnerID(req.JobID) || !validRunnerID(req.RunnerID) {
		return LeaseRejected, nil
	}
	result := "null"
	if len(req.Result) > 0 {
		result = string(req.Result)
	}

	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return LeaseRejected, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	// A terminal job keeps no lease: the runner that held it has finished, and a
	// lease left behind would make a future claim look contended.
	tag, err := tx.Exec(ctx, `UPDATE runner_jobs
		   SET status = $5, result_json = $6::jsonb, error_class = $7, completed_at = $8, updated_at = $8,
		       lease_owner_runner_id = NULL, lease_expires_at = NULL
		 WHERE id = $1::uuid AND lease_owner_runner_id = $2::uuid AND lease_epoch = $3
		   AND status IN ($4, $9)`,
		req.JobID, req.RunnerID, req.Epoch, jobAssigned, req.Status, result, nullableText(req.ErrorClass), req.Now, jobRunning)
	if err != nil {
		return LeaseRejected, fmt.Errorf("finish job: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return LeaseRejected, nil
	}
	if err := closeJobTx(ctx, tx, req.JobID, req.Now); err != nil {
		return LeaseRejected, fmt.Errorf("close session: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return LeaseRejected, fmt.Errorf("commit: %w", err)
	}
	return LeaseApplied, nil
}

// CancelQueued cancels a job that no runner holds. It reports the job and
// whether the caller still has to tell a runner to stop: true when the job was
// Queued (nothing to tell), false when a lease is outstanding.
func (s *PostgresJobStore) CancelQueued(ctx context.Context, jobID string, now time.Time) (Job, bool, error) {
	job, err := s.GetJob(ctx, jobID)
	if err != nil {
		return Job{}, false, err
	}
	if job.Status != jobQueued {
		return job, false, nil
	}
	tag, err := s.pool.Exec(ctx, `UPDATE runner_jobs
		   SET status = $2, completed_at = $3, updated_at = $3,
		       lease_owner_runner_id = NULL, lease_expires_at = NULL
		 WHERE id = $1::uuid AND status = $4`,
		jobID, jobCancelled, now, jobQueued)
	if err != nil {
		return Job{}, false, fmt.Errorf("cancel job: %w", err)
	}
	if tag.RowsAffected() == 0 {
		current, err := s.GetJob(ctx, jobID)
		return current, false, err
	}
	job.Status = jobCancelled
	return job, true, nil
}

// CancelLease marks a leased job Cancelled at one epoch. The epoch matters: a
// cancellation that arrives after the job was reassigned must not kill the new
// owner's run, and "the gateway ... discards any later message with that epoch".
func (s *PostgresJobStore) CancelLease(ctx context.Context, jobID string, epoch int64, now time.Time) (LeaseOutcome, error) {
	if !validRunnerID(jobID) {
		return LeaseRejected, nil
	}
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return LeaseRejected, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	tag, err := tx.Exec(ctx, `UPDATE runner_jobs
		   SET status = $3, completed_at = $4, updated_at = $4,
		       lease_owner_runner_id = NULL, lease_expires_at = NULL
		 WHERE id = $1::uuid AND lease_epoch = $2 AND status IN ($5, $6)`,
		jobID, epoch, jobCancelled, now, jobAssigned, jobRunning)
	if err != nil {
		return LeaseRejected, fmt.Errorf("cancel lease: %w", err)
	}
	if tag.RowsAffected() == 0 {
		return LeaseRejected, nil
	}
	if err := closeJobTx(ctx, tx, jobID, now); err != nil {
		return LeaseRejected, fmt.Errorf("close session: %w", err)
	}
	if err := tx.Commit(ctx); err != nil {
		return LeaseRejected, fmt.Errorf("commit: %w", err)
	}
	return LeaseApplied, nil
}

// ActiveJobsForRunner lists the jobs a runner currently holds. It backs the
// revocation path: a revoked device's jobs are cancelled before its socket is
// closed.
func (s *PostgresJobStore) ActiveJobsForRunner(ctx context.Context, runnerID string) ([]Job, error) {
	if !validRunnerID(runnerID) {
		return nil, nil
	}
	rows, err := s.pool.Query(ctx,
		"SELECT "+jobColumns+" FROM runner_jobs WHERE lease_owner_runner_id = $1::uuid AND status IN ($2, $3) ORDER BY created_at",
		runnerID, jobAssigned, jobRunning)
	if err != nil {
		return nil, fmt.Errorf("read runner_jobs: %w", err)
	}
	defer rows.Close()
	return scanJobs(rows)
}

// ExpireLeases is the reaper's sweep. For every Assigned or Running job whose
// lease has expired it applies the lifecycle rule:
//
//   - a session job goes to Expired (its ops died with the lease; the engine is
//     told session_lost on its next long-poll and decides whether to open a new
//     session);
//   - a one-shot job is requeued while attempt < max_attempts, and otherwise
//     Failed with class platform — the platform observed this failure, so the
//     class is not a runner claim.
//
// v1 defines no one-shot job type: all three types in the catalogue are
// sessions. The requeue branch is therefore reached by a job whose type is
// OUTSIDE the v1 session set — which is exactly the one-shot type the
// specification reserves ("A one-shot job type (a single request, a single
// result, requeue on loss) is reserved by the lifecycle rules but none is
// defined in v1"), and it is what the WP's acceptance criterion exercises.
func (s *PostgresJobStore) ExpireLeases(ctx context.Context, now time.Time) ([]ExpiredJob, error) {
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	rows, err := tx.Query(ctx,
		"SELECT "+jobColumns+" FROM runner_jobs WHERE status IN ($1, $2) AND lease_expires_at IS NOT NULL AND lease_expires_at < $3 ORDER BY lease_expires_at FOR UPDATE SKIP LOCKED",
		jobAssigned, jobRunning, now)
	if err != nil {
		return nil, fmt.Errorf("select expired leases: %w", err)
	}
	expired, err := scanJobs(rows)
	rows.Close()
	if err != nil {
		return nil, err
	}

	out := make([]ExpiredJob, 0, len(expired))
	for _, job := range expired {
		next, status := requeueDecision(job)
		result := ExpiredJob{
			JobID: job.ID, RunnerID: job.LeaseOwnerRunnerID, JobType: job.JobType,
			Target: job.Target, Status: status, Attempt: job.Attempt,
		}
		if next {
			_, err := tx.Exec(ctx, `UPDATE runner_jobs
				   SET status = $2, lease_owner_runner_id = NULL, lease_expires_at = NULL,
				       gateway_instance = NULL, updated_at = $3
				 WHERE id = $1::uuid`, job.ID, jobQueued, now)
			if err != nil {
				return nil, fmt.Errorf("requeue expired job: %w", err)
			}
			// The requeued job is new work for whoever takes it next, so its
			// session is closed and reopened at the next assignment.
			if err := closeJobTx(ctx, tx, job.ID, now); err != nil {
				return nil, fmt.Errorf("close session: %w", err)
			}
			out = append(out, result)
			continue
		}
		errorClass := any(nil)
		if status == jobFailed {
			errorClass = protocol.FailPlatform
		}
		_, err := tx.Exec(ctx, `UPDATE runner_jobs
			   SET status = $2, error_class = $3, completed_at = $4, updated_at = $4,
			       lease_owner_runner_id = NULL, lease_expires_at = NULL
			 WHERE id = $1::uuid`, job.ID, status, errorClass, now)
		if err != nil {
			return nil, fmt.Errorf("expire job: %w", err)
		}
		if err := closeJobTx(ctx, tx, job.ID, now); err != nil {
			return nil, fmt.Errorf("close session: %w", err)
		}
		out = append(out, result)
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit: %w", err)
	}
	return out, nil
}

// requeueDecision applies the lifecycle rule to one expired lease and reports
// whether the job goes back to Queued, plus the status it lands in otherwise.
func requeueDecision(job Job) (requeue bool, status string) {
	if isSessionJobType(job.JobType) {
		return false, jobExpired
	}
	maxAttempts := job.MaxAttempts
	if maxAttempts <= 0 {
		maxAttempts = protocol.MaxAttempts
	}
	if job.Attempt < maxAttempts {
		return true, jobQueued
	}
	return false, jobFailed
}

// sessionJobTypes are the v1 job types, all of which are sessions. An unknown
// type is not one of them: enum-like string values are open sets, and a type
// this gateway does not know is treated as the one-shot shape the lifecycle
// rules reserve rather than as a session whose ops it cannot route.
var sessionJobTypes = map[string]struct{}{
	protocol.JobRemotionWorkspace: {},
	protocol.JobVideoCompile:      {},
	protocol.JobVideoAnalyzeEx:    {},
}

// isSessionJobType reports whether a job type is a v1 session.
func isSessionJobType(jobType string) bool {
	_, ok := sessionJobTypes[jobType]
	return ok
}

// ReleaseRunnerLeases applies the lease-expiry rule to every job a runner holds
// right now. It is how a revoked device's jobs are handled: "the gateway sends
// cancel (reason revoked) for each of its jobs, sends bye, closes with 4401, and
// handles the jobs as lease expiry" — the same rule, without waiting 45 seconds
// for a device that can never come back.
func (s *PostgresJobStore) ReleaseRunnerLeases(ctx context.Context, runnerID string, now time.Time) ([]ExpiredJob, error) {
	if !validRunnerID(runnerID) {
		return nil, nil
	}
	tx, err := s.pool.Begin(ctx)
	if err != nil {
		return nil, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()
	rows, err := tx.Query(ctx,
		"SELECT "+jobColumns+" FROM runner_jobs WHERE lease_owner_runner_id = $1::uuid AND status IN ($2, $3) FOR UPDATE SKIP LOCKED",
		runnerID, jobAssigned, jobRunning)
	if err != nil {
		return nil, fmt.Errorf("select runner leases: %w", err)
	}
	held, err := scanJobs(rows)
	rows.Close()
	if err != nil {
		return nil, err
	}
	out := make([]ExpiredJob, 0, len(held))
	for _, job := range held {
		next, status := requeueDecision(job)
		out = append(out, ExpiredJob{
			JobID: job.ID, RunnerID: runnerID, JobType: job.JobType,
			Target: job.Target, Status: status, Attempt: job.Attempt,
		})
		if next {
			if _, err := tx.Exec(ctx, `UPDATE runner_jobs
				   SET status = $2, lease_owner_runner_id = NULL, lease_expires_at = NULL,
				       gateway_instance = NULL, updated_at = $3 WHERE id = $1::uuid`,
				job.ID, jobQueued, now); err != nil {
				return nil, fmt.Errorf("requeue revoked job: %w", err)
			}
		} else {
			errorClass := any(nil)
			if status == jobFailed {
				errorClass = protocol.FailPlatform
			}
			if _, err := tx.Exec(ctx, `UPDATE runner_jobs
				   SET status = $2, error_class = $3, completed_at = $4, updated_at = $4,
				       lease_owner_runner_id = NULL, lease_expires_at = NULL
				 WHERE id = $1::uuid`,
				job.ID, status, errorClass, now); err != nil {
				return nil, fmt.Errorf("expire revoked job: %w", err)
			}
		}
		if err := closeJobTx(ctx, tx, job.ID, now); err != nil {
			return nil, fmt.Errorf("close session: %w", err)
		}
	}
	if err := tx.Commit(ctx); err != nil {
		return nil, fmt.Errorf("commit: %w", err)
	}
	return out, nil
}

// JobsForEndedExecutions lists leased jobs whose workflow execution is no longer
// Running. The engine closing an execution is why the reaper cancels them: ops
// nobody will read must not hold a runner slot.
//
// The execution table belongs to the WorkflowEngine; it is read here, never
// written. A deployment without it (the self-host profile runs Compute:Mode=Local
// and has no runner work at all) answers "no such table", which the caller
// treats as "this sweep is unavailable" rather than as an error.
func (s *PostgresJobStore) JobsForEndedExecutions(ctx context.Context, limit int) ([]Job, error) {
	if limit <= 0 {
		limit = 100
	}
	rows, err := s.pool.Query(ctx, `SELECT `+jobColumns+` FROM runner_jobs j
		 WHERE j.status IN ($1, $2)
		   AND EXISTS (SELECT 1 FROM workflow_executions e
		                WHERE e.id = j.execution_id AND e.status::text <> $3)
		 ORDER BY j.created_at
		 LIMIT $4`, jobAssigned, jobRunning, "Running", limit)
	if err != nil {
		return nil, fmt.Errorf("read ended executions: %w", err)
	}
	defer rows.Close()
	return scanJobs(rows)
}

// QueueDepth reports the queued jobs per (target, job type). It is one SQL
// query — one of the reasons the queue is Postgres and not a broker.
func (s *PostgresJobStore) QueueDepth(ctx context.Context) ([]QueueDepth, error) {
	rows, err := s.pool.Query(ctx,
		"SELECT target, type, count(*) FROM runner_jobs WHERE status = $1 GROUP BY target, type", jobQueued)
	if err != nil {
		return nil, fmt.Errorf("queue depth: %w", err)
	}
	defer rows.Close()
	var out []QueueDepth
	for rows.Next() {
		var row QueueDepth
		if err := rows.Scan(&row.Target, &row.JobType, &row.Queued); err != nil {
			return nil, fmt.Errorf("scan queue depth: %w", err)
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

// poolSlotCounts counts the CloudPool jobs each organisation is running now, for
// the fair-share cap.
func (s *PostgresJobStore) poolSlotCounts(ctx context.Context) (map[string]int, error) {
	rows, err := s.pool.Query(ctx,
		"SELECT organization_id::text, count(*) FROM runner_jobs WHERE target = $1 AND status IN ($2, $3) GROUP BY 1",
		targetCloudPool, jobAssigned, jobRunning)
	if err != nil {
		return nil, fmt.Errorf("pool slot counts: %w", err)
	}
	defer rows.Close()
	out := map[string]int{}
	for rows.Next() {
		var org string
		var n int
		if err := rows.Scan(&org, &n); err != nil {
			return nil, fmt.Errorf("scan pool slot counts: %w", err)
		}
		out[org] = n
	}
	return out, rows.Err()
}

// targetForKind maps a runner kind to the only target it may be assigned.
func targetForKind(kind string) (string, error) {
	switch kind {
	case protocol.KindCloudPool:
		return targetCloudPool, nil
	case protocol.KindDesktop:
		return targetOrgRunner, nil
	default:
		return "", fmt.Errorf("runner kind %q is not one of %s, %s", kind, protocol.KindDesktop, protocol.KindCloudPool)
	}
}

func getJobTx(ctx context.Context, tx pgx.Tx, where string, args ...any) (Job, error) {
	rows, err := tx.Query(ctx, "SELECT "+jobColumns+" FROM runner_jobs WHERE "+where, args...)
	if err != nil {
		return Job{}, fmt.Errorf("read runner_jobs: %w", err)
	}
	defer rows.Close()
	return scanJob(rows)
}

// rowScanner is the shape both pgx.Rows and pgx.Row satisfy for one job.
type rowScanner interface {
	Scan(dest ...any) error
}

// jobsScanner is the shape a multi-row query satisfies.
type jobsScanner interface {
	Next() bool
	Scan(dest ...any) error
	Err() error
}

func scanJob(rows jobsScanner) (Job, error) {
	if !rows.Next() {
		if err := rows.Err(); err != nil {
			return Job{}, fmt.Errorf("read runner_jobs: %w", err)
		}
		// pgx's own "no rows", so a caller can tell an empty result from a broken
		// query; ErrJobNotFound is the API-level translation GetJob applies.
		return Job{}, pgx.ErrNoRows
	}
	job, err := scanJobRow(rows)
	if err != nil {
		return Job{}, err
	}
	return job, rows.Err()
}

// scanJobWithSession reads a row projected with jobColumnsWithSession.
func scanJobWithSession(rows jobsScanner) (Job, error) {
	if !rows.Next() {
		if err := rows.Err(); err != nil {
			return Job{}, fmt.Errorf("read runner_jobs: %w", err)
		}
		return Job{}, pgx.ErrNoRows
	}
	var (
		job       Job
		expires   *time.Time
		completed *time.Time
	)
	err := rows.Scan(
		&job.ID, &job.IdempotencyKey, &job.OrganizationID, &job.ExecutionID,
		&job.StepID, &job.JobType, &job.Target, &job.RequiredCapsJSON,
		&job.SpecJSON, &job.Status, &job.Priority, &job.Attempt, &job.MaxAttempts,
		&job.LeaseOwnerRunnerID, &job.LeaseEpoch, &expires,
		&job.GatewayInstance, &job.ResultJSON, &job.ErrorClass,
		&job.CreatedAt, &job.UpdatedAt, &completed, &job.SessionID)
	if err != nil {
		return Job{}, fmt.Errorf("scan runner_jobs: %w", err)
	}
	if expires != nil {
		job.LeaseExpiresAt = expires.UTC()
	}
	if completed != nil {
		job.CompletedAt = completed.UTC()
	}
	job.CreatedAt = job.CreatedAt.UTC()
	job.UpdatedAt = job.UpdatedAt.UTC()
	return job, rows.Err()
}

func scanJobRow(row rowScanner) (Job, error) {
	var (
		job       Job
		expires   *time.Time
		completed *time.Time
	)
	err := row.Scan(
		&job.ID, &job.IdempotencyKey, &job.OrganizationID, &job.ExecutionID,
		&job.StepID, &job.JobType, &job.Target, &job.RequiredCapsJSON,
		&job.SpecJSON, &job.Status, &job.Priority, &job.Attempt, &job.MaxAttempts,
		&job.LeaseOwnerRunnerID, &job.LeaseEpoch, &expires,
		&job.GatewayInstance, &job.ResultJSON, &job.ErrorClass,
		&job.CreatedAt, &job.UpdatedAt, &completed)
	if err != nil {
		return Job{}, fmt.Errorf("scan runner_jobs: %w", err)
	}
	if expires != nil {
		job.LeaseExpiresAt = expires.UTC()
	}
	if completed != nil {
		job.CompletedAt = completed.UTC()
	}
	job.CreatedAt = job.CreatedAt.UTC()
	job.UpdatedAt = job.UpdatedAt.UTC()
	return job, nil
}

func scanJobs(rows jobsScanner) ([]Job, error) {
	var out []Job
	for rows.Next() {
		job, err := scanJobRow(rows)
		if err != nil {
			return nil, err
		}
		out = append(out, job)
	}
	if err := rows.Err(); err != nil {
		return nil, fmt.Errorf("read runner_jobs: %w", err)
	}
	return out, nil
}

// nullableUUID renders an optional uuid column: an empty string is NULL, never
// an empty uuid (which would be a database error).
func nullableUUID(v string) any {
	if strings.TrimSpace(v) == "" {
		return nil
	}
	return v
}

// nullableText renders an optional text column.
func nullableText(v string) any {
	if strings.TrimSpace(v) == "" {
		return nil
	}
	return v
}
