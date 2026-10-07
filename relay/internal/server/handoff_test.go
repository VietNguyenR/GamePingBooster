package server

import (
	"encoding/json"
	"net/netip"
	"testing"
)

// The table a hot restart hands over must come back as the same sessions on the same addresses,
// with the pool short of exactly the addresses that are live or reserved - an address both in use
// and free is two players on one inner IP.
func TestHandoffRestoresSessionsAndReservations(t *testing.T) {
	old := testServer(8)
	from := netip.MustParseAddrPort("203.0.113.7:40000")
	a, _ := old.allocSession(from, clientID(1), sessionIdent{userID: 42, deviceKey: []byte{4, 1, 2}})
	b, _ := old.allocSession(from, clientID(2), sessionIdent{})
	gone, _ := old.allocSession(from, clientID(3), sessionIdent{})
	old.releaseSession(gone, false) // idle timeout: reservation kept

	state, err := json.Marshal(old.exportLocked())
	if err != nil {
		t.Fatal(err)
	}

	fresh := testServer(8)
	n, _, err := fresh.restore(state)
	if err != nil || n != 2 {
		t.Fatalf("restore = %d, %v; want 2 sessions", n, err)
	}
	for _, want := range []*session{a, b} {
		got := fresh.lookup(want.id)
		if got == nil || got.innerIP != want.innerIP || got.born != want.born || *got.addr.Load() != from {
			t.Fatalf("session %v did not come back intact: %+v", want.id, got)
		}
	}
	if got := fresh.lookup(a.id).ident; got.userID != 42 || len(got.deviceKey) != 3 {
		t.Fatalf("identity lost: %+v", got)
	}
	if fresh.reservedIPs[clientID(3)] != gone.innerIP {
		t.Fatal("the idle client's reservation was lost")
	}
	if len(fresh.freeIPs) != 8-3 {
		t.Fatalf("pool has %d free, want 5", len(fresh.freeIPs))
	}
	for _, ip := range fresh.freeIPs {
		if ip == a.innerIP || ip == b.innerIP || ip == gone.innerIP {
			t.Fatalf("%s is both held and free", ip)
		}
	}
	// The returning idle client gets its old address back, as it would have without a restart.
	back, _ := fresh.allocSession(from, clientID(3), sessionIdent{})
	if back.innerIP != gone.innerIP {
		t.Fatalf("returning client got %s, want %s", back.innerIP, gone.innerIP)
	}
}

// A table this binary cannot trust is refused whole, leaving the empty table a cold restart would.
func TestHandoffRefusesUnknownFormatAndForeignAddresses(t *testing.T) {
	s := testServer(4)
	if _, _, err := s.restore([]byte(`{"v":99}`)); err == nil {
		t.Fatal("an unknown format was accepted")
	}
	if _, _, err := s.restore(nil); err == nil {
		t.Fatal("an empty table was accepted")
	}

	st := handoffState{V: handoffVersion, Sessions: []handoffSession{
		{ID: [8]byte{1}, InnerIP: netip.MustParseAddr("10.99.0.5"), Addr: netip.MustParseAddrPort("203.0.113.7:1")},
		{ID: [8]byte{2}, InnerIP: netip.MustParseAddr("10.77.0.2"), Addr: netip.MustParseAddrPort("203.0.113.7:2")},
		{ID: [8]byte{3}, InnerIP: netip.MustParseAddr("10.77.0.2"), Addr: netip.MustParseAddrPort("203.0.113.7:3")},
	}}
	data, _ := json.Marshal(st)
	if n, _, err := s.restore(data); err != nil || n != 1 {
		t.Fatalf("restore = %d, %v; want only the one valid, unduplicated session", n, err)
	}
}
