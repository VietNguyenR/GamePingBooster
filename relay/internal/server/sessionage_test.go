package server

import (
	"net/netip"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

func addr(t *testing.T, s string) netip.AddrPort {
	t.Helper()
	a, err := netip.ParseAddrPort(s)
	if err != nil {
		t.Fatalf("bad test address %q: %v", s, err)
	}
	return a
}

// A session is authenticated once, at handshake, and never re-checked while it runs - cutting a
// customer off in the middle of a match is the worst possible moment. The price of that choice is
// that one handshake would otherwise be good forever, which is a way to keep using a relay after
// a subscription has lapsed. The maximum age is what closes it: the client reconnects, and at
// that point its credential is checked again.
//
// sweep takes the time as an argument precisely so this can be tested without waiting a day.
func TestSessionIsDroppedWhenItReachesMaximumAge(t *testing.T) {
	s := testServer(4)
	s.cfg.MaxSessionAge = 24 * time.Hour

	sess, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(1))
	if !ok {
		t.Fatal("could not allocate a session")
	}

	// lastSeen is written directly rather than through touch(), which reads the real clock: at a
	// simulated 23 hours a session touched "now" still looks idle by 23 hours, and the test would
	// then pass or fail for a reason that has nothing to do with age. Writing the field is how
	// the test says "the client was still sending at that moment".
	at23 := time.Unix(0, sess.born).Add(23 * time.Hour)
	sess.lastSeen.Store(at23.UnixNano())
	s.sweep(at23)
	if s.lookup(sess.id) == nil {
		t.Fatal("a 23-hour-old session that is still active was dropped, but the cap is 24 hours")
	}

	// Past the cap, and still active, so age is the only thing that can remove it. Without that
	// isolation this would pass for the wrong reason - the idle timeout would have caught it
	// anyway and the age check could be missing entirely.
	at25 := time.Unix(0, sess.born).Add(25 * time.Hour)
	sess.lastSeen.Store(at25.UnixNano())
	s.sweep(at25)
	if s.lookup(sess.id) != nil {
		t.Error("a session past the maximum age is still live, so one handshake lasts forever")
	}
}

// Sessions are only ever built by New() in production, which fills the default in. The session
// tests build a Server by hand, and a zero cap read literally means "born before now" - every
// session too old on the very first tick. A relay that drops every client thirty seconds after
// it starts is a worse failure than a cap that is not applied.
func TestZeroMaximumAgeDoesNotExpireEverything(t *testing.T) {
	s := testServer(4)
	s.cfg.MaxSessionAge = 0

	sess, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(1))
	if !ok {
		t.Fatal("could not allocate a session")
	}

	s.sweep(time.Now())
	if s.lookup(sess.id) == nil {
		t.Error("a zero cap expired a session that had just been created")
	}
}

// The two reasons a session goes away are different and must stay distinguishable: idle means
// the client vanished, age means the client is fine and is being asked to prove entitlement
// again. A session that is both must not be counted twice, or the statistics line reports fewer
// live sessions than there are.
func TestIdleAndTooOldAreNotBothCounted(t *testing.T) {
	s := testServer(4)
	s.cfg.IdleTimeout = 90 * time.Second
	s.cfg.MaxSessionAge = 24 * time.Hour

	sess, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(1))
	if !ok {
		t.Fatal("could not allocate a session")
	}

	// Old AND silent. releaseSession must run once, not twice.
	s.sweep(time.Unix(0, sess.born).Add(25 * time.Hour))

	if s.lookup(sess.id) != nil {
		t.Fatal("the session survived")
	}
	if n := len(s.bySession); n != 0 {
		t.Errorf("%d sessions left in the table, want 0", n)
	}
	// The address must be back in the pool exactly once. Releasing twice would put it in
	// twice, and two clients would later be handed the same inner address.
	seen := map[netip.Addr]int{}
	for _, ip := range s.freeIPs {
		seen[ip]++
	}
	for ip, n := range seen {
		if n > 1 {
			t.Errorf("%s is in the free pool %d times - two clients would get the same address", ip, n)
		}
	}
}

var _ = protocol.MaxSessionAge
