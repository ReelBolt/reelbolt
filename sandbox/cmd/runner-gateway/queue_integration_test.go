package main

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// These tests exercise the queue against a THROWAWAY Postgres, created and
// dropped by the test itself. The Go CI leg has no database, so they run only
// when RUNNER_GATEWAY_TEST_DATABASE_URL points at one that may be destroyed:
//
//	docker run --rm -d --name reelbolt-d5b-pg -e POSTGRES_PASSWORD=postgres -p 55433:5432 postgres:17-alpine
//	RUNNER_GATEWAY_TEST_DATABASE_URL='postgres://postgres:postgres@127.0.0.1:55433/postgres?sslmode=disable' \
//	  go test ./cmd/runner-gateway/ -run 'TestQueue|TestJob|TestClaim|TestReaper|TestLease|TestCancel|TestRevoke' -v
//
// The DDL below is transcribed from the real migrations —
// ReelBolt.Inference.Api/Migrations/*_AddRunnerDevices.cs and
// ReelBolt.WorkflowEngine/Migrations/*_AddRunnerJobsAndSessions.cs — including
// the partial indexes the queue's queries depend on. The foreign keys to
// application_users/organizations are omitted because this database has no such
// tables; every column, constraint, type and index the gateway's SQL touches is
// exactly as the migrations create it.
const queueTestDDL = `
CREATE TABLE IF NOT EXISTS runner_devices (
	id uuid PRIMARY KEY,
	organization_id uuid NULL,
	kind character varying(50) NOT NULL,
	name character varying(200) NOT NULL,
	public_key text NULL,
	public_key_fingerprint character varying(128) NULL,
	capabilities_json jsonb NULL,
	os text NULL,
	arch text NULL,
	runner_version text NULL,
	last_seen_at timestamp with time zone NULL,
	revoked_at timestamp with time zone NULL,
	revoked_by_user_id uuid NULL,
	created_by_user_id uuid NULL,
	paired_by_user_id uuid NULL,
	created_at timestamp with time zone NOT NULL DEFAULT now(),
	CONSTRAINT ck_runner_devices_org_or_pool CHECK (kind = 'CloudPool' OR organization_id IS NOT NULL)
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_devices_public_key_fingerprint" ON runner_devices (public_key_fingerprint);

CREATE TABLE IF NOT EXISTS runner_jobs (
	id uuid PRIMARY KEY,
	idempotency_key character varying(200) NOT NULL,
	organization_id uuid NOT NULL,
	execution_id uuid NOT NULL,
	step_id uuid NULL,
	type character varying(50) NOT NULL,
	target character varying(50) NOT NULL,
	required_caps_json jsonb NULL,
	spec_json jsonb NULL,
	status character varying(50) NOT NULL,
	priority integer NOT NULL,
	attempt integer NOT NULL,
	max_attempts integer NOT NULL,
	lease_owner_runner_id uuid NULL,
	lease_epoch bigint NOT NULL,
	lease_expires_at timestamp with time zone NULL,
	gateway_instance character varying(200) NULL,
	result_json jsonb NULL,
	error_class character varying(50) NULL,
	created_at timestamp with time zone NOT NULL,
	updated_at timestamp with time zone NOT NULL,
	completed_at timestamp with time zone NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_jobs_idempotency_key" ON runner_jobs (idempotency_key);
CREATE INDEX IF NOT EXISTS ix_runner_jobs_execution_id ON runner_jobs (execution_id);
CREATE INDEX IF NOT EXISTS "IX_runner_jobs_lease_expires_at" ON runner_jobs (lease_expires_at) WHERE status IN ('Assigned', 'Running');
CREATE INDEX IF NOT EXISTS "IX_runner_jobs_status_target_organization_id_created_at" ON runner_jobs (status, target, organization_id, created_at);

CREATE TABLE IF NOT EXISTS runner_sessions (
	id uuid PRIMARY KEY,
	job_id uuid NOT NULL,
	execution_id uuid NOT NULL,
	runner_id uuid NOT NULL,
	gateway_instance character varying(200) NULL,
	opened_at timestamp with time zone NOT NULL,
	last_op_at timestamp with time zone NULL,
	closed_at timestamp with time zone NULL,
	CONSTRAINT fk_runner_sessions_runner_jobs_job_id FOREIGN KEY (job_id) REFERENCES runner_jobs (id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_runner_sessions_job_id ON runner_sessions (job_id);
CREATE UNIQUE INDEX IF NOT EXISTS "IX_runner_sessions_execution_id" ON runner_sessions (execution_id) WHERE closed_at IS NULL;

-- The reaper's second duty reads the execution's status. The engine owns this
-- table; only these two columns are ever read, and the status column is text
-- here exactly as the engine's enum-to-string conversion stores it.
CREATE TABLE IF NOT EXISTS workflow_executions (
	id uuid PRIMARY KEY,
	status text NOT NULL
);

-- F11's platform_settings, transcribed from the frozen schema the EF migration
-- is written against. The gateway reads it once per cache window and never
-- writes it, so what has to be exact here is the shape the store's single query
-- and its row semantics depend on: a text primary key, a non-null boolean
-- defaulting to false, and a nullable integer cap. updated_at/updated_by belong
-- to the operator that flipped the switch and are not read by the gateway.
CREATE TABLE IF NOT EXISTS platform_settings (
	key text NOT NULL PRIMARY KEY,
	paused boolean NOT NULL DEFAULT false,
	reason text NULL,
	pool_slot_cap integer NULL,
	updated_at timestamp with time zone NOT NULL DEFAULT now(),
	updated_by text NULL
);
`

var (
	queueTestOnce sync.Once
	queueTestPool *pgxpool.Pool
	queueTestErr  error
)

// queueTestDatabase connects to the throwaway database, resets the schema and
// returns a pool. Every test gets empty tables; the schema is created once per
// process.
func queueTestDatabase(t *testing.T) *pgxpool.Pool {
	t.Helper()
	url := os.Getenv("RUNNER_GATEWAY_TEST_DATABASE_URL")
	if url == "" {
		t.Skip("set RUNNER_GATEWAY_TEST_DATABASE_URL to run the queue integration tests")
	}
	queueTestOnce.Do(func() {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer cancel()
		pool, err := pgxpool.New(ctx, url)
		if err != nil {
			queueTestErr = err
			return
		}
		if _, err := pool.Exec(ctx, `DROP TABLE IF EXISTS runner_sessions, runner_jobs, runner_devices, workflow_executions, platform_settings CASCADE`); err != nil {
			queueTestErr = err
			return
		}
		if _, err := pool.Exec(ctx, queueTestDDL); err != nil {
			queueTestErr = err
			return
		}
		queueTestPool = pool
	})
	if queueTestErr != nil {
		t.Fatalf("prepare the test database: %v", queueTestErr)
	}
	// The DDL is re-applied every time, not only once per process: the
	// device-registry integration test in this package drops runner_devices in
	// its own cleanup, so a repeated run (or any test that drops a table it owns)
	// must find the schema whole again. Every statement is IF NOT EXISTS, so this
	// is a no-op when nothing has changed.
	if _, err := queueTestPool.Exec(context.Background(), queueTestDDL); err != nil {
		t.Fatalf("prepare the queue schema: %v", err)
	}
	if _, err := queueTestPool.Exec(context.Background(),
		"TRUNCATE runner_jobs, runner_sessions, runner_devices, workflow_executions, platform_settings"); err != nil {
		t.Fatalf("truncate: %v", err)
	}
	return queueTestPool
}

// queueTestStore returns a store over the throwaway database.
func queueTestStore(t *testing.T) *PostgresJobStore {
	t.Helper()
	pool := queueTestDatabase(t)
	return &PostgresJobStore{pool: pool}
}

// ── fixtures ────────────────────────────────────────────────────────────────

