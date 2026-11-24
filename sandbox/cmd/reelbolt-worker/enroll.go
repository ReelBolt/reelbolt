package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log"
	"runtime"
	"time"

	"github.com/google/uuid"
	"github.com/jackc/pgx/v5/pgxpool"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// This file closes the gap D5a found and D5b confirmed: a pool worker is refused
// 4401 unless runner_devices holds a row with kind='CloudPool' and its name, and
// nothing wrote one, so CloudPool jobs stayed Queued forever.
//
// Why enrollment is a command run by cloud-init and not something the worker
// does for itself:
//
//   - The refusal is fail-closed ON PURPOSE (D5a). If a worker could create its
//     own row, a leaked pool credential would be an identity-minting capability
//     and revocation would be a suggestion: the revoked worker would simply
//     enroll again on its next boot. Enrollment is therefore out of band, with
//     a credential the pool secret does not imply.
//   - Revocation stays sticky. This command never clears revoked_at unless the
//     operator says so explicitly with -reinstate, so a recycled VM name cannot
//     quietly return a burned worker to service.
//
// The row carries no secret — public_key is null for a pool worker, which
// authenticates with the pool credential — so the only thing enrollment has to
// protect is the ability to name an identity, and that is exactly what
// DATABASE_URL is.

// enrollOutcome is what happened, for the log line and for the test.
type enrollOutcome struct {
	ID         string
	Name       string
	Created    bool
	Revoked    bool
	Reinstated bool
	Updated    bool
	Version    string
}

