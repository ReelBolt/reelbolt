package main

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/vecchiotom/reelbolt/sandbox/pkg/protocol"
)

// fakeDisplayStore answers one lookup the way a test wants it answered.
type fakeDisplayStore struct {
	display protocol.AssignDisplay
	err     error
	delay   time.Duration
	gotExec string
	gotOrg  string
}

func (f *fakeDisplayStore) JobDisplay(ctx context.Context, executionID, organizationID string) (protocol.AssignDisplay, error) {
	f.gotExec, f.gotOrg = executionID, organizationID
	if f.delay > 0 {
		select {
		case <-time.After(f.delay):
		case <-ctx.Done():
			return protocol.AssignDisplay{}, ctx.Err()
		}
	}
	return f.display, f.err
}

func displayJob() Job {
	return Job{ID: "job-1", OrganizationID: testOrgA, ExecutionID: testExecA}
}

func TestJobDisplayCarriesTheNamesOfTheJobsOwnOrganization(t *testing.T) {
	g := newGateway(appConfig{}, newFakeStore())
	store := &fakeDisplayStore{display: protocol.AssignDisplay{WorkspaceName: "Acme", ProjectName: "Launch", WorkflowName: "Promo"}}
	g.attachDisplay(store)

	got := g.jobDisplay(context.Background(), displayJob())
	if got == nil || got.ProjectName != "Launch" || got.WorkspaceName != "Acme" {
		t.Fatalf("display = %+v", got)
	}
	if store.gotExec != testExecA || store.gotOrg != testOrgA {
		t.Fatalf("looked up (%q, %q), want the job's own execution and organization", store.gotExec, store.gotOrg)
	}
}

// Every failure is "no names": the assignment is never held back by a lookup.
func TestJobDisplayFailuresOmitTheDisplay(t *testing.T) {
	cases := map[string]*fakeDisplayStore{
		"no row in the job's organization": {err: ErrJobNotFound},
		"a broken database":                {err: errors.New("connection refused")},
		"an empty row":                     {},
	}
	for name, store := range cases {
		g := newGateway(appConfig{}, newFakeStore())
		g.attachDisplay(store)
		if got := g.jobDisplay(context.Background(), displayJob()); got != nil {
			t.Errorf("%s: display = %+v, want none", name, got)
		}
	}
}

func TestASlowDisplayLookupIsAbandonedInsteadOfDelayingTheOffer(t *testing.T) {
	g := newGateway(appConfig{}, newFakeStore())
	g.attachDisplay(&fakeDisplayStore{delay: time.Minute, display: protocol.AssignDisplay{ProjectName: "late"}})
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	started := time.Now()
	if got := g.jobDisplay(ctx, displayJob()); got != nil {
		t.Fatalf("display = %+v, want none", got)
	}
	if elapsed := time.Since(started); elapsed > displayLookupTimeout {
		t.Fatalf("the lookup held the offer for %s", elapsed)
	}
}

func TestNoDisplayStoreMeansNoDisplay(t *testing.T) {
	g := newGateway(appConfig{}, newFakeStore())
	if got := g.jobDisplay(context.Background(), displayJob()); got != nil {
		t.Fatalf("display = %+v with no store attached", got)
	}
	// A job with no execution (a future job type) is not looked up at all.
	store := &fakeDisplayStore{display: protocol.AssignDisplay{ProjectName: "x"}}
	g.attachDisplay(store)
	if got := g.jobDisplay(context.Background(), Job{OrganizationID: testOrgA}); got != nil || store.gotExec != "" {
		t.Fatalf("a job with no execution was looked up: %+v", got)
	}
}