const (
	testOrgA    = "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a22"
	testOrgB    = "5e2b1c90-7a3d-4f68-8b21-0c9d4e6f1a23"
	testExecA   = "1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c01"
	testExecB   = "1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b5c02"
	testRunnerA = "c3a1f0de-2b4c-4d8e-9a10-000000000101"
	testRunnerB = "c3a1f0de-2b4c-4d8e-9a10-000000000102"
	testPoolOne = "c3a1f0de-2b4c-4d8e-9a10-000000000201"
	testStepID  = "9f8e7d6c-5b4a-4392-8170-6f5e4d3c2b11"
	testOneShot = "video.render.one-shot"
	testNow     = "2026-10-08T09:00:00Z"
)

func testTime(t *testing.T) time.Time {
	t.Helper()
	parsed, err := time.Parse(time.RFC3339, testNow)
	if err != nil {
		t.Fatalf("parse test time: %v", err)
	}
	return parsed
}

// createJob is the fixture every test starts from.
func createJob(t *testing.T, store *PostgresJobStore, params CreateJobParams) Job {
	t.Helper()
	job, created, err := store.CreateJob(context.Background(), params)
	if err != nil {
		t.Fatalf("CreateJob: %v", err)
	}
	if !created {
		t.Fatalf("CreateJob reported an existing job for a fresh key: %+v", job)
	}
	return job
}

func orgRunnerRequest(want int, now time.Time) ClaimRequest {
	return ClaimRequest{
		RunnerID: testRunnerA, Kind: protocol.KindDesktop, OrgID: testOrgA,
		Caps: defaultCapabilities(), Want: want, Now: now,
		LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17",
	}
}

// oneShotCapabilities is a runner that advertises the one-shot job type v1 does
// not define. Without it the type is never claimable at all, because "the
// gateway MUST NOT assign a job whose requires are not met: jobType in
// jobTypes" — which is itself the point of the capability filter.
func oneShotCapabilities() protocol.Capabilities {
	caps := defaultCapabilities()
	caps.JobTypes = append(append([]string{}, caps.JobTypes...), testOneShot)
	return caps
}

// oneShotRunnerRequest claims as a desktop runner that can run the one-shot
// fixture type.
func oneShotRunnerRequest(want int, now time.Time) ClaimRequest {
	req := orgRunnerRequest(want, now)
	req.Caps = oneShotCapabilities()
	return req
}

func insertRunner(t *testing.T, pool *pgxpool.Pool, id, orgID, kind, name, publicKey string) {
	t.Helper()
	var org any
	if orgID != "" {
		org = orgID
	}
	_, err := pool.Exec(context.Background(),
		`INSERT INTO runner_devices (id, organization_id, kind, name, public_key) VALUES ($1::uuid, $2, $3, $4, NULLIF($5, ''))`,
		id, org, kind, name, publicKey)
	if err != nil {
		t.Fatalf("insert runner: %v", err)
	}
}

// ── idempotency ─────────────────────────────────────────────────────────────

