package services

import (
	"context"
	"log"

	"github.com/google/uuid"
)

// SeatChangeNotifier is told whenever the number of members of a Team
// organization grows, so a per-seat subscription can be adjusted. The
// implementation arrives with the billing provider work (B9a/B9b: it calls
// the provider's ChangeSeatsAsync for orgs on a seat-priced plan); until then
// the default does nothing.
type SeatChangeNotifier interface {
	// SeatsChanged is called after a member was added to orgID. Failures are
	// logged by the caller and never undo the membership: the join already
	// happened, and billing reconciles seat counts against membership (B12).
	SeatsChanged(ctx context.Context, orgID uuid.UUID) error
}

// NoopSeatNotifier is the default: no billing provider is wired.
type NoopSeatNotifier struct{}

func (NoopSeatNotifier) SeatsChanged(context.Context, uuid.UUID) error { return nil }

// SeatChanges is the process-wide notifier; B9a/B9b replace it at startup.
var SeatChanges SeatChangeNotifier = NoopSeatNotifier{}

// NotifySeatsChanged calls SeatChanges and logs, never returns, a failure.
func NotifySeatsChanged(ctx context.Context, orgID uuid.UUID) {
	if err := SeatChanges.SeatsChanged(ctx, orgID); err != nil {
		log.Printf("seat change notification for org %s failed: %v", orgID, err)
	}
}
