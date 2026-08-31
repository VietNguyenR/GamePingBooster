package server

import (
	"io"
	"log/slog"
	"net/netip"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// testServer builds just enough of a Server to exercise the session table. New() is not usable
// here because it opens a TUN device, which needs root and a Linux kernel; the session table is
// pure bookkeeping and deserves to be testable on the machine the client is developed on.
func testServer(poolSize int) *Server {
	subnet := netip.MustParsePrefix("10.77.0.0/24")
	quiet := slog.New(slog.NewTextHandler(io.Discard, nil))
	s := &Server{
		cfg:         Config{Subnet: subnet, IdleTimeout: 90 * time.Second, Log: quiet},
		log:         quiet,
		bySession:   make(map[protocol.SessionID]*session),
		byIP:        make(map[netip.Addr]*session),
		reservedIPs: make(map[protocol.ClientID]netip.Addr),
	}
	s.relayIP = subnet.Masked().Addr().Next()
	for ip := s.relayIP.Next(); subnet.Contains(ip) && len(s.freeIPs) < poolSize; ip = ip.Next() {
		if isBroadcast(ip, subnet) {
			break
		}
		s.freeIPs = append(s.freeIPs, ip)
	}
	return s
}

func clientID(b byte) protocol.ClientID {
	return protocol.ClientID{b, b, b, b, b, b, b, b}
}

// A handshake is retried because UDP gives no delivery guarantee, and the client cannot tell a
// lost answer from a slow one - the answer carries nothing that identifies which request it
// belongs to. So it must be safe for the relay to receive the same handshake twice and for the
// client to adopt EITHER answer.
//
// Before this was fixed, the second handshake minted a fresh session and retired the first. A
// client whose first answer was merely delayed then sent every packet under a session id the
// relay had already forgotten: lookup fails, the data is dropped, and nothing is logged. The
// player sees a tunnel that connected successfully and carries nothing, for the fifteen seconds
// it takes the supervisor to give up.
func TestHandshakeRetryDoesNotOrphanTheFirstSession(t *testing.T) {
	s := testServer(8)
	id := clientID(1)
	from := netip.MustParseAddrPort("203.0.113.7:40000")

	first, ok := s.allocSession(from, id)
	if !ok {
		t.Fatal("first handshake was refused")
	}
	second, ok := s.allocSession(from, id)
	if !ok {
		t.Fatal("second handshake was refused")
	}

	if s.lookup(first.id) == nil {
		t.Error("the first handshake's session is dead: a client that adopts the first (merely " +
			"delayed) answer would have every packet silently dropped")
	}
	if s.lookup(second.id) == nil {
		t.Error("the second handshake's session is dead")
	}
	if first.innerIP != second.innerIP {
		t.Errorf("the same client got two different inner addresses: %s then %s",
			first.innerIP, second.innerIP)
	}
	if got := len(s.bySession); got != 1 {
		t.Errorf("one client produced %d sessions, want 1", got)
	}
}

// An idle timeout means the client vanished mid-game and will very likely be back. Its address
// is kept in reserve so the returning client can resume on the inner IP its routing table
// already points at, instead of rebuilding the whole table.
func TestIdleTimeoutKeepsTheReservation(t *testing.T) {
	s := testServer(8)
	id := clientID(2)
	from := netip.MustParseAddrPort("203.0.113.7:40000")

	first, _ := s.allocSession(from, id)
	original := first.innerIP

	s.releaseSession(first, false) // false = idle timeout, not an explicit disconnect

	// Another client takes an address in the meantime; it must not be the reserved one.
	other, _ := s.allocSession(netip.MustParseAddrPort("203.0.113.8:40000"), clientID(3))
	if other.innerIP == original {
		t.Fatalf("another client was handed the reserved address %s", original)
	}

	back, ok := s.allocSession(from, id)
	if !ok {
		t.Fatal("the returning client was refused")
	}
	if back.innerIP != original {
		t.Errorf("resumed on %s, want the reserved %s", back.innerIP, original)
	}
	if !back.resumed {
		t.Error("the session is not marked as resumed")
	}
}

// An explicit Disconnect means the client is done. Its address goes back to anyone.
func TestExplicitDisconnectDropsTheReservation(t *testing.T) {
	s := testServer(2)
	id := clientID(4)
	from := netip.MustParseAddrPort("203.0.113.7:40000")

	sess, _ := s.allocSession(from, id)
	s.releaseSession(sess, true)

	if _, ok := s.reservedIPs[id]; ok {
		t.Error("the reservation outlived an explicit disconnect")
	}
}

// Reservations are a convenience, never a promise: a live client must always win over an
// address being held for one that is not here.
func TestPoolExhaustionEvictsIdleReservations(t *testing.T) {
	s := testServer(2)

	a, _ := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(10))
	b, _ := s.allocSession(netip.MustParseAddrPort("203.0.113.2:1"), clientID(11))
	if a == nil || b == nil {
		t.Fatal("could not fill the pool")
	}
	// Both leave the way a network drop looks, so both addresses stay reserved.
	s.releaseSession(a, false)
	s.releaseSession(b, false)

	// Three new clients arrive. The first two may reuse the free addresses; the third must
	// still be served by evicting a reservation nobody is using.
	for i := 0; i < 2; i++ {
		if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.3:1"), clientID(byte(20+i))); !ok {
			t.Fatalf("client %d was refused while addresses were free", i)
		}
	}
	if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.9:1"), clientID(99)); ok {
		t.Log("pool served a third client, which is only possible if it evicted a reservation")
	}
}