func enrollCommand(args []string) error {
	fs := flag.NewFlagSet("enroll", flag.ContinueOnError)
	name := fs.String("name", "", "worker name to enroll (default: REELBOLT_WORKER_NAME, then the hostname)")
	version := fs.String("version", "", "runner_version to record (default: REELBOLT_WORKER_VERSION, then the build version)")
	reinstate := fs.Bool("reinstate", false, "clear revoked_at on an existing row. An explicit operator action: enrollment never does this by itself")
	dryRun := fs.Bool("dry-run", false, "report what would change and write nothing")
	if err := fs.Parse(args); err != nil {
		return err
	}

	cfg, err := loadConfig()
	if err != nil {
		return err
	}
	if *name != "" {
		cfg.WorkerName = *name
	}
	if cfg.WorkerName == "" {
		return errors.New("no worker name: pass -name, or set REELBOLT_WORKER_NAME")
	}
	if err := validateWorkerName(cfg.WorkerName); err != nil {
		return err
	}
	if cfg.DatabaseURL == "" {
		return errors.New("DATABASE_URL is required to enroll a pool worker; the running worker never needs it")
	}
	versionValue := cfg.AgentVersion
	if *version != "" {
		versionValue = *version
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	pool, err := pgxpool.New(ctx, cfg.DatabaseURL)
	if err != nil {
		return fmt.Errorf("connect to postgres: %w", err)
	}
	defer pool.Close()
	if err := ensureRunnerSchema(ctx, pool); err != nil {
		return err
	}

	out, err := enrollPoolWorker(ctx, pool, cfg.WorkerName, versionValue, *reinstate, *dryRun)
	if err != nil {
		return err
	}
	switch {
	case *dryRun:
		log.Printf("dry run: would %s pool worker %q (id %s)", createdWord(out), out.Name, out.ID)
	case out.Created:
		log.Printf("enrolled pool worker %q as %s", out.Name, out.ID)
	case out.Reinstated:
		log.Printf("reinstated pool worker %q (%s): revoked_at cleared", out.Name, out.ID)
	case out.Revoked:
		log.Printf("pool worker %q (%s) is REVOKED and was left that way", out.Name, out.ID)
	default:
		log.Printf("pool worker %q is already enrolled as %s", out.Name, out.ID)
	}
	if out.Revoked && !out.Reinstated {
		return fmt.Errorf("pool worker %q (%s) is revoked: this worker is refused 4401 until an operator "+
			"returns it to service with -reinstate, or enrolls under a different name", out.Name, out.ID)
	}
	return nil
}

func createdWord(out enrollOutcome) string {
	if out.Created {
		return "create"
	}
	return "update"
}

// validateWorkerName refuses a name the column cannot hold. The name is the
// identity, so a name the database would truncate is not a name.
func validateWorkerName(name string) error {
	if name == "" {
		return errors.New("worker name is empty")
	}
	if len(name) > 200 {
		return fmt.Errorf("worker name is %d bytes; runner_devices.name holds 200", len(name))
	}
	return nil
}

// enrollPoolWorker creates or refreshes the CloudPool row for one worker.
//
// It runs in one transaction under an advisory lock keyed on the name, because
// runner_devices has no unique index on (kind, name) — the gateway's lookup is
// ORDER BY created_at LIMIT 1 — and two workers booting under the same name
// would otherwise both insert and leave an ambiguous identity that the gateway
// resolves by row age.
func enrollPoolWorker(ctx context.Context, pool *pgxpool.Pool, name, version string, reinstate, dryRun bool) (enrollOutcome, error) {
	out := enrollOutcome{Name: name, Version: version}
	tx, err := pool.Begin(ctx)
	if err != nil {
		return out, fmt.Errorf("begin: %w", err)
	}
	defer func() { _ = tx.Rollback(ctx) }()

	if _, err := tx.Exec(ctx, "SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", "reelbolt-pool-enroll:"+name); err != nil {
		return out, fmt.Errorf("lock: %w", err)
	}

	rows, err := tx.Query(ctx,
		"SELECT id::text, revoked_at FROM runner_devices WHERE kind = $1 AND name = $2 ORDER BY created_at, id",
		protocol.KindCloudPool, name)
	if err != nil {
		return out, fmt.Errorf("read runner_devices: %w", err)
	}
	var (
		ids     []string
		revoked []bool
	)
	for rows.Next() {
		var (
			id string
			at *time.Time
		)
		if err := rows.Scan(&id, &at); err != nil {
			rows.Close()
			return out, fmt.Errorf("scan runner_devices: %w", err)
		}
		ids = append(ids, id)
		revoked = append(revoked, at != nil)
	}
	rows.Close()
	if err := rows.Err(); err != nil {
		return out, fmt.Errorf("read runner_devices: %w", err)
	}
	if len(ids) > 1 {
		return out, fmt.Errorf("%d CloudPool rows are named %q (%v): the gateway would pick the oldest, so this "+
			"worker's identity would be whichever row that is. Rename or delete the extra rows", len(ids), name, ids)
	}

	goos, goarch := runtime.GOOS, runtime.GOARCH
	switch len(ids) {
	case 0:
		id := uuid.NewString()
		if dryRun {
			out.ID, out.Created = id, true
			return out, nil
		}
		if _, err := tx.Exec(ctx,
			`INSERT INTO runner_devices (id, organization_id, kind, name, os, arch, runner_version, created_at)
			 VALUES ($1::uuid, NULL, $2, $3, $4, $5, $6, now())`,
			id, protocol.KindCloudPool, name, goos, goarch, version); err != nil {
			return out, fmt.Errorf("insert runner_devices: %w", err)
		}
		out.ID, out.Created = id, true
		// A desktop device with the same name is not a conflict (the gateway's
		// lookup filters on kind) but it is a footgun worth naming.
		var desktops int
		if err := tx.QueryRow(ctx,
			"SELECT count(*) FROM runner_devices WHERE kind = $1 AND name = $2",
			protocol.KindDesktop, name).Scan(&desktops); err == nil && desktops > 0 {
			log.Printf("WARNING: %d desktop device(s) are also named %q; the pool row is separate but the two will be hard to tell apart in a log",
				desktops, name)
		}
	default:
		out.ID = ids[0]
		out.Revoked = revoked[0]
		if out.Revoked && !reinstate {
			// Deliberately not an error here: the caller renders it, so the
			// operator sees the row and the remedy in one line.
			return out, nil
		}
		if dryRun {
			out.Reinstated = out.Revoked
			return out, nil
		}
		tag, err := tx.Exec(ctx,
			`UPDATE runner_devices
			    SET runner_version = $2, os = $3, arch = $4,
			        revoked_at = CASE WHEN $5 THEN NULL ELSE revoked_at END,
			        revoked_by_user_id = CASE WHEN $5 THEN NULL ELSE revoked_by_user_id END
			  WHERE id = $1::uuid`,
			out.ID, version, goos, goarch, reinstate)
		if err != nil {
			return out, fmt.Errorf("update runner_devices: %w", err)
		}
		out.Updated = tag.RowsAffected() == 1
		out.Reinstated = out.Revoked && reinstate
	}

	if dryRun {
		return out, nil
	}
	if err := tx.Commit(ctx); err != nil {
		return out, fmt.Errorf("commit: %w", err)
	}
	return out, nil
}

// ensureRunnerSchema reports whether the migration that owns runner_devices has
// run. Without it, enrollment against a database whose schema is not there yet
// fails with a Postgres error that says nothing about the deployment order (the
// Inference API migrates first, the engine second).
func ensureRunnerSchema(ctx context.Context, pool *pgxpool.Pool) error {
	var exists bool
	err := pool.QueryRow(ctx,
		"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'runner_devices')").Scan(&exists)
	if err != nil {
		return fmt.Errorf("inspect the schema: %w", err)
	}
	if !exists {
		return errors.New("runner_devices does not exist: the Inference API migrates it on startup, so run enroll after that service is healthy")
	}
	return nil
}