func TestJobIdempotencyKeyReturnsTheSameJob(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()

	key := IdempotencyKey(testExecA, testStepID, 1, "video.compile")
	if len(key) != 64 {
		t.Fatalf("idempotency key = %q, want a hex sha256", key)
	}
	// The digest is sha256(executionId|stepId|iteration|purpose): two different
	// iterations of the same step are two different jobs.
	if other := IdempotencyKey(testExecA, testStepID, 2, "video.compile"); other == key {
		t.Error("a different iteration produced the same idempotency key")
	}

	first := createJob(t, store, CreateJobParams{
		IdempotencyKey: key, OrganizationID: testOrgA, ExecutionID: testExecA,
		StepID: testStepID, JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})

	// The engine retried after a timeout: same key, same job, no second row.
	second, created, err := store.CreateJob(ctx, CreateJobParams{
		IdempotencyKey: key, OrganizationID: testOrgA, ExecutionID: testExecA,
		StepID: testStepID, JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if err != nil {
		t.Fatalf("CreateJob (repeat): %v", err)
	}
	if created {
		t.Fatal("a repeated idempotency key created a second job")
	}
	if second.ID != first.ID {
		t.Errorf("repeat returned job %s, want %s", second.ID, first.ID)
	}
	if second.Status != jobQueued {
		t.Errorf("status = %q, want %q", second.Status, jobQueued)
	}

	var count int
	if err := store.pool.QueryRow(ctx, "SELECT count(*) FROM runner_jobs").Scan(&count); err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 1 {
		t.Fatalf("runner_jobs has %d rows, want 1", count)
	}
}

// TestJobIdempotencyKeyComputedFromComponents covers the case where the engine
// does not supply a key: the gateway computes the specification's digest, so a
// retry with the same components still deduplicates.
func TestJobIdempotencyKeyComputedFromComponents(t *testing.T) {
	store := queueTestStore(t)

	first := createJob(t, store, CreateJobParams{
		OrganizationID: testOrgA, ExecutionID: testExecA, StepID: testStepID,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if first.IdempotencyKey != IdempotencyKey(testExecA, testStepID, 0, protocol.JobVideoCompile) {
		t.Errorf("computed key = %q", first.IdempotencyKey)
	}
	second, created, err := store.CreateJob(context.Background(), CreateJobParams{
		OrganizationID: testOrgA, ExecutionID: testExecA, StepID: testStepID,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if err != nil {
		t.Fatalf("CreateJob (repeat): %v", err)
	}
	if created || second.ID != first.ID {
		t.Fatalf("repeat created=%v job=%s, want the existing %s", created, second.ID, first.ID)
	}
}

// TestRepeatedKeyRetargetsAJobNobodyHasLeased is the bug a real run found: a CloudPool job was
// created while no pool worker existed, the user's desktop runner then connected, and the engine's
// retry (same key, now routed to the runner) got the old CloudPool job back. No desktop runner may
// claim a CloudPool job, so the step waited out every earlier attempt. The key does not name the
// target, so the target of a still-Queued job is whatever the latest request says.
func TestRepeatedKeyRetargetsAJobNobodyHasLeased(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	key := IdempotencyKey(testExecA, testStepID, 0, "remotion.workspace-session")
	first := createJob(t, store, CreateJobParams{
		IdempotencyKey: key, OrganizationID: testOrgA, ExecutionID: testExecA,
		StepID: testStepID, JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})

	// Nothing eligible yet: a desktop runner cannot take a CloudPool job.
	none, err := store.ClaimJobs(ctx, orgRunnerRequest(5, now))
	if err != nil {
		t.Fatalf("ClaimJobs (before retarget): %v", err)
	}
	if len(none) != 0 {
		t.Fatalf("a desktop runner claimed a CloudPool job: %v", jobIDs(none))
	}

	// The engine retries, now routed to the desktop runner.
	second, created, err := store.CreateJob(ctx, CreateJobParams{
		IdempotencyKey: key, OrganizationID: testOrgA, ExecutionID: testExecA,
		StepID: testStepID, JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if err != nil {
		t.Fatalf("CreateJob (retarget): %v", err)
	}
	if created {
		t.Fatal("a retarget created a second job")
	}
	if second.ID != first.ID {
		t.Errorf("retarget returned job %s, want the same job %s", second.ID, first.ID)
	}
	if second.Target != targetOrgRunner {
		t.Errorf("target = %q after the retarget, want %q", second.Target, targetOrgRunner)
	}

	// And it is claimable now, by the runner that just connected.
	claimed, err := store.ClaimJobs(ctx, orgRunnerRequest(5, now))
	if err != nil {
		t.Fatalf("ClaimJobs (after retarget): %v", err)
	}
	if len(claimed) != 1 || claimed[0].ID != first.ID {
		t.Fatalf("the desktop runner claimed %v, want the retargeted job %s", jobIDs(claimed), first.ID)
	}

	var count int
	if err := store.pool.QueryRow(ctx, "SELECT count(*) FROM runner_jobs").Scan(&count); err != nil {
		t.Fatalf("count: %v", err)
	}
	if count != 1 {
		t.Fatalf("runner_jobs has %d rows, want 1", count)
	}
}

// A job that has been leased is running somewhere, and a repeat of its key must not move it: the
// retarget only applies while the job is still a queue entry.
func TestRepeatedKeyNeverMovesAJobThatIsAlreadyLeased(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	key := IdempotencyKey(testExecA, testStepID, 0, "video.compile")
	first := createJob(t, store, CreateJobParams{
		IdempotencyKey: key, OrganizationID: testOrgA, ExecutionID: testExecA,
		StepID: testStepID, JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	claimed, err := store.ClaimJobs(ctx, orgRunnerRequest(5, now))
	if err != nil || len(claimed) != 1 || claimed[0].ID != first.ID {
		t.Fatalf("setup: the desktop runner did not claim the job: %v, %v", jobIDs(claimed), err)
	}

	again, created, err := store.CreateJob(ctx, CreateJobParams{
		IdempotencyKey: key, OrganizationID: testOrgA, ExecutionID: testExecA,
		StepID: testStepID, JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	if err != nil {
		t.Fatalf("CreateJob (repeat of a leased job): %v", err)
	}
	if created || again.ID != first.ID {
		t.Fatalf("repeat created=%v job=%s, want the existing %s", created, again.ID, first.ID)
	}
	if again.Target != targetOrgRunner {
		t.Errorf("a leased job was moved to %q", again.Target)
	}
	if again.Status != jobAssigned {
		t.Errorf("status = %q, want %q", again.Status, jobAssigned)
	}
}

// ── routing ─────────────────────────────────────────────────────────────────

func TestClaimRoutingIsolation(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	orgA := createJob(t, store, CreateJobParams{
		IdempotencyKey: "org-a", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	orgB := createJob(t, store, CreateJobParams{
		IdempotencyKey: "org-b", OrganizationID: testOrgB, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	pool := createJob(t, store, CreateJobParams{
		IdempotencyKey: "pool", OrganizationID: testOrgB, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})

	// An org-A desktop runner gets only org A's OrgRunner job.
	claimed, err := store.ClaimJobs(ctx, orgRunnerRequest(5, now))
	if err != nil {
		t.Fatalf("ClaimJobs (org A): %v", err)
	}
	if len(claimed) != 1 || claimed[0].ID != orgA.ID {
		t.Fatalf("org A runner claimed %v, want only %s", jobIDs(claimed), orgA.ID)
	}
	for _, job := range claimed {
		if job.OrganizationID != testOrgA || job.Target != targetOrgRunner {
			t.Errorf("cross-assignment: claimed %s org=%s target=%s", job.ID, job.OrganizationID, job.Target)
		}
	}

	// An org-B desktop runner gets only org B's OrgRunner job, never the pool job
	// and never org A's.
	reqB := orgRunnerRequest(5, now)
	reqB.RunnerID = testRunnerB
	reqB.OrgID = testOrgB
	claimedB, err := store.ClaimJobs(ctx, reqB)
	if err != nil {
		t.Fatalf("ClaimJobs (org B): %v", err)
	}
	if len(claimedB) != 1 || claimedB[0].ID != orgB.ID {
		t.Fatalf("org B runner claimed %v, want only %s", jobIDs(claimedB), orgB.ID)
	}

	// A pool worker takes the CloudPool job for any organisation, and nothing
	// else: an OrgRunner job never falls through to the pool inside the gateway.
	claimedPool, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testPoolOne, Kind: protocol.KindCloudPool, Caps: defaultCapabilities(),
		Want: 5, Now: now, LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17",
	})
	if err != nil {
		t.Fatalf("ClaimJobs (pool): %v", err)
	}
	if len(claimedPool) != 1 || claimedPool[0].ID != pool.ID {
		t.Fatalf("pool claimed %v, want only %s", jobIDs(claimedPool), pool.ID)
	}

	// A desktop runner with no organisation can never be handed work at all.
	orphan := orgRunnerRequest(5, now)
	orphan.OrgID = ""
	claimedOrphan, err := store.ClaimJobs(ctx, orphan)
	if err != nil {
		t.Fatalf("ClaimJobs (org-less desktop): %v", err)
	}
	if len(claimedOrphan) != 0 {
		t.Errorf("an org-less desktop runner claimed %v", jobIDs(claimedOrphan))
	}
}

func TestClaimFiltersOnCapabilities(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	digest := "sha256:" + strings.Repeat("b", 64)
	needsB := createJob(t, store, CreateJobParams{
		IdempotencyKey: "needs-digest-b", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
		RequiredCaps: RequiredCaps{RuntimeImageDigest: digest, FfmpegMajor: 7},
	})
	// The runner advertises a different digest, so this one must be skipped and
	// the next eligible job taken instead: an ineligible job is never assigned.
	runs := createJob(t, store, CreateJobParams{
		IdempotencyKey: "plain", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})

	claimed, err := store.ClaimJobs(ctx, orgRunnerRequest(5, now))
	if err != nil {
		t.Fatalf("ClaimJobs: %v", err)
	}
	if len(claimed) != 1 || claimed[0].ID != runs.ID {
		t.Fatalf("claimed %v, want only %s (the digest requirement must be skipped)", jobIDs(claimed), runs.ID)
	}

	// A runner that does not advertise the job type never sees it.
	capped := defaultCapabilities()
	capped.JobTypes = []string{protocol.JobVideoAnalyzeEx}
	claimedUnsupported, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testRunnerA, Kind: protocol.KindDesktop, OrgID: testOrgA, Caps: capped,
		Want: 5, Now: now, LeaseBy: protocol.LeaseSec * time.Second,
	})
	if err != nil {
		t.Fatalf("ClaimJobs: %v", err)
	}
	if len(claimedUnsupported) != 0 {
		t.Errorf("a runner without the job type claimed %v", jobIDs(claimedUnsupported))
	}

	// The digest matches now, but the runner's ffmpeg major is below what the job
	// requires: the requirement fails closed rather than being ignored.
	digestOK := defaultCapabilities()
	digestOK.RuntimeImageDigest = &digest
	digestOK.FfmpegVersion = "6.1"
	claimedOldFfmpeg, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testRunnerA, Kind: protocol.KindDesktop, OrgID: testOrgA, Caps: digestOK,
		Want: 5, Now: now, LeaseBy: protocol.LeaseSec * time.Second,
	})
	if err != nil {
		t.Fatalf("ClaimJobs: %v", err)
	}
	if len(claimedOldFfmpeg) != 0 {
		t.Fatalf("claimed %v with ffmpeg 6.1, want nothing (the job needs major 7)", jobIDs(claimedOldFfmpeg))
	}

	// With a new enough ffmpeg it is assigned.
	digestOK.FfmpegVersion = "7.1"
	claimedMatching, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testRunnerA, Kind: protocol.KindDesktop, OrgID: testOrgA, Caps: digestOK,
		Want: 5, Now: now, LeaseBy: protocol.LeaseSec * time.Second,
	})
	if err != nil {
		t.Fatalf("ClaimJobs: %v", err)
	}
	if len(claimedMatching) != 1 || claimedMatching[0].ID != needsB.ID {
		t.Fatalf("claimed %v, want %s once the digest and the ffmpeg version match",
			jobIDs(claimedMatching), needsB.ID)
	}
}

// TestClaimAppliesThePerOrgPoolCap covers the fair-share rule: a per-organisation
// cap on concurrent CloudPool slots applies at claim time ("Routing and
// targets"). The cap comes from configuration; the default is unlimited, because
// the entitlement numbers are F11's and a gateway that invented one would
// silently throttle a paying organisation.
func TestClaimAppliesThePerOrgPoolCap(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	busy := createJob(t, store, CreateJobParams{
		IdempotencyKey: "org-a-1", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	waiting := createJob(t, store, CreateJobParams{
		IdempotencyKey: "org-a-2", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	orgBJob := createJob(t, store, CreateJobParams{
		IdempotencyKey: "org-b-1", OrganizationID: testOrgB, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})

	pool := func(want, cap int) []Job {
		t.Helper()
		jobs, err := store.ClaimJobs(ctx, ClaimRequest{
			RunnerID: testPoolOne, Kind: protocol.KindCloudPool, Caps: defaultCapabilities(),
			Want: want, Now: now, LeaseBy: protocol.LeaseSec * time.Second,
			Instance: "10.0.3.17", PoolOrgCap: cap,
		})
		if err != nil {
			t.Fatalf("ClaimJobs: %v", err)
		}
		return jobs
	}

	// The first claim takes one CloudPool job for org A and one for org B: the
	// cap is per organisation, not per pool worker.
	first := pool(5, 1)
	got := map[string]int{}
	for _, job := range first {
		got[job.OrganizationID]++
	}
	if len(first) != 2 || got[testOrgA] != 1 || got[testOrgB] != 1 {
		t.Fatalf("first claim = %v, want one job each for org A and org B", jobIDs(first))
	}
	_ = orgBJob
	// Org A is at its cap now, so its second job waits while other organisations
	// keep flowing.
	second := pool(5, 1)
	if len(second) != 0 {
		t.Fatalf("second claim = %v, want nothing: org A is at its cap and org B has no more work",
			jobIDs(second))
	}
	for _, job := range second {
		if job.ID == waiting.ID {
			t.Errorf("job %s was claimed past the organisation cap", waiting.ID)
		}
	}
	if status := jobStatus(t, store, waiting.ID); status != jobQueued {
		t.Errorf("the capped job is %q, want Queued", status)
	}
	// With no cap configured (the default) it is claimable again.
	if len(pool(5, 0)) != 1 {
		t.Error("an uncapped claim took nothing")
	}
	if status := jobStatus(t, store, busy.ID); status != jobAssigned {
		t.Errorf("the already-claimed job is %q", status)
	}
}

func TestClaimOrdersByPriorityThenCreatedAt(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	first := createJob(t, store, CreateJobParams{
		IdempotencyKey: "first", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	urgent := createJob(t, store, CreateJobParams{
		IdempotencyKey: "urgent", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner, Priority: 10,
	})
	second := createJob(t, store, CreateJobParams{
		IdempotencyKey: "second", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})

	claimed, err := store.ClaimJobs(ctx, orgRunnerRequest(3, now))
	if err != nil {
		t.Fatalf("ClaimJobs: %v", err)
	}
	order := jobIDs(claimed)
	want := []string{urgent.ID, first.ID, second.ID}
	if strings.Join(order, ",") != strings.Join(want, ",") {
		t.Errorf("claim order = %v, want priority first then creation order %v", order, want)
	}
}

// ── concurrency ─────────────────────────────────────────────────────────────

// TestTwoWorkersCannotHoldOneJob is the mutual exclusion the lease machinery
// exists for. Eight jobs, twelve claimers running at once, each on its own
// connection: SELECT ... FOR UPDATE SKIP LOCKED plus the status flip committing
// under the same row lock is what makes the union exactly the eight jobs, with
// no job claimed twice.
func TestTwoWorkersCannotHoldOneJob(t *testing.T) {
	store := queueTestStore(t)
	url := os.Getenv("RUNNER_GATEWAY_TEST_DATABASE_URL")
	ctx := context.Background()
	now := testTime(t)

	const jobs = 8
	for i := 0; i < jobs; i++ {
		createJob(t, store, CreateJobParams{
			IdempotencyKey: fmt.Sprintf("contended-%d", i), OrganizationID: testOrgA,
			ExecutionID: testExecA, JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
		})
	}

	const workers = 12
	var (
		mu      sync.Mutex
		wg      sync.WaitGroup
		claimed []string
		errs    []error
	)
	start := make(chan struct{})
	for i := 0; i < workers; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			pool, err := pgxpool.New(ctx, url)
			if err != nil {
				mu.Lock()
				errs = append(errs, err)
				mu.Unlock()
				return
			}
			defer pool.Close()
			worker := &PostgresJobStore{pool: pool}
			<-start
			got, err := worker.ClaimJobs(ctx, ClaimRequest{
				RunnerID: fmt.Sprintf("c3a1f0de-2b4c-4d8e-9a10-%012d", 300+i),
				Kind:     protocol.KindDesktop, OrgID: testOrgA, Caps: defaultCapabilities(),
				Want: jobs, Now: now, LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17",
			})
			mu.Lock()
			defer mu.Unlock()
			if err != nil {
				errs = append(errs, err)
				return
			}
			for _, job := range got {
				claimed = append(claimed, job.ID)
			}
		}(i)
	}
	close(start)
	wg.Wait()

	for _, err := range errs {
		t.Fatalf("claim: %v", err)
	}
	seen := map[string]int{}
	for _, id := range claimed {
		seen[id]++
	}
	for id, n := range seen {
		if n > 1 {
			t.Errorf("job %s was claimed %d times: two workers hold one job", id, n)
		}
	}
	if len(seen) != jobs {
		t.Fatalf("%d of %d jobs were claimed", len(seen), jobs)
	}

	// Every claimed job is Assigned to exactly one runner, and its epoch advanced
	// exactly once from zero.
	rows, err := store.pool.Query(ctx,
		"SELECT status, lease_epoch, lease_owner_runner_id::text FROM runner_jobs")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	defer rows.Close()
	for rows.Next() {
		var status, owner string
		var epoch int64
		if err := rows.Scan(&status, &epoch, &owner); err != nil {
			t.Fatalf("scan: %v", err)
		}
		if status != jobAssigned || epoch != 1 || owner == "" {
			t.Errorf("job row status=%s epoch=%d owner=%q, want Assigned/1/one owner", status, epoch, owner)
		}
	}
}

// TestClaimIsSafeUnderRepeatedRacingRounds drives the contention that a runner
// fleet actually produces: several claimers looping while jobs arrive.
func TestClaimIsSafeUnderRepeatedRacingRounds(t *testing.T) {
	store := queueTestStore(t)
	url := os.Getenv("RUNNER_GATEWAY_TEST_DATABASE_URL")
	ctx := context.Background()
	now := testTime(t)

	const jobs = 30
	for i := 0; i < jobs; i++ {
		createJob(t, store, CreateJobParams{
			IdempotencyKey: fmt.Sprintf("rounds-%d", i), OrganizationID: testOrgA,
			ExecutionID: fmt.Sprintf("1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b%04d", i),
			JobType:     protocol.JobVideoCompile, Target: targetOrgRunner,
		})
	}

	var (
		mu      sync.Mutex
		wg      sync.WaitGroup
		claimed = map[string]int{}
	)
	for i := 0; i < 6; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			pool, err := pgxpool.New(ctx, url)
			if err != nil {
				t.Errorf("pool: %v", err)
				return
			}
			defer pool.Close()
			worker := &PostgresJobStore{pool: pool}
			for round := 0; round < 40; round++ {
				got, err := worker.ClaimJobs(ctx, ClaimRequest{
					RunnerID: fmt.Sprintf("c3a1f0de-2b4c-4d8e-9a10-%012d", 400+i),
					Kind:     protocol.KindDesktop, OrgID: testOrgA, Caps: defaultCapabilities(),
					Want: 2, Now: now, LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17",
				})
				if err != nil {
					t.Errorf("claim: %v", err)
					return
				}
				mu.Lock()
				for _, job := range got {
					claimed[job.ID]++
				}
				mu.Unlock()
				if len(got) == 0 {
					time.Sleep(2 * time.Millisecond)
				}
			}
		}(i)
	}
	wg.Wait()

	for id, n := range claimed {
		if n > 1 {
			t.Errorf("job %s claimed %d times", id, n)
		}
	}
	if len(claimed) != jobs {
		t.Errorf("%d of %d jobs claimed across all rounds", len(claimed), jobs)
	}
}

// ── lease transitions and fencing ───────────────────────────────────────────

func TestStaleEpochIsRejected(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "fencing", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	claimed, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now))
	if err != nil {
		t.Fatalf("ClaimJobs: %v", err)
	}
	if len(claimed) != 1 || claimed[0].LeaseEpoch != 1 {
		t.Fatalf("claim = %+v, want one job at epoch 1", claimed)
	}
	if outcome, err := store.AcceptJob(ctx, job.ID, testRunnerA, 1, now); err != nil || outcome != LeaseApplied {
		t.Fatalf("AcceptJob = %v, %v", outcome, err)
	}

	// A stale epoch cannot finish the job.
	outcome, err := store.FinishJob(ctx, FinishRequest{
		JobID: job.ID, RunnerID: testRunnerA, Epoch: 0, Status: jobSucceeded, Now: now,
	})
	if err != nil || outcome != LeaseRejected {
		t.Fatalf("FinishJob at epoch 0 = %v, %v; want the stale epoch rejected", outcome, err)
	}
	// Nor can the wrong runner.
	outcome, err = store.FinishJob(ctx, FinishRequest{
		JobID: job.ID, RunnerID: testRunnerB, Epoch: 1, Status: jobSucceeded, Now: now,
	})
	if err != nil || outcome != LeaseRejected {
		t.Fatalf("FinishJob by another runner = %v, %v", outcome, err)
	}
	// The right epoch does.
	outcome, err = store.FinishJob(ctx, FinishRequest{
		JobID: job.ID, RunnerID: testRunnerA, Epoch: 1, Status: jobSucceeded,
		Result: json.RawMessage(`{"result":{"ok":true}}`), Now: now,
	})
	if err != nil || outcome != LeaseApplied {
		t.Fatalf("FinishJob = %v, %v", outcome, err)
	}
	final, err := store.GetJob(ctx, job.ID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if final.Status != jobSucceeded || final.CompletedAt.IsZero() {
		t.Errorf("final job = %+v", final)
	}
}

// TestStaleEpochAfterReassignment is the scenario fencing exists for: a runner
// slept past its lease, the job was requeued and given to another runner, and
// the sleeper wakes up and tries to finish "its" job. The stale epoch must be
// rejected so the new owner's result is the one that counts.
func TestStaleEpochAfterReassignment(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "reassigned", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	first, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now))
	if err != nil || len(first) != 1 {
		t.Fatalf("first claim: %v %v", first, err)
	}
	if _, err := store.AcceptJob(ctx, job.ID, testRunnerA, first[0].LeaseEpoch, now); err != nil {
		t.Fatalf("accept: %v", err)
	}

	// The lease lapses and the reaper takes it back.
	later := now.Add(protocol.LeaseSec*time.Second + time.Second)
	expired, err := store.ExpireLeases(ctx, later)
	if err != nil {
		t.Fatalf("ExpireLeases: %v", err)
	}
	if len(expired) != 1 || expired[0].Status != jobExpired {
		t.Fatalf("expired = %+v, want the session job Expired", expired)
	}

	// A requeued one-shot job is the case where the epoch moves on; drive it
	// directly so the reassignment is explicit.
	oneShot := createJob(t, store, CreateJobParams{
		IdempotencyKey: "reassigned-oneshot", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: testOneShot, Target: targetOrgRunner,
	})
	firstClaim, err := store.ClaimJobs(ctx, oneShotRunnerRequest(1, now))
	if err != nil || len(firstClaim) != 1 || firstClaim[0].ID != oneShot.ID {
		t.Fatalf("first claim of the one-shot job: %v %v", firstClaim, err)
	}
	if expired, err := store.ExpireLeases(ctx, later); err != nil || len(expired) != 1 {
		t.Fatalf("reap: %v %v", expired, err)
	}
	second := oneShotRunnerRequest(1, later)
	second.RunnerID = testRunnerB
	reclaimed, err := store.ClaimJobs(ctx, second)
	if err != nil || len(reclaimed) != 1 {
		t.Fatalf("second claim: %v %v", reclaimed, err)
	}
	if reclaimed[0].LeaseEpoch != 2 {
		t.Fatalf("reclaimed epoch = %d, want 2", reclaimed[0].LeaseEpoch)
	}

	// The original owner wakes up and reports success at its old epoch.
	outcome, err := store.FinishJob(ctx, FinishRequest{
		JobID: oneShot.ID, RunnerID: testRunnerA, Epoch: 1, Status: jobSucceeded, Now: later,
	})
	if err != nil {
		t.Fatalf("FinishJob: %v", err)
	}
	if outcome != LeaseRejected {
		t.Fatal("a stale-epoch completion was accepted over the new owner's assignment")
	}
	// And the new owner's own completion is the one that lands.
	outcome, err = store.FinishJob(ctx, FinishRequest{
		JobID: oneShot.ID, RunnerID: testRunnerB, Epoch: 2, Status: jobSucceeded, Now: later,
	})
	if err != nil || outcome != LeaseApplied {
		t.Fatalf("the new owner's completion = %v, %v", outcome, err)
	}
}

func TestRejectReturnsToQueueWithoutConsumingAttempt(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "rejected", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	outcome, err := store.RejectJob(ctx, job.ID, testRunnerA, 1, now)
	if err != nil || outcome != LeaseApplied {
		t.Fatalf("RejectJob = %v, %v", outcome, err)
	}
	back, err := store.GetJob(ctx, job.ID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if back.Status != jobQueued {
		t.Errorf("status = %q, want Queued", back.Status)
	}
	// "the attempt count is not incremented"
	if back.Attempt != 0 {
		t.Errorf("attempt = %d, want 0 after a rejection", back.Attempt)
	}
	if back.LeaseOwnerRunnerID != "" || !back.LeaseExpiresAt.IsZero() {
		t.Errorf("the lease was not released: owner=%q expires=%v", back.LeaseOwnerRunnerID, back.LeaseExpiresAt)
	}

	// A rejection at the wrong epoch is refused.
	if outcome, err := store.RejectJob(ctx, job.ID, testRunnerA, 1, now); err != nil || outcome != LeaseRejected {
		t.Errorf("RejectJob on an already-queued job = %v, %v; want rejected", outcome, err)
	}
}

func TestRenewLeaseRefusesAnExpiredLease(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "renew", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}

	// A live lease is renewed and pushed forward.
	renewed := now.Add(30 * time.Second)
	outcome, err := store.RenewLease(ctx, job.ID, testRunnerA, 1, renewed.Add(45*time.Second), renewed)
	if err != nil || outcome != LeaseApplied {
		t.Fatalf("RenewLease = %v, %v", outcome, err)
	}

	// Past the deadline it is not: the lease is what bounds the job, and reviving
	// it from a heartbeat would race the reaper into two owners.
	afterExpiry := renewed.Add(46 * time.Second)
	outcome, err = store.RenewLease(ctx, job.ID, testRunnerA, 1, afterExpiry.Add(45*time.Second), afterExpiry)
	if err != nil || outcome != LeaseRejected {
		t.Fatalf("RenewLease past the deadline = %v, %v; want rejected", outcome, err)
	}
	// The wrong epoch is refused even on a live lease.
	outcome, err = store.RenewLease(ctx, job.ID, testRunnerA, 2, renewed, renewed)
	if err != nil || outcome != LeaseRejected {
		t.Fatalf("RenewLease at the wrong epoch = %v, %v", outcome, err)
	}
}

// ── the reaper ──────────────────────────────────────────────────────────────

// TestReaperRequeuesOnceThenFails is the WP's acceptance criterion for lease
// expiry: an expired lease requeues once, and the second expiry fails the job
// with class platform. v1 defines no one-shot job type (all three of its types
// are sessions), so the requeue branch is exercised by a job type outside that
// set, which is the one-shot type the specification reserves.
func TestReaperRequeuesOnceThenFails(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "oneshot", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: testOneShot, Target: targetOrgRunner, MaxAttempts: protocol.MaxAttempts,
	})

	if _, err := store.ClaimJobs(ctx, oneShotRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	expireAt := now.Add(protocol.LeaseSec*time.Second + time.Second)

	reaped, err := store.ExpireLeases(ctx, expireAt)
	if err != nil {
		t.Fatalf("ExpireLeases: %v", err)
	}
	if len(reaped) != 1 || reaped[0].Status != jobQueued || reaped[0].Attempt != 1 {
		t.Fatalf("first reap = %+v, want the one-shot job requeued at attempt 1", reaped)
	}
	if status := jobStatus(t, store, job.ID); status != jobQueued {
		t.Fatalf("status after the first reap = %q, want Queued", status)
	}

	// Second attempt: claimed again, expires again, and this time the budget is
	// spent.
	reclaim := oneShotRunnerRequest(1, expireAt)
	reclaimed, err := store.ClaimJobs(ctx, reclaim)
	if err != nil || len(reclaimed) != 1 {
		t.Fatalf("second claim: %v %v", reclaimed, err)
	}
	if reclaimed[0].Attempt != 2 {
		t.Fatalf("attempt = %d, want 2", reclaimed[0].Attempt)
	}
	secondExpiry := expireAt.Add(protocol.LeaseSec*time.Second + time.Second)
	reaped, err = store.ExpireLeases(ctx, secondExpiry)
	if err != nil {
		t.Fatalf("ExpireLeases: %v", err)
	}
	if len(reaped) != 1 || reaped[0].Status != jobFailed {
		t.Fatalf("second reap = %+v, want Failed", reaped)
	}
	final, err := store.GetJob(ctx, job.ID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if final.Status != jobFailed {
		t.Errorf("status = %q, want Failed", final.Status)
	}
	if final.LeaseOwnerRunnerID != "" || !final.LeaseExpiresAt.IsZero() {
		t.Errorf("the failed job still carries a lease: owner=%q", final.LeaseOwnerRunnerID)
	}
	// The platform observed this failure, so the class is not a runner claim.
	if final.ErrorClass != protocol.FailPlatform {
		t.Errorf("error_class = %q, want %q", final.ErrorClass, protocol.FailPlatform)
	}
	if final.CompletedAt.IsZero() {
		t.Error("a failed job has no completed_at")
	}
}

// TestReaperExpiresASessionJobInsteadOfRequeuing covers the other half of the
// rule: a session's ops died with its lease, so it goes to Expired and the
// engine decides what to do (the gateway reports sessionLost).
func TestReaperExpiresASessionJobInsteadOfRequeuing(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "session", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobRemotionWorkspace, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	reaped, err := store.ExpireLeases(ctx, now.Add(protocol.LeaseSec*time.Second+time.Second))
	if err != nil {
		t.Fatalf("ExpireLeases: %v", err)
	}
	if len(reaped) != 1 || reaped[0].Status != jobExpired {
		t.Fatalf("reaped = %+v, want the session job Expired", reaped)
	}
	final, err := store.GetJob(ctx, job.ID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if final.Status != jobExpired || final.ErrorClass != "" {
		t.Errorf("session job after expiry = %+v", final)
	}
	// An expired job is nobody's: the lease is released with the status, so a
	// later claim does not look contended and the runner's heartbeat answers
	// ok:false rather than renewing a job that is gone.
	if final.LeaseOwnerRunnerID != "" || !final.LeaseExpiresAt.IsZero() {
		t.Errorf("the expired job still carries a lease: owner=%q expires=%v",
			final.LeaseOwnerRunnerID, final.LeaseExpiresAt)
	}
}

func TestReaperLeavesLiveLeasesAlone(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	createJob(t, store, CreateJobParams{
		IdempotencyKey: "live", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: testOneShot, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, oneShotRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	// One second before the lease runs out there is nothing to reap.
	reaped, err := store.ExpireLeases(ctx, now.Add(protocol.LeaseSec*time.Second-time.Second))
	if err != nil {
		t.Fatalf("ExpireLeases: %v", err)
	}
	if len(reaped) != 0 {
		t.Fatalf("reaped %+v from a live lease", reaped)
	}
}

// TestReaperRequeueIsIdempotent: a second reaper pass, and a reaper on another
// replica, must not requeue the same job twice or consume a second attempt.
func TestReaperRequeueIsIdempotent(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	job := createJob(t, store, CreateJobParams{
		IdempotencyKey: "idempotent-reap", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: testOneShot, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, oneShotRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	expireAt := now.Add(protocol.LeaseSec*time.Second + time.Second)

	first, err := store.ExpireLeases(ctx, expireAt)
	if err != nil {
		t.Fatalf("ExpireLeases: %v", err)
	}
	second, err := store.ExpireLeases(ctx, expireAt.Add(50*time.Second))
	if err != nil {
		t.Fatalf("ExpireLeases (repeat): %v", err)
	}
	if len(first) != 1 || len(second) != 0 {
		t.Fatalf("first=%d second=%d reaps, want 1 then 0", len(first), len(second))
	}
	back, err := store.GetJob(ctx, job.ID)
	if err != nil {
		t.Fatalf("GetJob: %v", err)
	}
	if back.Status != jobQueued || back.Attempt != 1 {
		t.Errorf("after two reaper passes: status=%q attempt=%d, want Queued/1", back.Status, back.Attempt)
	}
}

// TestCancelWithinGraceWindow covers DELETE /internal/v1/jobs/{id}: a queued job
// is cancelled outright, and a leased one is cancelled at exactly its epoch —
// a cancellation that arrives after the job was reassigned must not kill the new
// owner's run.
func TestCancelWithinGraceWindow(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	queued := createJob(t, store, CreateJobParams{
		IdempotencyKey: "cancel-queued", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if job, immediate, err := store.CancelQueued(ctx, queued.ID, now); err != nil || !immediate {
		t.Fatalf("CancelQueued = %+v %v %v", job, immediate, err)
	}
	if status := jobStatus(t, store, queued.ID); status != jobCancelled {
		t.Fatalf("queued job status = %q, want Cancelled", status)
	}

	leased := createJob(t, store, CreateJobParams{
		IdempotencyKey: "cancel-leased", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	// CancelQueued on a leased job reports that a runner still has to be told.
	if _, immediate, err := store.CancelQueued(ctx, leased.ID, now); err != nil || immediate {
		t.Fatalf("CancelQueued on a leased job = immediate %v, %v", immediate, err)
	}
	// The grace window closes: the job becomes Cancelled at its own epoch.
	if outcome, err := store.CancelLease(ctx, leased.ID, 1, now.Add(10*time.Second)); err != nil || outcome != LeaseApplied {
		t.Fatalf("CancelLease = %v, %v", outcome, err)
	}
	if status := jobStatus(t, store, leased.ID); status != jobCancelled {
		t.Fatalf("leased job status = %q, want Cancelled", status)
	}
	// A cancellation at a different epoch is discarded.
	other := createJob(t, store, CreateJobParams{
		IdempotencyKey: "cancel-epoch", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	if outcome, err := store.CancelLease(ctx, other.ID, 7, now); err != nil || outcome != LeaseRejected {
		t.Fatalf("CancelLease at the wrong epoch = %v, %v", outcome, err)
	}
}

func TestRevokedRunnerLeasesAreDisposedOf(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	session := createJob(t, store, CreateJobParams{
		IdempotencyKey: "revoked-session", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobRemotionWorkspace, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}
	released, err := store.ReleaseRunnerLeases(ctx, testRunnerA, now)
	if err != nil {
		t.Fatalf("ReleaseRunnerLeases: %v", err)
	}
	if len(released) != 1 || released[0].Status != jobExpired {
		t.Fatalf("released = %+v, want the session job Expired", released)
	}
	if status := jobStatus(t, store, session.ID); status != jobExpired {
		t.Errorf("status = %q, want Expired", status)
	}
	// A revoked device cannot hand in a result afterwards.
	outcome, err := store.FinishJob(ctx, FinishRequest{
		JobID: session.ID, RunnerID: testRunnerA, Epoch: 1, Status: jobSucceeded, Now: now,
	})
	if err != nil || outcome != LeaseRejected {
		t.Errorf("FinishJob after revocation = %v, %v", outcome, err)
	}
}

func TestJobsForEndedExecutions(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	insert := `INSERT INTO workflow_executions (id, status) VALUES ($1::uuid, $2)`
	if _, err := store.pool.Exec(ctx, insert, testExecA, "Running"); err != nil {
		t.Fatalf("insert execution: %v", err)
	}
	if _, err := store.pool.Exec(ctx, insert, testExecB, "Passed"); err != nil {
		t.Fatalf("insert execution: %v", err)
	}

	live := createJob(t, store, CreateJobParams{
		IdempotencyKey: "live-exec", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	ended := createJob(t, store, CreateJobParams{
		IdempotencyKey: "ended-exec", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(2, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}

	jobs, err := store.JobsForEndedExecutions(ctx, 100)
	if err != nil {
		t.Fatalf("JobsForEndedExecutions: %v", err)
	}
	if len(jobs) != 1 || jobs[0].ID != ended.ID {
		t.Fatalf("ended-execution jobs = %v, want only %s", jobIDs(jobs), ended.ID)
	}
	if jobs[0].ID == live.ID {
		t.Fatal("a job of a running execution was selected for cancellation")
	}
}

func TestQueueDepthCountsQueuedJobs(t *testing.T) {
	store := queueTestStore(t)
	ctx := context.Background()
	now := testTime(t)

	for i := 0; i < 3; i++ {
		createJob(t, store, CreateJobParams{
			IdempotencyKey: fmt.Sprintf("depth-%d", i), OrganizationID: testOrgA,
			ExecutionID: fmt.Sprintf("1a2b3c4d-5e6f-4a8b-9c0d-1e2f3a4b%04d", i),
			JobType:     protocol.JobVideoCompile, Target: targetOrgRunner,
		})
	}
	if _, err := store.ClaimJobs(ctx, orgRunnerRequest(1, now)); err != nil {
		t.Fatalf("claim: %v", err)
	}

	depth, err := store.QueueDepth(ctx)
	if err != nil {
		t.Fatalf("QueueDepth: %v", err)
	}
	if len(depth) != 1 || depth[0].Queued != 2 || depth[0].Target != targetOrgRunner ||
		depth[0].JobType != protocol.JobVideoCompile {
		t.Fatalf("queue depth = %+v, want 2 queued OrgRunner video.compile jobs", depth)
	}
}

// TestStartupMigrationsShapeMatchesTheStore guards the transcription above: the
// columns, indexes and check constraints the store's SQL depends on really are
// the ones the migrations create.
func TestStartupMigrationsShapeMatchesTheStore(t *testing.T) {
	pool := queueTestDatabase(t)
	ctx := context.Background()

	// The two indexes the queue's correctness rests on: the unique idempotency
	// key (which makes a repeated POST return the same job) and the partial lease
	// index the reaper scans.
	for _, want := range []struct {
		name    string
		partial bool
		unique  bool
	}{
		{"IX_runner_jobs_idempotency_key", false, true},
		{"IX_runner_jobs_lease_expires_at", true, false},
		{"IX_runner_sessions_execution_id", true, true},
		{"ix_runner_sessions_job_id", false, true},
	} {
		var def string
		err := pool.QueryRow(ctx, `SELECT indexdef FROM pg_indexes WHERE indexname = $1`, want.name).Scan(&def)
		if err != nil {
			t.Fatalf("read index %s: %v", want.name, err)
		}
		if want.unique != strings.Contains(def, "UNIQUE INDEX") {
			t.Errorf("%s = %q, unique=%v", want.name, def, want.unique)
		}
		if want.partial != strings.Contains(def, "WHERE") {
			t.Errorf("%s = %q, partial=%v", want.name, def, want.partial)
		}
	}

	// The check constraint that makes organization_id nullable only for a pool
	// worker: the routing filter depends on it being impossible to have an
	// org-less desktop row in the first place.
	var checkDef string
	if err := pool.QueryRow(ctx,
		`SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'ck_runner_devices_org_or_pool'`).
		Scan(&checkDef); err != nil {
		t.Fatalf("read check constraint: %v", err)
	}
	if !strings.Contains(checkDef, "CloudPool") || !strings.Contains(checkDef, "IS NOT NULL") {
		t.Errorf("ck_runner_devices_org_or_pool = %q", checkDef)
	}

	// The column types the store's scans depend on.
	for _, want := range []struct{ column, dataType string }{
		{"lease_epoch", "bigint"},
		{"lease_expires_at", "timestamp with time zone"},
		{"idempotency_key", "character varying"},
		{"required_caps_json", "jsonb"},
		{"result_json", "jsonb"},
		{"status", "character varying"},
	} {
		var dataType string
		if err := pool.QueryRow(ctx,
			`SELECT data_type FROM information_schema.columns WHERE table_name = 'runner_jobs' AND column_name = $1`,
			want.column).Scan(&dataType); err != nil {
			t.Fatalf("read the type of runner_jobs.%s: %v", want.column, err)
		}
		if dataType != want.dataType {
			t.Errorf("runner_jobs.%s is %s, want %s", want.column, dataType, want.dataType)
		}
	}
}

// ── small helpers ───────────────────────────────────────────────────────────

func jobIDs(jobs []Job) []string {
	out := make([]string, 0, len(jobs))
	for _, job := range jobs {
		out = append(out, job.ID)
	}
	return out
}

func jobStatus(t *testing.T, store *PostgresJobStore, jobID string) string {
	t.Helper()
	job, err := store.GetJob(context.Background(), jobID)
	if err != nil {
		t.Fatalf("GetJob(%s): %v", jobID, err)
	}
	return job.Status
}

// ── the platform kill switches (F11) ────────────────────────────────────────

// setPlatformSetting writes one platform_settings row. poolSlotCap is any so a
// caller can pass nil for the column's own default (NULL), and everything the
// schema gives a default is left to the schema — which is what the shape test
// below then checks.
func setPlatformSetting(t *testing.T, pool *pgxpool.Pool, key string, paused bool, poolSlotCap any) {
	t.Helper()
	_, err := pool.Exec(context.Background(),
		`INSERT INTO platform_settings (key, paused, pool_slot_cap) VALUES ($1, $2, $3)
		 ON CONFLICT (key) DO UPDATE
		    SET paused = EXCLUDED.paused, pool_slot_cap = EXCLUDED.pool_slot_cap, updated_at = now()`,
		key, paused, poolSlotCap)
	if err != nil {
		t.Fatalf("write platform_settings %q: %v", key, err)
	}
}

// TestPlatformSettingsShapeMatchesTheFrozenSchema guards the transcription above
// against the schema F11 froze. The store parses rows and the gateway refuses
// work on them, so a column that is not the shape it is read as is not a test
// detail: it is the difference between reading a switch and guessing at one.
func TestPlatformSettingsShapeMatchesTheFrozenSchema(t *testing.T) {
	pool := queueTestDatabase(t)
	ctx := context.Background()

	for _, want := range []struct{ column, dataType, nullable string }{
		{"key", "text", "NO"},
		{"paused", "boolean", "NO"},
		{"reason", "text", "YES"},
		{"pool_slot_cap", "integer", "YES"},
		{"updated_at", "timestamp with time zone", "NO"},
		{"updated_by", "text", "YES"},
	} {
		var dataType, nullable string
		if err := pool.QueryRow(ctx, `SELECT data_type, is_nullable FROM information_schema.columns
			WHERE table_name = 'platform_settings' AND column_name = $1`, want.column).
			Scan(&dataType, &nullable); err != nil {
			t.Fatalf("read the type of platform_settings.%s: %v", want.column, err)
		}
		if dataType != want.dataType || nullable != want.nullable {
			t.Errorf("platform_settings.%s is %s %s, want %s %s", want.column, dataType, nullable, want.dataType, want.nullable)
		}
	}

	// key is the primary key, which is what makes a settings write an upsert and
	// a second row for one key impossible — an operator flipping a switch must
	// not be able to leave two rows disagreeing about it.
	var primaryKey string
	if err := pool.QueryRow(ctx,
		`SELECT pg_get_constraintdef(oid) FROM pg_constraint
		  WHERE conrelid = 'platform_settings'::regclass AND contype = 'p'`).Scan(&primaryKey); err != nil {
		t.Fatalf("read the primary key: %v", err)
	}
	if primaryKey != "PRIMARY KEY (key)" {
		t.Errorf("platform_settings primary key = %q, want %q", primaryKey, "PRIMARY KEY (key)")
	}

	// A row written for its key alone carries the schema's defaults, which is how
	// the store reads "the flag is false and there is no cap" for anything an
	// operator has not set.
	if _, err := pool.Exec(ctx, `INSERT INTO platform_settings (key) VALUES ('shape-check')`); err != nil {
		t.Fatalf("insert a defaults-only row: %v", err)
	}
	var (
		paused    bool
		reason    *string
		cap       *int
		updatedAt time.Time
		updatedBy *string
	)
	if err := pool.QueryRow(ctx, `SELECT paused, reason, pool_slot_cap, updated_at, updated_by
		FROM platform_settings WHERE key = 'shape-check'`).
		Scan(&paused, &reason, &cap, &updatedAt, &updatedBy); err != nil {
		t.Fatalf("read the row back: %v", err)
	}
	if paused || reason != nil || cap != nil || updatedBy != nil || updatedAt.IsZero() {
		t.Errorf("defaults-only row = paused:%v reason:%v cap:%v updatedAt:%v updatedBy:%v, want false/nil/nil/set/nil",
			paused, reason, cap, updatedAt, updatedBy)
	}
	if _, err := pool.Exec(ctx, `INSERT INTO platform_settings (key) VALUES ('shape-check')`); err == nil {
		t.Error("a second row for one settings key was accepted")
	}

	// Finally the row the store actually reads, end to end: the DDL and the
	// single query have to agree on more than the column list.
	setPlatformSetting(t, pool, settingsKeyCompute, true, nil)
	snapshot, err := (&PostgresSwitchStore{pool: pool}).ReadPlatformSettings(ctx)
	if err != nil {
		t.Fatalf("ReadPlatformSettings: %v", err)
	}
	if !snapshot.ComputePaused() {
		t.Error("a paused compute row was not read as paused")
	}
}

// TestClaimSkipsDisabledOrganizations covers F11 rule 4 at the level it is
// enforced: per candidate job. A desktop runner whose organisation was disabled
// while it was connected is already past the handshake, and a pool worker serves
// every organisation, so neither can be gated per connection.
func TestClaimSkipsDisabledOrganizations(t *testing.T) {
	store := queueTestStore(t)
	pool := queueTestDatabase(t)
	ctx := context.Background()
	now := testTime(t)

	desktop := createJob(t, store, CreateJobParams{
		IdempotencyKey: "disabled-desktop", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetOrgRunner,
	})
	pooled := createJob(t, store, CreateJobParams{
		IdempotencyKey: "disabled-pool", OrganizationID: testOrgA, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	setPlatformSetting(t, pool, settingsKeyOrgPrefix+testOrgA, true, nil)

	snapshot, err := (&PostgresSwitchStore{pool: pool}).ReadPlatformSettings(ctx)
	if err != nil {
		t.Fatalf("ReadPlatformSettings: %v", err)
	}
	if !snapshot.OrganizationDisabled(testOrgA) {
		t.Fatal("the disabled organisation's row was not read")
	}
	gates := snapshot.ClaimGates()

	// The desktop runner of the disabled organisation.
	claimed, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testRunnerA, Kind: protocol.KindDesktop, OrgID: testOrgA,
		Caps: defaultCapabilities(), Want: 5, Now: now,
		LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17", Gates: gates,
	})
	if err != nil {
		t.Fatalf("ClaimJobs (desktop): %v", err)
	}
	if len(claimed) != 0 {
		t.Errorf("a disabled organisation's desktop job %s was claimed: %v", desktop.ID, jobIDs(claimed))
	}

	// The pool worker, which sees every organisation at once.
	claimedPool, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testPoolOne, Kind: protocol.KindCloudPool,
		Caps: defaultCapabilities(), Want: 5, Now: now,
		LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17", Gates: gates,
	})
	if err != nil {
		t.Fatalf("ClaimJobs (pool): %v", err)
	}
	if len(claimedPool) != 0 {
		t.Errorf("a pool worker was given a disabled organisation's job %s: %v", pooled.ID, jobIDs(claimedPool))
	}
	if status := jobStatus(t, store, desktop.ID); status != jobQueued {
		t.Errorf("the disabled organisation's job is %q, want Queued", status)
	}

	// Nothing about the other organisations changed: re-enabling org A is all it
	// takes for the same claim to find the work again.
	setPlatformSetting(t, pool, settingsKeyOrgPrefix+testOrgA, false, nil)
	snapshot, err = (&PostgresSwitchStore{pool: pool}).ReadPlatformSettings(ctx)
	if err != nil {
		t.Fatalf("ReadPlatformSettings: %v", err)
	}
	released, err := store.ClaimJobs(ctx, ClaimRequest{
		RunnerID: testRunnerA, Kind: protocol.KindDesktop, OrgID: testOrgA,
		Caps: defaultCapabilities(), Want: 5, Now: now,
		LeaseBy: protocol.LeaseSec * time.Second, Instance: "10.0.3.17", Gates: snapshot.ClaimGates(),
	})
	if err != nil {
		t.Fatalf("ClaimJobs (re-enabled): %v", err)
	}
	if len(released) != 1 || released[0].ID != desktop.ID {
		t.Errorf("after the organisation was re-enabled the claim = %v, want %s", jobIDs(released), desktop.ID)
	}
}

// TestClaimHonoursTheOrganizationSlotCapOverride: the pool_slot_cap column is
// F11's per-organisation entitlement, and it beats the deployment-wide
// RUNNER_POOL_ORG_SLOT_CAP for the organisation it names.
func TestClaimHonoursTheOrganizationSlotCapOverride(t *testing.T) {
	store := queueTestStore(t)
	pool := queueTestDatabase(t)
	ctx := context.Background()
	now := testTime(t)

	createJob(t, store, CreateJobParams{
		IdempotencyKey: "cap-a-1", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	second := createJob(t, store, CreateJobParams{
		IdempotencyKey: "cap-a-2", OrganizationID: testOrgA, ExecutionID: testExecA,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	createJob(t, store, CreateJobParams{
		IdempotencyKey: "cap-b-1", OrganizationID: testOrgB, ExecutionID: testExecB,
		JobType: protocol.JobVideoCompile, Target: targetCloudPool,
	})
	// One concurrent CloudPool slot for org A. The deployment-wide cap in the
	// same claim is five, so an override that were ignored would take every job
	// here and the assertion below could not pass.
	setPlatformSetting(t, pool, settingsKeyOrgPrefix+testOrgA, false, 1)

	snapshot, err := (&PostgresSwitchStore{pool: pool}).ReadPlatformSettings(ctx)
	if err != nil {
		t.Fatalf("ReadPlatformSettings: %v", err)
	}
	if cap, ok := snapshot.PoolSlotCap(testOrgA); !ok || cap != 1 {
		t.Fatalf("PoolSlotCap(orgA) = %d, %v; want 1, true", cap, ok)
	}
	claim := func() []Job {
		t.Helper()
		jobs, err := store.ClaimJobs(ctx, ClaimRequest{
			RunnerID: testPoolOne, Kind: protocol.KindCloudPool, Caps: defaultCapabilities(),
			Want: 5, Now: now, LeaseBy: protocol.LeaseSec * time.Second,
			Instance: "10.0.3.17", PoolOrgCap: 5, Gates: snapshot.ClaimGates(),
		})
		if err != nil {
			t.Fatalf("ClaimJobs: %v", err)
		}
		return jobs
	}

	claimed := claim()
	perOrg := map[string]int{}
	for _, job := range claimed {
		perOrg[job.OrganizationID]++
	}
	if len(claimed) != 2 || perOrg[testOrgA] != 1 || perOrg[testOrgB] != 1 {
		t.Fatalf("claim = %v, want one job for org A (its override) and one for org B", jobIDs(claimed))
	}
	for _, job := range claimed {
		if job.ID == second.ID {
			t.Errorf("org A's second job %s was claimed past the override cap of 1", second.ID)
		}
	}
	// Org A is at its cap and org B has no further work, so the next round takes
	// the same nothing the deployment-wide cap would have produced.
	if next := claim(); len(next) != 0 {
		t.Errorf("second claim = %v, want nothing", jobIDs(next))
	}
	if status := jobStatus(t, store, second.ID); status != jobQueued {
		t.Errorf("the capped job is %q, want Queued", status)
	}
}