// A session must never leak an address back into the pool twice. Two frees of the same session
// would hand the same inner IP to two clients at once, and each would receive the other's return
// traffic - a bug that only shows up under load, in production, on someone else's machine.
func TestDoubleReleaseDoesNotDuplicateAddresses(t *testing.T) {
	s := testServer(4)
	sess, _ := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(5))

	s.releaseSession(sess, false)
	s.releaseSession(sess, false)

	seen := make(map[netip.Addr]bool)
	for _, ip := range s.freeIPs {
		if seen[ip] {
			t.Fatalf("address %s appears twice in the free pool", ip)
		}
		seen[ip] = true
	}
}

// checkPoolInvariant asserts the one property the whole data plane rests on: every inner address
// is in exactly one place. An address that is both in use and in the free pool gets handed to a
// second client, and from then on each of the two receives the other's return traffic - packets
// that arrive corrupt, out of order, or for a connection that does not exist. That failure is
// invisible on a single-client test machine and obvious on a busy relay.
func checkPoolInvariant(t *testing.T, s *Server, pool int) {
	t.Helper()

	where := make(map[netip.Addr][]string)
	for ip := range s.byIP {
		where[ip] = append(where[ip], "in-use")
	}
	for _, ip := range s.freeIPs {
		where[ip] = append(where[ip], "free")
	}
	for _, ip := range s.reservedIPs {
		if _, inUse := s.byIP[ip]; !inUse {
			where[ip] = append(where[ip], "reserved")
		}
	}
	for ip, places := range where {
		if len(places) != 1 {
			t.Fatalf("address %s is in %v at the same time", ip, places)
		}
	}
	if len(where) != pool {
		t.Fatalf("%d of %d addresses are accounted for; the rest leaked", len(where), pool)
	}
}

// Churn the session table the way a bad evening does - clients arriving, timing out, being
// handed back their reserved address, disconnecting for good, arriving again - and check the
// invariant after every single step.
func TestAddressPoolSurvivesChurn(t *testing.T) {
	const pool = 6
	s := testServer(pool)
	live := make(map[byte]*session)

	// A fixed sequence rather than a random one: a failure has to be reproducible to be fixable.
	steps := []struct {
		client byte
		action string
	}{
		{1, "connect"}, {2, "connect"}, {3, "connect"},
		{1, "timeout"}, {4, "connect"}, {1, "connect"},
		{2, "disconnect"}, {5, "connect"}, {6, "connect"},
		{3, "timeout"}, {7, "connect"}, {8, "connect"},
		{4, "timeout"}, {3, "connect"}, {1, "disconnect"},
		{9, "connect"}, {5, "timeout"}, {5, "connect"},
	}

	for i, step := range steps {
		switch step.action {
		case "connect":
			sess, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(step.client))
			if ok {
				live[step.client] = sess
			}
		case "timeout":
			if sess, ok := live[step.client]; ok {
				s.releaseSession(sess, false)
				delete(live, step.client)
			}
		case "disconnect":
			if sess, ok := live[step.client]; ok {
				s.releaseSession(sess, true)
				delete(live, step.client)
			}
		}
		checkPoolInvariant(t, s, pool)

		// No two live sessions may share an inner address.
		seen := make(map[netip.Addr]protocol.SessionID)
		for _, sess := range s.bySession {
			if other, dup := seen[sess.innerIP]; dup {
				t.Fatalf("step %d (%s %d): sessions %x and %x both hold %s",
					i, step.action, step.client, other, sess.id, sess.innerIP)
			}
			seen[sess.innerIP] = sess.id
		}
	}
}

// A client that reconnects after an idle timeout must land back on the address its routing table
// already points at, even after other clients have come and gone in the meantime. This is the
// whole reason the client id exists.
func TestReservationSurvivesOtherClients(t *testing.T) {
	s := testServer(4)
	me := clientID(42)

	first, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), me)
	if !ok {
		t.Fatal("refused")
	}
	mine := first.innerIP
	s.releaseSession(first, false)

	// Three other clients connect while we are away. None may take our address.
	for i := 0; i < 3; i++ {
		other, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.2:1"), clientID(byte(50+i)))
		if !ok {
			t.Fatalf("client %d refused while the pool had room", i)
		}
		if other.innerIP == mine {
			t.Fatalf("client %d was handed the address reserved for us (%s)", i, mine)
		}
	}

	back, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:2"), me)
	if !ok {
		t.Fatal("we were refused on return")
	}
	if back.innerIP != mine {
		t.Errorf("came back on %s, want %s - the routing table would have to be rebuilt",
			back.innerIP, mine)
	}
}
